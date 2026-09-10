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
    /// 标定类型枚举
    /// </summary>
    public enum CalibrationType
    {
        NinePointHandEye,      // 1. 标准九点手眼标定 (仅 X/Y 平移矩阵)
        Checkerboard2D,        // 3. 2D 棋盘格/畸变矫正标定
        HandEyeWithRotation,   // 多点手眼 + 旋转中心拟合
        CameraLensDistortion,  // 畸变/内参标定 (棋盘格/圆点阵列)
        PixelScale,            // 像素当量标定
        PickPlaceHandEye       // 吸放式标定（行业标准：机械臂吸住工件 → 放到网格点 → 回固定拍照位拍照；
                               // 旋转段吸住转 U → 放料 → 回拍照位 → 圆拟合求旋转中心+工具偏心矢量）
    }
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
        public string CameraId { get; set; } = "Cam_01";
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
        /// <summary>轴末端是否有吸嘴/夹具旋转偏移 (TCP)</summary>
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

        private CalibrationType _type = CalibrationType.NinePointHandEye;
        public CalibrationType Type { get => _type; set => Set(ref _type, value); }

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
    }




    /// <summary>
    /// 标定数据存储模型
    /// </summary>
    public class CalibrationProfileModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "默认标定方案";
        public CalibrationType Type { get; set; } = CalibrationType.NinePointHandEye;
        public string BoundDeviceOrStation { get; set; } = "Cam1 / Station1";

        /// <summary>重投影均方根误差 (RMS) mm</summary>
        public double RmsError { get; set; }

        /// <summary>标定矩阵存储路径 (.tup)</summary>
        public string MatrixFilePath { get; set; }

        /// <summary>最后标定时间</summary>
        public DateTime LastCalibratedTime { get; set; }

        /// <summary>是否有效标定</summary>
        public bool IsCalibrated => !string.IsNullOrEmpty(MatrixFilePath) && System.IO.File.Exists(MatrixFilePath);
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