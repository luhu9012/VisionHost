//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 标定相关业务模型
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// <summary>
    /// 相机安装物理模式
    /// </summary>
    public enum EyeMode
    {
        EyeToHand, // 眼在手外 (相机固定，工件/平台移动)
        EyeInHand  // 眼在手上 (相机随轴/机器人末端移动)
    }

    /// <summary>
    /// TCP 对针（工具中心偏置 TCO）的采集方式（2026-09-08：区分两种布局的求解方法，
    /// 供迁移器判定 EyeInHand 档案上对针结果的时效性——间接对针有效，旧图像对针残留过期）。
    /// </summary>
    public enum ToolOffsetMethod
    {
        /// <summary>旧档案反序列化默认值 / 未知来源——EyeInHand 布局下视为历史残留（过期，需按间接对针重标）</summary>
        LegacyUnknown = 0,

        /// <summary>EyeInHand 间接对针：工具尖压特征记基准位 R_n → 抬 Z 拍同点 → TCO = H(u) − R_n</summary>
        EyeInHandIndirect = 1,

        /// <summary>EyeToHand 图像对针：固定相机观测，工具对准特征锁 M_tool → 移开抓拍 → 点选实际落点</summary>
        EyeToHandImage = 2
    }
    /// <summary>
    /// 标定特征类型（第二步"特征配置"中由用户选择，决定特征提取算子）
    /// </summary>
    public enum CalibrationFeatureType
    {
        CircleMark, // 圆形 Mark 点 (提取圆心)：阈值分割 + 圆度筛选 + 亚像素圆拟合
        CrossMark,  // 十字 Mark 点 (形状匹配)：几何结构法（骨架 + 直线交叉点）
        TemplateMatch // 模板匹配（复用全局模板库 Shape/NCC 模板，`MatchByName`）：对工件整体/特征区域匹配，
                      // 返回模板参考点中心——对光照/反光最稳，吸放式标定中工件正面图案无 Mark 时首选
    }
    public class CalibrationProfile : ViewModelBase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string BoundStationCode { get; set; }
        public string BoundDeviceId { get; set; }

        /// <summary>
        /// 吸嘴/工具通道键（"1"/"2"/...）：双吸嘴等"多工具与 R/U 不同轴"的工位，
        /// 每个吸嘴一套独立标定（各自的旋转中心/工具偏心/真空通道）。默认 "1"=单吸嘴工位。
        /// 同一工位允许多条 profile（NozzleKey 不同）；配方中每个吸嘴的 CalibrationApply 节点
        /// 各自引用对应 .tup，业务流吸嘴1 引导抓取后再引导吸嘴2。
        /// </summary>
        public string NozzleKey { get; set; } = "1";

        public string BindingInfo { get; set; }

        /// <summary>
        /// 物理相机设备标识（绑定设备后的 DeviceKey/DeviceId）。
        /// ⚠ 它代表"这台物理相机"，【不等于】工位档案里的相机槽——绑设备会覆盖本字段。
        /// 槽语义请用 <see cref="CameraSlotKey"/>（2026-09-11 解耦）。
        /// </summary>
        public string CameraId { get; set; } = "Cam_01";

        private string _cameraSlotKey;
        /// <summary>
        /// ★ 工位档案相机槽键（Cam_A / Cam_C …）——与物理设备 <see cref="CameraId"/> 解耦。
        /// 用途：标定产物 ArtifactId = {站}|{量}|{槽}|{吸嘴}，槽决定任务卡身份、依赖匹配与
        /// 配方 CalibrationApply 的引用口径。复合工位（上相机 Cam_A + 下相机 Cam_C）必须靠它区分。
        /// 背景：旧逻辑只能从 CameraId 猜槽（GuessSlotKey）——而向导第一步绑物理相机时 CameraId 会被
        /// 写成设备名（如 Hikvision_…_DownCamera），不以 "Cam_" 开头 → 恒猜成 "Cam_01"，
        /// 导致上下相机两条标定产物撞在同一个槽上。为空时回退旧猜测逻辑。
        /// </summary>
        public string CameraSlotKey
        {
            get => _cameraSlotKey;
            set => Set(ref _cameraSlotKey, value);
        }

        public string AxisId { get; set; } = "Axis_X";
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // --- 新增：物理场景参数 ---
        // ⚠ 2026-09-04 默认 EyeInHand：本产品工位（ST001/ST002 等）均为"相机随轴移动"的眼在手上布局，
        //   旧默认 EyeToHand 会让九点走位方向语义/几何检查按错误模式引导（首次标定极易镜像出错）。
        //   EyeToHand（相机固定、工件台动）的用户请在向导步骤1 手动切回。
        private EyeMode _eyeMode = EyeMode.EyeInHand;
        public EyeMode EyeMode
        {
            get => _eyeMode;
            set => Set(ref _eyeMode, value);
        }

        private bool _hasToolOffset;
        /// <summary>
        /// 轴末端是否有吸嘴/夹具旋转偏移 (TCP)。
        ///
        /// ★★2026-09-15 重要澄清——**本字段是「声明」不是「证据」，禁止用作"旋转段跑过"的判据**：
        ///   向导在"会话规格含旋转段"时会**一进会话就自动置 true**（早于任何采样）⇒
        ///   `HasToolOffset==true` 只表示"本次打算转"，旋转段**失败/根本没跑**时它同样为 true。
        ///   曾有三处判据把它写进 OR 链（CalibrationCardDeriver 两处 + CalibrationProfileSessionPlanner
        ///   一处）⇒ "勾了旋转类型但没转出结果"被当"有旋转证据"，e 卡从 Draft 被提升为已完成（假绿）。
        ///   已全部删除该 OR 项；要判"旋转是否真跑过"，只认**数值**：CalibU0 / ToolCenterW(x,y) /
        ///   ToolCenterP(x,y) / ToolEccW(x,y) / ToolOffsetPureW(x,y) 非零。
        ///   要判"t 对针是否真做了"，用 <c>IsToolOffsetCalibrated</c> + 非零（见 CameraCalibrationBundle.HasToolOffset）。
        /// </summary>
        public bool HasToolOffset
        {
            get => _hasToolOffset;
            set => Set(ref _hasToolOffset, value);
        }

        private string _boundDistortionProfileId;
        /// <summary>关联的前置相机畸变标定方案 ID (若为空则不执行畸变矫正)</summary>
        public string BoundDistortionProfileId
        {
            get => _boundDistortionProfileId;
            set => Set(ref _boundDistortionProfileId, value);
        }

        // --- 新增：拟合计算输出结果 ---
        private double _toolCenterPx;
        public double ToolCenterPx { get => _toolCenterPx; set => Set(ref _toolCenterPx, value); }

        private double _toolCenterPy;
        public double ToolCenterPy { get => _toolCenterPy; set => Set(ref _toolCenterPy, value); }

        private double _toolCenterWx;
        /// <summary>
        /// ★ 旋转中心 O = 三点定圆圆心经 H 映射 = F0 − r（F0=九点基准特征世界位，r=回转中心相对相机光心的固定偏移）。
        /// ⚠ 它是"H 域/命令位域"的量，不是机械手绝对 Base 坐标；消费式 X_obj = P_photo + O − H(u) 里
        ///   就是直接用它（不需要再与机位做差）。★ 半径（=延伸杆长度）已丢弃，故杆长/杆偏心不影响。
        /// ⚠ 旧注释里"与 RotationBaseX/Y 做差得不变量 K / 见 RotCenterOffsetW"的推导【已推翻】，
        ///   RotCenterOffsetW 已废弃（2026-09-08）：O 本身就是不变量，不要再做差。
        /// </summary>
        public double ToolCenterWx { get => _toolCenterWx; set => Set(ref _toolCenterWx, value); }

        private double _toolCenterWy;
        /// <summary>U 轴旋转中心 Y（H 域/命令位域），语义见 ToolCenterWx</summary>
        public double ToolCenterWy { get => _toolCenterWy; set => Set(ref _toolCenterWy, value); }

        // ===== 2026-09-08 定案语义（数值仿真验证：任意拍照位/特征/U角 误差 0.000000mm）=====
        // 完整推导与唯一真源实现见 Grayson.Vision.Contracts.Calibration.CalibrationGeometry。
        //   命令位 P 时：回转中心真位 = P + r；相机光心真位 = P + c（r、c 都不随 U 转，只随 XY 平移）
        //   九点 H   ：H(u) = "使特征成像在 u 的命令位" = F0 − c − g(u)，F0=九点标定特征的世界位
        //               ⟹ V(u) ≡ 命令点→像素 u 所成像世界点的向量 = F0 − H(u)
        //               ⟹ 【世界点真位 = P + V(u) = P + F0 − H(u)】（H 输出是命令位域，不是真位！）
        //   旋转标定 ：Mark 绕回转心做圆 ⟹ H(u_θ) = F0 − r − R(θ)·m，圆心 = F0 − r，半径 = |m|【丢弃】
        //               ⟹ O ≡ ToolCenterW = 三点定圆圆心经 H 映射 = F0 − r
        //               ⟹ 延伸杆长度/偏心率完全不参与（只要求采样期间杆不松动）
        //   物理对针 ：U=U0 吸嘴尖压针尖(命令位 R_n)，抬 Z 回标定高度拍该特征得 p_tip：
        //               针尖真位 = R_n + r + e = R_n + F0 − H(p_tip) ⟹ r + e = F0 − H(p_tip)
        //               ⟹ ★ e = O − H(p_tip)（F0 自动消掉；同心杆旋转标定给同一量，互为交叉验证）
        //   唯一正确消费式（EIH 眼在手）：
        //       X_obj = P_photo + O − H(u)          （点选特征的 Base 真位）
        //       P_go  = X_obj − R(U_go − U0)·e      （吸嘴尖压中该特征的命令位）
        //       ETH（相机固定）时 X_obj = H(u)，O/P_photo 不参与。
        // ⚠ 已证伪的旧式（禁止再用）：
        //   v5  R_go = H(u) − TCO（TCO 含绝对坐标 R_n）→ 仿真 200 组最大误差 451mm；
        //       仅在"点选像素恰为 p_tip 且拍照位=R_n"时自洽，属定义循环，不是有效验证。
        //   v4  R_go = P_photo + H(p_tip) − H(u)   → 缺旋转项，U≠U0 时最大误差 27.8mm（θ=0 时才对）。
        //   ToolEccW（偏心延伸杆 Mark 偏心）= −m，不是吸嘴偏心，禁止当 e 发布到生产。

        private double _rotationBaseX;
        /// <summary>旋转采样时的机械手命令位 X（mm）= P_f。K=ToolCenterWx−RotationBaseX。</summary>
        public double RotationBaseX { get => _rotationBaseX; set => Set(ref _rotationBaseX, value); }

        private double _rotationBaseY;
        /// <summary>旋转采样时的机械手命令位 Y（mm）= P_f。</summary>
        public double RotationBaseY { get => _rotationBaseY; set => Set(ref _rotationBaseY, value); }

        // ==================== 标定过程证据（2026-09-15 新增）====================
        // 为什么要有这一组：出问题时（本工位 2026-09-15 就踩到）最需要的三个量当时【都拿不到】——
        //   ① 旋转采样点从落盘（Samples 只装九点，RotationPoints 全丢）⇒ 弧覆盖度/逐点残差无法离线复核；
        //   ② 定圆半径被丢弃，而半径 = |杆端 mark ↔ 回转轴| = |b|，正是最需要的量的独立估计；
        //   ③ 各采样点的机位（BaseX/BaseY）无人填 ⇒ 无法判断"采样期间机器人是否动过 XY"。
        // 有了这一组，"重标一次"就能离线判定 b 的量级/方向/参考姿态是否可信，不必再上机试。

        /// <summary>各旋转采样点原始证据（角度→像素→机位→匹配分）。空=旧档案（重标后即有）。</summary>
        public List<RotationSampleEvidence> RotationSamples { get; set; } = new List<RotationSampleEvidence>();

        /// <summary>★ 定圆半径（像素）= 杆端 mark 到回转轴的距离在图像里的半径。旧档案为空（当年被丢弃）。</summary>
        public double? RotationFitRadiusPx { get; set; }

        /// <summary>★ 定圆半径映射到 H 域（mm）—— 与 |ToolEccW| 是同一物理量的两种量法，可互为交叉校核。</summary>
        public double? RotationFitRadiusMm { get; set; }

        /// <summary>采样角的环向覆盖弧长（度）。&lt;90° 时圆心沿缺弧方向误差被放大数倍。</summary>
        public double? RotationArcCoverageDeg { get; set; }

        /// <summary>定圆拟合 RMS（px，像素域）。</summary>
        public double? RotationFitRmsPx { get; set; }

        /// <summary>各采样点机位的最大分散度（mm）：&gt;1mm 说明采样期间机器人被移动过，b/O 不可信。</summary>
        public double? RotationMotionSpreadMm { get; set; }

        /// <summary>★ 旋转采样基准角 U_ref（度）= b 的参考姿态。缺它无法离线复原偏心方向。</summary>
        public double? RotationBaseU { get; set; }

        /// <summary>旋转拟合摘要（人可读：圆心/半径/覆盖/RMS/剔脏/方法）。</summary>
        public string RotationFitSummary { get; set; }

        /// <summary>保存时自动跑出的交叉校核结论（多行文本，PASS/FAIL 逐条）。</summary>
        public string CalibrationEvidence { get; set; }

        /// <summary>过程证据 JSON 的落盘路径（每次标定新写一份，不覆盖，便于两次标定 diff）。</summary>
        public string EvidenceFilePath { get; set; }

        private double _rotCenterOffsetWx;
        /// <summary>【已废弃 2026-09-08】旧推导的 K=r−c 不成立（正确不变量就是 ToolCenterW 本身 = F0−r）。保留仅为兼容旧档案，勿再使用。</summary>
        public double RotCenterOffsetWx { get => _rotCenterOffsetWx; set => Set(ref _rotCenterOffsetWx, value); }

        private double _rotCenterOffsetWy;
        /// <summary>【已废弃】见 RotCenterOffsetWx</summary>
        public double RotCenterOffsetWy { get => _rotCenterOffsetWy; set => Set(ref _rotCenterOffsetWy, value); }

        private double _toolOffsetPureWx;
        /// <summary>
        /// ★ 真吸嘴偏心 e_x（mm，U=U0 参考，Base 向量）= 吸嘴尖相对 U 轴回转中心的偏移。
        /// 求法：e = O − H(p_tip)（O=ToolCenterW，p_tip=对针像素），由 CalibrationGeometry.SolveNozzleEcc 统一计算。
        /// 与延伸杆长度/偏心【无关】；偏心延伸杆得到的 ToolEccW(=−m) 不是这个量。
        /// </summary>
        public double ToolOffsetPureWx { get => _toolOffsetPureWx; set => Set(ref _toolOffsetPureWx, value); }

        private double _toolOffsetPureWy;
        /// <summary>★ 真吸嘴偏心 e_y（mm，U=U0 参考），语义见 ToolOffsetPureWx</summary>
        public double ToolOffsetPureWy { get => _toolOffsetPureWy; set => Set(ref _toolOffsetPureWy, value); }

        private bool _isNozzleEccCalibrated;
        /// <summary>真吸嘴偏心 e（ToolOffsetPureW）是否已标定（需旋转中心 O + 物理对针 p_tip 两者齐备）</summary>
        public bool IsNozzleEccCalibrated
        {
            get => _isNozzleEccCalibrated;
            set => Set(ref _isNozzleEccCalibrated, value);
        }

        private bool _hasRotationCenter;
        /// <summary>
        /// 旋转中心 O（ToolCenterW）是否已标定 —— e 与消费式都依赖它。
        /// ★ 旧档兼容：本标志是 2026-09-08 新增的，此前已做过旋转标定的档案里它是 false，
        ///   但 ToolCenterW 有值。故 getter 自动把"ToolCenterW 非零"也视为已标定，
        ///   避免现场不重做标定就被误判"缺 O"而退到不准的差分式。
        ///   （旋转中心恰好落在世界原点 (0,0) 的概率可忽略；真出现请手动置 false。）
        /// </summary>
        public bool HasRotationCenter
        {
            get => _hasRotationCenter
                   || Math.Abs(_toolCenterWx) > 1e-9
                   || Math.Abs(_toolCenterWy) > 1e-9;
            set => Set(ref _hasRotationCenter, value);
        }

        private bool _isCalibrated;
        public bool IsCalibrated { get => _isCalibrated; set => Set(ref _isCalibrated, value); }

        private double _rmsError;
        public double RmsError { get => _rmsError; set => Set(ref _rmsError, value); }

        private string _homMatFilePath;
        public string HomMatFilePath { get => _homMatFilePath; set => Set(ref _homMatFilePath, value); }

        /// <summary>
        /// 本次标定的拍照/放料面 Z 高度（mm，2026-09-04）。
        /// HomMat 矩阵对拍照高度敏感：现场换高度/换治具后应重标。
        /// 吸放式=固定拍照位 PhotoPoseZ；九点/九点+旋转=网格中心点(Index5)成功采样时实测轴 Z。
        /// null=未记录（例如中心点缺采）。
        /// </summary>
        public double? CalibZ { get; set; }

        private bool? _cameraMovesWithZ;
        /// <summary>
        /// 相机是否随 Z 轴升降（硬件安装特性，2026-09-08 泛化——不得按单机写死）。
        /// true = 相机与工具头同装在 Z 运动件上（Z 升降相机跟着动）→ 拍照高度决定像素当量，
        ///        HomMat 是"该 Z 高度"的 2D 仿射：拍照/点选/到位必须在 CalibZ±容差（旧 Z 铁律适用）。
        /// false= 相机刚性安装、只随 XY/臂动，Z 行程不改变相机位置（2026-09-08 本工位实测）→
        ///        成像与 Z 无关：任意 Z 抓拍/点选均可，但工具头的图像投影随 Z 变（透视视差），
        ///        目视贴面判定须下压到 R_nZ（NozzleAlignZ 压住高度）。
        /// null = 未声明 → 消费端按保守口径（等同 true 守护，避免无声丢失精度保护）并提示声明。
        /// </summary>
        public bool? CameraMovesWithZ
        {
            get => _cameraMovesWithZ;
            set => Set(ref _cameraMovesWithZ, value);
        }

        /// <summary>
        /// 标定时的 U 旋转基准读数（°，2026-09-04）——旋转补偿 R(U−U₀)·e 中的 U₀ 参考。
        /// 平移采样阶段 U 轴不动，取中心点实测；吸放式取固定拍照位 PhotoPoseU。
        /// null=未记录。
        /// </summary>
        public double? CalibU0 { get; set; }

        /// <summary>
        /// ★v2 标定物理量（2026-09-12 起为唯一权威语义源，取代旧 CalibrationType）：
        /// HandEye(H)/ToolRotation(e)/ToolOffset(t)/PixelScale(s)/LensDistortion。
        /// 采集方法由 PrimaryPath（CalibrationAcquirePath）承载，与物理量正交。
        /// </summary>
        public CalibrationQuantity Quantity { get; set; } = CalibrationQuantity.HandEye;

        /// <summary>
        /// ★v2 采集路径：决定向导步骤装配与真值语义。含下相机专属路径
        /// DownCameraWalk / DownCameraPixelRotCenter（仰视二次对位）。
        /// </summary>
        public CalibrationAcquirePath? PrimaryPath { get; set; }

        // ===== ★2026-09-15：消费口径的两个显式声明（决定同轴吸嘴的 X_obj / 吸点怎么算）=====
        // 背景（复合工位 Cam_A：固定上相机 + 延伸杆辅助标定 + 吸嘴与 Z 同轴）：
        //   现场把"旋转拟合圆心的偏差"手工融合进九点矩阵后发布，H 其实已经处于【吸嘴域】
        //   （命令到 H(u) 就让吸嘴对准 u）。但平台按 PrimaryPath==CameraTruthWalk 一刀切判
        //   "H 是杆端域、需叠 O/e"，于是对一份【已经消过杆】的 H 又补一遍 O 和 R(U−U0)·e
        //   —— 双重补偿，还把 P_photo 混进物位 ⇒ 校验必然不对。
        //   所以"H 落在哪个域"和"吸嘴是否与 U 同轴"必须能声明出来，不能靠猜。

        private bool? _handEyeInNozzleDomain;
        /// <summary>
        /// ★ 九点矩阵 H 是否已在【吸嘴域】（2026-09-15，消费口径声明）。
        ///   true  = H 已消杆：命令到 H(u) 即让【吸嘴】对准像素 u（延伸杆的偏心 b 已在标定阶段
        ///           从矩阵平移列里扣掉）⇒ 消费直接 X_obj = H(u)，**不叠 O、不用 P_photo**。
        ///   false = 明确声明 H 在"杆端 / 相机中心域"（等价旧行为）。
        ///   null  = 未声明（旧档案）⇒ 按 PrimaryPath 旧口径保守判定，行为与本次改动前完全一致。
        /// ⚠ 与 PrimaryPath 的分工：PrimaryPath 描述【怎么采】，本字段描述【采完落在哪个域】。
        ///   同一条采集路径（如 CameraTruthWalk）既可能发布未消杆的 H（需 O/e），也可能发布
        ///   已消杆的 H（直吸）—— 单看 PrimaryPath 分辨不出来，这正是 09-14/15 现场踩的坑。
        /// </summary>
        public bool? HandEyeInNozzleDomain
        {
            get => _handEyeInNozzleDomain;
            set => Set(ref _handEyeInNozzleDomain, value);
        }

        private bool? _nozzleAxisCoaxial;
        /// <summary>
        /// ★ 吸嘴是否与 U 回转轴同轴（2026-09-15，消费口径声明）。
        ///   true  = 同轴：转 U 时吸嘴尖在 XY 上【原地不动】⇒ 消费**不做** R(U−U0)·e 补偿，
        ///           U 只决定姿态（targetU = 当前U + ΔU）。本工位（复合工位 Cam_A）属此。
        ///   false = 吸嘴偏在轴外（经典偏心吸嘴）：转 U 时吸嘴尖画圆，必须减 R(U−U0)·e。
        ///   null  = 未声明（旧档案）⇒ 保守按"偏心"处理（保留 R 项），行为与改动前一致。
        /// ⚠ 判据：绕 U 转 30° 前后各测一次同一不动特征，吸嘴尖偏差 d0、d30 都 ≈0 且 d30−d0 ≈0
        ///   ⇒ 同轴成立。见《复合工位Cam_A_上机验证单》。
        /// </summary>
        public bool? NozzleAxisCoaxial
        {
            get => _nozzleAxisCoaxial;
            set => Set(ref _nozzleAxisCoaxial, value);
        }

        private bool? _rodOffsetInProduction;
        /// <summary>
        /// ★ 是否把『杆端→吸嘴偏移 b』带进生产（2026-09-15，消费口径声明）。
        ///
        /// 背景：固定相机 + 延伸杆辅助标定时，九点 H 的域是【杆端 mark】——命令到 H(u) 时落在特征上
        ///   的是杆端，不是吸嘴尖。同心吸嘴坐在 U 回转轴上 ⇒ 送吸嘴尖要补一个**与 U 无关**的常量位移 b：
        ///       **吸点 = H(u) + b**
        ///   ★ 校验台一直按这个口径算 ⇒ **校验台"压中了"不等于生产"压中了"**（两者口径不同，差一个 |b|）。
        ///   true  = 生产端 X_obj = H(u) + b（生产 PickAnchor 第②条路径，b 取档案 ToolEccWx/Wy
        ///           或对针 t，符号默认 +1）。
        ///   false/null = 不补（默认）。生产端走 X_obj = H(u)，比正确落点**少一个 |b|**
        ///           （本工位实测 |b| = 106.39mm）⇒ 用现成配置直接生产必然偏这么多。
        ///
        /// ⚠ 为什么不默认 true：b 的**符号**要靠现场 A/B 判定（选错会偏 2|b| ≈ 213mm，比不补更糟）。
        ///   流程：① 校验台反复验到吸嘴压中（⇒ b 的量级与方向都对）→ ② 把本字段置 true 并重新发布。
        /// ⚠ 与 HandEyeInNozzleDomain 互斥：H 已消杆（true）时本字段必须 false，否则双重补偿。
        /// </summary>
        public bool? RodOffsetInProduction
        {
            get => _rodOffsetInProduction;
            set => Set(ref _rodOffsetInProduction, value);
        }

        private double? _downRotCenterRow;
        /// <summary>
        /// ★下相机像素旋转中心 Row（像素；DownCameraPixelRotCenter 路径产出，2026-09-12）。
        /// 吸嘴旋转轴在下相机图像里的投影行坐标 R_cdown。消费端相对纠偏
        /// δ = H_down(R_img) − H_down(R_cdown)（见 CalibrationGeometry.DownCameraOffset），
        /// 与上相机绝对定位语义正交；null=未标定（下相机无法做二次纠偏）。
        /// </summary>
        public double? DownRotCenterRow
        {
            get => _downRotCenterRow;
            set => Set(ref _downRotCenterRow, value);
        }

        private double? _downRotCenterCol;
        /// <summary>★下相机像素旋转中心 Col（像素；DownCameraPixelRotCenter 路径产出），语义见 DownRotCenterRow</summary>
        public double? DownRotCenterCol
        {
            get => _downRotCenterCol;
            set => Set(ref _downRotCenterCol, value);
        }

        // --- 新增：标定所绑定的轴索引和基准位置 ---
        private int _bindXAxisIndex = 1;
        /// <summary>标定所绑定的 X 轴索引 (默认1号轴)</summary>
        public int BindXAxisIndex
        {
            get => _bindXAxisIndex;
            set => Set(ref _bindXAxisIndex, value);
        }

        private int _bindYAxisIndex = 3;
        /// <summary>标定所绑定的 Y 轴索引 (左工位设为3，右工位设为2)</summary>
        public int BindYAxisIndex
        {
            get => _bindYAxisIndex;
            set => Set(ref _bindYAxisIndex, value);
        }

        private int _bindRotationAxisIndex = 0;
        /// <summary>
        /// 旋转中心标定所绑定的旋转轴索引。
        /// 默认 0 = ZMC 双滑台工位约定（0号轴 = Z/旋转轴）；
        /// Epson SCARA 工位必须改为 3（3号轴 = U 旋转轴），否则旋转采样会把
        /// X 轴当旋转轴转到几十毫米的"角度值"——模拟器里表现为乱走，实机上有撞机风险。
        /// TODO(现场-Epson工位)：在标定向导步骤1把旋转轴选为「3号轴：U轴（末端旋转）」。
        /// </summary>
        public int BindRotationAxisIndex
        {
            get => _bindRotationAxisIndex;
            set => Set(ref _bindRotationAxisIndex, value);
        }

        private double _basePosX;
        /// <summary>标定起始基准 X 坐标。
        /// ★口径(2026-09-06 定稿，勿再按旧文案"相机视野对准"操作)：= 手动 JOG 让【执行机构(吸嘴1 尖)压住标定工件特征中心】时读的回转中心(XY 读数)，工件放在生产实际取料位。
        /// 相机随动(眼在手)时"相机视野对准工件"机位与"吸嘴压工件"机位相差 相机安装偏移+偏心 合成量(几十 mm 级)——九点真值=命令位(回转中心)，BasePos 是工件真位锚点，
        /// 口径错了矩阵/偏心重标多少次 C2 换算都系统性漂移。与向导 H 重标核对文案(CalibrationWizardViewModel:1726)对齐。</summary>
        public double BasePosX
        {
            get => _basePosX;
            set
            {
                if (Set(ref _basePosX, value))
                {
                    IsBasePosSet = true; // 手动输入或按钮设置均视为已设置基准
                }
            }
        }

        private double _basePosY;
        /// <summary>标定起始基准 Y 坐标</summary>
        public double BasePosY
        {
            get => _basePosY;
            set
            {
                if (Set(ref _basePosY, value))
                {
                    IsBasePosSet = true;
                }
            }
        }

        private double? _basePosU;
        /// <summary>
        /// 标定起始基准 U（回转）角（°，2026-09-10）。设基准点时锁定：把执行机构对准基准特征时读的当前 U。
        /// 九点平移走位据此保持固定 U 姿态（不再强制归零），旋转采样以此作 U_ref（相对角 0 参考）。
        /// 基准 U 可能恰为 0（机台原点）或 180°，不能用 ==0 判断是否设置，用 HasValue 判。
        /// </summary>
        public double? BasePosU
        {
            get => _basePosU;
            set => Set(ref _basePosU, value);
        }

        private double? _basePosZ;
        /// <summary>
        /// 标定起始基准 Z 高度（mm，2026-09-10）。设基准点时锁定：把执行机构对准基准特征时读的当前 Z。
        /// 九点走位全程保持该 Z（拍照/走位面一致），HomMat 即"该 Z 高度"的 2D 仿射。
        /// 九点中心点(#5)成功采样时会用实测 Z 覆盖为 CalibZ（若基准 Z 未设则兜底）。
        /// </summary>
        public double? BasePosZ
        {
            get => _basePosZ;
            set => Set(ref _basePosZ, value);
        }

        private double _nozzleAlignX;
        private double _nozzleAlignY;
        /// <summary>
        /// ★吸嘴对准工件锚点 X（EIH 换算锚点 R_n，2026-09-06 与 BasePos 解耦）。
        /// 口径 = 手动 JOG 让【吸嘴1 尖压住/对准当前工件特征中心】时读的回转中心 XY（工件放生产实际取料位）。
        /// 与 BasePos（九点网格中心=相机取景位）解耦的原因：眼在手布局下"相机能拍到工件"与
        /// "吸嘴对准工件"两个机位相差相机安装偏移+偏心合成量（可达几十 mm），BasePos 一肩挑两职时
        /// 要么九点中心点出视野（BasePos 取吸嘴对准位）要么换算锚错（BasePos 取相机取景位）。
        /// 消费（校验台 ApplyPick，2026-09-08 v5 定稿）：间接对针 TCO = H(p_tip) − R_n（存 ToolOffsetWx/Wy，
        /// = R(U0)·e − c 合成量）；落点 R_go = w_click − TCO（U==U0 时）＝任意工件位/拍照位下让吸嘴尖压住
        /// 点选特征的回转中心目标（工件挪动也成立；实测铁证：工件不动点同特征时 R_go 精确回压 R_n）。
        /// </summary>
        public double NozzleAlignX
        {
            get => _nozzleAlignX;
            set
            {
                if (Set(ref _nozzleAlignX, value))
                {
                    IsNozzleAlignSet = true;
                }
            }
        }
        /// <summary>吸嘴对准工件锚点 Y（见 NozzleAlignX 注释口径）</summary>
        public double NozzleAlignY
        {
            get => _nozzleAlignY;
            set
            {
                if (Set(ref _nozzleAlignY, value))
                {
                    IsNozzleAlignSet = true;
                }
            }
        }
        private bool _isNozzleAlignSet;
        /// <summary>吸嘴对准工件锚点是否已设置（锚点坐标恰可为 0，用标志位判断）</summary>
        public bool IsNozzleAlignSet
        {
            get => _isNozzleAlignSet;
            set => Set(ref _isNozzleAlignSet, value);
        }

        private double? _nozzleAlignZ;
        /// <summary>
        /// 吸嘴对准工件锚点 Z——压住高度（mm，2026-09-08 实机补）。
        /// 口径 = 记 R_n 压点时（工具头尖轻压工件特征、贴工件面）的轴 Z 读数。
        /// 用途：校验台"到位目视/贴面判定"的下压目标——尖需回到该高度才"够到工件"；
        /// 实机相机刚性安装（不随 Z 伸缩）时成像与 Z 无关，但尖的图像投影随 Z 变（透视视差），
        /// 目视对准必须落在 R_nZ 附近视差才最小。null=未记录（旧档案，到位目视请参考标定高度或工件面）。
        /// </summary>
        public double? NozzleAlignZ
        {
            get => _nozzleAlignZ;
            set => Set(ref _nozzleAlignZ, value);
        }

        // ---- 吸嘴尖对准像素 p_tip（2026-09-06 v3 差分式消费锚，与标定工件 M 解耦的关键）----
        // 背景：眼在手"命令位网格"标定的矩阵裸输出 w=H(u)=拍照位+M−M'（M=标定工件位，隐含在矩阵平移项），
        //       直接"绝对换算"只在当前特征==标定时工件 M（同位）时成立——生产任意位/自检挪件必整体漂移。
        //       v3 消费改为像素差分：R_go = 拍照位 + H(p_tip) − H(u_click)，H 差分自动消掉 M，
        //       任意工件位/拍照位都让吸嘴尖压住点选特征（U 与标 p_tip 时一致的前提下）。
        // p_tip 标定（一次，设备常量，相机-吸嘴刚体）：JOG 吸嘴1 尖压住工件特征(回转中心=R_n) →
        //       抬 Z 回标定高度、XY 保持不动 → 抓拍定格 → 点选该特征 → 记下像素。吸嘴不进画面也成立：
        //       p_tip 是"机位=R_n 时该特征在相机里的像"，不要求看到吸嘴。
        private double _toolAlignPixelX = -1;
        private double _toolAlignPixelY = -1;
        /// <summary>吸嘴尖对准像素 X（col）。见 IsToolAlignPixelSet 注释口径。未设置=-1。</summary>
        public double ToolAlignPixelX
        {
            get => _toolAlignPixelX;
            set
            {
                if (Set(ref _toolAlignPixelX, value))
                {
                    IsToolAlignPixelSet = _toolAlignPixelX >= 0 && _toolAlignPixelY >= 0;
                }
            }
        }
        /// <summary>吸嘴尖对准像素 Y（row）。未设置=-1。</summary>
        public double ToolAlignPixelY
        {
            get => _toolAlignPixelY;
            set
            {
                if (Set(ref _toolAlignPixelY, value))
                {
                    IsToolAlignPixelSet = _toolAlignPixelX >= 0 && _toolAlignPixelY >= 0;
                }
            }
        }
        private bool _isToolAlignPixelSet;
        /// <summary>对准像素是否已设置（像素恰可为 0,0，用标志位判断；旧档案无该字段=未设置，需校验台现场标一次）</summary>
        public bool IsToolAlignPixelSet
        {
            get => _isToolAlignPixelSet;
            set => Set(ref _isToolAlignPixelSet, value);
        }

        private bool _invertXAxis;
        /// <summary>
        /// 标定 X 轴方向反转：现场轴实际运动方向与软件假设相反时勾选（走位网格沿 X 镜像）。
        /// 典型现象：传统逐行走位时每行呈现"右→中→左"（期望"左→中→右"）。
        /// </summary>
        public bool InvertXAxis
        {
            get => _invertXAxis;
            set => Set(ref _invertXAxis, value);
        }

        private bool _invertYAxis;
        /// <summary>
        /// 标定 Y 轴方向反转：现场 Y 轴实际运动方向与软件假设相反时勾选（走位网格沿 Y 镜像）。
        /// </summary>
        public bool InvertYAxis
        {
            get => _invertYAxis;
            set => Set(ref _invertYAxis, value);
        }

        private bool _isBasePosSet;
        /// <summary>
        /// 基准位置是否已通过"设当前轴位置为基准"或手动输入设置。
        /// 基准坐标可能恰为 0（机台原点），因此不能用 BasePosX==0 判断是否设置过；
        /// 九点采样前必须校验该标志，避免未设基准就采样导致走位出视野、首点提取失败。
        /// </summary>
        public bool IsBasePosSet
        {
            get => _isBasePosSet;
            set => Set(ref _isBasePosSet, value);
        }

        private CalibrationFeatureType _featureType = CalibrationFeatureType.CircleMark;
        /// <summary>标定特征类型：圆形 Mark (提取圆心) / 十字 Mark (形状匹配) / 模板匹配，第二步特征配置中由用户选择</summary>
        public CalibrationFeatureType FeatureType
        {
            get => _featureType;
            set => Set(ref _featureType, value);
        }

        // ================= 模板匹配特征参数（FeatureType=TemplateMatch 时生效） =================
        private string _featureTemplateName;
        /// <summary>使用的全局模板名称（Shape/NCC，模板管理页创建，名称全局唯一）</summary>
        public string FeatureTemplateName
        {
            get => _featureTemplateName;
            set => Set(ref _featureTemplateName, value);
        }

        private double _templateMinScore = 0.6;
        /// <summary>模板匹配最低分数（0~1；生产 ShapeMatch 节点 MinScore 同义）</summary>
        public double TemplateMinScore
        {
            get => _templateMinScore;
            set => Set(ref _templateMinScore, value);
        }

        private double _templateAngleStart = -180;
        /// <summary>模板匹配角度搜索下限（°）</summary>
        public double TemplateAngleStart
        {
            get => _templateAngleStart;
            set => Set(ref _templateAngleStart, value);
        }

        private double _templateAngleEnd = 180;
        /// <summary>模板匹配角度搜索上限（°）</summary>
        public double TemplateAngleEnd
        {
            get => _templateAngleEnd;
            set => Set(ref _templateAngleEnd, value);
        }

        // ================= 吸放式标定几何输入（Type=PickPlaceHandEye） =================
        private double _pickBaseX;
        private bool _isPickBaseSet;
        /// <summary>初始吸取位 X（人工示教：吸嘴在此吸住工件，真空检知确认=基准）。坐标可能恰为 0，用标志位判断是否已设置</summary>
        public double PickBaseX
        {
            get => _pickBaseX;
            set { if (Set(ref _pickBaseX, value)) IsPickBaseSet = true; }
        }

        private double _pickBaseY;
        /// <summary>初始吸取位 Y</summary>
        public double PickBaseY
        {
            get => _pickBaseY;
            set { if (Set(ref _pickBaseY, value)) IsPickBaseSet = true; }
        }

        private double _pickBaseU;
        /// <summary>初始吸取位 U 角（°）</summary>
        public double PickBaseU { get => _pickBaseU; set => Set(ref _pickBaseU, value); }

        /// <summary>吸取位是否已设置（吸取位坐标恰可为 0，不能以 ==0 判断）</summary>
        public bool IsPickBaseSet
        {
            get => _isPickBaseSet;
            set => Set(ref _isPickBaseSet, value);
        }

        private double _photoPoseX;
        private bool _isPhotoPoseSet;
        /// <summary>固定拍照位 X（每次拍照机械臂回到的姿态；等效固定相机成像）</summary>
        public double PhotoPoseX
        {
            get => _photoPoseX;
            set { if (Set(ref _photoPoseX, value)) IsPhotoPoseSet = true; }
        }

        private double _photoPoseY;
        /// <summary>固定拍照位 Y</summary>
        public double PhotoPoseY
        {
            get => _photoPoseY;
            set { if (Set(ref _photoPoseY, value)) IsPhotoPoseSet = true; }
        }

        /// <summary>拍照位是否已设置</summary>
        public bool IsPhotoPoseSet
        {
            get => _isPhotoPoseSet;
            set => Set(ref _isPhotoPoseSet, value);
        }

        private double _photoPoseU;
        /// <summary>固定拍照位 U 角（°）——旋转采样拍照必须与九点拍照同 U，保证成像姿态一致</summary>
        public double PhotoPoseU { get => _photoPoseU; set => Set(ref _photoPoseU, value); }

        private double _photoPoseZ;
        /// <summary>固定拍照位 Z（保证工件在焦；由工件高度/景深决定）</summary>
        public double PhotoPoseZ { get => _photoPoseZ; set => Set(ref _photoPoseZ, value); }

        // ---- 吸放动作参数（Epson Z 槽位 2 为负向下；ZMC 0 号轴语义同向由用户按现场设置） ----
        private int _bindZAxisIndex = 2;
        /// <summary>Z（升降）轴槽位索引：Epson=2；ZMC 双滑台=0（吸放式专用，九点不升降）</summary>
        public int BindZAxisIndex { get => _bindZAxisIndex; set => Set(ref _bindZAxisIndex, value); }

        private double _safeZ = 0;
        /// <summary>安全抬升高度（绝对坐标；吸取/放料之间的移动高度）</summary>
        public double SafeZ { get => _safeZ; set => Set(ref _safeZ, value); }

        private double _pickZ = -75;
        /// <summary>吸取高度（Z 下探到工件表面的绝对坐标，真空检知确认吸住后抬升）</summary>
        public double PickZ { get => _pickZ; set => Set(ref _pickZ, value); }

        private double _placeZ = -75;
        /// <summary>放料高度（Z 下探放开工件的绝对坐标）</summary>
        public double PlaceZ { get => _placeZ; set => Set(ref _placeZ, value); }

        private int _pickVacuumIoIndex = 0;
        /// <summary>吸取真空 IO 通道索引（IIoDevice 通道；Epson 吸嘴1=OUT15 映射通道 0）</summary>
        public int PickVacuumIoIndex { get => _pickVacuumIoIndex; set => Set(ref _pickVacuumIoIndex, value); }

        private int _pickDetectIoIndex = -1;
        /// <summary>真空检知输入通道（IIoDevice ReadDi；-1=不检知）</summary>
        public int PickDetectIoIndex { get => _pickDetectIoIndex; set => Set(ref _pickDetectIoIndex, value); }

        private int _pickVacuumOnDelayMs = 300;
        /// <summary>真空开启后等待吸附稳定时间(ms)</summary>
        public int PickVacuumOnDelayMs { get => _pickVacuumOnDelayMs; set => Set(ref _pickVacuumOnDelayMs, value); }

        private int _pickVacuumOffDelayMs = 150;
        /// <summary>真空关闭后等待工件释放时间(ms)</summary>
        public int PickVacuumOffDelayMs { get => _pickVacuumOffDelayMs; set => Set(ref _pickVacuumOffDelayMs, value); }

        // ================= 吸放式标定输出（旋转采样拟合后回填） =================
        // ⚠ 2026-09-08：ToolEcc 语义 = 旋转采样杆末端 Mark 相对 U 轴回转中心的偏心（纯工具偏心 Ecc）。
        //   吸嘴吸【偏心延伸杆】采样时测得值 = 真吸嘴偏心 + 杆自身偏心 → 不可信；
        //   须换【同心短杆】（杆端 Mark XY = 吸嘴 TCP XY）重采旋转(-45/0/45)才可信。
        //   消费：放料 C = 放料位 − R(PlaceU)·Ecc；吸取 U_go≠U0 时的旋转差分项（勿与 TCO 混用）。
        private double _toolEccWx;
        /// <summary>工具偏心矢量 X（mm，世界系，U=0 参考）：旋转圆心(U 轴回转中心)相对工件特征参考的偏心——业务角度补偿用</summary>
        public double ToolEccWx { get => _toolEccWx; set => Set(ref _toolEccWx, value); }

        private double _toolEccWy;
        /// <summary>工具偏心矢量 Y（mm，世界系）</summary>
        public double ToolEccWy { get => _toolEccWy; set => Set(ref _toolEccWy, value); }

        private double _toolEccAngleDeg;
        /// <summary>工具偏心矢量方向角（°）</summary>
        public double ToolEccAngleDeg { get => _toolEccAngleDeg; set => Set(ref _toolEccAngleDeg, value); }

        private double _toolEccPx;
        /// <summary>工具偏心矢量 X（px，像素系，拍照位视角）</summary>
        public double ToolEccPx { get => _toolEccPx; set => Set(ref _toolEccPx, value); }

        private double _toolEccPy;
        /// <summary>工具偏心矢量 Y（px，像素系，拍照位视角）</summary>
        public double ToolEccPy { get => _toolEccPy; set => Set(ref _toolEccPy, value); }

        // ================= P3 校验台数据（2026-09-05） =================
        //   Samples：本次标定采样点快照（世界命令位 + 像素识别位 + 可靠度）。向导拟合保存时写入；
        //   供校验台"残差反投影体检(A 块)"与现场复验使用。null=旧档案未采集/未落盘 → 校验台提示
        //   "需重新标定后才可反投影"，不影响在线打点验收(B 块，逐点实拍现算)。
        private List<CalibrationSampleModel> _samples;
        public List<CalibrationSampleModel> Samples
        {
            get => _samples;
            set => Set(ref _samples, value);
        }

        //   VerificationRecords：校验台验收历史（最近 N 次），构成发布前质量证据链；
        //   最新一条展示于标定中心行内"最近校验"摘要。
        private List<CalibrationVerificationRecord> _verificationRecords;
        public List<CalibrationVerificationRecord> VerificationRecords
        {
            get => _verificationRecords;
            set => Set(ref _verificationRecords, value);
        }

        /// <summary>最近一次校验记录（null=尚无校验）——方法形态避免被持久化框架当作属性序列化</summary>
        public CalibrationVerificationRecord GetLatestVerification()
        {
            if (_verificationRecords == null || _verificationRecords.Count == 0)
            {
                return null;
            }
            return _verificationRecords[_verificationRecords.Count - 1];
        }

        // ================= P4 对针补偿输出（2026-09-05；TCO 语义 2026-09-08 定稿） =================
        //   语义（EIH 间接对针，EyeInHandIndirect）：TCO = ToolOffsetWx/Wy = H(p_tip) − R_n
        //     = R(U0)·e − c ——「吸嘴工具偏心旋转量 − 相机安装偏移」的合成量（U0=对针示教时 U 角）。
        //   消费（与校验台同一口径，注意是【减】）：
        //     吸取回转中心 C = w − TCO + [R(U0)−R(U_go)]·Ecc（w=视觉输出命令位域；U_go==U0 时 C = w − TCO）
        //     发布链写入工位配置 Nozzle{N}TcoX/Y（独立于 Ecc），勿再把 TCO 当 Ecc 发布。
        //   ⚠ 与 HasToolOffset 区分：HasToolOffset=向导"旋转末端工具"开关(旋转标定类型自动开)，
        //   勿复用为对针标志。P4 标志 = IsToolOffsetCalibrated（对针完成才有值）。
        private double _toolOffsetWx;
        /// <summary>对针平移补偿 X（mm，机械域）</summary>
        public double ToolOffsetWx { get => _toolOffsetWx; set => Set(ref _toolOffsetWx, value); }

        private double _toolOffsetWy;
        /// <summary>对针平移补偿 Y（mm，机械域）</summary>
        public double ToolOffsetWy { get => _toolOffsetWy; set => Set(ref _toolOffsetWy, value); }

        private bool _isToolOffsetCalibrated;
        /// <summary>是否已完成对针补偿（写入 ToolOffsetWx/Wy）</summary>
        public bool IsToolOffsetCalibrated
        {
            get => _isToolOffsetCalibrated;
            set => Set(ref _isToolOffsetCalibrated, value);
        }

        private DateTime? _toolOffsetCalibTime;
        /// <summary>最近对针时间</summary>
        public DateTime? ToolOffsetCalibTime
        {
            get => _toolOffsetCalibTime;
            set => Set(ref _toolOffsetCalibTime, value);
        }

        private ToolOffsetMethod _toolOffsetMethod = ToolOffsetMethod.LegacyUnknown;
        /// <summary>
        /// 对针采集方式（间接对针 EyeInHandIndirect / 图像对针 EyeToHandImage / 旧档案未知 LegacyUnknown）。
        /// 迁移器规则：EyeInHand 布局下仅 EyeInHandIndirect 结果有效；LegacyUnknown/图像法 → 过期（历史残留）。
        /// </summary>
        public ToolOffsetMethod ToolOffsetMethod
        {
            get => _toolOffsetMethod;
            set => Set(ref _toolOffsetMethod, value);
        }

        /// <summary>写入对针结果（自动置完成标志与时间）。method 缺省按 EyeToHand 图像对针（既有调用语义）。</summary>
        public void ApplyToolOffset(double wx, double wy,
            ToolOffsetMethod method = ToolOffsetMethod.EyeToHandImage)
        {
            ToolOffsetWx = wx;
            ToolOffsetWy = wy;
            ToolOffsetMethod = method;
            IsToolOffsetCalibrated = true;
            ToolOffsetCalibTime = DateTime.Now;
        }

        /// <summary>
        /// 解析"工具尖压住工件特征"的 Z 高度 —— 校验台/点哪吸哪【低速到位】的下压目标（2026-09-11）。
        /// 之所以要兜底链：NozzleAlignZ 只在 EyeInHand 间接对针（向导内记 R_n）才被写入；
        /// ETH（固定相机）工位的对针走独立【对针专窗】，历史档案里该字段恒为 null
        /// → 到位只动 XY、Z 悬在高位，吸嘴够不到工件面，开真空也吸不住。
        /// 优先级：① NozzleAlignZ 对针压住高度 R_nZ（最精确） → ② BasePosZ 标定基准点 Z
        ///        （设基准时"吸嘴尖压住标定工件特征"读的 Z） → ③ CalibZ 标定面高度（退化口径）。
        /// </summary>
        /// <param name="z">命中的 Z 高度（mm）</param>
        /// <param name="source">命中口径的人话描述（写日志/提示用）</param>
        /// <returns>三个口径都没有值时为 false（调用方应提示 JOG 手动下压）</returns>
        public bool TryResolvePressDownZ(out double z, out string source)
        {
            if (NozzleAlignZ.HasValue)
            {
                z = NozzleAlignZ.Value;
                source = "对针压住高度 R_nZ";
                return true;
            }
            if (BasePosZ.HasValue)
            {
                z = BasePosZ.Value;
                source = "标定基准点 Z（设基准时工具尖压住特征的高度）";
                return true;
            }
            if (CalibZ.HasValue)
            {
                z = CalibZ.Value;
                source = "标定面高度 CalibZ（无对针/基准 Z，退化取值）";
                return true;
            }
            z = 0.0;
            source = null;
            return false;
        }
    }




    /// <summary>
    /// 标定点数据模型
    /// </summary>
    public class CalibrationPointModel : ViewModelBase
    {
        private int _index;
        public int Index
        {
            get => _index;
            set => Set(ref _index, value);
        }

        private double _pixelX;
        public double PixelX
        {
            get => _pixelX;
            set => Set(ref _pixelX, value);
        }

        private double _pixelY;
        public double PixelY
        {
            get => _pixelY;
            set => Set(ref _pixelY, value);
        }

        private double _worldX;
        public double WorldX
        {
            get => _worldX;
            set => Set(ref _worldX, value);
        }

        private double _worldY;
        public double WorldY
        {
            get => _worldY;
            set => Set(ref _worldY, value);
        }

        private bool _isCaptured;
        /// <summary>
        /// 该点是否已成功采集回填。显式标志替代 (0,0) 哨兵判断：
        /// 真实特征点像素坐标可能恰为 0（图像左上角），哨兵会把已采集点误判为未采集，
        /// 导致 FirstOrDefault 永远选中同一个点、坐标反复不回填。
        /// </summary>
        public bool IsCaptured
        {
            get => _isCaptured;
            set => Set(ref _isCaptured, value);
        }

        private bool _isReliable = true;
        /// <summary>
        /// 识别可信度：采集时若识别位置与预测偏差过大（疑似取错特征点）会被标记为不可靠。
        /// 不可靠点不参与后续点位的外推预测，阻断"一个点取错导致后续预测连环偏移"的误差累积；
        /// 该点数据仍保留参与拟合（拟合 RMS 会反映其偏差），可重新采样覆盖。
        /// </summary>
        public bool IsReliable
        {
            get => _isReliable;
            set => Set(ref _isReliable, value);
        }

        private double _matchScore;
        /// <summary>该点采集时的匹配质量分数（0~100，越高识别越可靠；由底层视觉服务识别时回填）。</summary>
        public double MatchScore
        {
            get => _matchScore;
            set => Set(ref _matchScore, value);
        }
    }

    /// <summary>
    /// R轴旋转拟合点模型
    /// </summary>
    public class RotationPointModel : ViewModelBase
    {
        private double _angleDeg;
        public double AngleDeg
        {
            get => _angleDeg;
            set => Set(ref _angleDeg, value);
        }

        private double _pixelX;
        public double PixelX
        {
            get => _pixelX;
            set => Set(ref _pixelX, value);
        }

        private double _pixelY;
        public double PixelY
        {
            get => _pixelY;
            set => Set(ref _pixelY, value);
        }

        // 2026-09-08 新语义：旋转采样必须同时记录【该点采集瞬间的机械手命令位】，
        // 才能把"H 域圆心"换算成与机位无关的不变量 K = ToolCenterW − P_f。
        // 标准流程是"XY 固定、只转 U"，故各点机位应一致；分散度>1mm 说明采样期间机器被移动过，K 不可信。

        private double _baseX;
        /// <summary>该旋转采样点采集时的机械手命令位 X（mm）</summary>
        public double BaseX { get => _baseX; set => Set(ref _baseX, value); }

        private double _baseY;
        /// <summary>该旋转采样点采集时的机械手命令位 Y（mm）</summary>
        public double BaseY { get => _baseY; set => Set(ref _baseY, value); }

        private double _readUDeg;
        /// <summary>
        /// ★2026-09-15：该点采集瞬间【实读】的 U 角（度），由 StampRotationMotion 统一回填。
        /// 与表内相对角相比：表内 AngleDeg 是"相对基准角 U_ref 的增量"，本字段是"机械手自报的绝对 U"。
        /// 用途：|实读U − (U_ref+AngleDeg)| 就是"指令角 vs 实际到位角"的偏差——偏心结算按**实**转角才准，
        /// 该差值偏大（&gt;0.5°）说明 U 到位精度不足或回读滞后，会直接污染 b 的方向。旧档案为 0（当年未记）。
        /// </summary>
        public double ReadUDeg { get => _readUDeg; set => Set(ref _readUDeg, value); }

        private bool _isCaptured;
        /// <summary>是否已采集回填（同 CalibrationPointModel，替代 0 哨兵判断）</summary>
        public bool IsCaptured
        {
            get => _isCaptured;
            set => Set(ref _isCaptured, value);
        }

        private double _matchScore;
        /// <summary>该旋转采样点采集时的匹配质量分数（0~100，越高识别越可靠）。</summary>
        public double MatchScore
        {
            get => _matchScore;
            set => Set(ref _matchScore, value);
        }
    }

    /// <summary>
    /// 旋转采样点证据（2026-09-15）：把「角度 → 像素 → 采样瞬间机位 → 匹配分」全部落盘。
    /// 用途（重标后离线复核，全都不必再上机）：
    ///   · 弧覆盖度：把各 AngleDeg 按圆上点算"360° − 最大空隙"；&lt;90° ⇒ 圆心/偏心方向不可信。
    ///   · 机位固定性：各点 BaseX/BaseY 的分散度必须 &lt;1mm（标准流程=XY 固定、只转 U）；
    ///     若明显分散，说明采样期间机器人动过 XY —— 此时 O/b 的"圆心=回转轴−b"模型要按动过的情形重推。
    ///   · 半径互校：|M·e_px|（=ToolEccW 的模）应与定圆半径一致（同一物理量的两种量法），
    ///     差得多 ⇒ 采样点里有脏点（低分乱匹配）或弧段太短，b 的方向不可信。
    ///   · 逐点残差：由像素反算该点到圆心的距离与半径之差，定位是哪个角度采坏了。
    /// </summary>
    public class RotationSampleEvidence
    {
        public double AngleDeg { get; set; }
        public double PixelX { get; set; }
        public double PixelY { get; set; }
        /// <summary>该点采集瞬间的机械手命令位 X（mm，实读回填；旧流程未填时为 0）</summary>
        public double BaseX { get; set; }
        /// <summary>该点采集瞬间的机械手命令位 Y（mm，实读回填；旧流程未填时为 0）</summary>
        public double BaseY { get; set; }
        /// <summary>该点采集瞬间实读的 U 角（度），来自 RotationPointModel.ReadUDeg（单一来源）</summary>
        public double ReadUDeg { get; set; }
        /// <summary>匹配质量分（0~100）</summary>
        public double MatchScore { get; set; }
        public bool IsCaptured { get; set; }
        /// <summary>该点到拟合圆心的距离（px，拟合后回填）</summary>
        public double RadiusPx { get; set; }
        /// <summary>该点半径相对定圆半径的偏差（px，拟合后回填；用于定位坏点）</summary>
        public double ResidualPx { get; set; }
    }

    /// <summary>
    /// 标定样本模型 - 包含一张标定图像及其标定点集合
    /// </summary>
    public class CalibrationSampleModel : ViewModelBase
    {
        private string _imagePath;
        /// <summary>标定图像路径</summary>
        public string ImagePath
        {
            get => _imagePath;
            set => Set(ref _imagePath, value);
        }

        private byte[] _thumbnailData;
        /// <summary>缩略图二进制数据（支持序列化存储）</summary>
        public byte[] ThumbnailData
        {
            get => _thumbnailData;
            set => Set(ref _thumbnailData, value);
        }

        private double[] _px;
        /// <summary>像素 X 坐标数组</summary>
        public double[] Px
        {
            get => _px;
            set => Set(ref _px, value);
        }

        private double[] _py;
        /// <summary>像素 Y 坐标数组</summary>
        public double[] Py
        {
            get => _py;
            set => Set(ref _py, value);
        }

        private bool _accepted;
        /// <summary>是否通过标定验收</summary>
        public bool Accepted
        {
            get => _accepted;
            set => Set(ref _accepted, value);
        }

        /// <summary>标定点详细列表</summary>
        public ObservableCollection<CalibrationPointModel> Points { get; set; } =
            new ObservableCollection<CalibrationPointModel>();
    }
}