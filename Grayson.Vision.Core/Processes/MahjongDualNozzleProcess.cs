using Grayson.Vision.Contracts.Calibration.Chain;
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
    ///     AcquireImage(相机) → ShapeMatch(工件模板)；消费其原始像素 MatchCol/MatchRow/MatchAngle
    ///   ★2026-09-28 R6 原生范式2：标定真源 = 链图 Chain.json（向导产出），CalibrationApply 不再参与物位换算；
    /// - 运动时序段（本类代码写死，机械逻辑不随产品变化）：
    ///     安全检查 → 触发视觉流定位第一块牌 → 吸嘴1 吸取 → [双吸嘴: 吸嘴2 吸取
    ///     （第二块按阵列节距推算）] → 依次摆盘放料 → 回待机。
    ///
    /// 落点几何（★2026-09-28 R6 原生范式2，唯一真源 = 链图 Chain.json，本类不得另写一份公式）：
    ///   像素 → 工件真位：ChainEngine.ResolvePixelToWorld（口径由链的形状宣告，域不存储）；
    ///   工件真位 → 法兰命令位：ChainEngine.ResolveFlangeTarget（TCP 偏心 + U 旋转补偿由链图承担）。
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
    ///   AcquireImage（相机别名=工位监视/设备管理里登记的相机）
    ///   → ShapeMatch（模板名=模板管理里创建的工件模板，MinScore ≥ 配置阈值）。
    ///   CalibrationApply 节点可留可删——物位换算改由生产端链求值承担，其输出不再被消费。
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

        // ============================================================================
        // ★★2026-09-28 R6：原生范式2 链运行时（与 VisionPickPlaceProcess 同型，无对照期/无翻译）：
        //
        //   链图唯一真源 = Chain.json（Recipes\Workstations\{工位码}\Calib\Chain.json，向导产出）。
        //   像素 → 工件物位 = ChainEngine.ResolvePixelToWorld（口径由链的形状宣告，域不存储）；
        //   物位 → 法兰命令位 = ChainEngine.ResolveFlangeTarget（TCP 偏心 + U 旋转补偿由链图承担；
        //   双吸嘴 = 链图里的两个工具节点，偏移矢量带符号内蕴，不再有外置符号口径）。
        //   fail-closed：Chain.json 缺失/身份不符/未过门禁 ⇒ 抛出 ⇒ 不进入生产运动序列
        //   （范式1「口径闸 fail-open + 快照兜底」整体退役，旧口径契约不再被任何生产端引用）。
        // ============================================================================
        private const string Nozzle1ToolId = "Nozzle1";
        private const string Nozzle2ToolId = "Nozzle2";

        private StationCalibGraph _chain;
        private ChainCameraNode _pickCamera1;       // 吸点引导相机（由 PickAnchor 边找到）

        /// <summary>
        /// 加载并校验本工位链图（Phase -1 调用一次；失败即抛，绝不取默认值继续跑）。
        /// 装载/门禁/形状解析统一走 ChainRuntime（与示教面板同尺），本方法只做过程级补充检查。
        /// </summary>
        private void EnsureChainGraph()
        {
            string station = Worker?.StationId;
            string path = ChainRuntime.ChainPathFor(station);
            _chain = ChainRuntime.LoadValidated(station);

            _pickCamera1 = ChainRuntime.FindPickCamera(_chain);
            if (_pickCamera1 == null)
                throw new ChainResolveException(
                    "链图缺 " + ChainRuntime.PickAnchorUsage + " 边 ⇒ 无吸点引导相机，拒绝生产。请检查向导产出的链形状。");
            if (_chain.FindTool(Nozzle1ToolId) == null)
                throw new ChainResolveException("链图缺工具节点 " + Nozzle1ToolId + "，拒绝生产。");
            if (!_cfg.UseSingleNozzle && _chain.FindTool(Nozzle2ToolId) == null)
                throw new ChainResolveException("链图缺工具节点 " + Nozzle2ToolId + "（双吸嘴形态），拒绝生产。");

            Log("[链] 范式2 链图就绪：工位 " + _chain.StationCode
                + " · 相机 " + _chain.Cameras.Count + " · 工具 " + _chain.Tools.Count
                + " · 吸点引导=" + _pickCamera1.CameraId
                + "（" + path + "）");
        }

        /// <summary>像素 → 工件物位 X_obj（链求值；pose = 拍照位 PhotoBase + 拍照角 WorkU）</summary>
        private (double X, double Y) PickAnchor(double pixelCol, double pixelRow)
        {
            ChainEngine.ResolvePixelToWorld(_chain, _pickCamera1.CameraId, pixelCol, pixelRow,
                new ChainRobotPose { X = _cfg.PhotoBaseX, Y = _cfg.PhotoBaseY, U = _cfg.WorkU },
                out double ox, out double oy, Worker?.StationId);
            return (ox, oy);
        }

        /// <summary>物位 → 吸点法兰命令位（链逆解：TCP 偏心 + U 旋转补偿，主/副工具由链图决定）</summary>
        private (double X, double Y) PickCommand(string toolId, double objX, double objY, double armU)
        {
            ChainEngine.ResolveFlangeTarget(_chain, toolId, objX, objY, armU,
                out double fx, out double fy, Worker?.StationId);
            return (fx, fy);
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
                // ---- Phase -1: 链图装载 + 门禁（★2026-09-28 R6 原生范式2）----
                // 链图唯一真源 = 向导产出的 Chain.json（工位级 Calib 目录）；
                //   缺失/身份不符/未过门禁 ⇒ fail-closed 抛出，**不进入生产运动序列**。
                // ⚠ 措辞纪律：EnsureChainGraph 抛出 ⇒ 不进入生产运动序列（不是「整条业务一步没动」）。
                //   下面的 catch 兜底仍会发一条【原地抬 Z 至安全高度】，这是刻意的：
                //   宁可多发一条原地抬 Z，也不能把机械手留在低位。
                EnsureChainGraph();

                // ---- Phase 0: 安全检查 ----
                // 运动卡通道探活（★2026-09-16）：半死 TCP 时 State 仍报 Connected，故障会被推迟到
                // 下面第一条 IO 指令；先探活，失败即断开重连，仍不通则抛出带处置指引的异常。
                EnsureMotionCardAlive();

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

                // 读取视觉结果：第一块牌的原始像素与匹配分数
                // ★ 原生范式2：读 ShapeMatch 原始像素做链求值输入（CalibrationApply 不再参与物位换算）
                var matchNode = FindNode(NodeType.ShapeMatch);
                if (matchNode == null)
                {
                    throw new InvalidOperationException(
                        "当前配方缺少 ShapeMatch 节点，请确认编辑器中已编排视觉定位流");
                }

                double score = GetOutValue<double>(matchNode, "MatchScore");
                double matchCol1 = GetOutValue<double>(matchNode, "MatchCol");
                double matchRow1 = GetOutValue<double>(matchNode, "MatchRow");
                double matchAngle = GetOutValue<double>(matchNode, "MatchAngle");

                // ★ 角度读数兜底（2026-09-12）：同上，防匹配分支异常时 DataValue 为 null 导致角度被误当 0°。
                if (double.IsNaN(matchAngle))
                {
                    Log("⚠ [Phase1] MatchAngle 为 NaN（匹配端口异常），角度归正将按 0° 处理，请检查模板匹配是否正常输出角度。");
                    matchAngle = 0;
                }
                if (_cfg.EnableVisionAngleCorrection && Math.Abs(matchAngle) < 1e-9)
                {
                    Log("⚠ [Phase1] 视觉角归正已开启但 MatchAngle=0°：若来料带角度，请确认 ShapeMatch.MatchAngle 端口已正确输出。");
                }

                Log($"[Phase1] 牌1视觉结果: Score={score:F3}, Pixel=({matchCol1:F1}, {matchRow1:F1}), Angle={matchAngle:F2}°");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // ---- 示教模式（落点公式验证）：视觉定位完成，不执行走位/吸取 ----
                // 链求值同尺（与生产走位同一对函数）：
                //   X_obj = ResolvePixelToWorld（口径由链形状宣告）
                //   C*    = ResolveFlangeTarget(Nozzle1, X_obj, WorkU) ← 吸嘴1 正对麻将中心时，回转中心应停的位置
                //   手动把吸嘴1 移到麻将中心正上方读回 X*/Y* 与 C* 对照（差异应≈0）。
                if (_cfg.TeachMode)
                {
                    var (ax, ay) = PickAnchor(matchCol1, matchRow1);
                    var (cx, cy) = PickCommand(Nozzle1ToolId, ax, ay, _cfg.WorkU);
                    Log("[Phase2] 👉【示教模式】视觉定位完成，不执行走位/吸取。请手动移动机械手让吸嘴1 正对麻将中心，");
                    Log($"[Phase2] 👉【示教模式】  工件真位 X_obj = ({ax:F3},{ay:F3})（链求值：{_pickCamera1.CameraId} 像素 ({matchCol1:F1},{matchRow1:F1})）");
                    Log($"[Phase2] 👉【示教模式】  理论回转中心 C* = ({cx:F3},{cy:F3})（链逆解 Nozzle1；吸嘴对准麻将中心时机械手应停在此处）");
                    Log($"[示教记录] 像素=({matchCol1:F1},{matchRow1:F1}), X_obj=({ax:F3},{ay:F3}), C*=({cx:F3},{cy:F3}), WorkU={_cfg.WorkU:F1}, 实测X*/Y*=____");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return true;
                }

                // ---- Phase 2: 吸嘴1 吸取牌1 ----
                // 工件真位 X_obj = 链求值；吸点命令位 = 链逆解（TCP 偏心 + U 旋转补偿由链图承担）
                Log("[Phase2] 吸嘴1 吸取牌1...");
                var (p1x, p1y) = PickAnchor(matchCol1, matchRow1);
                Log($"[Phase2] 牌1工件真位 X_obj=({p1x:F3},{p1y:F3})（链求值：{_pickCamera1.CameraId}）");
                await MoveToWorkAsync(p1x, p1y, _cfg.WorkU, Nozzle1ToolId, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 3: 吸嘴2 吸取牌2（仅双吸嘴形态）----
                // 牌2 中心 = 牌1 真位 + 阵列节距（料盘阵列推算）；吸嘴1 已吸牌1 保持，
                // 回转中心移去让吸嘴2 对准牌2：C = P2 − R(WorkU)·Ecc2
                if (!_cfg.UseSingleNozzle)
                {
                    double p2x = p1x + _cfg.TilePitchX;
                    double p2y = p1y + _cfg.TilePitchY;
                    Log($"[Phase3] 吸嘴2 吸取牌2: P2=({p2x:F3}, {p2y:F3})（P1+节距 {_cfg.TilePitchX:F1}/{_cfg.TilePitchY:F1}）");
                    await MoveToWorkAsync(p2x, p2y, _cfg.WorkU, Nozzle2ToolId, token).ConfigureAwait(false);
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
                await MoveToWorkAsync(_cfg.Place1X, _cfg.Place1Y, uPlace, Nozzle1ToolId, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 5: 吸嘴2 放料（摆盘位2，仅双吸嘴形态）----
                if (!_cfg.UseSingleNozzle)
                {
                    Log("[Phase5] 吸嘴2 放料至摆盘位2...");
                    await MoveToWorkAsync(_cfg.Place2X, _cfg.Place2Y, uPlace, Nozzle2ToolId, token).ConfigureAwait(false);
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
        // 移动原语（吸点求值：工件中心 → 法兰命令位，链逆解）
        // ============================================================

        /// <summary>
        /// 拍照前把 XY 回到【拍照基准位】（EIH 链求值的 P_photo 必须与标定时一致）：
        ///   · EIH 判定来自链图（吸点引导相机 Mount=EyeInHand），不再读配置布尔；
        ///   · ETH 固定相机：X_obj 与机位无关 → 不动，保持原流程；
        ///   · EIH 已配置 PhotoBase（非 0,0）：先走到该位再拍，保证 P_photo 与配置一致；
        ///   · EIH 但未配置 PhotoBase：只打 ⚠ 继续，此时 P_photo 取实际机位，可能整体偏移。
        /// 相机随 Z 的工位请注意：拍照还要求 Z 回到标定高度，否则像素当量失真（本流程 Z 已在安全高度）。
        /// </summary>
        private async Task EnsurePhotoBaseAsync(CancellationToken token)
        {
            bool isEih = _pickCamera1 != null && _pickCamera1.Mount == ChainCameraMount.EyeInHand;
            if (!isEih) return;
            bool configured = Math.Abs(_cfg.PhotoBaseX) > 1e-6 || Math.Abs(_cfg.PhotoBaseY) > 1e-6;
            if (!configured)
            {
                Log("⚠ [Phase1] EIH 眼在手，但工位未配置拍照基准位 PhotoBase —— EIH 链求值 X_obj=T_F→B(P_photo)∘H_Cam→Flange(像素)"
                    + " 里的 P_photo 只能取当前实际机位，若与标定时不同会整体偏移。请在工位配置补拍照基准位，或在标定向导重测后重发 Chain.json。");
                return;
            }
            Log($"[Phase1] EIH 眼在手：先回拍照基准位 ({_cfg.PhotoBaseX:F3},{_cfg.PhotoBaseY:F3}) 再触发视觉。");
            await MoveXyAsync(_cfg.PhotoBaseX, _cfg.PhotoBaseY, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 把指定吸嘴移到「工件目标点 objX/objY」正上方（原生范式2 链逆解）：
        /// 法兰命令位 = ResolveFlangeTarget(toolId, obj, armU)——TCP 偏心 + U 旋转补偿全部由链图承担
        /// （主/副工具偏移量是向导实测的带符号矢量，符号内蕴）。
        /// 适用：吸取（obj=工件真位，armU=WorkU）与放料（obj=摆盘位，armU=U_place）——统一公式。
        /// </summary>
        private async Task MoveToWorkAsync(double objX, double objY, double armU,
            string toolId, CancellationToken token)
        {
            var (fx, fy) = PickCommand(toolId, objX, objY, armU);
            await MoveXyAsync(fx, fy, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 平面平移。★2026-09-16 起**默认走门型**（先抬到 JumpLimZ → 水平走 → 降回 SafeZ），
        /// 不再逐轴拆成"先 X 后 Y"。
        ///
        /// 为什么改：逐轴拆两步末端走 L 形（先在拐角点停一次），实测该 L 形路径第二段
        /// 离内圈边界只剩 1.5mm（贴边飞过）；门型走同一对点的直线余量 31.3mm。
        /// </summary>
        private async Task MoveXyAsync(double x, double y, CancellationToken token)
        {
            await MoveGantryAsync(x, y, _cfg.SafeZ, token).ConfigureAwait(false);
        }

        // ============================================================
        // 门型走位参数（转发工位配置）
        //
        // ★门型算法本体只有一份：StationProcessBase.MoveGantryAsync / ResolveGantryLimZ。
        //   这里只供货轴号与高度 —— 刻意不各写一份，避免"同一条判据写两遍 ⇒ 两边分叉"。
        // ============================================================

        /// <summary>X 轴号</summary>
        protected override int AxisX => _cfg.AxisX;
        /// <summary>Y 轴号</summary>
        protected override int AxisY => _cfg.AxisY;
        /// <summary>Z 轴号</summary>
        protected override int AxisZ => _cfg.AxisZ;
        /// <summary>U 轴号</summary>
        protected override int AxisU => _cfg.AxisU;
        /// <summary>门型水平段高度（安全通过高度，不是速度）</summary>
        protected override float JumpLimZ => _cfg.JumpLimZ;
        protected override float XySpeed => _cfg.XySpeed;
        protected override float ZSpeed => _cfg.ZSpeed;
        protected override int SettleMs => _cfg.SettleMs;

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
