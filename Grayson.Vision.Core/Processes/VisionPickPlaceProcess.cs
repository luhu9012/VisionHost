using Grayson.Vision.Contracts.Calibration.Chain;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
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
    ///   AcquireImage → ShapeMatch（模板=工件；消费其原始像素 MatchCol/MatchRow/MatchAngle）
    ///   ★2026-09-27 原生范式2：标定真源 = 链图 Chain.json（向导产出），CalibrationApply 不再参与物位换算。
    /// 每周期视觉流输出一个最佳匹配目标（像素系）；双目标形态第二目标 = 目标1真位 + TilePitch 节距推算。
    /// </summary>
    public class VisionPickPlaceProcess : StationProcessBase
    {
        private readonly VisionPickPlaceConfig _cfg;

        /// <summary>
        /// 主流程里"下相机纠偏段"的子流程名称关键字（用于按名字定位段下标，不硬编码 0/1）。
        /// 复合配方两个子流程命名约定：上相机段含"上相机"、下相机段含"下相机"。
        /// </summary>
        private const string DownCameraSegmentKeyword = "下相机";

        // 下相机纠偏结果暂存（单次循环内有效；Phase3.6 算好后 Phase4 放料消费）
        private double _downCameraPlaceX;
        private double _downCameraPlaceY;
        private float _downCameraPlaceU;

        /// <summary>业务过程唯一键（与 StationConfigModel.ProcessKey 对应）</summary>
        public override string ProcessKey => "VisionPickPlace";

        // ============================================================================
        // ★★2026-09-27 原生范式2 链运行时（无对照期/无翻译，链图只由向导产出）：
        //
        //   链图唯一真源 = Chain.json（Recipes\Workstations\{工位码}\Calib\Chain.json）。
        //   偏移量 = 向导实测带符号矢量（符号内蕴）；域不存储——由链的形状（Edges 的 Usage）宣告：
        //     PickAnchor 边 = 吸点引导相机；DownCameraCorrect 边 = 下相机纠偏。
        //   fail-closed：Chain.json 缺失/身份不符/未过门禁 ⇒ 抛出 ⇒ 不进入生产运动序列
        //   （范式1「档案不可得⇒拿兜底值跑完」的 fail-open 行为整体退役）。
        // ============================================================================
        private const string Nozzle1ToolId = "Nozzle1";
        private const string Nozzle2ToolId = "Nozzle2";
        // 链边 Usage 常量与装载/形状解析统一收口 Contracts 的 ChainRuntime（本文件不再本地定义）

        private StationCalibGraph _chain;
        private ChainCameraNode _pickCamera1;       // 吸点引导相机（由 PickAnchor 边找到）
        private ChainCameraNode _downCamera;        // 下相机节点（DownCameraCorrect 边；可缺）

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
            _downCamera = ChainRuntime.FindDownCamera(_chain);
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
                + " · 下相机=" + (_downCamera == null ? "无" : _downCamera.CameraId)
                + "（" + path + "）");
        }

        /// <summary>
        /// 拍照位姿（★2026-09-28 T6：真源 = 链图 PhotoPose，G1 硬拦保证 EIH 必有）：
        /// EIH 用 PhotoPose[0..1] + 拍照角 WorkU；ETH 求值与机位无关，位姿仅占位。
        /// </summary>
        private ChainRobotPose PickPhotoPose()
        {
            if (_pickCamera1.Mount == ChainCameraMount.EyeInHand
                && _pickCamera1.PhotoPose != null && _pickCamera1.PhotoPose.Length == 2)
                return new ChainRobotPose { X = _pickCamera1.PhotoPose[0], Y = _pickCamera1.PhotoPose[1], U = _cfg.WorkU };
            // ETH：链求值 X_obj = H(u) 与拍照位姿无关 ⇒ 0 占位（不再回落到可能陈旧的配置值）。
            return new ChainRobotPose { X = 0, Y = 0, U = _cfg.WorkU };
        }

        /// <summary>像素 → 工件物位 X_obj（链求值；pose = 链图拍照基准位 + 拍照角 WorkU）</summary>
        private (double X, double Y) PickAnchor(double pixelCol, double pixelRow)
        {
            ChainEngine.ResolvePixelToWorld(_chain, _pickCamera1.CameraId, pixelCol, pixelRow,
                PickPhotoPose(), out double ox, out double oy, Worker?.StationId);
            return (ox, oy);
        }

        /// <summary>物位 → 吸点法兰命令位（链逆解：TCP 偏心 + U 旋转补偿，主/副工具由链图决定）</summary>
        private (double X, double Y) PickCommand(string toolId, double objX, double objY, double armU)
        {
            ChainEngine.ResolveFlangeTarget(_chain, toolId, objX, objY, armU,
                out double fx, out double fy, Worker?.StationId);
            return (fx, fy);
        }

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
                // ---- Phase -1: 链图装载 + 门禁（★2026-09-27 原生范式2）----
                // 链图唯一真源 = 向导产出的 Chain.json（工位级 Calib 目录）；
                //   缺失/身份不符/未过门禁 ⇒ fail-closed 抛出，**不进入生产运动序列**。
                // ⚠ 措辞纪律：EnsureChainGraph 抛出 ⇒ 不进入生产运动序列（不是「整条业务一步没动」）。
                //   下面的 catch 兜底仍会发一条【原地抬 Z 至安全高度】，这是刻意的：
                //   宁可多发一条原地抬 Z，也不能把机械手留在低位。
                EnsureChainGraph();

                // ---- Phase 0: 安全检查 ----
                // 运动卡通道探活（★2026-09-16）：半死 TCP（RC+ 脚本任务崩溃/断网）时 State 仍报
                // Connected，故障会被推迟到下面第一条 IO 指令，现场只看到"通道忙"刷屏。
                // 这里先 PING 探活，失败即断开重连；仍不通则抛出带处置指引的异常。
                EnsureMotionCardAlive();

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

                // 🌟 复合工位分段节拍。约定：复合配方主流程 = [上相机引导段, 下相机纠偏段]。
                //    ★ 段下标不再硬编码 0/1：按子流程 DisplayName 关键字（默认"下相机"）在**执行链里的真实下标**定位，
                //      画布重排/改名后不会再静默跑错段（2026-09-11 加固）。
                int downSegIndex = FindSubProcessIndexByKeyword(DownCameraSegmentKeyword);

                if (_cfg.EnableDownCameraCorrection)
                {
                    // 开工前预检：配方结构 / 模板 / 矩阵 / 机位。任一缺失直接抛，避免"静默按固定位放置"掩盖配置错误。
                    ValidateDownCameraSegment(downSegIndex);

                    Log($"[Phase1] 触发上相机段视觉流（复合工位：仅上相机引导定位，下相机段=第 {downSegIndex} 段留到 Phase3.6）...");
                    await RunVisionFlowSegmentAsync(0, downSegIndex, $"VPP_UP_{DateTime.Now:HHmmss}").ConfigureAwait(false);
                }
                else if (downSegIndex >= 0)
                {
                    // 配方里挂着下相机子流程、但开关没开（单相机验证阶段）：
                    // 只跳过该段，不要整链跑 —— 否则空模板/空矩阵会每周期刷两行错误日志。
                    Log($"⚠ [Phase1] 配方含「{DownCameraSegmentKeyword}」子流程（第 {downSegIndex} 段）但未启用下相机二次校准 → 本次跳过该段，只跑其余段。");
                    await RunVisionFlowExceptSegmentAsync(downSegIndex, $"VPP_{DateTime.Now:HHmmss}").ConfigureAwait(false);
                }
                else
                {
                    Log("[Phase1] 触发视觉流（采图 → 工件模板匹配 → 坐标标定换算）...");
                    await RunVisionFlowAsync($"VPP_{DateTime.Now:HHmmss}").ConfigureAwait(false);
                }

                // 读取视觉结果：目标1 的物理坐标与匹配分数。
                // ★ 复合配方里两个子流程各有一个 ShapeMatch/CalibrationApply，
                //   "按链里第一个同类型节点"去找会串台（拿到下相机段的读数）→ 必须按【上相机段的下标】取。
                var matchNode = (FlowNodeBase)null;
                var calibNode = (FlowNodeBase)null;
                if (HasCompositeFlowNode())
                {
                    int upSegIndex = downSegIndex == 0 ? 1 : 0;
                    matchNode = FindNodeInSegment(upSegIndex, NodeType.ShapeMatch);
                    calibNode = FindNodeInSegment(upSegIndex, NodeType.CalibrationApply);
                    Log($"[Phase1] 段定位：上相机段=第 {upSegIndex} 段，下相机段={(downSegIndex < 0 ? "无" : "第 " + downSegIndex + " 段")}。");
                }
                else
                {
                    // 传统单链配方（无子流程）：按类型在整链里找
                    matchNode = FindNode(NodeType.ShapeMatch);
                    calibNode = FindNode(NodeType.CalibrationApply);
                }
                if (matchNode == null || calibNode == null)
                {
                    throw new InvalidOperationException(
                        "当前配方缺少 ShapeMatch / CalibrationApply 节点，请确认编辑器中已编排视觉定位流");
                }

                double score = GetOutValue<double>(matchNode, "MatchScore");
                // ★ 原生范式2：读 ShapeMatch 原始像素做链求值输入（CalibrationApply 不再参与物位换算）
                double matchCol1 = GetOutValue<double>(matchNode, "MatchCol");
                double matchRow1 = GetOutValue<double>(matchNode, "MatchRow");
                double matchAngle = GetOutValue<double>(matchNode, "MatchAngle");

                // ★ 角度读数兜底（2026-09-12）：MatchAngle 是 ShapeMatch 无条件 set 的端口，正常必有值；
                //   但若匹配分支异常（outRes 失败未走 SetOutputValue）DataValue 为 null → GetOutValue 返回 0，
                //   视觉角归正开启时会把随机姿态误当 0° 处理。这里显式告警，避免静默错归正。
                if (double.IsNaN(matchAngle))
                {
                    Log("⚠ [Phase1] MatchAngle 为 NaN（匹配端口异常），角度归正将按 0° 处理，请检查模板匹配是否正常输出角度。");
                    matchAngle = 0;
                }
                if (_cfg.EnableVisionAngleCorrection && Math.Abs(matchAngle) < 1e-9)
                {
                    Log("⚠ [Phase1] 视觉角归正已开启但 MatchAngle=0°：若来料带角度，请确认 ShapeMatch.MatchAngle 端口已正确输出（模板角度范围 / 匹配是否命中姿态）。");
                }

                Log($"[Phase1] 目标1视觉结果: Score={score:F3}, Pixel=({matchCol1:F1},{matchRow1:F1}), Angle={matchAngle:F2}°");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // ---- 示教模式（2026-09-08 定案式；★2026-09-27 原生范式2 链求值）----
                // 视觉定位完成，不执行走位/吸取，打印理论落点供人工对照：
                //   X_obj = 链求值(像素, pose=拍照位+拍照角)；
                //   C*    = ResolveFlangeTarget(Nozzle1, X_obj, WorkU) ← 吸嘴1 正对目标中心时，回转中心应停的位置
                if (_cfg.TeachMode)
                {
                    var (ax, ay) = PickAnchor(matchCol1, matchRow1);
                    var (cx, cy) = PickCommand(Nozzle1ToolId, ax, ay, _cfg.WorkU);
                    Log("[示教模式] 视觉定位完成，不执行走位/吸取。请手动移动机械手让吸嘴1 正对目标中心，");
                    Log($"[示教模式]  工件真位 X_obj = ({ax:F3},{ay:F3})（链求值：{_pickCamera1.CameraId} 像素 ({matchCol1:F1},{matchRow1:F1})）");
                    Log($"[示教模式]  理论回转中心 C* = ({cx:F3},{cy:F3})（吸嘴对准目标中心时机械手应停在此处）");
                    var pp = PickPhotoPose();
                    Log($"[示教记录] Pixel=({matchCol1:F1},{matchRow1:F1}), Angle={matchAngle:F2}°, P_photo=({pp.X:F3},{pp.Y:F3}), " +
                        $"WorkU={_cfg.WorkU:F1}, 理论C*=({cx:F3},{cy:F3}), 实测X*/Y*=____");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return true;
                }

                // ---- Phase 2: 吸嘴1 吸取目标1 ----
                // 工件真位 X_obj = 链求值（像素 → Robot Base）；
                // 吸点命令位 = ResolveFlangeTarget（TCP 偏心 + U 旋转补偿由链图承担）
                Log("[Phase2] 吸嘴1 吸取目标1...");
                var (p1x, p1y) = PickAnchor(matchCol1, matchRow1);
                Log($"[Phase2] 目标1工件真位 X_obj=({p1x:F3},{p1y:F3})（链求值：{_pickCamera1.CameraId}）");
                await MoveToWorkAsync(p1x, p1y, _cfg.WorkU, Nozzle1ToolId, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 3: 吸嘴2 吸取目标2（仅双目标形态；= 目标1真位 + 阵列节距）----
                if (!_cfg.UseSingleNozzle)
                {
                    double p2x = p1x + _cfg.TilePitchX;
                    double p2y = p1y + _cfg.TilePitchY;
                    Log($"[Phase3] 吸嘴2 吸取目标2: P2=({p2x:F3}, {p2y:F3})（P1+节距 {_cfg.TilePitchX:F1}/{_cfg.TilePitchY:F1}）");
                    await MoveToWorkAsync(p2x, p2y, _cfg.WorkU, Nozzle2ToolId, token).ConfigureAwait(false);
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

                // ---- Phase 3.6: 下相机二次校准（复合工位：吸取后 → 移下相机 → 二次视觉 → 纠偏）----
                // 仅单目标形态支持（双目标复合校准语义未定义，开启即忽略）。
                double placeX = _cfg.Place1X;
                double placeY = _cfg.Place1Y;
                if (_cfg.EnableDownCameraCorrection && _cfg.UseSingleNozzle)
                {
                    // ★ 最终放料角与放置位都由下相机段定（角度先定、位置按 U 变化量旋转），返回最终 U。
                    uPlace = await DownCameraCorrectAsync(uPlace, downSegIndex, token).ConfigureAwait(false);
                    placeX = _downCameraPlaceX;
                    placeY = _downCameraPlaceY;
                }
                else if (_cfg.EnableDownCameraCorrection && !_cfg.UseSingleNozzle)
                {
                    Log("⚠ [Phase3.6] 下相机二次校准暂仅支持单目标形态，双目标形态跳过下相机段。");
                }

                await MoveAbsAsync(_cfg.AxisU, uPlace, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 4: 吸嘴1 放料（摆盘位1，工件中心落 Place1，经下相机纠偏）----
                Log("[Phase4] 吸嘴1 放料至摆盘位1...");
                await MoveToWorkAsync(placeX, placeY, uPlace, Nozzle1ToolId, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 5: 吸嘴2 放料（摆盘位2，仅双目标形态）----
                if (!_cfg.UseSingleNozzle)
                {
                    Log("[Phase5] 吸嘴2 放料至摆盘位2...");
                    await MoveToWorkAsync(_cfg.Place2X, _cfg.Place2Y, uPlace, Nozzle2ToolId, token).ConfigureAwait(false);
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
            // ⚠ 如实描述（2026-09-16）：本过程【没有真空检知输入】——上面只证明"阀已开"，
            //   不证明"真吸住"。此前写"吸取完成"是拿硬编码文案替错误背书：吸空/吸偏时
            //   日志照样说"完成"，现场只能等到放料丢了才知道。
            Log($"  {nozzleName} 真空阀已开（IO{vacuumIo} ON）+ 保压 {_cfg.VacuumOnDelayMs}ms、Z 已抬至 SafeZ；"
                + "⚠ 本过程未接真空检知 ⇒ 仅表示阀已打开，不表示已吸住（下相机成像是第一个间接证据）。");
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
            Log($"  {nozzleName} 真空阀已关（IO{vacuumIo} OFF）+ 破空等待 {_cfg.VacuumOffDelayMs}ms；"
                + "⚠ 未接脱落检知：是否已放进槽位无反馈。");
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
        /// 拍照前把 XY 回到【拍照基准位】（范式2 EIH 链求值配套）：
        ///   · ETH 固定相机：P = H_Cam→Robot(像素)，与机位无关 → 不动，保持原流程；
        ///   · EIH 眼在手：X_obj = T_F→B(机位) ∘ H_Cam→Flange(像素)，机位必须可复现
        ///     ⇒ 真源 = 链图 PhotoPose（门禁 G1 硬拦保证存在）：先走到该位再拍。
        /// 相机随 Z 的工位请注意：拍照还要求 Z 回到标定高度，否则像素当量失真（本流程 Z 已在安全高度）。
        /// </summary>
        private async Task EnsurePhotoBaseAsync(CancellationToken token)
        {
            if (_pickCamera1.Mount != ChainCameraMount.EyeInHand) return;
            double px = _pickCamera1.PhotoPose[0], py = _pickCamera1.PhotoPose[1];
            Log($"[Phase1] EIH 眼在手：先回拍照基准位 ({px:F3},{py:F3})（链图 PhotoPose）再触发视觉。");
            await MoveXyAsync(px, py, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 把指定吸嘴移到「工件目标点 objX/objY」正上方（原生范式2 链逆解）：
        /// 法兰命令位 = ResolveFlangeTarget(toolId, obj, armU)——TCP 偏心 + U 旋转补偿全部由链图承担
        /// （主/副工具偏移量是向导实测的带符号矢量，符号内蕴；副工具偏移在法兰系与主工具求和）。
        /// 适用：吸取（obj=工件真位，armU=WorkU）与放料（obj=摆盘位，armU=U_place）——统一公式。
        /// </summary>
        private async Task MoveToWorkAsync(double objX, double objY, double armU,
            string toolId, CancellationToken token)
        {
            var (fx, fy) = PickCommand(toolId, objX, objY, armU);
            await MoveXyAsync(fx, fy, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 下相机二次校准（复合工位 S3）：吸取后把卡片移到下相机上方 → 拍卡片底面 →
        /// 测位置/角度偏差 → 算纠偏后的放置位与放置角。
        ///
        /// 语义（v1.1，2026-09-11 补 U 旋转补偿）：
        ///   - 角度：uPlace = PlaceU − DownCameraAngleSign·(dAngle − RefAngleDeg)（下相机仰视镜像，Sign 默认 −1）。
        ///   - 位置：下相机 H 矩阵把卡片中心像素换算成机械位 (mx,my)（= 让工具尖落到该像素所需的法兰命令位域），
        ///     故 δ = (mx,my) − 拍照机位 = 【工具尖 → 卡片中心】的偏移量。
        ///     ★ 卡片是刚性吸持在吸嘴上的：放料时 U 从拍照姿态 WorkU 转到 uPlace，
        ///       卡片会绕吸嘴轴同步转过 ΔU = uPlace − WorkU → δ 必须跟着旋转：
        ///       **放置位 = 固定位 − R(ΔU)·δ**（U 不变时退化为 Place1 − δ，与旧版一致）。
        ///       漏这一步的表现："位置纠偏开了、还是偏，且偏的方向随放料角变"。
        /// ⚠ 仍属可上机调参的简化口径：精确的吸嘴偏心/相机光轴耦合以现场标定数据为准。
        /// 结果写入 _downCameraPlaceX/Y/U 字段，由 Phase4 放料消费。
        /// </summary>
        /// <param name="uPlaceFromUp">Phase3.5 按上相机结果算出的放料角（角度纠偏关闭时沿用）</param>
        /// <param name="downSegIndex">下相机段在执行链中的下标（Phase1 已定位）</param>
        /// <returns>最终放料角 U（可能被下相机实测角修正）</returns>
        private async Task<float> DownCameraCorrectAsync(float uPlaceFromUp, int downSegIndex, CancellationToken token)
        {
            // 0. 兜底基线：任何一步判 NG 都回落到"固定位 + 固定角"，绝不用半套纠偏值放料
            ResetDownCameraPlacementToFixed(uPlaceFromUp);

            Log($"[Phase3.6] 下相机二次校准：移卡片到下相机上方 ({_cfg.DownCameraX:F3},{_cfg.DownCameraY:F3})，Z={_cfg.DownCameraZ:F3} ...");

            // 1. 移到下相机视场中心正上方 + 降到拍照 Z —— ★2026-09-16 合并为【一次门型到位】
            //    原来是"XY 平面走位 + 再单独降 Z"两段，且 XY 段逐轴走 L 形。现场事故形态：
            //    X 先横移 174mm（Y 停在取料位不动）⇒ 实际发出去的指令是 MOVE 94.512,207.908,-50.000；
            //    紧接着第 2 段 Y 要横穿 429mm，而实测那条 L 形路径第二段离内圈边界只剩 1.5mm。
            //    改为门型：抬到 JumpLimZ → 水平走到相机上方 → 直接降到拍照 Z，一步到位。
            await MoveGantryAsync(_cfg.DownCameraX, _cfg.DownCameraY, _cfg.DownCameraZ, token).ConfigureAwait(false);

            // 2. 触发下相机段视觉流（下标由 Phase1 定位，不再硬编码 1）
            if (downSegIndex < 0)
            {
                Log($"❌ [Phase3.6] 主流程未找到名称含「{DownCameraSegmentKeyword}」的子流程，跳过下相机纠偏（按固定位放置）。");
                return _downCameraPlaceU;
            }
            await RunVisionFlowSegmentAsync(downSegIndex, downSegIndex + 1, $"VPP_DOWN_{DateTime.Now:HHmmss}").ConfigureAwait(false);

            // 3. 读下相机段结果（下钻子流程取 ShapeMatch / CalibrationApply）
            var downSub = FindSubProcessByKeyword(DownCameraSegmentKeyword);
            if (downSub == null)
            {
                Log("❌ [Phase3.6] 未找到「下相机」子流程，跳过下相机纠偏（按固定位放置）。");
                return _downCameraPlaceU;
            }

            var downMatch = FindNodeInProcess(downSub, NodeType.ShapeMatch);
            var downCalib = FindNodeInProcess(downSub, NodeType.CalibrationApply);
            if (downMatch == null || downCalib == null)
            {
                Log("❌ [Phase3.6] 下相机子流程缺少 ShapeMatch/CalibrationApply 节点，跳过纠偏。");
                return _downCameraPlaceU;
            }

            double downScore = GetOutValue<double>(downMatch, "MatchScore");
            double downAngle = GetOutValue<double>(downMatch, "MatchAngle");
            double mx = GetOutValue<double>(downCalib, "OutputX");
            double my = GetOutValue<double>(downCalib, "OutputY");
            // ★ 原生范式2：读原始像素（mx/my 仅保留用于日志；CalibrationApply 输出不再进 δ）
            double dCol = GetOutValue<double>(downMatch, "MatchCol");
            double dRow = GetOutValue<double>(downMatch, "MatchRow");

            Log($"[Phase3.6] 下相机结果: Score={downScore:F3}, 卡片机械位=({mx:F3},{my:F3}), Angle={downAngle:F2}°");

            if (downScore < _cfg.DownCameraMinScore)
            {
                Log($"❌ [Phase3.6] 下相机匹配 {downScore:F3} 低于阈值 {_cfg.DownCameraMinScore:F2}，纠偏 NG，按固定位放置。");
                return _downCameraPlaceU;
            }

            // 4. 先定角度：最终放料角（下面的位置补偿要按它算 U 变化量）
            float uFinal = uPlaceFromUp;
            if (_cfg.EnableDownCameraAngleCorrection)
            {
                double angleDelta = downAngle - _cfg.RefAngleDeg;
                uFinal = _cfg.PlaceU - _cfg.DownCameraAngleSign * (float)angleDelta;
                _downCameraPlaceU = uFinal;
                Log($"[Phase3.6] 角度纠偏：U_place = PlaceU({_cfg.PlaceU:F1}) − Sign({_cfg.DownCameraAngleSign:F0})·(dAngle({downAngle:F2}) − Ref({_cfg.RefAngleDeg:F1})) = {uFinal:F1}°");
            }
            else
            {
                _downCameraPlaceU = uFinal;
                Log($"[Phase3.6] 角度纠偏关闭，U 用 Phase3.5 放料角 {uFinal:F1}°。");
            }

            // 5. 再定位置：δ = 两次链求值做差（差分基准像素 = 向导实测吸嘴 U 轴图像投影 R_cdown）
            //
            // ★★2026-09-27 原生范式2：δ = Chain(R_img) − Chain(DeltaRefPixel)。
            //   背景（范式1 血泪，保留结论）：下相机 H 的语义是「机器人停在哪、卡片 mark 就出现在哪个像素」
            //   （九点真值=吸附工件落点），工件【刚性吸在吸嘴上】⇒ 相对「拍照机位」做差恒 ≈0，
            //   位置纠偏会「日志照打、机构一步没动」。
            //   正确基准 = 吸嘴旋转轴在下相机图像里的投影（与卡片 Seat 无关，是相机+轴的固有投影）：
            //   同一位姿下对【卡片像素】与【基准像素】各做一次链求值，平移项相减消掉，
            //   差值 = 卡片相对吸嘴轴的偏移。基准像素由向导写入链图（DeltaRefPixel）。
            //   缺失 ⇒ 显式降级 dx=dy=0 并说明「本次位置纠偏未生效」，绝不静默假装纠偏成功。
            double dx, dy;
            bool hasDeltaRef = _downCamera != null && _downCamera.DeltaRefPixel != null
                               && _downCamera.DeltaRefPixel.Length == 2;
            if (hasDeltaRef)
            {
                var pose = new ChainRobotPose { X = _cfg.DownCameraX, Y = _cfg.DownCameraY, U = _cfg.WorkU };
                ChainEngine.ResolvePixelToWorld(_chain, _downCamera.CameraId, dCol, dRow,
                    pose, out double wxCard, out double wyCard, Worker?.StationId);
                ChainEngine.ResolvePixelToWorld(_chain, _downCamera.CameraId,
                    _downCamera.DeltaRefPixel[0], _downCamera.DeltaRefPixel[1],
                    pose, out double wxRef, out double wyRef, Worker?.StationId);
                dx = wxCard - wxRef; dy = wyCard - wyRef;
                Log($"[Phase3.6] δ=Chain(R_img)−Chain(R_cdown)：像素 ({dCol:F1},{dRow:F1}) − "
                    + $"({_downCamera.DeltaRefPixel[0]:F1},{_downCamera.DeltaRefPixel[1]:F1}) = ({dx:F3},{dy:F3})mm"
                    + $"（差分基准={_downCamera.CameraId}.DeltaRefPixel，向导实测）");
            }
            else
            {
                dx = 0; dy = 0;
                Log("❌ [Phase3.6] 链图缺下相机差分基准像素（DeltaRefPixel = 吸嘴 U 轴图像投影 R_cdown）"
                    + "⇒ 本次位置纠偏**未生效**（dx=dy=0）。处方：重跑标定向导补测基准像素后重发 Chain.json。");
            }
            float deltaU = uFinal - _cfg.WorkU;      // 拍照姿态 WorkU（Phase0 已把 U 转到该角）→ 放料姿态
            var (rx, ry) = RotEcc(deltaU, dx, dy);

            if (_cfg.EnableDownCameraPositionCorrection)
            {
                _downCameraPlaceX = _cfg.Place1X - rx;
                _downCameraPlaceY = _cfg.Place1Y - ry;
                Log($"[Phase3.6] 位置纠偏：δ=({dx:F3},{dy:F3}) → 按 ΔU={deltaU:F1}° 旋转 → ({rx:F3},{ry:F3}) → 放置位=({_downCameraPlaceX:F3},{_downCameraPlaceY:F3})");
                if (Math.Abs(deltaU) > 0.1 && (Math.Abs(dx) > 1e-3 || Math.Abs(dy) > 1e-3))
                    Log($"[Phase3.6]   （U 变化 {deltaU:F1}°，δ 已随吸嘴旋转补偿；U 恒定时此项自动退化为原值）");
            }
            else
            {
                Log("[Phase3.6] 位置纠偏关闭，放置位=固定位。");
            }

            // 6. ★★2026-09-15：交回 Phase4 之前把 Z 抬回 SafeZ。
            //   本函数把 Z 降到 DownCameraZ 之后【全程不再抬回】，而 Phase4 的 MoveToWorkAsync
            //   是一段平面平移（本工位实测约 132mm）——那是在低于 SafeZ 约 52mm 的高度上做的 ⇒ 撞机风险。
            //   SafeZ 的定义就是"XY 运动前 Z 必须在此之上"，此处必须补回。幂等：已经在 SafeZ 上再走一次无害。
            await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: _cfg.SettleMs, token: token).ConfigureAwait(false);
            Log($"[Phase3.6] 纠偏完成，Z 抬回 SafeZ={_cfg.SafeZ:F3}（Phase4 平面平移前必须的安全高度）。");

            return uFinal;
        }

        /// <summary>下相机纠偏兜底基线：固定放置位 + 传入的放料角（NG/缺件/未标定时统一回落）。</summary>
        private void ResetDownCameraPlacementToFixed(float uPlaceFromUp)
        {
            _downCameraPlaceX = _cfg.Place1X;
            _downCameraPlaceY = _cfg.Place1Y;
            _downCameraPlaceU = uPlaceFromUp;
        }

        /// <summary>
        /// 执行主流程中"除指定段以外"的全部段（用于：配方挂着下相机子流程但开关未开时，
        /// 跳过该空壳段而不是整链跑）。分两段区间执行 [0, exclude) 与 (exclude, count)。
        /// </summary>
        private async Task RunVisionFlowExceptSegmentAsync(int excludeIndex, string batchId)
        {
            int count = TopLevelChainNodeCount;
            if (excludeIndex < 0 || excludeIndex >= count)
            {
                await RunVisionFlowAsync(batchId).ConfigureAwait(false);
                return;
            }

            int ranSegments = 0;
            if (excludeIndex > 0)
            {
                await RunVisionFlowSegmentAsync(0, excludeIndex, batchId).ConfigureAwait(false);
                ranSegments++;
            }
            if (excludeIndex + 1 < count)
            {
                await RunVisionFlowSegmentAsync(excludeIndex + 1, count, batchId).ConfigureAwait(false);
                ranSegments++;
            }
            if (ranSegments == 0)
                Log($"⚠ 跳过第 {excludeIndex} 段后主流程已无可执行段（请检查配方节点编排）。");
        }

        /// <summary>
        /// 下相机段的开工前预检（启用 <see cref="VisionPickPlaceConfig.EnableDownCameraCorrection"/> 时执行一次）。
        /// 把"配方结构不对 / 模板没选 / 矩阵没绑 / 矩阵文件不在"这类配置错误在**第一个周期就报出来**，
        /// 而不是每周期静默按固定位放置。任一不通过直接抛异常（由 RunAsync 的 catch 收尾抬 Z）。
        /// </summary>
        private void ValidateDownCameraSegment(int downSegIndex)
        {
            if (!HasCompositeFlowNode())
                throw new InvalidOperationException(
                    "下相机二次校准预检未通过：当前配方是传统单链（无子流程节点），而下相机二次校准需要复合配方" +
                    "（主流程 = [上相机引导子流程, 下相机纠偏子流程]）。请先把配方改造成两段子流程。");

            int count = TopLevelChainNodeCount;
            if (count < 2)
                throw new InvalidOperationException(
                    $"下相机二次校准预检未通过：主流程只有 {count} 个节点。需要 ≥2 段（上相机引导段 + 下相机纠偏段）。");

            if (downSegIndex < 0)
                throw new InvalidOperationException(
                    $"下相机二次校准预检未通过：主流程中找不到名称含「{DownCameraSegmentKeyword}」的子流程节点。请检查编排里下相机子流程的名称。");

            if (downSegIndex == 0)
                throw new InvalidOperationException(
                    $"下相机二次校准预检未通过：「{DownCameraSegmentKeyword}」子流程位于主流程第 0 段；约定第 0 段必须是上相机引导段。请调整画布中两个子流程的顺序。");

            var downSub = FindSubProcessByKeyword(DownCameraSegmentKeyword);
            var problems = new System.Collections.Generic.List<string>();

            var match = FindNodeInProcess(downSub, NodeType.ShapeMatch);
            if (match == null)
            {
                problems.Add("缺少【形状匹配】节点");
            }
            else
            {
                string tpl = ReadNodeParamString(match, "TemplateName");
                if (string.IsNullOrWhiteSpace(tpl))
                    problems.Add("【形状匹配】未选模板（TemplateName 为空）——需先在下相机视角建立【卡片底面】模板并回填");
            }

            var calib = FindNodeInProcess(downSub, NodeType.CalibrationApply);
            if (calib == null)
            {
                problems.Add("缺少【标定转换】节点");
            }
            else
            {
                string path = ReadNodeParamString(calib, "HomMatFilePath");
                if (string.IsNullOrWhiteSpace(path))
                    problems.Add("【标定转换】未指定标定矩阵（HomMatFilePath 为空）——需先完成下相机九点标定并回填矩阵路径");
                else if (!System.IO.File.Exists(path))
                    problems.Add($"【标定转换】矩阵文件不存在: {path}");
            }

            if (problems.Count > 0)
                throw new InvalidOperationException(
                    "下相机二次校准预检未通过：" + string.Join("；", problems) + "。补全后可重新触发。");

            if (Math.Abs(_cfg.DownCameraX) < 1e-6 && Math.Abs(_cfg.DownCameraY) < 1e-6 && Math.Abs(_cfg.DownCameraZ) < 1e-6)
                Log("⚠ [预检] DownCameraX/Y/Z 全为 0（疑似未示教下相机视场中心机位与拍照高度）——位置纠偏基准会错，请先在示教面板补上。");

            // ★★2026-09-27：把「下相机能不能真用起来」的前置条件在开工前一次说清（范式2：查链图）。
            //   刻意做成 ⚠ 而非抛异常：不满足时行为与改动前一致（位置纠偏不生效），
            //   而抛异常会连「用上相机链验证其余环节」都做不了。给了确切处方，不许静默。
            if (_downCamera == null)
            {
                Log("⚠ [预检] 链图无 DownCameraCorrect 消费边 ⇒ 下相机 δ 无法链求值，Phase3.6 位置纠偏不会生效。"
                    + "处方：重跑标定向导，为下相机补一条 DownCameraCorrect 消费边后重发 Chain.json。");
            }
            else if (_downCamera.DeltaRefPixel == null || _downCamera.DeltaRefPixel.Length != 2)
            {
                Log("⚠ [预检] 链图下相机节点缺差分基准像素 DeltaRefPixel（= 吸嘴 U 轴图像投影 R_cdown）"
                    + "⇒ Phase3.6 位置纠偏不会真正生效（缺基准 ⇒ δ 无法做差）。"
                    + "处方：重跑标定向导补测基准像素后重发 Chain.json。");
            }

            if (Math.Abs(_cfg.DownCameraCalibZ) > 1e-6 && Math.Abs(_cfg.DownCameraZ) > 1e-6)
            {
                double dz = _cfg.DownCameraZ - _cfg.DownCameraCalibZ;
                if (Math.Abs(dz) > 1.0)
                {
                    Log($"⚠ [预检] 下相机拍照高度 DownCameraZ={_cfg.DownCameraZ:F3} 与【标定高度】CalibZ={_cfg.DownCameraCalibZ:F3} "
                        + $"相差 {dz:F3}mm（>1mm）——下相机拍的是吸嘴上的悬空工件，工件 Z 变了物距就变，"
                        + $"像素当量按 1/物距 被系统性缩放 ⇒ δ 会整体被缩放，表现为『纠偏开了还是偏、且偏量与 δ 不成比例』。"
                        + $"处方（二选一）：① 把 DownCameraZ 改成 {_cfg.DownCameraCalibZ:F3}；② 在实际作业高度重标下相机九点。");
                }
            }
            else if (Math.Abs(_cfg.DownCameraZ) > 1e-6)
            {
                // ★★2026-09-16：以前这条判据在 CalibZ=0 时【静默跳过】—— 现场看到"预检全过"，
                //   其实"拍照高度对不对"这件事根本没被判定过（判据失效被当成了通过）。
                //   判据不可得必须明说，不许沉默。
                Log($"⚠ [预检] 缺『标定高度』DownCameraCalibZ（当前 0/未设）⇒ **本次无法核对**拍照高度 "
                    + $"DownCameraZ={_cfg.DownCameraZ:F3} 是否与标定时的 Z 一致（该判据本轮失效，不等于通过）。"
                    + $"下相机是在别的 Z 上标的九点的话，工件 Z 一变物距就变、像素当量按 1/物距 缩放 ⇒ δ 被整体缩放。"
                    + $"处方：把标定当时机械手的 Z 填进 DownCameraCalibZ（多数机型=Z 域顶/0）。");
            }

            Log($"✅ [预检] 下相机段就绪（第 {downSegIndex} 段；机位 ({_cfg.DownCameraX:F3},{_cfg.DownCameraY:F3},{_cfg.DownCameraZ:F3})，MinScore={_cfg.DownCameraMinScore:F2}，角度符号={_cfg.DownCameraAngleSign:F0}）。");
        }


        /// <summary>
        /// 平面平移。★2026-09-16 起**默认走门型**（先抬到 JumpLimZ → 水平走 → 降回 SafeZ），
        /// 不再逐轴拆成"先 X 后 Y"。
        ///
        /// 为什么改：逐轴拆两步末端走 L 形，会在拐角点停一次，且实测本工位那条 L 形路径
        /// （取料位→下相机位）第二段最紧点离内圈边界只剩 1.5mm（贴边飞过）；
        /// 门型走同一对点的直线，最紧点余量 31.3mm。
        /// </summary>
        private async Task MoveXyAsync(double x, double y, CancellationToken token)
        {
            await MoveGantryAsync(x, y, _cfg.SafeZ, token).ConfigureAwait(false);
        }

        // ============================================================
        // 门型走位参数（转发工位配置）
        //
        // ★门型算法本体只有一份：StationProcessBase.MoveGantryAsync / ResolveGantryLimZ。
        //   这里只供货轴号与高度 —— 刻意不在各过程各写一份，本项目吃过
        //   "同一条判据写两遍 ⇒ 两边分叉 ⇒ 靠巧合正确"的亏。
        // ============================================================

        /// <summary>X 轴号</summary>
        protected override int AxisX => _cfg.AxisX;
        /// <summary>Y 轴号</summary>
        protected override int AxisY => _cfg.AxisY;
        /// <summary>Z 轴号</summary>
        protected override int AxisZ => _cfg.AxisZ;
        /// <summary>U 轴号</summary>
        protected override int AxisU => _cfg.AxisU;
        /// <summary>门型水平段高度（本工位配 -30，比 SafeZ 再高 20mm；它是【安全通过高度】不是速度）</summary>
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
            // ★ 2026-09-16：加容差。实测反馈 -50.0002 对配置 -50.000 会打印
            //   "当前 -50.000 低于安全高度 -50.000"这种自相矛盾的日志（差 0.2um），
            //   还白发一条运动指令。判据要能区分"真的低"与"就是安全高度"。
            if (z < _cfg.SafeZ - 0.01f)
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
