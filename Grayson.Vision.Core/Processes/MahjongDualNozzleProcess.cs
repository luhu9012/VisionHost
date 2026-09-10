using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Flow.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「巴斯勒相机 + Epson 4 轴 SCARA（双吸嘴）」工件双牌分拣业务过程。
    ///
    /// 职责分层（与 MahjongPickProcess 同一约定）：
    /// - 视觉定位段（编辑器节点编排，换产品调参）：
    ///     AcquireImage(巴斯勒相机) → ShapeMatch(工件模板) → CalibrationApply(像素→机器人坐标)
    ///   跑完后从 ShapeMatch.MatchScore 与 CalibrationApply.OutputX/OutputY 读回结果；
    /// - 运动时序段（本类代码写死，机械逻辑不随产品变化）：
    ///     安全检查 → 触发视觉流定位第一块牌 → 吸嘴1 吸取 → [双吸嘴: 吸嘴2 吸取
    ///     （第二块按阵列节距推算）] → 依次摆盘放料 → 回待机。
    ///
    /// 落点几何（2026-09-08 定案，唯一真源 = CalibrationGeometry，本类不得另写一份公式）：
    ///   每吸嘴只认【一个】几何量：Ecc = 真吸嘴偏心 e（U0=ToolAlignU 参考），
    ///   由「旋转中心 O」+「物理对针」反解得到（e = O − H(p_tip)），发布链写入 NozzleN EccX/Y。
    ///   ⚠ 旧字段 Nozzle1TcoX/Y（TCO）已废弃且发布时清零，不要再参与计算。
    ///   工件真位与走位命令（人话版见 标定语义_人话版_2026-09-08.md）：
    ///     眼在手 EIH：X_obj = 拍照机位 P_photo + 旋转中心 O − H(像素 u)
    ///     固定相机 ETH：X_obj = H(像素 u)
    ///     吸取/放料：  C = X_obj − R(姿态U − U0)·Ecc
    ///       （U 不转时 R 就是 Ecc 本身；U≠U0 必须转，否则差十几 mm）
    ///   全部坐标由机械手绝对定位保证，转动/平移均在 SafeZ 进行。
    ///
    /// 吸嘴形态由 MahjongDualNozzleConfig.UseSingleNozzle 决定：
    /// - 单吸嘴（当前调试形态，一次循环搬运 1 块牌）：
    ///   Phase 0  安全检查：Z 抬至安全高度、真空关闭、配方节点校验
    ///   Phase 1  视觉定位：触发视觉流，读回第一块牌的机器人坐标 + 匹配分数
    ///   Phase 2  吸嘴1 吸取：移到牌上方 → Z 下探 → 真空 ON → Z 抬起
    ///   Phase 4  吸嘴1 放料：移到放料位1 → Z 下探 → 真空 OFF → Z 抬起
    ///   Phase 6  回待机位
    /// - 双吸嘴（一次循环搬运 2 块，产能翻倍）：
    ///   追加 Phase 3 吸嘴2 吸取（牌1坐标+节距推算）与 Phase 5 吸嘴2 放料。
    ///
    /// ⚠ 配方节点链要求（工位绑定的视觉配方里必须存在）：
    ///   AcquireImage（相机别名=工位监视/设备管理里登记的海康相机）
    ///   → ShapeMatch（模板名=模板管理里创建的工件模板，MinScore ≥ 配置阈值）
    ///   → CalibrationApply（HomMatFilePath=14点标定发布的矩阵文件）。
    /// </summary>
    public class MahjongDualNozzleProcess : StationProcessBase
    {
        private readonly MahjongDualNozzleConfig _cfg;

        /// <summary>业务过程唯一键（与 StationConfigModel.ProcessKey 对应）</summary>
        public override string ProcessKey => "MahjongDualNozzle";

        public MahjongDualNozzleProcess(StationWorker worker, MahjongDualNozzleConfig config = null)
            : base(worker, (config ?? new MahjongDualNozzleConfig()).CardAlias)
        {
            _cfg = config ?? new MahjongDualNozzleConfig();
        }

        /// <summary>
        /// 执行一次「吸取摆盘」循环（单吸嘴搬 1 块 / 双吸嘴搬 2 块）。
        /// 返回 true = 成功；false = 视觉 NG 或中途异常（Z 轴已抬至安全高度）。
        /// </summary>
        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            string nozzleMode = _cfg.UseSingleNozzle ? "单吸嘴（1块/循环）" : "双吸嘴（2块/循环）";
            Log($"========== 工件吸嘴分拣 开始 [{nozzleMode}] ==========");
            try
            {
                // ---- Phase 0: 安全检查 ----
                // Z 抬至安全高度（低于安全高度说明上次异常残留，先抬升）
                await EnsureSafeZAsync(token).ConfigureAwait(false);

                // 真空强制关闭（防止带料悬停撞机；单吸嘴形态下 VacuumIo2 无实际阀，写了也无害）
                SetOutput(_cfg.VacuumIo1, false);
                if (!_cfg.UseSingleNozzle)
                {
                    SetOutput(_cfg.VacuumIo2, false);
                }

                // U 轴转到作业角度（工件牌无需旋转，仅首圈或被人为动过后有实际运动）
                await MoveAbsAsync(_cfg.AxisU, _cfg.WorkU, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 1: 视觉定位（相机固定于料盘上方，触发即拍）----
                // EIH 眼在手：X_obj = P_photo + O − H(u) 里的 P_photo 必须是【拍这张图时】的机位，
                //            所以先回到发布时写入的拍照基准位再触发（ETH 固定相机不需要）
                await EnsurePhotoBaseAsync(token).ConfigureAwait(false);
                Log("[Phase1] 触发视觉流（海康采图 → 工件模板匹配 → 坐标标定换算）...");
                await RunVisionFlowAsync($"MJ2_{DateTime.Now:HHmmss}").ConfigureAwait(false);

                // 读取视觉结果：第一块牌的物理坐标与匹配分数
                var matchNode = FindNode(NodeType.ShapeMatch);
                var calibNode = FindNode(NodeType.CalibrationApply);
                if (matchNode == null || calibNode == null)
                {
                    throw new InvalidOperationException(
                        "当前配方缺少 ShapeMatch / CalibrationApply 节点，请确认编辑器中已编排视觉定位流");
                }

                double score = GetOutValue<double>(matchNode, "MatchScore");
                double worldX1 = GetOutValue<double>(calibNode, "OutputX");
                double worldY1 = GetOutValue<double>(calibNode, "OutputY");
                double matchAngle = GetOutValue<double>(matchNode, "MatchAngle");

                Log($"[Phase1] 牌1视觉结果: Score={score:F3}, World=({worldX1:F3}, {worldY1:F3}), Angle={matchAngle:F2}°");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // ---- 示教模式（落点公式验证，2026-09-08 定案式）：视觉定位完成，不执行走位/吸取 ----
                // 定案式（与校验台同一真源 CalibrationGeometry）：
                //   X_obj = P_photo + O − H(u)        （EIH 眼在手；ETH 固定相机时 X_obj = H(u)）
                //   C*    = X_obj − R(WorkU − U0)·Ecc1 ← 吸嘴1 正对麻将中心时，回转中心应停的位置
                //   手动把吸嘴1 移到麻将中心正上方读回 X*/Y* 与 C* 对照（差异应≈0）。
                bool hasEcc1 = Math.Abs(_cfg.Nozzle1EccX) > 1e-6 || Math.Abs(_cfg.Nozzle1EccY) > 1e-6;
                if (_cfg.TeachMode)
                {
                    var (ax, ay) = PickAnchor(worldX1, worldY1);
                    var (wx, wy) = RotEcc(_cfg.WorkU - _cfg.ToolAlignU, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY);
                    double expectX = ax - wx;
                    double expectY = ay - wy;
                    Log("[Phase2] 👉【示教模式】视觉定位完成，不执行走位/吸取。请手动移动机械手让吸嘴1 正对麻将中心，");
                    Log($"[Phase2] 👉【示教模式】  工件真位 X_obj = P_photo + O − H(u) = ({ax:F3},{ay:F3})" +
                        (_cfg.CameraMountEih ? "（EIH 眼在手）" : "（ETH 固定相机：X_obj = H(u)）"));
                    Log($"[Phase2] 👉【示教模式】  理论回转中心 C* = X_obj − R({_cfg.WorkU:F0}° − U0 {_cfg.ToolAlignU:F0}°)·Ecc1 = ({expectX:F3},{expectY:F3})（吸嘴对准麻将中心时机械手应停在此处）");
                    Log($"[示教记录] H(u)=({worldX1:F3},{worldY1:F3}), P_photo=({_cfg.PhotoBaseX:F3},{_cfg.PhotoBaseY:F3}), O=({_cfg.RotCenterWx:F3},{_cfg.RotCenterWy:F3}), " +
                        $"EIH={_cfg.CameraMountEih}, ToolAlignU={_cfg.ToolAlignU:F1}, WorkU={_cfg.WorkU:F1}, " +
                        $"Ecc1=({_cfg.Nozzle1EccX:F3},{_cfg.Nozzle1EccY:F3}), 理论C*=({expectX:F3},{expectY:F3}), 实测X*/Y*=____");
                    if (!hasEcc1)
                        Log("[Phase2] ⚠【示教模式】Ecc1=0（未发布吸嘴偏心 e）：走位等于「视觉直吸」，只在 U 恒=U0 时才准");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return true;
                }

                // ---- Phase 2: 吸嘴1 吸取牌1 ----
                // 工件真位 X_obj = P_photo + O − H(u)（EIH）/ H(u)（ETH）；
                // 回转中心 C = X_obj − R(WorkU − U0)·Ecc1（U 不转时 R 即 Ecc 本身）
                Log("[Phase2] 吸嘴1 吸取牌1...");
                var (p1x, p1y) = PickAnchor(worldX1, worldY1);
                Log($"[Phase2] 牌1工件真位 X_obj=({p1x:F3},{p1y:F3})（定案式：{(_cfg.CameraMountEih ? "P_photo + O − H(u)" : "H(u)，ETH")}）");
                await MoveToWorkAsync(p1x, p1y, _cfg.WorkU, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 3: 吸嘴2 吸取牌2（仅双吸嘴形态）----
                // 牌2 中心 = 牌1 真位 + 阵列节距（料盘阵列推算）；吸嘴1 已吸牌1 保持，
                // 回转中心移去让吸嘴2 对准牌2：C = P2 − R(WorkU)·Ecc2
                if (!_cfg.UseSingleNozzle)
                {
                    double p2x = p1x + _cfg.TilePitchX;
                    double p2y = p1y + _cfg.TilePitchY;
                    Log($"[Phase3] 吸嘴2 吸取牌2: P2=({p2x:F3}, {p2y:F3})（P1+节距 {_cfg.TilePitchX:F1}/{_cfg.TilePitchY:F1}）");
                    await MoveToWorkAsync(p2x, p2y, _cfg.WorkU, _cfg.Nozzle2EccX, _cfg.Nozzle2EccY, token).ConfigureAwait(false);
                    await PickOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);
                }

                // ---- Phase 3.5: 摆盘放料角统一（2026-09-03 固定角；2026-09-06 增视觉实测角归正）----
                // 固定角归正：U 直接转 PlaceU（来料姿态固定/正位）；
                // 视觉实测角归正（EnableVisionAngleCorrection）：U_place = PlaceU − Sign·(MatchAngle − RefAngleDeg)
                //   把实测姿态偏差反向补掉，使工件按 RefAngleDeg 参考姿态落盘；
                //   符号错误（越归正越歪）现场翻转 AngleCorrectionSign，无需改代码。
                // 转 U 使工件中心相对回转中心变为 R(U_place)·Ecc，后续放料位坐标按
                // C = Place − R(U_place)·Ecc 计算，落点不受偏心影响。
                float uPlace = _cfg.PlaceU;
                if (_cfg.EnableVisionAngleCorrection)
                {
                    double angleDelta = matchAngle - _cfg.RefAngleDeg;
                    uPlace = _cfg.PlaceU - _cfg.AngleCorrectionSign * (float)angleDelta;
                    Log($"[Phase3.5] 视觉归正：U_place = PlaceU({_cfg.PlaceU:F1}) − Sign({_cfg.AngleCorrectionSign:F0})·(MatchAngle({matchAngle:F2}) − Ref({_cfg.RefAngleDeg:F1})) = {uPlace:F1}°");
                }
                else
                {
                    Log($"[Phase3.5] U 轴转到摆盘放料角 {_cfg.PlaceU:F1}°（统一放置姿态）...");
                }
                await MoveAbsAsync(_cfg.AxisU, uPlace, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 4: 吸嘴1 放料（摆盘位1，麻将中心落 Place1）----
                Log("[Phase4] 吸嘴1 放料至摆盘位1...");
                await MoveToWorkAsync(_cfg.Place1X, _cfg.Place1Y, uPlace, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 5: 吸嘴2 放料（摆盘位2，仅双吸嘴形态）----
                if (!_cfg.UseSingleNozzle)
                {
                    Log("[Phase5] 吸嘴2 放料至摆盘位2...");
                    await MoveToWorkAsync(_cfg.Place2X, _cfg.Place2Y, uPlace, _cfg.Nozzle2EccX, _cfg.Nozzle2EccY, token).ConfigureAwait(false);
                    await PlaceOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);
                }

                // ---- Phase 6: 回待机 ----
                await BackToStandbyAsync(token).ConfigureAwait(false);

                Log($"========== 工件吸嘴分拣 成功（本次搬运 {(_cfg.UseSingleNozzle ? 1 : 2)} 块） ==========");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 业务过程异常: {ex.Message}");
                // 异常兜底：尽力抬 Z 至安全高度，避免吸着料悬停低位
                await EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed).ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// 安全收尾：关闭双真空（防带料悬停）+ Z 抬至安全高度。
        /// 由 Worker.StopAsync 在停止/急停时调用，幂等可重复执行。
        /// </summary>
        public override Task SafeStopAsync(CancellationToken token = default)
        {
            Log("安全收尾：关闭双真空，Z 抬至安全高度...");
            try
            {
                if (Card != null)
                {
                    SetOutput(_cfg.VacuumIo1, false);
                    SetOutput(_cfg.VacuumIo2, false);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ 安全收尾关闭真空失败: {ex.Message}");
            }
            return EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, token);
        }

        // ============================================================
        // 吸/放动作原语（Z 下探 + 真空 + Z 抬起，双吸嘴复用）
        // ============================================================

        /// <summary>
        /// 吸取原语：Z 下探至吸取高度 → 开真空 → 保压等待 → Z 抬至安全高度。
        /// 调用前机器人 XY 必须已在目标牌正上方。
        /// </summary>
        private async Task PickOnceAsync(int vacuumIo, string nozzleName, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisZ, _cfg.PickZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            SetOutput(vacuumIo, true);
            await Task.Delay(_cfg.VacuumOnDelayMs, token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            Log($"  {nozzleName} 吸取完成（真空 IO{vacuumIo} ON）");
        }

        /// <summary>
        /// 放料原语：Z 下探至放料高度 → 关真空(破空) → 等待脱落 → Z 抬至安全高度。
        /// </summary>
        private async Task PlaceOnceAsync(int vacuumIo, string nozzleName, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisZ, _cfg.PlaceZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            SetOutput(vacuumIo, false);
            await Task.Delay(_cfg.VacuumOffDelayMs, token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            Log($"  {nozzleName} 放料完成（真空 IO{vacuumIo} OFF）");
        }

        // ============================================================
        // 移动原语（偏心补偿：工件中心 → 回转中心）
        // ============================================================

        /// <summary>
        /// 旋转偏心矢量（世界，U=0 参考）：R(angDeg)·(ex,ey) = (ex·cos−ey·sin, ex·sin+ey·cos)。
        /// </summary>
        private static (double X, double Y) RotEcc(double angDeg, double ex, double ey)
        {
            double r = angDeg * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            return (ex * c - ey * s, ex * s + ey * c);
        }

        /// <summary>
        /// 拍照前把 XY 回到【拍照基准位】（2026-09-08 定案式配套，防 P_photo 错位）：
        ///   · ETH 固定相机（CameraMountEih=false）：X_obj = H(u)，与机位无关 → 不动，保持原流程；
        ///   · EIH 眼在手且已发布 PhotoBase（非 0,0）：先走到该位再拍，保证 P_photo 与配置一致；
        ///   · EIH 但未配置 PhotoBase：只打 ⚠ 继续（保持旧行为），此时 P_photo 取实际机位，可能整体偏移。
        /// 相机随 Z 的工位请注意：拍照还要求 Z 回到标定高度，否则像素当量失真（本流程 Z 已在安全高度）。
        /// </summary>
        private async Task EnsurePhotoBaseAsync(CancellationToken token)
        {
            if (!_cfg.CameraMountEih) return;
            bool configured = Math.Abs(_cfg.PhotoBaseX) > 1e-6 || Math.Abs(_cfg.PhotoBaseY) > 1e-6;
            if (!configured)
            {
                Log("⚠ [Phase1] EIH 眼在手，但工位未配置拍照基准位 PhotoBase —— X_obj=P_photo+O−H(u) 中的 P_photo"
                    + " 只能取当前实际机位，若与标定时不同会整体偏移。请重做/重新发布标定以写入 PhotoBase。");
                return;
            }
            Log($"[Phase1] EIH 眼在手：先回拍照基准位 ({_cfg.PhotoBaseX:F3},{_cfg.PhotoBaseY:F3}) 再触发视觉。");
            await MoveXyAsync(_cfg.PhotoBaseX, _cfg.PhotoBaseY, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 像素 → 工件中心真位 X_obj（2026-09-08 定案式，唯一真源 CalibrationGeometry）。
        /// 视觉输出 w = H(像素)，口径是「机械手该停哪」，不是工件真位：
        ///   - 眼在手 EIH：X_obj = 拍照机位 P_photo + 旋转中心 O − H(u)
        ///   - 固定相机 ETH：X_obj = H(u)
        /// 调用方（吸取/示教/放料）再按 C = X_obj − R(姿态U − U0)·Ecc 求回转中心目标。
        /// </summary>
        private (double X, double Y) PickAnchor(double wX, double wY)
        {
            // 定案式（与校验台共用一个真源，杜绝公式分叉）：
            //   EIH：X_obj = P_photo + O − H(u) ；ETH：X_obj = H(u)
            return CalibrationGeometry.ObjectBase(wX, wY,
                _cfg.PhotoBaseX, _cfg.PhotoBaseY, _cfg.RotCenterWx, _cfg.RotCenterWy, _cfg.CameraMountEih);
        }

        /// <summary>
        /// 把指定吸嘴移到「工件中心 workX/workY 目标点」正上方：
        /// 回转中心 C = 工件目标中心 − R(姿态角 armU)·吸嘴偏心(eccX,eccY)。
        /// 适用：吸取（work=工件真位，armU=WorkU）与放料（work=摆盘位，armU=PlaceU）——统一公式。
        /// </summary>
        private async Task MoveToWorkAsync(double workX, double workY, double armU,
            float eccX, float eccY, CancellationToken token)
        {
            // e 以 U0(ToolAlignU) 为参考 → 旋转量取 (armU − U0)；旧档 U0=0 时与原来完全一致
            var (ex, ey) = RotEcc(armU - _cfg.ToolAlignU, eccX, eccY);
            Log($"  偏心补偿：工件中心({workX:F3},{workY:F3}) − R({armU:F1}°−U0 {_cfg.ToolAlignU:F1}°)·Ecc({eccX:F3},{eccY:F3})" +
                $" → 回转中心({workX - ex:F3},{workY - ey:F3})");
            await MoveXyAsync(workX - ex, workY - ey, token).ConfigureAwait(false);
        }

        /// <summary>
        /// XY 平移（Z 已在安全高度，可直接平面移动）。
        /// </summary>
        private async Task MoveXyAsync(double x, double y, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisX, (float)x, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisY, (float)y, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
        }

        // ============================================================
        // 安全与待机
        // ============================================================

        /// <summary>
        /// 确保 Z 轴处于安全高度：若当前低于安全高度则先抬升。
        /// </summary>
        private async Task EnsureSafeZAsync(CancellationToken token)
        {
            float z = GetAxisPos(_cfg.AxisZ);
            if (z < _cfg.SafeZ)
            {
                Log($"Z 轴当前 {z:F3} 低于安全高度 {_cfg.SafeZ:F3}，先抬升");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            }
        }

        private async Task BackToStandbyAsync(CancellationToken token)
        {
            Log("[Phase6] 回待机位...");
            await MoveXyAsync(_cfg.StandbyX, _cfg.StandbyY, token).ConfigureAwait(false);
        }
    }
}
