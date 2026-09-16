using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 几何健康诊断。★ 口径与主项目 <c>CalibrationService.BuildHomMatHealthReport</c>
    /// <b>逐条对等</b>（2026-09-14 逐行核对过源码），再额外提供 LOO 留一法。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 血汗结论（改之前先读完）
    /// ══════════════════════════════════════════════════════════════════
    /// ① <b>唯一硬拦截是形状失真 σ1/σ2</b>：|σ1/σ2 − 1| &gt; 0.03 才是 ⛔。
    ///    两轴当量差只作提示（scaleRatio），不拦。
    /// ② <b>det &lt; 0 不等于坏矩阵</b>：EyeInHand（相机随动）下机械 X+ 使静止特征相对视场左移，
    ///    H 首列系数必然反号 → det 恒负，这是<b>固有物理</b>。只有 EyeToHand（固定相机）
    ///    的镜像才保留阻断。真机已发布的 .tup 实测 det = −0.0405，就是这一条。
    /// ③ 两轴当量必须用<b>列向量范数</b>：旧写法拿 |h11| vs |h22| 比对，
    ///    旋转 ~45° 时两者都趋近 0、判据退化；而在本工位 151° 的图像旋转下它又会
    ///    <b>假报警</b>——真机矩阵 |h11|=0.00536 / |h22|=0.00888 差 39.6%，
    ///    而列范数 sCol=0.19880 / sRow=0.20351 只差 2.31%。
    /// ④ <b>残差小 ≠ 落点准</b>：落点是差分 H(p_tip) − H(u)，形状失真会按差分距离线性放大
    ///    （失真 22.7% × 跨 80 mm ≈ 19 mm），而九点 RMS 可能只有 0.5 mm —— RMS 根本发现不了。
    ///    所以本类用 LOO 留一法补一个更诚实的残差估计。
    /// ⑤ <b>剪切同样不能用轴对齐分母</b>：|h12|/|h11| 的分母 h11/h22 一旋转就趋近 0，
    ///    真机矩阵（151°）上报 X=3792.8% / Y=2238.5%，而<b>列夹角法只有 1.67%</b> ——
    ///    即"每次标定都弹剪切告警"。与 ③ 是同一个错、同一个修法（改用旋转不变量）。
    /// </summary>
    public sealed class CalibDiagnostics
    {
        // ── ① 两轴像素当量（列范数；旋转不变）──
        public double ColumnNormFirst;
        public double ColumnNormSecond;

        /// <summary>两轴当量相对差（%）。提示性，不拦。</summary>
        public double AxisScaleDeviationPct;

        // ── 形状：奇异值比（★ 唯一的硬拦判据）──
        public double Sigma1;
        public double Sigma2;

        /// <summary>σ1/σ2，恒 ≥ 1，理想 1.000。</summary>
        public double SigmaRatio;

        /// <summary>(σ1/σ2 − 1) × 100，即"失真量 %"。</summary>
        public double AnisotropyPct;

        /// <summary>形状是否非法（|σ1/σ2 − 1| &gt; 阈值，默认 0.03）。</summary>
        public bool BadShape;

        // ── ② 正交性 / 剪切（提示性）──
        /// <summary>
        /// 两列（像素 X 轴、Y 轴）在像里的夹角（度），理想 90°。
        /// ★ 用<b>列向量夹角</b>而不是 |h12|/|h11|：
        ///   后者把分母写成 h11/h22，图像一旋转它们就趋近 0，比值直接爆掉 ——
        ///   真机矩阵（151°）上它报 X=3792.8% / Y=2238.5%，而真实剪切只有 1.67%，
        ///   于是<b>每一次标定都会弹出剪切告警</b>。与 |h11| vs |h22| 是同一个错。
        /// </summary>
        public double ColumnAngleDeg;

        /// <summary>
        /// 剪切量 = |cos(列夹角)|，理想 0（正交）。★ <b>对旋转不变</b>，可直接当判据。
        /// </summary>
        public double ShearRatio;

        /// <summary>剪切是否偏大（|cos| &gt; 阈值，默认 0.05 ≈ 偏离正交 2.87°）。</summary>
        public bool ShearSuspicious;

        // ── ③ 行列式 / 镜像 ──
        public double DetA;

        /// <summary>det(A) &lt; 0。</summary>
        public bool MirrorDetected;

        /// <summary>镜像是否应当阻断（★ 仅 EyeToHand 固定相机为 true）。</summary>
        public bool MirrorIsBlocker;

        public CameraMountKind CameraMount = CameraMountKind.Unknown;

        // ── ④ 网格重建：行/列边长的一致性（CV）──
        public double GridRowEdgeMeanMm;
        public double GridRowEdgeCvPct;
        public double GridColEdgeMeanMm;
        public double GridColEdgeCvPct;
        public bool GridIrregular;

        // ── ⑤ 网格行/列夹角 ──
        public double GridAngleDeg;
        public bool GridAngleSuspicious;

        // ── 残差 ──
        /// <summary>自拟合 RMS（mm）。会系统性偏乐观。</summary>
        public double RmsMm;

        /// <summary>★ LOO 留一法 RMS（mm）。本工具额外提供，比自拟合更接近真实落点能力。</summary>
        public double LooRmsMm;

        public double[] PerPointResidualMm;
        public double[] PerPointRadiusMm;

        public int ExcludedCount;

        /// <summary>最近角点半径（mm）= 中心半径 − 步长×√2（内圈第一杀手）。</summary>
        public double NearestCornerRadiusMm;

        // ── ⑥ 反投影（★ 判据优先级高于任何 RMS 数字）──
        /// <summary>像素域重投影残差 RMS（px）。</summary>
        public double ReprojectionRmsPx;

        /// <summary>像素域重投影残差最大值（px）。</summary>
        public double ReprojectionMaxPx;

        /// <summary>世界域重投影残差 RMS（mm）。</summary>
        public double ReprojectionRmsMm;

        /// <summary>是否全部点都落在目视重合容差内。</summary>
        public bool ReprojectionWithinTolerance;

        /// <summary>平均偏置（px）。非零即"系统性往一个方向偏"。</summary>
        public double ReprojectionBiasPx;

        /// <summary>残差对半径的线性斜率（mm/mm）。正值 ⇒ "越往外偏得越多"。</summary>
        public double ReprojectionRadialTrendMmPerMm;

        /// <summary>反投影人话判读（多行）。</summary>
        public string ReprojectionVerdict;

        /// <summary>内圈余量（mm）：&lt;0 ⇒ 该点必被拒。</summary>
        public double InnerMarginMm = double.NaN;

        /// <summary>外圈余量（mm）：越小越危险。</summary>
        public double OuterMarginMm = double.NaN;

        /// <summary>人话结论（直接进 UI 与报告）。</summary>
        public readonly List<string> Notes = new List<string>();

        /// <summary>是否存在硬性拦截项（形状非法 / EyeToHand 镜像）。</summary>
        public bool HasBlocker;

        /// <summary>去畸变前后的对比实测（"先量化再定"决策的数据来源）。</summary>
        public DistortionImpactAssessment DistortionImpact;

        /// <summary>渲染成与主项目同构的多行健康报告文本（便于人眼比对与粘贴到记录里）。</summary>
        public string ToReportText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ 标定矩阵健康检查 ═══");

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "① 像素当量: col={0:F5} row={1:F5} mm/px，两轴差 {2:F2}% ｜ {3}",
                ColumnNormFirst, ColumnNormSecond, AxisScaleDeviationPct,
                BadShape
                    ? string.Format(CultureInfo.InvariantCulture, "⛔ 形状非法（各向异性 σ1/σ2={0:F3}，应≈1.000）", SigmaRatio)
                    : string.Format(CultureInfo.InvariantCulture, "✓ 形状合法（各向异性 σ1/σ2={0:F3}）", SigmaRatio));
            sb.AppendLine();

            if (BadShape)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "   ↳ 失真量 {0:F1}%。落点误差按差分距离线性放大，RMS 小根本发现不了；先查世界侧轨迹是否真直线。",
                    AnisotropyPct);
                sb.AppendLine();
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "② 正交性: 列夹角 {0:F3}°（偏离正交 {1:F3}°）剪切 {2:F2}% {3}",
                ColumnAngleDeg, Math.Abs(90.0 - ColumnAngleDeg), ShearRatio * 100.0,
                ShearSuspicious ? "⚠ 剪切偏大（提示性，非阻断）" : "✓ 近正交");
            sb.AppendLine();

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "③ 行列式: det={0:F4} {1}",
                DetA,
                MirrorDetected
                    ? (CameraMount == CameraMountKind.EyeInHand
                        ? "⚠ 负值=镜像（EyeInHand 相机随动属固有物理，不阻断）"
                        : MirrorIsBlocker
                            ? "⛔ 负值=镜像（固定相机，需检查轴方向/相机成像）"
                            : "⚠ 负值=镜像（眼型未知，按保守提示处理）")
                    : "✓ 正向（无镜像）");
            sb.AppendLine();

            if (GridRowEdgeMeanMm > 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "④ 网格重建: 行边长均值 {0:F2}mm(CV={1:F1}%)，列边长均值 {2:F2}mm(CV={3:F1}%) {4}",
                    GridRowEdgeMeanMm, GridRowEdgeCvPct, GridColEdgeMeanMm, GridColEdgeCvPct,
                    GridIrregular ? "⚠ 边长离散大，个别采样点不可靠" : "✓ 网格规整");
                sb.AppendLine();

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "⑤ 网格夹角: {0:F1}° {1}",
                    GridAngleDeg,
                    GridAngleSuspicious ? "⚠ 偏离 90°（提示性）" : "✓ 近正交");
                sb.AppendLine();
            }

            sb.AppendFormat(CultureInfo.InvariantCulture,
                "RMS 重投影误差: {0:F4} mm {1}",
                RmsMm, RmsMm > 0.5 ? "⚠ 建议 <0.5mm 才可用于高精度引导" : "✓ 可接受");
            sb.AppendLine();

            if (!double.IsNaN(LooRmsMm))
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "LOO 留一残差（本工具补充）: {0:F4} mm{1}",
                    LooRmsMm,
                    (RmsMm > 1e-6 && LooRmsMm > RmsMm * 1.5)
                        ? "  ⚠ 明显高于自拟合，自拟合 RMS 偏乐观"
                        : string.Empty);
                sb.AppendLine();
            }

            // ⑥ 反投影 —— 放在 RMS 之后单独成段：它是"看见才算通过"的那一项，
            //    优先级高于任何数字，所以不能被混在注释里一笔带过。
            if (ReprojectionRmsPx > 0.0 || ReprojectionMaxPx > 0.0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "⑥ 反投影: 像素残差 RMS {0:F3} px / 最大 {1:F3} px，世界残差 RMS {2:F4} mm ｜ {3}",
                    ReprojectionRmsPx, ReprojectionMaxPx, ReprojectionRmsMm,
                    ReprojectionWithinTolerance
                        ? "✓ 目视重合（判据优先级高于 RMS）"
                        : "⚠ 有超容差点，先看残差矢量往哪偏");
                sb.AppendLine();

                if (ReprojectionBiasPx > 0.05)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "   ↳ 系统性偏置 {0:F3} px（非随机噪声）", ReprojectionBiasPx);
                    sb.AppendLine();
                }

                if (Math.Abs(ReprojectionRadialTrendMmPerMm) > 1e-6)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "   ↳ 残差随半径变化 {0:F5} mm/mm{1}",
                        ReprojectionRadialTrendMmPerMm,
                        ReprojectionRadialTrendMmPerMm > 0.0
                            ? "（越往外偏得越多 → 尺度或畸变问题）"
                            : string.Empty);
                    sb.AppendLine();
                }

                if (!double.IsNaN(InnerMarginMm))
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "   ↳ 内圈余量 {0:F2} mm{1}",
                        InnerMarginMm, InnerMarginMm < 0.0 ? "  ⛔ 已越界" : string.Empty);
                    sb.AppendLine();
                }

                if (!double.IsNaN(OuterMarginMm))
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "   ↳ 外圈余量 {0:F2} mm{1}",
                        OuterMarginMm, OuterMarginMm < 50.0 ? "  ⚠ 偏薄" : string.Empty);
                    sb.AppendLine();
                }
            }

            for (int i = 0; i < Notes.Count; i++)
            {
                sb.AppendLine("· " + Notes[i]);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 畸变影响量化。★ 产物里 H 的语义<b>不变</b>（仍在原图上标），
    /// 本对象只回答"若把 H 定义在矫正图上，精度能改善多少毫米"。
    /// 让"要不要改消费语义"变成有数据的决策，而不是拍脑袋。
    /// </summary>
    public sealed class DistortionImpactAssessment
    {
        public bool Measured;

        /// <summary>没测出来时的人话原因（缺数据 / κ 不自洽 / 点太少）。</summary>
        public string Reason;

        /// <summary>量从哪来的：<c>board</c> = 内参链用标定板自身当尺子；<c>ninepoint</c> = 九点采样对比。</summary>
        public string Source;

        /// <summary>像素 → 毫米的换算（逐图取中位数：物理 mark 间距 ÷ 视在像素间距）。</summary>
        public double ScaleMmPerPx;

        /// <summary>本标定真正覆盖到的像面半径（px）与它折合的世界半径（mm）。</summary>
        public double WorstRadiusPx;
        public double WorkRadiusMm;

        /// <summary>畸变造成的最大位移。<b>这两个数就是决策 3 要的量级证据。</b></summary>
        public double MaxShiftPx;
        public double MaxShiftMm;

        /// <summary>
        /// 位移随半径变化的曲线（三条等长数组，供画出"越往边角越偏"）。
        /// ★ 畸变是径向的 —— 一条一维曲线就足以表达，不用铺成二维图。
        /// </summary>
        public double[] CurveRadiusPx;
        public double[] CurveShiftPx;
        public double[] CurveShiftMm;

        public double SigmaRatioOnRaw;
        public double SigmaRatioOnUndistorted;

        public double LooRmsOnRawMm;
        public double LooRmsOnUndistortedMm;

        /// <summary>各采样点因去畸变产生的位移量（像素）。</summary>
        public double[] PointShiftPx;

        /// <summary>各采样点因去畸变产生的位移量（mm，经 H 换算）。</summary>
        public double[] PointShiftMm;

        /// <summary>人话结论（含"要不要处理"的建议）。</summary>
        public string Verdict;

        /// <summary>去畸变把 LOO 残差改善了多少（%）；NaN = 没算。</summary>
        public double LooImprovementPct
        {
            get
            {
                if (double.IsNaN(LooRmsOnRawMm) || double.IsNaN(LooRmsOnUndistortedMm) || LooRmsOnRawMm <= 1e-12)
                {
                    return double.NaN;
                }

                return (LooRmsOnRawMm - LooRmsOnUndistortedMm) / LooRmsOnRawMm * 100.0;
            }
        }
    }

    /// <summary>
    /// 对外唯一持久化格式（中性导出契约）。
    /// ★ 字段一旦写入就<b>只增不删</b>：将来无论选"仅诊断 / H 改口径 / 生产端补偿"哪条路，
    ///   都不需要改格式，只需改消费端读哪些字段。
    /// </summary>
    public sealed class CalibExport
    {
        /// <summary>导出格式版本。</summary>
        public string FormatVersion = "1.0";

        public string SourceSessionId;
        public CalibChainKind Chain = CalibChainKind.NinePoint;

        public string StationCode;
        public string CameraSlotKey;

        // ── 几何量（与 CalibrationGeometry 的消费语义对齐）──

        /// <summary>H —— 像素 → 世界（法兰命令位域），6 参数仿射。</summary>
        public HomMat2D H;
        public bool HasH;

        /// <summary>O —— 旋转中心（世界）。</summary>
        public Vec2 RotCenterWorld;
        public bool HasRotCenter;

        /// <summary>e —— 真吸嘴偏心 = O − H(p_tip)。注意 ToolEccW = −m ≠ e，别混。</summary>
        public Vec2 Ecc;
        public bool HasEcc;

        /// <summary>工具尖世界位置 H(p_tip)。</summary>
        public Vec2 TipWorld;
        public bool HasTipWorld;

        /// <summary>标定基准角 U0（发布链写入 ToolAlignU）。</summary>
        public double RefU0;

        /// <summary>★ 手系必须随产物存档（左手工位 / 右手工位解不同，误用会直接报 4007）。</summary>
        public string Handedness = "Unknown";

        /// <summary>相机安装方式（决定消费端是否要叠 O 补偿）。</summary>
        public string CameraMount = "Unknown";

        // ── 内参 / 畸变 ──
        public double[] FocalLengthPx;
        public double[] PrincipalPointPx;

        /// <summary>
        /// 焦距（<b>米</b>）与像元尺寸（<b>米</b>）。
        /// ★ 只有这两个才能拼出 HALCON 的 campar —— 光有"焦距 3431 px"是喂不进
        ///   <c>area_scan_division</c> 的（它的 focus / sx / sy 单位都是米）。
        ///   消费端要调 <c>change_radial_distortion_*</c> 时，缺了这两个数就只能靠猜像元尺寸。
        /// </summary>
        public double FocalLengthM;
        public double PixelPitchM;

        /// <summary>
        /// ★ 畸变系数 —— <b>HALCON「除法模型」口径</b>：单个数 κ，量纲 <b>1/m²</b>
        /// （乘的是<b>像面公制半径</b>，不是归一化半径）。
        ///
        /// 存这个口径是因为消费端要拿它直接喂 <c>area_scan_division</c> 的 campar；
        /// 想按熟悉的 Brown/Conrady 归一化系数理解，见
        /// <see cref="DistortionNormalizedK1"/>（两者差一个 f²）。
        /// <b>不要</b>拿一个归一化系数（例如 −0.15）直接当这个字段用：那等于"没有畸变"，
        /// 而且不会有任何报错 —— 这是本工具踩过的最贵的一个坑。
        /// </summary>
        public double[] Distortion;

        /// <summary>同一份畸变的归一化口径 k1（= κ·f²），仅用于人看 / 与旧资料对照，不参与消费。</summary>
        public double DistortionNormalizedK1;

        public int[] ImageSize;
        public double ReprojectionErrorPx;

        // ── 可复现信息 ──
        public CalibDiagnostics Diagnostics;

        public string BoardDescription;
        public BoardKind? Board;

        public string ProducedBy;
        public DateTime ProducedUtc = DateTime.UtcNow;

        /// <summary>源采样帧留档目录（便于复盘）。</summary>
        public string FramesDir;
    }
}
