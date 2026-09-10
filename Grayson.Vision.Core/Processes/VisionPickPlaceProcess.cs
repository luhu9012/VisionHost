using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Flow.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「通用视觉定位取放」业务过程 —— VisionPickPlace（2026-09-06 建档）。
    ///
    /// 目标：一台代码覆盖多台取放机型，新机型 = 建工位 + 填配置 + 绑配方 + 发布标定，不写 C#。
    ///   母版：MahjongDualNozzleProcess（Epson SCARA 双吸嘴，偏心/角度体系 2026-09-03 重构后唯一口径）。
    ///   差异化能力（相对母版）：
    ///     1. 角度策略二「视觉实测角归正」：读取 ShapeMatch.MatchAngle，放料角
    ///        U_place = PlaceU − AngleCorrectionSign·(MatchAngle − RefAngleDeg)，随机姿态来料可落正位；
    ///     2. 同心吸嘴（Ecc=0）自然支持 —— 全部偏心补偿公式退化为直吸直放，无需任何特殊分支；
    ///     3. 字段即配置：轴号/吸嘴数/偏心/位点/角度策略全部可覆盖，S2/S3 共用一码。
    ///
    /// 职责分层（与全部业务过程同一约定）：
    ///   - 视觉定位段 = 配方节点编排（AcquireImage → ShapeMatch → CalibrationApply），换产品调参；
    ///   - 运动/节拍段 = 本类（机械结构决定，不随产品变化）→ 由 VisionPickPlaceConfig 配置化；
    ///   - 触发调度/安全/急停 = StationWorker + StationProcessBase（系统内核）。
    ///
    /// ⚠ 配方节点链要求（工位绑定配方必须存在，缺节点直接抛异常）：
    ///   AcquireImage → ShapeMatch（模板=工件） → CalibrationApply（HomMatFilePath=标定发布矩阵）
    /// 每周期视觉流输出一个最佳匹配目标（世界系 w）；双目标形态第二目标 = w + TilePitch 节距推算。
    /// </summary>
    public class VisionPickPlaceProcess : StationProcessBase
    {
        private readonly VisionPickPlaceConfig _cfg;

        /// <summary>业务过程唯一键（与 StationConfigModel.ProcessKey 对应）</summary>
        public override string ProcessKey => "VisionPickPlace";

        public VisionPickPlaceProcess(StationWorker worker, VisionPickPlaceConfig config = null)
            : base(worker, (config ?? new VisionPickPlaceConfig()).CardAlias)
        {
            _cfg = config ?? new VisionPickPlaceConfig();
        }

        /// <summary>
        /// 执行一次「视觉定位取放」循环（单目标搬 1 个 / 双目标搬 2 个）。
        /// 返回 true = 成功；false = 视觉 NG 或中途异常（Z 轴已抬至安全高度）。
        /// </summary>
        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            string nozzleMode = _cfg.UseSingleNozzle ? "单目标（1个/循环）" : "双目标（2个/循环）";
            string angleMode = _cfg.EnableVisionAngleCorrection ? "视觉实测角归正" : "固定角归正";
            Log($"========== VisionPickPlace 取放 开始 [{nozzleMode} | {angleMode}] ==========");
            try
            {
                // ---- Phase 0: 安全检查 ----
                // Z 抬至安全高度（低于安全高度说明上次异常残留，先抬升）
                await EnsureSafeZAsync(token).ConfigureAwait(false);

                // 真空强制关闭（防止带料悬停撞机；单目标形态下 VacuumIo2 无实际阀，写了也无害）
                SetOutput(_cfg.VacuumIo1, false);
                if (!_cfg.UseSingleNozzle)
                {
                    SetOutput(_cfg.VacuumIo2, false);
                }

                // U 轴转到作业角（吸取姿态；首圈或被人为动过后有实际运动）
                await MoveAbsAsync(_cfg.AxisU, _cfg.WorkU, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 1: 视觉定位（相机来源由配方 AcquireImage 决定：眼在手/固定上相机均可）----
                // EIH 眼在手：X_obj = P_photo + O − H(u) 里的 P_photo 必须是【拍这张图时】的机位，
                //            所以先回到发布时写入的拍照基准位再触发（ETH 固定相机不需要）
                await EnsurePhotoBaseAsync(token).ConfigureAwait(false);
                Log("[Phase1] 触发视觉流（采图 → 工件模板匹配 → 坐标标定换算）...");
                await RunVisionFlowAsync($"VPP_{DateTime.Now:HHmmss}").ConfigureAwait(false);

                // 读取视觉结果：目标1 的物理坐标与匹配分数
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

                Log($"[Phase1] 目标1视觉结果: Score={score:F3}, World=({worldX1:F3}, {worldY1:F3}), Angle={matchAngle:F2}°");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // ---- 示教模式（2026-09-08 定案式）：视觉定位完成，不执行走位/吸取，打印理论落点供人工对照 ----
                // 定案式（与校验台同一真源 CalibrationGeometry）：
                //   X_obj = P_photo + O − H(u)        （EIH 眼在手；ETH 固定相机时 X_obj = H(u)）
                //   C*    = X_obj − R(WorkU − U0)·Ecc1 ← 吸嘴1 正对目标中心时，回转中心应停的位置
                bool hasEcc1 = Math.Abs(_cfg.Nozzle1EccX) > 1e-6 || Math.Abs(_cfg.Nozzle1EccY) > 1e-6;
                if (_cfg.TeachMode)
                {
                    var (ax, ay) = PickAnchor(worldX1, worldY1);
                    var (wx, wy) = RotEcc(_cfg.WorkU - _cfg.ToolAlignU, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY);
                    double expectX = ax - wx;
                    double expectY = ay - wy;
                    Log("[示教模式] 视觉定位完成，不执行走位/吸取。请手动移动机械手让吸嘴1 正对目标中心，");
                    Log($"[示教模式]  工件真位 X_obj = P_photo + O − H(u) = ({ax:F3},{ay:F3})" +
                        (_cfg.CameraMountEih ? "（EIH 眼在手）" : "（ETH 固定相机：X_obj = H(u)）"));
                    Log($"[示教模式]  理论回转中心 C* = X_obj − R({_cfg.WorkU:F0}° − U0 {_cfg.ToolAlignU:F0}°)·Ecc1 = ({expectX:F3},{expectY:F3})（吸嘴对准目标中心时机械手应停在此处）");
                    Log($"[示教记录] H(u)=({worldX1:F3},{worldY1:F3}), Angle={matchAngle:F2}°, P_photo=({_cfg.PhotoBaseX:F3},{_cfg.PhotoBaseY:F3}), " +
                        $"O=({_cfg.RotCenterWx:F3},{_cfg.RotCenterWy:F3}), EIH={_cfg.CameraMountEih}, ToolAlignU={_cfg.ToolAlignU:F1}, WorkU={_cfg.WorkU:F1}, " +
                        $"Ecc1=({_cfg.Nozzle1EccX:F3},{_cfg.Nozzle1EccY:F3}), 理论C*=({expectX:F3},{expectY:F3}), 实测X*/Y*=____");
                    if (!hasEcc1)
                        Log("[示教模式] ⚠ Ecc1=0（未发布吸嘴偏心 e）：走位等于「视觉直吸」，只在 U 恒=U0 时才准");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return true;
                }

                // ---- Phase 2: 吸嘴1 吸取目标1 ----
                // 工件真位 X_obj = P_photo + O − H(u)（EIH）/ H(u)（ETH）；
                // 回转中心 C = X_obj − R(WorkU − U0)·Ecc1（U 不转时 R 即 Ecc 本身）
                Log("[Phase2] 吸嘴1 吸取目标1...");
                var (p1x, p1y) = PickAnchor(worldX1, worldY1);
                Log($"[Phase2] 目标1工件真位 X_obj=({p1x:F3},{p1y:F3})（定案式：{(_cfg.CameraMountEih ? "P_photo + O − H(u)" : "H(u)，ETH")}）");
                await MoveToWorkAsync(p1x, p1y, _cfg.WorkU, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 3: 吸嘴2 吸取目标2（仅双目标形态；= 目标1真位 + 阵列节距）----
                if (!_cfg.UseSingleNozzle)
                {
                    double p2x = p1x + _cfg.TilePitchX;
                    double p2y = p1y + _cfg.TilePitchY;
                    Log($"[Phase3] 吸嘴2 吸取目标2: P2=({p2x:F3}, {p2y:F3})（P1+节距 {_cfg.TilePitchX:F1}/{_cfg.TilePitchY:F1}）");
                    await MoveToWorkAsync(p2x, p2y, _cfg.WorkU, _cfg.Nozzle2EccX, _cfg.Nozzle2EccY, token).ConfigureAwait(false);
                    await PickOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);
                }

                // ---- Phase 3.5: 计算放料统一角并转 U（角度策略在此分叉）----
                // 固定角归正：U_place = PlaceU（来料姿态固定/正位）
                // 视觉实测角归正：U_place = PlaceU − Sign·(MatchAngle − RefAngleDeg)
                //   —— MatchAngle 为模板相对当前工件姿态的旋转量；把实测偏差反向补掉，
                //      使工件按 RefAngleDeg（模板建时的参考姿态，通常 0°）落盘。
                //   ⚠ 符号：相机朝下常规=+1；朝上(下相机检知)=视轴镜像取 −1，越归正越歪就翻它。
                float uPlace = _cfg.PlaceU;
                if (_cfg.EnableVisionAngleCorrection)
                {
                    double angleDelta = matchAngle - _cfg.RefAngleDeg;
                    uPlace = _cfg.PlaceU - _cfg.AngleCorrectionSign * (float)angleDelta;
                    Log($"[Phase3.5] 视觉归正：U_place = PlaceU({_cfg.PlaceU:F1}) − Sign({_cfg.AngleCorrectionSign:F0})·(MatchAngle({matchAngle:F2}) − Ref({_cfg.RefAngleDeg:F1})) = {uPlace:F1}°");
                }
                else
                {
                    Log($"[Phase3.5] 固定角归正：U 转到放料角 {uPlace:F1}°（统一放置姿态）");
                }
                await MoveAbsAsync(_cfg.AxisU, uPlace, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 4: 吸嘴1 放料（摆盘位1，工件中心落 Place1）----
                Log("[Phase4] 吸嘴1 放料至摆盘位1...");
                await MoveToWorkAsync(_cfg.Place1X, _cfg.Place1Y, uPlace, _cfg.Nozzle1EccX, _cfg.Nozzle1EccY, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 5: 吸嘴2 放料（摆盘位2，仅双目标形态）----
                if (!_cfg.UseSingleNozzle)
                {
                    Log("[Phase5] 吸嘴2 放料至摆盘位2...");
                    await MoveToWorkAsync(_cfg.Place2X, _cfg.Place2Y, uPlace, _cfg.Nozzle2EccX, _cfg.Nozzle2EccY, token).ConfigureAwait(false);
                    await PlaceOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);
                }

                // ---- Phase 6: 回待机 ----
                await BackToStandbyAsync(token).ConfigureAwait(false);

                Log($"========== VisionPickPlace 取放 成功（本次搬运 {(_cfg.UseSingleNozzle ? 1 : 2)} 个目标） ==========");
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
        /// 安全收尾：关闭双真空（防带料悬停）+ Z 抬至安全高度。由 Worker.StopAsync 调用，幂等。
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
        /// 调用前机械手 XY 必须已在目标正上方。
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
        // 移动原语（偏心补偿：工件中心 → 回转中心；同心吸嘴 Ecc=0 时退化为直移）
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
            // 定案式（与校验台同源）：EIH → X_obj = P_photo + O − H(u)；ETH → X_obj = H(u)
            return CalibrationGeometry.ObjectBase(wX, wY,
                _cfg.PhotoBaseX, _cfg.PhotoBaseY, _cfg.RotCenterWx, _cfg.RotCenterWy, _cfg.CameraMountEih);
        }

        /// <summary>
        /// 把指定吸嘴移到「工件中心 workX/workY 目标点」正上方：
        /// 回转中心 C = 工件目标中心 − R(姿态角 armU)·吸嘴偏心(eccX,eccY)。
        /// 适用：吸取（work=工件真位，armU=WorkU）与放料（work=摆盘位，armU=U_place）——统一公式。
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
