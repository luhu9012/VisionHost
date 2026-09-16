using System;
using System.Collections.Generic;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 失败记录。★ 铁律：运动被拒时必须保留控制器的<b>原始错误码</b>（4001/4007/2997…），
    /// 不允许翻译成"操作失败"之类的模糊文案。
    /// </summary>
    public sealed class CalibError
    {
        public CalibFailureKind Kind;
        public string Message;

        /// <summary>控制器 / 设备返回的原始错误码，无则为 null。</summary>
        public int? RawCode;

        /// <summary>关联的采样点序号（从 1 开始），无则为 0。</summary>
        public int SampleIndex;

        public DateTime Utc;

        public static CalibError Create(CalibFailureKind kind, string message)
        {
            return new CalibError { Kind = kind, Message = message, Utc = DateTime.UtcNow };
        }

        public CalibError WithRawCode(int rawCode)
        {
            RawCode = rawCode;
            return this;
        }

        public CalibError WithSample(int index)
        {
            SampleIndex = index;
            return this;
        }

        public override string ToString()
        {
            string code = RawCode.HasValue
                ? string.Format(CultureInfo.InvariantCulture, " [raw={0}]", RawCode.Value)
                : string.Empty;
            string idx = SampleIndex > 0
                ? string.Format(CultureInfo.InvariantCulture, " (点 #{0})", SampleIndex)
                : string.Empty;
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}: {3}", Kind, code, idx, Message);
        }
    }

    /// <summary>
    /// 物理拓扑快照。★ 这是"降低心智模型"的数据基础：
    /// 向导第 2 步靠它<b>回显</b>场景（而不是问用户参数），第 3 步靠它决定采样策略。
    /// </summary>
    public sealed class CalibTopology
    {
        public string StationCode;
        public string StationName;

        /// <summary>相机安装方式（决定是否叠加 O 补偿）。</summary>
        public CameraMountKind CameraMount = CameraMountKind.Unknown;

        /// <summary>相机命名空间槽位键（如 Cam_Top / Cam_Bottom）。</summary>
        public string CameraSlotKey;

        /// <summary>当前手系（Epson 必须显式下发）。</summary>
        public Handedness Hand = Handedness.Unknown;

        /// <summary>工具头数量（单头 / 双头）。</summary>
        public int ToolHeadCount = 1;

        /// <summary>旋转轴名称（一般 U）。</summary>
        public string RotateAxisName = "U";

        /// <summary>相机是否随 Z 移动。null = 未知（按最保守 true 处理）。</summary>
        public bool? CameraMovesWithZ;

        /// <summary>
        /// ★ 像面是否随法兰角 U 一起<b>滚转</b>（相机绕自身光轴转动）。
        ///
        /// 为什么它必须是一等公民：这条物理事实决定了「偏心链要不要按角度归一」——
        ///   · <b>true</b>（相机装在法兰上：眼在手）：转 U 时画面与相机一起滚，
        ///     于是"吸嘴尖压住基准特征"这个状态相对法兰是<b>刚性的</b> ⇒ 观测像素与角度无关
        ///     ⇒ O − H(p_k) <b>本来就是基准角口径</b>，再乘一次 R(U0−U) 就是双重旋转
        ///     （会把 |e| 按 (1+2cosΔ)/3 缩水，并伪造出"对针不稳"的方向偏差告警）。
        ///   · <b>false</b>（固定相机 / 非随动安装）：O − H(p_k) 带着 R(ΔU) ⇒ 必须转回 U0。
        ///
        /// 默认 true —— 与眼在手（本工具三条链的主场景）的物理一致；
        /// 固定相机必须显式置 false（相机根本不动，谈不上"随法兰滚转"）。
        /// </summary>
        public bool CameraRollsWithFlange = true;

        /// <summary>工作 Z（mm）。</summary>
        public double WorkZ;

        /// <summary>安全抬升 Z（mm）。</summary>
        public double SafeZ;

        /// <summary>标定基准角 U0（绝对角，度）。</summary>
        public double CalibU0;

        /// <summary>采样九点的步长（mm），X/Y 可不同。</summary>
        public double StepX = 10.0;
        public double StepY = 10.0;

        /// <summary>标定基准位（九点网格中心）。</summary>
        public Vec2 BasePosXY;

        /// <summary>
        /// ★ 基准位是否<b>已确知</b>。
        /// 为什么必须有这个标志：<see cref="Vec2"/> 是结构体，无法用 null 表达"没设过"，
        /// 而恰好落在原点的合法基准位与"从未设置"在原子上不可区分。
        /// 少了它，规划器就只能靠 (0,0) 猜 —— 那是"看着像通过了、其实拿了个假基准"的经典来源。
        /// </summary>
        public bool BasePosKnown;

        /// <summary>|XY| 行程上限（mm）。仅用于规划期的外围预判；权威仍在控制器侧。</summary>
        public double MaxRadiusXy = 2000.0;

        /// <summary>标定基准位锁定的 Z / U（与主项目 SetCurrentPositionAsBase 一致）。</summary>
        public double? BasePosZ;
        public double? BasePosU;

        /// <summary>软限位（矩形，仅用于外围校验；权威在控制器侧）。</summary>
        public double? XMin, XMax, YMin, YMax, ZMin, ZMax;

        /// <summary>是否已确认（向导第 2 步用户点"对"）。</summary>
        public bool Confirmed;

        /// <summary>
        /// 复制一份<b>工作副本</b>。
        ///
        /// ★★ 为什么必须有它：第 2 步允许用户改前置信息（相机装哪、基准位、步长、手系…）。
        ///   而 <see cref="IVisualCalibEnvironment.ReadTopology"/> 返回的是环境内部<b>那一个实例</b>
        ///   （实测如此，不是拷贝）⇒ 直接改它的话，用户**只是看了看、还没点确认**
        ///   改动就已经生效了；中途放弃，环境就被悄悄改脏了。
        ///   ⇒ 改动一律落在副本上，用户点"确认"时才整体写回。
        ///
        /// ★ 逐字段复制而不是"挑几个重要的"：漏一个字段 = 确认时静默丢一项设置，
        ///   而这种丢失在界面上一丁点征兆都没有（值还在，只是变成默认值了）。
        /// </summary>
        public CalibTopology Clone()
        {
            return new CalibTopology
            {
                StationCode = StationCode,
                StationName = StationName,
                CameraMount = CameraMount,
                CameraSlotKey = CameraSlotKey,
                Hand = Hand,
                ToolHeadCount = ToolHeadCount,
                RotateAxisName = RotateAxisName,
                CameraMovesWithZ = CameraMovesWithZ,
                CameraRollsWithFlange = CameraRollsWithFlange,
                WorkZ = WorkZ,
                SafeZ = SafeZ,
                CalibU0 = CalibU0,
                StepX = StepX,
                StepY = StepY,
                BasePosXY = BasePosXY,
                BasePosKnown = BasePosKnown,
                MaxRadiusXy = MaxRadiusXy,
                BasePosZ = BasePosZ,
                BasePosU = BasePosU,
                XMin = XMin,
                XMax = XMax,
                YMin = YMin,
                YMax = YMax,
                ZMin = ZMin,
                ZMax = ZMax,
                Confirmed = Confirmed
            };
        }
    }

    /// <summary>单个采样点的完整记录（含留帧路径，供复盘）。</summary>
    public sealed class CalibSample
    {
        /// <summary>序号，从 1 开始。</summary>
        public int Index;

        public CalibSampleState State = CalibSampleState.Pending;

        /// <summary>规划目标位（世界坐标）。</summary>
        public Vec2 PlanXy;
        public double PlanZ;
        public double PlanU;

        /// <summary>
        /// ★ 真值一律记<b>控制器反馈位</b>（GetFeedbackPosition），不是下发值。
        /// 下发值与反馈位在 PTP/CP 混用时会不一致，用下发值等于把伺服跟随误差算进标定误差里。
        /// </summary>
        public Vec2 FeedbackXy;
        public double FeedbackU;

        /// <summary>提取到的像素坐标（图像坐标系）。</summary>
        public Vec2 Pixel;
        public bool HasPixel;

        /// <summary>特征质量分 [0,1]。低分点应被标记而非静默丢弃。</summary>
        public double Quality;

        /// <summary>提取到的候选数量（>1 说明有伪特征，需人工关注）。</summary>
        public int CandidateCount;

        /// <summary>留帧文件路径（可为空）。</summary>
        public string FramePath;

        /// <summary>本次采样耗时（ms），用于定位"卡哪了"。</summary>
        public long ElapsedMs;

        public CalibError Error;

        /// <summary>是否参与解算（失败/人工剔除的点不参与）。</summary>
        public bool IsUsable
        {
            get
            {
                return State == CalibSampleState.Ok && HasPixel && Pixel.IsFinite;
            }
        }
    }

    /// <summary>采样集。</summary>
    public sealed class CalibSampleSet
    {
        public readonly List<CalibSample> Samples = new List<CalibSample>();

        public int UsableCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Samples.Count; i++)
                {
                    if (Samples[i].IsUsable)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        /// <summary>剔除第 index 个点（人工删点，不改变后续索引语义）。</summary>
        public void Exclude(int index)
        {
            for (int i = 0; i < Samples.Count; i++)
            {
                if (Samples[i].Index == index)
                {
                    Samples[i].State = CalibSampleState.Rejected;
                    return;
                }
            }
        }
    }

    /// <summary>九点（H）解算结果。</summary>
    public sealed class NinePointResult
    {
        public HomMat2D H;

        /// <summary>各点残差（世界坐标系，mm）。</summary>
        public double[] ResidualsMm;

        public double RmsMm;

        /// <summary>最近角点半径（= 中心半径 − 步长×√2 的实测值，内圈第一杀手）。</summary>
        public double NearestCornerRadiusMm;

        public bool Success;
        public CalibError Error;
    }

    /// <summary>
    /// 一个旋转采样点在"映射域"里的位置（供可视化与诊断）。
    /// ★ 放在 Domain 而不是 Algorithm：它是<b>纯数据</b>，而且视图模型要用它把
    ///   "各角度下的映射点 + 拟合圆 + 圆心"画出来（设计文档 §10.4 的判读要求）。
    /// </summary>
    public sealed class RotationSamplePoint
    {
        public int Index;

        /// <summary>观测像素。</summary>
        public Vec2 Pixel;

        /// <summary>★ 映射域坐标 = H(像素)。定圆在这个域里做（像素域会把圆变成椭圆）。</summary>
        public Vec2 Mapped;

        /// <summary>该次采样的绝对角度 U（度）。</summary>
        public double AbsoluteU;

        /// <summary>相对基准角的增量（度）。★ 表里的 −45/0/+45 是<b>相对增量</b>，不是绝对角。</summary>
        public double RelativeU;

        /// <summary>到拟合圆心的距离（mm）。</summary>
        public double DistanceToCenterMm;

        /// <summary>残差 = 到圆心距离 − 拟合半径（mm）。散布大 ⇒ 数据有问题或存在奇异。</summary>
        public double ResidualMm;

        /// <summary>该点距机器人原点的半径（mm）。仅诊断。</summary>
        public double OriginRadiusMm;
    }

    /// <summary>旋转中心（O）解算结果。★ 半径丢弃：只取圆心，取半径会被延伸杆/偏心污染。</summary>
    public sealed class RotationCenterResult
    {
        /// <summary>旋转中心（世界坐标）。</summary>
        public Vec2 Center;

        /// <summary>各旋转采样点拟合后的残差（mm）。</summary>
        public double[] ResidualsMm;

        public double ResidualMaxMm;

        /// <summary>残差 RMS（mm）。</summary>
        public double RmsMm;

        /// <summary>拟合圆半径（仅用于诊断，<b>不得</b>写入产物作为几何量）。</summary>
        public double FittedRadiusMm;

        /// <summary>采样角度序列（相对基准角的增量）。</summary>
        public double[] AnglesDeg;

        /// <summary>基准角（绝对角，度）。★ 只锁一次 = 旋转阶段首次采样读到的 U。</summary>
        public double RefU0Deg;

        /// <summary>角度覆盖跨度（度）。★ 用 360−最大空隙算，不是 max−min。</summary>
        public double AngularSpanDeg;

        public int MappedPointCount;

        /// <summary>是否在映射域定圆（恒 true；像素域定圆是错的做法，见 RotationCircleSolver 注释）。</summary>
        public bool FittedAtMapped;

        /// <summary>对照用：在<b>像素域</b>直接定圆得到的圆心（错误做法）。</summary>
        public Vec2 PixelDomainCenter;

        /// <summary>把像素域圆心经 H 映射到世界后的位置。</summary>
        public Vec2 PixelDomainCenterInWorld;

        /// <summary>
        /// ★ 像素域圆心与映射域圆心之差（mm）。这个数字就是"不能在像素域定圆"的现场证据：
        ///   仿射把圆变成椭圆，椭圆心 ≠ 圆心的映射。
        /// </summary>
        public double PixelVsMappedCenterMm;

        /// <summary>逐点明细（可视化用）。</summary>
        public readonly List<RotationSamplePoint> SamplePoints = new List<RotationSamplePoint>();

        public bool Success;
        public CalibError Error;

        public string Describe()
        {
            if (!Success)
            {
                return "旋转中心解算失败：" + (Error == null ? "未知原因" : Error.ToString());
            }

            return string.Format(CultureInfo.InvariantCulture,
                "O = ({0:F4}, {1:F4})，映射点 {2} 个，跨度 {3:F1}°，残差 RMS {4:F4} / 最大 {5:F4} mm"
                + "（拟合半径 {6:F4} mm 仅诊断，不写入产物）",
                Center.X, Center.Y, MappedPointCount, AngularSpanDeg, RmsMm, ResidualMaxMm, FittedRadiusMm);
        }
    }

    /// <summary>吸嘴偏心（e）解算结果：e = O − H(p_tip)。</summary>
    public sealed class ToolOffsetResult
    {
        /// <summary>吸嘴偏心 e（世界坐标，mm）。</summary>
        public Vec2 Ecc;

        /// <summary>工具尖像素位置（H 映射域）。</summary>
        public Vec2 TipPixel;

        /// <summary>工具尖世界位置 H(p_tip)。</summary>
        public Vec2 TipWorld;

        /// <summary>旋转中心 O（世界）。</summary>
        public Vec2 RotCenterWorld;

        /// <summary>使用的标定方法。★ LegacyUnknown 在 EyeToHand 下视为过期。</summary>
        public ToolOffsetMethod Method = ToolOffsetMethod.LegacyUnknown;

        /// <summary>工具尖采样点数（间接对针法为多次对针的平均/拟合点）。</summary>
        public int TipSampleCount;

        /// <summary>工具尖重复定位离散度（mm）。大 ⇒ 对针没对好。</summary>
        public double TipScatterMm;

        /// <summary>|e|（mm）—— 界面直接显示"吸嘴偏了多少毫米"。</summary>
        public double EccMagnitudeMm;

        /// <summary>e 的方向（度），相对世界 X 轴。</summary>
        public double EccDirectionDeg;

        public bool Success;
        public CalibError Error;

        public string Describe()
        {
            if (!Success)
            {
                return "吸嘴偏心解算失败：" + (Error == null ? "未知原因" : Error.ToString());
            }

            return string.Format(CultureInfo.InvariantCulture,
                "e = ({0:F4}, {1:F4})，偏心量 {2:F3} mm，方向 {3:F1}°；工具尖 H(p_tip) = ({4:F4}, {5:F4})，O = ({6:F4}, {7:F4})",
                Ecc.X, Ecc.Y, EccMagnitudeMm, EccDirectionDeg,
                TipWorld.X, TipWorld.Y, RotCenterWorld.X, RotCenterWorld.Y);
        }
    }

    /// <summary>相机内参 + 畸变结果（本工具新增能力）。</summary>
    public sealed class IntrinsicsResult
    {
        /// <summary>焦距 [fx, fy]（像素）。</summary>
        public double[] FocalLengthPx;

        /// <summary>主点 [cx, cy]（像素）。</summary>
        public double[] PrincipalPointPx;

        /// <summary>
        /// 畸变系数。<b>口径 = HALCON「除法模型」的 κ，量纲 1/m²</b>（乘的是像面公制半径）。
        /// ★ 不是常见的归一化 [k1,k2,k3,p1,p2] —— 两条口径差一个 f²，混用不会报错、只会算歪。
        /// 归一化值见 <see cref="DistortionNormalizedK1"/>。
        /// </summary>
        public double[] Distortion;

        /// <summary>归一化口径的 k1（= κ·f²），仅用于人看与旧资料对照。</summary>
        public double DistortionNormalizedK1;

        /// <summary>图像尺寸 [w, h]。</summary>
        public int[] ImageSize;

        /// <summary>各姿态的重投影误差（像素），用于"倾斜姿态是否足够"的判读。</summary>
        public double[] ReprojectionErrorsPx;

        public double MeanReprojectionErrorPx;

        /// <summary>实际用于标定的姿态数。</summary>
        public int UsedPoseCount;

        /// <summary>姿态覆盖度告警（例如"缺倾斜姿态，焦距不可解"）。</summary>
        public string PoseCoverageWarning;

        public bool Success;
        public CalibError Error;
    }
}
