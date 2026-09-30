//===================================================================================
// 文件名: ChainWizardViewModel.cs
// 说 明: 范式2 链向导 v2（工位驱动版）——骨架不再手拼，由 ChainTopologyPlanner 从
//        工位档案（VisionSlotInfo.InstallKind/Purpose/IsDisabled + ToolHeadCount）推导；
//        步骤清单=骨架遍历+依赖排序；采集区按节点动态生成；Workflow 只产 DTO 回填骨架。
//
// v2 纪律（设计真源《标定链路v2_工位驱动标定工作流设计_2026-09-27.md》§4~§5）：
//   · 链的形状由档案事实决定；人工只做两处裁决（主工具指派 / 相机角色覆盖），其余全自动；
//   · Mount=档案安装方式（EIH/ETH 由推导器定，UI 只展示不可改——改档案不是改向导）；
//   · Edges 由推导器生成（R3/R4），向导与人都禁止手填；
//   · 下相机必须实测 DeltaRefPixel 才允许落盘 DownCameraCorrect 边（fail-closed 前置）；
//   · 形状硬拦 |σ1/σ2−1|>0.03 在所有门禁之前（RMS 抓不到形状失真）；
//   · 落盘 = 骨架回填完毕 + G0~G4 全过 ⇒ Chain.json（生产端唯一真源）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Grayson.Vision.Contracts.Calibration.Chain;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Calibration.Models;   // CalibrationFeatureType（圆/十字/模板）
using Grayson.Vision.Contracts.Imaging;              // IRenderImage
using Grayson.Vision.Contracts.Templates.Models;     // TemplateInfo（全局模板库）
using Grayson.Vision.HalconWrapper.Calibration;      // CalibrationService / FeatureMatchReport（识别算子）
using Grayson.Vision.HalconWrapper.Templates;        // TemplateManager（模板库枚举）
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;   // ImageDisplayVm（Halcon 视窗数据源）
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using Plugins.Robot.Epson;   // 标定走位须用 EpsonRobot.MoveToLinear（CP 直线；PTP 弧线会被形状门拦）

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>一个采集点：目标系坐标 + 像素 + （EIH 时）拍照位姿</summary>
    public class ChainPointRow : ViewModelBase
    {
        private double _worldX;         // ETH：Robot Base 世界坐标；EIH：拍照时工件的世界坐标
        private double _worldY;
        private double _pixelCol;       // 像素列（=特征在图像里的 col）
        private double _pixelRow;       // 像素行
        private double _photoX;         // EIH：拍照时法兰/机位 X（Base 系）
        private double _photoY;         // EIH：拍照时法兰/机位 Y
        private double _photoU;         // EIH：拍照时 U 角（度）
        private string _note;
        private double _residualMm = double.NaN;   // 拟合后回填的逐点残差（mm）；NaN=未拟合
        private bool _residualHigh;

        public double WorldX { get { return _worldX; } set { Set(ref _worldX, value); } }
        public double WorldY { get { return _worldY; } set { Set(ref _worldY, value); } }
        public double PixelCol { get { return _pixelCol; } set { Set(ref _pixelCol, value); } }
        public double PixelRow { get { return _pixelRow; } set { Set(ref _pixelRow, value); } }
        public double PhotoX { get { return _photoX; } set { Set(ref _photoX, value); } }
        public double PhotoY { get { return _photoY; } set { Set(ref _photoY, value); } }
        public double PhotoU { get { return _photoU; } set { Set(ref _photoU, value); } }
        public string Note { get { return _note; } set { Set(ref _note, value); } }

        /// <summary>本点重投影残差（mm）；NaN=未拟合（表格显示空）</summary>
        public double ResidualMm
        {
            get { return _residualMm; }
            set
            {
                if (Set(ref _residualMm, value))
                    OnPropertyChanged(nameof(ResidualText));
            }
        }
        /// <summary>残差显示（NaN→空串）</summary>
        public string ResidualText
        {
            get { return double.IsNaN(_residualMm) ? "" : _residualMm.ToString("F4", CultureInfo.InvariantCulture); }
        }
        /// <summary>离群点（残差 > max(2.5×RMS, 0.1mm)）⇒ 行标红，提示重采该点</summary>
        public bool ResidualHigh
        {
            get { return _residualHigh; }
            set { Set(ref _residualHigh, value); }
        }
    }

    /// <summary>pivoting（针尖对点法）采集一行：法兰转到某 U 角扎针后的读数</summary>
    public class ChainPivotRow : ViewModelBase
    {
        private double _uDeg;
        private double _flangeX;
        private double _flangeY;
        private double _residualMm = double.NaN;
        private bool _residualHigh;
        private string _note;

        public ChainPivotRow(string note) { _note = note; }
        public string Note { get { return _note; } }
        /// <summary>扎针时的法兰 U 角（度）——必须散开 ≥30°，推荐覆盖 ≥90°</summary>
        public double UDeg { get { return _uDeg; } set { Set(ref _uDeg, value); } }
        public double FlangeX { get { return _flangeX; } set { Set(ref _flangeX, value); } }
        public double FlangeY { get { return _flangeY; } set { Set(ref _flangeY, value); } }
        public double ResidualMm
        {
            get { return _residualMm; }
            set { if (Set(ref _residualMm, value)) OnPropertyChanged(nameof(ResidualText)); }
        }
        public string ResidualText
        {
            get { return double.IsNaN(_residualMm) ? "" : _residualMm.ToString("F4", CultureInfo.InvariantCulture); }
        }
        public bool ResidualHigh
        {
            get { return _residualHigh; }
            set { Set(ref _residualHigh, value); }
        }
    }

    /// <summary>
    /// 旋转中心 e 采样行（2026-09-30）：EIH 法兰绕 U 转、相机始终看同一固定特征
    /// ⇒ 特征在【法兰系】里画圆，圆心就是工具尖相对法兰的偏心 e。
    /// 采样量是**像素**（圆拟合在像素域做，与范式1 RotCircle 同口径），圆心再由相机矩阵 H_FC 映到法兰系。
    /// 与 pivoting（针尖接触法）是同一物理量的两条独立路径——两者可交叉校验。
    /// </summary>
    public class ChainRotationRow : ViewModelBase
    {
        private double _uDeg;
        private double _pixelCol;
        private double _pixelRow;
        private double _residualPx = double.NaN;
        private bool _residualHigh;
        private string _note;

        public ChainRotationRow(string note) { _note = note; }
        public string Note { get { return _note; } }
        /// <summary>拍照时的法兰 U 角（度）——须覆盖 ≥90°（行业惯例 0/90/180/270）</summary>
        public double UDeg { get { return _uDeg; } set { Set(ref _uDeg, value); } }
        /// <summary>特征像素列（col）</summary>
        public double PixelCol
        {
            get { return _pixelCol; }
            set { if (Set(ref _pixelCol, value)) OnPropertyChanged(nameof(PixelText)); }
        }
        /// <summary>特征像素行（row）</summary>
        public double PixelRow
        {
            get { return _pixelRow; }
            set { if (Set(ref _pixelRow, value)) OnPropertyChanged(nameof(PixelText)); }
        }
        /// <summary>有效采样（有像素）——同一行的判据用「像素非零」，U 角可以为 0</summary>
        public bool HasPixel { get { return !(_pixelCol == 0 && _pixelRow == 0); } }
        public string PixelText
        {
            get { return HasPixel ? string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", _pixelCol, _pixelRow) : ""; }
        }
        /// <summary>该点到拟合圆心的半径偏差（px）</summary>
        public double ResidualPx
        {
            get { return _residualPx; }
            set { if (Set(ref _residualPx, value)) OnPropertyChanged(nameof(ResidualText)); }
        }
        public string ResidualText
        {
            get { return double.IsNaN(_residualPx) ? "" : _residualPx.ToString("F2", CultureInfo.InvariantCulture); }
        }
        public bool ResidualHigh
        {
            get { return _residualHigh; }
            set { Set(ref _residualHigh, value); }
        }
    }

    /// <summary>
    /// pivoting 可视化的一个散点（2026-09-30）：坐标已换算到 XAML Canvas 的像素域。
    /// 靶心 = P_ref，点到靶心的距离 = 该角的残差（预测针尖位置对 P_ref 的偏差）。
    /// </summary>
    public class PivotVizDot
    {
        /// <summary>画布 X（px，左上原点）——该角预测针尖位置</summary>
        public double CanvasX { get; set; }
        /// <summary>画布 Y（px，已做世界Y→屏幕Y 翻转）</summary>
        public double CanvasY { get; set; }
        /// <summary>靶心画布 X（= 画布中心；残差射线起点）</summary>
        public double CenterX { get; set; }
        /// <summary>靶心画布 Y（= 画布中心；残差射线起点）</summary>
        public double CenterY { get; set; }
        /// <summary>超出验收门 0.5mm ⇒ 该角"扎歪"（画红点）</summary>
        public bool IsOutlier { get; set; }
        /// <summary>行号标签（"1".."8"）</summary>
        public string Label { get; set; }
        /// <summary>悬停提示（U 角与残差）</summary>
        public string Tip { get; set; }
    }

    /// <summary>一个相机节点的采集与拟合（数量/挂链由骨架决定，向导动态生成）</summary>
    public class ChainCameraSectionViewModel : ViewModelBase
    {
        private readonly bool _isEih;               // 档案事实（推导器定），UI 不可改
        private readonly bool _isDownCorrect;       // 有 DownCameraCorrect 边 ⇒ 须实测 DeltaRefPixel
        private ChainPointRow _selectedRow;
        private double _deltaRefCol;                // 下相机专属：差分基准像素（R_cdown）
        private double _deltaRefRow;
        private bool _hasDeltaRef;

        private double[] _matrix;                   // 拟合产物 [a11,a12,tx,a21,a22,ty]
        private string _fitResult = "未拟合";
        private bool _fitOk;
        private bool _shapeGateFailed;
        private double _rmsMm, _sigma1, _sigma2, _shapeDev;
        private int _pointCount;

        public ChainCameraSectionViewModel(ChainCameraNode draftNode, bool isDownCorrect, int pickEdgeCount,
                                           HalconImageRenderService renderService)
        {
            _isEih = draftNode.Mount == ChainCameraMount.EyeInHand;
            _isDownCorrect = isDownCorrect;
            CameraId = draftNode.CameraId;
            // 边数决定标题语义：引导相机显示服务几个工具，纠偏相机显示纠偏角色
            Title = CameraId + (isDownCorrect ? " ｜ 下相机纠偏（ETH·固定）"
                                              : " ｜ 吸点引导 ×" + pickEdgeCount + (IsEih ? "（EIH·随动）" : "（ETH·固定）"));
            ImageDisplay = new ImageDisplayVm(renderService);
            Points = new ObservableCollection<ChainPointRow>();
            for (int i = 0; i < 9; i++)
                Points.Add(new ChainPointRow { Note = "P" + (i + 1) });
            RefreshGridStatus();
            ClearRecognition();
            ReloadTemplates();
        }

        public string Title { get; private set; }
        public string CameraId { get; private set; }
        public ObservableCollection<ChainPointRow> Points { get; private set; }

        private ImageSource _image;
        /// <summary>采集底图（code-behind 载入后写入；模板绑定显示）</summary>
        public ImageSource Image
        {
            get { return _image; }
            set { Set(ref _image, value); }
        }

        /// <summary>true=眼在手（EIH，矩阵=T_Cam→Flange）；false=眼在手外（ETH，矩阵=T_Cam→Robot）</summary>
        public bool IsEih { get { return _isEih; } }
        /// <summary>有下相机纠偏边 ⇒ 右键点选 DeltaRefPixel 必测，否则拒绝落盘</summary>
        public bool IsDownCorrect { get { return _isDownCorrect; } }

        public string MountHeader { get { return IsEih ? "EIH（随动，矩阵=T_Cam→Flange）" : "ETH（固定，矩阵=T_Cam→Robot）"; } }

        public ChainPointRow SelectedRow
        {
            get { return _selectedRow; }
            set { Set(ref _selectedRow, value); }
        }

        /// <summary>下相机专属：差分基准像素列（吸嘴 U 轴图像投影 col）</summary>
        public double DeltaRefCol
        {
            get { return _deltaRefCol; }
            set { Set(ref _deltaRefCol, value); HasDeltaRef = Math.Abs(_deltaRefCol) > 1e-9 || Math.Abs(_deltaRefRow) > 1e-9; }
        }

        /// <summary>下相机专属：差分基准像素行</summary>
        public double DeltaRefRow
        {
            get { return _deltaRefRow; }
            set { Set(ref _deltaRefRow, value); HasDeltaRef = Math.Abs(_deltaRefCol) > 1e-9 || Math.Abs(_deltaRefRow) > 1e-9; }
        }

        public bool HasDeltaRef
        {
            get { return _hasDeltaRef; }
            private set { Set(ref _hasDeltaRef, value); }
        }

        private double _photoPoseX;                 // EIH 专属：拍照基准位（生产在此机位拍照）
        private double _photoPoseY;
        private bool _hasPhotoPose;

        /// <summary>EIH 专属：拍照基准位 X（Robot Base 系；落盘进 ChainCameraNode.PhotoPose）</summary>
        public double PhotoPoseX
        {
            get { return _photoPoseX; }
            set { Set(ref _photoPoseX, value); RefreshHasPhotoPose(); }
        }

        /// <summary>EIH 专属：拍照基准位 Y</summary>
        public double PhotoPoseY
        {
            get { return _photoPoseY; }
            set { Set(ref _photoPoseY, value); RefreshHasPhotoPose(); }
        }

        private string _calibZText = "";
        /// <summary>★标定高度 Z（mm）= 本次九点标定时的机械手 Z。写进链图 Meta.CalibZHeightMm：
        ///   · EIH：相机↔法兰的平移分量与 Z 线性相关（生产拍照 Z≠标定 Z ⇒ 乘性过纠，RMS 抓不到）；
        ///   · 下相机段：生产预检拿它比对拍照 Z（工件 Z 一变物距就变，像素当量按 1/物距缩放）。
        /// 留空 = 链图不记该值 ⇒ 审计列条目、生产预检明说『本轮无法核对』（不静默放行）。</summary>
        public string CalibZText { get { return _calibZText; } set { Set(ref _calibZText, value); } }

        public bool HasPhotoPose
        {
            get { return _hasPhotoPose; }
            private set { Set(ref _hasPhotoPose, value); }
        }

        private void RefreshHasPhotoPose()
        {
            HasPhotoPose = IsEih && (_photoPoseX != 0 || _photoPoseY != 0)
                && !double.IsNaN(_photoPoseX) && !double.IsNaN(_photoPoseY);
        }

        public double[] Matrix { get { return _matrix; } private set { Set(ref _matrix, value); } }
        public string FitResult { get { return _fitResult; } private set { Set(ref _fitResult, value); } }
        public bool FitOk { get { return _fitOk; } private set { Set(ref _fitOk, value); } }
        /// <summary>形状失真超门（0.03）——禁止保存（血泪：RMS 抓不到形状失真）</summary>
        public bool ShapeGateFailed { get { return _shapeGateFailed; } private set { Set(ref _shapeGateFailed, value); } }
        public double RmsMm { get { return _rmsMm; } private set { Set(ref _rmsMm, value); } }

        /// <summary>把图像点选/手输的像素写入当前选中行（无选中行 = 提示）</summary>
        public void SetPixelFromClick(double col, double row)
        {
            if (SelectedRow == null)
            {
                FitResult = "⚠ 请先在点对表格中【选中】要回填像素的那一行，再点击图像";
                return;
            }
            SelectedRow.PixelCol = Math.Round(col, 3);
            SelectedRow.PixelRow = Math.Round(row, 3);
            FitResult = "已回填第 " + (Points.IndexOf(SelectedRow) + 1) + " 行像素 ("
                        + SelectedRow.PixelCol.ToString("F1", CultureInfo.InvariantCulture)
                        + ", " + SelectedRow.PixelRow.ToString("F1", CultureInfo.InvariantCulture) + ")";
            RefreshGridStatus();
        }

        /// <summary>下相机专属：图像点选写入差分基准像素 DeltaRefPixel（右键）</summary>
        public void SetDeltaRefFromClick(double col, double row)
        {
            DeltaRefCol = Math.Round(col, 3);
            DeltaRefRow = Math.Round(row, 3);
            FitResult = "已写入差分基准像素 DeltaRefPixel=("
                        + DeltaRefCol.ToString("F1", CultureInfo.InvariantCulture)
                        + ", " + DeltaRefRow.ToString("F1", CultureInfo.InvariantCulture)
                        + ")（= 吸嘴 U 轴图像投影 R_cdown，向导实测）";
        }

        //---------------------------------------------------------------------
        // 特征 / 模板识别（2026-09-30 补，照搬范式1 向导 Step1「算法特征配置」）
        //   旧向导（CalibrationWizardViewModel，R4/R5 已删）本有完整识别闭环：
        //     特征类型（圆 / 十字 / 模板）→ 模板库下拉 + MinScore + 角度范围 →
        //     识别 → HALCON 把过程与结果叠加到视窗 → 匹配分卡片 → 回填像素。
        //   范式2 重写时丢掉，链向导只剩"肉眼点选"。本段把识别状态接回来；
        //   ★ 算法本体不另写——由父 VM 调 HalconWrapper 的 CalibrationService.ExtractFeaturePreview
        //     （圆=阈值+圆度+亚像素圆拟合 / 十字=骨架+直线交叉 / 模板=全局模板库 Shape·NCC，
        //      与生产 ShapeMatch 节点同源），识别结论经 ApplyMatchReport 落到本段。
        //---------------------------------------------------------------------

        private WpfImageRenderContext _imageContext;
        private CalibrationFeatureType _featureType = CalibrationFeatureType.CircleMark;
        private string _featureTemplateName = "";
        private double _templateMinScore = 0.5;
        private double _templateAngleStart = -180;
        private double _templateAngleEnd = 180;
        private bool _hasRecognized;
        private double _recognizedCol, _recognizedRow;
        private double _matchScore;
        private string _matchScoreText = "尚未提取";
        private string _matchScoreDetail = "";
        private string _matchCandidateSummary = "";
        private SolidColorBrush _matchScoreBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));

        /// <summary>Halcon 视窗数据源（XAML：HalconImageDisplayHost 的 DataContext）</summary>
        public ImageDisplayVm ImageDisplay { get; private set; }

        /// <summary>当前图像渲染上下文（显示与识别共用；换图时旧上下文由 ImageDisplayVm 统一释放）</summary>
        public WpfImageRenderContext ImageContext
        {
            get { return _imageContext; }
            private set { Set(ref _imageContext, value); OnPropertyChanged(nameof(HasImageForRecognize)); }
        }

        /// <summary>当前是否有可用于识别的图像</summary>
        public bool HasImageForRecognize
        {
            get { return _imageContext != null && _imageContext.Image != null; }
        }

        /// <summary>识别用图像句柄（CalibrationService 认 IRenderImage）</summary>
        public object RecognitionImage
        {
            get { return _imageContext != null ? (object)_imageContext.Image : null; }
        }

        /// <summary>把新图像交给本段视窗（同时作废上一次识别结论——防止把上一张图的像素当成这张的）</summary>
        public void SetImageContext(WpfImageRenderContext ctx)
        {
            if (ctx == null) return;
            ImageDisplay.AddOrUpdateImageContext(ctx);
            ImageContext = ctx;
            ClearRecognition();
        }

        /// <summary>特征类型（圆 / 十字 / 模板）</summary>
        public CalibrationFeatureType FeatureType
        {
            get { return _featureType; }
            set
            {
                if (Set(ref _featureType, value))
                {
                    OnPropertyChanged(nameof(FeatureTypeIndex));
                    OnPropertyChanged(nameof(IsCircleFeature));
                    OnPropertyChanged(nameof(IsCrossFeature));
                    OnPropertyChanged(nameof(IsTemplateFeature));
                    ClearRecognition();
                }
            }
        }

        /// <summary>特征类型下拉（枚举序 = CalibrationFeatureType：圆0 / 十字1 / 模板2）</summary>
        public string[] FeatureTypeOptions
        {
            get { return new[] { "圆形 Mark（圆心）", "十字 Mark（形状匹配）", "模板匹配（Shape/NCC）" }; }
        }

        public int FeatureTypeIndex
        {
            get { return (int)_featureType; }
            set { FeatureType = (CalibrationFeatureType)Math.Max(0, Math.Min(2, value)); }
        }

        public bool IsCircleFeature { get { return _featureType == CalibrationFeatureType.CircleMark; } }
        public bool IsCrossFeature { get { return _featureType == CalibrationFeatureType.CrossMark; } }
        public bool IsTemplateFeature { get { return _featureType == CalibrationFeatureType.TemplateMatch; } }

        /// <summary>全局模板库（模板管理页创建，与生产 ShapeMatch 同源）</summary>
        public ObservableCollection<TemplateInfo> AvailableTemplates { get; } = new ObservableCollection<TemplateInfo>();

        /// <summary>重新枚举全局模板库</summary>
        public void ReloadTemplates()
        {
            AvailableTemplates.Clear();
            try
            {
                var res = new TemplateManager().GetAll();
                if (res != null && res.Success && res.Data != null)
                    foreach (var t in res.Data) AvailableTemplates.Add(t);
            }
            catch { /* 模板库不可用不阻塞向导（圆/十字特征无需模板） */ }
            MatchScoreDetail = HasImageForRecognize
                ? "暂无提取结果——点『🎯 识别特征』。"
                : "当前无图像——请先『单帧取图』或『载入图像…』。";
        }

        /// <summary>模板名（模板匹配特征用；须与模板库中的名字一致）</summary>
        public string FeatureTemplateName
        {
            get { return _featureTemplateName; }
            set { Set(ref _featureTemplateName, value); }
        }

        /// <summary>模板匹配最低分门（0~1，低于则判未识别）</summary>
        public double TemplateMinScore
        {
            get { return _templateMinScore; }
            set { Set(ref _templateMinScore, value); }
        }

        /// <summary>模板搜索角度下限（度）</summary>
        public double TemplateAngleStart
        {
            get { return _templateAngleStart; }
            set { Set(ref _templateAngleStart, value); }
        }

        /// <summary>模板搜索角度上限（度）</summary>
        public double TemplateAngleEnd
        {
            get { return _templateAngleEnd; }
            set { Set(ref _templateAngleEnd, value); }
        }

        public double MatchScore { get { return _matchScore; } private set { Set(ref _matchScore, value); } }
        public string MatchScoreText { get { return _matchScoreText; } private set { Set(ref _matchScoreText, value); } }
        public string MatchScoreDetail { get { return _matchScoreDetail; } private set { Set(ref _matchScoreDetail, value); } }
        public string MatchCandidateSummary { get { return _matchCandidateSummary; } private set { Set(ref _matchCandidateSummary, value); } }
        public SolidColorBrush MatchScoreBrush { get { return _matchScoreBrush; } private set { Set(ref _matchScoreBrush, value); } }

        /// <summary>本次图像上是否已识别到特征（HasRecognized=true 才有可信像素可回填）</summary>
        public bool HasRecognized
        {
            get { return _hasRecognized; }
            private set { Set(ref _hasRecognized, value); OnPropertyChanged(nameof(RecognizedPixelText)); }
        }

        /// <summary>识别到的像素列（=u=X）</summary>
        public double RecognizedCol { get { return _recognizedCol; } private set { Set(ref _recognizedCol, value); } }
        /// <summary>识别到的像素行（=v=Y）</summary>
        public double RecognizedRow { get { return _recognizedRow; } private set { Set(ref _recognizedRow, value); } }

        public string RecognizedPixelText
        {
            get
            {
                return HasRecognized
                    ? string.Format(CultureInfo.InvariantCulture, "识别像素 u={0:F2}, v={1:F2}", _recognizedCol, _recognizedRow)
                    : "尚未识别";
            }
        }

        /// <summary>作废识别结论（换图 / 换特征类型时调用，防串帧）</summary>
        public void ClearRecognition()
        {
            HasRecognized = false;
            MatchScore = 0;
            MatchScoreText = "尚未提取";
            MatchScoreBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));
            MatchCandidateSummary = "";
            MatchScoreDetail = HasImageForRecognize ? "暂无提取结果。" : "当前无图像——请先『单帧取图』或『载入图像…』。";
        }

        /// <summary>把识别报告落到 UI（分数 / 等级 / 明细 / 中心像素）。失败时明确降为未识别。</summary>
        public void ApplyMatchReport(FeatureMatchReport report)
        {
            if (report == null) { ClearRecognition(); return; }
            MatchScore = report.Success ? Math.Max(0, Math.Min(100, report.Score)) : 0;
            if (!report.Success)
            {
                MatchScoreText = "✗ 未识别 0/100";
                MatchScoreBrush = new SolidColorBrush(Color.FromRgb(200, 60, 60));
                HasRecognized = false;
            }
            else
            {
                MatchScoreText = string.Format(CultureInfo.InvariantCulture, "{0:F0}/100 · {1}", MatchScore, report.Verdict);
                MatchScoreBrush = MatchScore >= 85 ? new SolidColorBrush(Color.FromRgb(46, 160, 90))
                    : MatchScore >= 70 ? new SolidColorBrush(Color.FromRgb(200, 160, 40))
                    : MatchScore >= 55 ? new SolidColorBrush(Color.FromRgb(230, 120, 40))
                    : new SolidColorBrush(Color.FromRgb(200, 60, 60));
                RecognizedCol = report.PixelX;
                RecognizedRow = report.PixelY;
                HasRecognized = true;
            }
            string detail = report.Detail ?? "";
            if (report.Success && report.UsedFallback)
                detail = "（走了降级兜底路径，分数已打折）" + detail;
            MatchScoreDetail = string.IsNullOrWhiteSpace(detail) ? "—" : detail;
            MatchCandidateSummary = report.CandidateCount <= 0 ? "无候选" : ("候选 " + report.CandidateCount + " 个");
            OnPropertyChanged(nameof(RecognizedPixelText));
        }

        public void AddPoint()
        {
            Points.Add(new ChainPointRow { Note = "P" + (Points.Count + 1) });
        }

        public void RemovePoint(ChainPointRow row)
        {
            if (row != null) Points.Remove(row);
        }

        //---------------------------------------------------------------------
        // 九点网格采集：点位规划 + 步长（2026-09-30 补）
        //   旧范式向导（CalibrationWizardViewModel，R4/R5 已删）本有完整采集闭环——
        //   「网格中心基准 + 步长 → 自动生成 9 点目标 → 自动走位 → HALCON 特征提取」；
        //   范式2 重写时只迁了"记录/拟合/落盘"，没迁"驱动采集"，链向导于是退化成
        //   "走一步、点一下、手抄一格"。本段把点位规划与步长接回来（走位见 ChainWizardViewModel）。
        //
        //   ★ 几何不另起一套：直接调 ReachMapGeometry.BuildGrid（机械手调试台可达图同源），
        //     其符号规则 eyeInHand ? -o : +o 与旧向导 TryGetNinePointTarget 逐字一致。
        //   ★ 落字段分型（写错则拟合语义变）：
        //       ETH（相机固定、平台带工件走） ⇒ 目标 = 世界坐标 → WorldX/WorldY；
        //       EIH（相机随动、机械手走位）   ⇒ 目标 = 拍照机位 → PhotoX/PhotoY
        //                                     （WorldX/WorldY 是工件世界坐标，由操作员另填）。
        //---------------------------------------------------------------------

        private double _gridBaseX, _gridBaseY;
        private bool _gridBaseSet;
        private double _gridStepX = 10, _gridStepY = 10;
        private bool _gridInvertX, _gridInvertY;
        private int _traverseModeIndex;

        /// <summary>本相机段是否支持九点网格：下相机纠偏段只需一次右键点选 DeltaRefPixel</summary>
        public bool SupportsGrid { get { return !IsDownCorrect; } }

        /// <summary>网格中心基准 X（机械坐标）——由「设当前轴位置为基准」写入</summary>
        public double GridBaseX { get { return _gridBaseX; } set { Set(ref _gridBaseX, value); RefreshGridStatus(); } }
        /// <summary>网格中心基准 Y（机械坐标）</summary>
        public double GridBaseY { get { return _gridBaseY; } set { Set(ref _gridBaseY, value); RefreshGridStatus(); } }

        /// <summary>基准是否已设（0 是合法坐标，故用显式标志，不判零）</summary>
        public bool HasGridBase { get { return _gridBaseSet; } private set { Set(ref _gridBaseSet, value); } }

        /// <summary>网格步长 X（mm）= 九点相邻点间距</summary>
        public double GridStepX { get { return _gridStepX; } set { Set(ref _gridStepX, value); RefreshGridStatus(); } }
        /// <summary>网格步长 Y（mm）= 九点相邻点间距</summary>
        public double GridStepY { get { return _gridStepY; } set { Set(ref _gridStepY, value); RefreshGridStatus(); } }

        public bool GridInvertX { get { return _gridInvertX; } set { Set(ref _gridInvertX, value); } }
        public bool GridInvertY { get { return _gridInvertY; } set { Set(ref _gridInvertY, value); } }

        /// <summary>走位次序：0=中心优先螺旋（推荐）1=传统逐行扫描</summary>
        public int TraverseModeIndex
        {
            get { return _traverseModeIndex; }
            set { if (Set(ref _traverseModeIndex, value)) RefreshGridStatus(); }
        }
        public string[] TraverseModeOptions { get { return new[] { "中心优先螺旋（推荐）", "逐行扫描" }; } }
        public string TraverseModeText
        {
            get { return _traverseModeIndex == 1 ? "逐行 1→2→…→9" : "螺旋 5→6→3→2→1→4→7→8→9"; }
        }

        private string _gridStatus = "";
        /// <summary>网格状态/进度（常驻可读）</summary>
        public string GridStatus { get { return _gridStatus; } private set { Set(ref _gridStatus, value); } }

        /// <summary>设网格中心基准（走位到网格中心后调用）</summary>
        public void SetGridBase(double x, double y)
        {
            _gridBaseX = x; _gridBaseY = y;
            OnPropertyChanged(nameof(GridBaseX));
            OnPropertyChanged(nameof(GridBaseY));
            HasGridBase = true;
            RefreshGridStatus();
        }

        /// <summary>该行是否已采（判据=像素已填）</summary>
        public bool IsCaptured(ChainPointRow r)
        {
            return r != null && (Math.Abs(r.PixelCol) > 1e-9 || Math.Abs(r.PixelRow) > 1e-9);
        }

        /// <summary>生成 9 点目标（基准 + 步长，按眼型定符号）。只写坐标、不动像素。</summary>
        public int BuildGridTargets()
        {
            if (!SupportsGrid) { GridStatus = "本段为下相机纠偏：只需一次右键点选 DeltaRefPixel，无九点网格。"; return 0; }
            if (!HasGridBase) { GridStatus = "⚠ 先设网格中心基准：走位到网格中心 →『设当前轴位置为基准』。"; return 0; }
            if (GridStepX <= 0 || GridStepY <= 0) { GridStatus = "⚠ 步长必须为正数。"; return 0; }

            var pts = ReachMapGeometry.BuildGrid(_gridBaseX, _gridBaseY, GridStepX, GridStepY,
                                                _gridInvertX, _gridInvertY, IsEih);
            while (Points.Count < 9) Points.Add(new ChainPointRow { Note = "P" + (Points.Count + 1) });

            for (int i = 0; i < 9; i++)
            {
                var r = Points[i];
                if (IsEih) { r.PhotoX = Math.Round(pts[i].X, 3); r.PhotoY = Math.Round(pts[i].Y, 3); }
                else { r.WorldX = Math.Round(pts[i].X, 3); r.WorldY = Math.Round(pts[i].Y, 3); }
            }
            RefreshGridStatus();
            return 9;
        }

        /// <summary>取该行的走位目标（ETH=世界坐标 / EIH=拍照机位）</summary>
        public bool TryGetMoveTarget(ChainPointRow r, out double x, out double y)
        {
            x = 0; y = 0;
            if (r == null) return false;
            if (IsEih) { x = r.PhotoX; y = r.PhotoY; }
            else { x = r.WorldX; y = r.WorldY; }
            return Math.Abs(x) > 1e-9 || Math.Abs(y) > 1e-9;
        }

        /// <summary>按走位次序取下一个未采集行；全采完返回 null</summary>
        public ChainPointRow NextUncapturedRow(out int orderPos)
        {
            orderPos = -1;
            var mode = _traverseModeIndex == 1 ? NinePointTraverseMode.RowScan : NinePointTraverseMode.SpiralCenterFirst;
            var order = NinePointTraverseOrder.GetOrder(mode);
            for (int k = 0; k < order.Length; k++)
            {
                int idx = order[k] - 1;                     // Index 1~9 → 行 0~8
                if (idx < 0 || idx >= Points.Count) continue;
                if (!IsCaptured(Points[idx])) { orderPos = k; return Points[idx]; }
            }
            return null;
        }

        /// <summary>刷新网格状态文案（基准 / 步长 / 次序 / 进度）</summary>
        public void RefreshGridStatus()
        {
            if (!SupportsGrid)
            {
                GridStatus = "本段为下相机纠偏：只需一次右键点选 DeltaRefPixel，无九点网格。";
                return;
            }
            int done = Points.Count(IsCaptured);
            if (!HasGridBase)
            {
                GridStatus = "① 走位到网格中心 →『设基准』｜② 填步长｜③『生成 9 点目标』（当前已采 "
                             + done + "/" + Points.Count + "）";
                return;
            }
            GridStatus = string.Format(CultureInfo.InvariantCulture,
                "基准=({0:F2},{1:F2}) 步长=({2:F1},{3:F1})mm ｜ {4} ｜ 已采 {5}/{6}",
                GridBaseX, GridBaseY, GridStepX, GridStepY, TraverseModeText, done, Points.Count);
        }

        /// <summary>
        /// 执行拟合。EIH：先用各点拍照位姿把世界点规范到法兰系 p_f = R(−U)·(w − t)，
        /// 再统一做像素→法兰系的最小二乘仿射（产物=T_Cam→Flange）。
        /// ★ 只取「已填」行（像素与目标坐标不全零）——空行是占位不是观测，混入=在原点加假点。
        /// ★ 拟合成功后逐点残差回填表格并点名最差点（行业标准反馈：哪点没采好当场可见）。
        /// </summary>
        public void Fit()
        {
            // 重置残差显示（重新拟合前旧残差必须清掉，防误读）
            foreach (var row in Points) { row.ResidualMm = double.NaN; row.ResidualHigh = false; }

            var filled = Points.Where(p =>
                (p.PixelCol != 0 || p.PixelRow != 0) &&
                (p.WorldX != 0 || p.WorldY != 0 ||
                 (IsEih && (p.PhotoX != 0 || p.PhotoY != 0 || p.PhotoU != 0)))).ToList();
            if (filled.Count < 4)
            {
                FitOk = false;
                Matrix = null;
                FitResult = "❌ 有效点对不足（≥4 可解，推荐 9 点；当前已填 " + filled.Count + " 行，空行不计）";
                return;
            }

            var px = filled.Select(p => p.PixelCol).ToArray();
            var py = filled.Select(p => p.PixelRow).ToArray();
            double[] tx, ty;
            if (!IsEih)
            {
                tx = filled.Select(p => p.WorldX).ToArray();
                ty = filled.Select(p => p.WorldY).ToArray();
            }
            else
            {
                var norm = filled.Select(p => ToFlangeFrame(p)).ToArray();
                tx = norm.Select(v => v.Item1).ToArray();
                ty = norm.Select(v => v.Item2).ToArray();
            }

            var r = ChainFitter.FitAffine(px, py, tx, ty);
            _pointCount = r.PointCount;
            _sigma1 = r.Sigma1;
            _sigma2 = r.Sigma2;
            _shapeDev = r.ShapeDeviation;
            ShapeGateFailed = r.Ok && r.ShapeDeviation > ChainFitter.ShapeGateRatio;
            RmsMm = r.Ok ? r.RmsMm : double.NaN;

            if (!r.Ok)
            {
                FitOk = false;
                Matrix = null;
                FitResult = "❌ " + r.Error;
                return;
            }

            // 逐点残差（‖H·pᵢ − qᵢ‖）回填到对应行；离群行标红（>max(2.5×RMS, 0.1mm)）
            double worst = 0; int worstIdx = -1;
            double hiGate = Math.Max(r.RmsMm * 2.5, 0.1);
            for (int i = 0; i < filled.Count; i++)
            {
                double exd = r.Matrix[0] * px[i] + r.Matrix[1] * py[i] + r.Matrix[2] - tx[i];
                double eyd = r.Matrix[3] * px[i] + r.Matrix[4] * py[i] + r.Matrix[5] - ty[i];
                double d = Math.Sqrt(exd * exd + eyd * eyd);
                filled[i].ResidualMm = d;
                filled[i].ResidualHigh = d > hiGate;
                if (d > worst) { worst = d; worstIdx = i; }
            }

            Matrix = r.Matrix;
            FitOk = true;
            // EIH：拍照基准位缺省取第一行实测拍照机位（可手改）——生产/示教必须回该位拍照
            if (IsEih && !HasPhotoPose)
            {
                var src = filled.FirstOrDefault(p => Math.Abs(p.PhotoX) > 1e-9 || Math.Abs(p.PhotoY) > 1e-9);
                if (src != null)
                {
                    PhotoPoseX = src.PhotoX;
                    PhotoPoseY = src.PhotoY;
                }
            }
            FitResult = string.Format(CultureInfo.InvariantCulture,
                "✓ 拟合成功：n={0}, RMS={1:F4}, σ1={2:F3}, σ2={3:F3}, 形状偏差={4:F4} ({5})，最大残差 {6}={7:F4}mm{8}，矩阵=[{9:G9},{10:G9},{11:G9},{12:G9},{13:G9},{14:G9}]",
                r.PointCount, r.RmsMm, r.Sigma1, r.Sigma2, r.ShapeDeviation,
                ShapeGateFailed ? "⚠超门0.03禁止保存" : "≤0.03 ✓",
                worstIdx >= 0 ? filled[worstIdx].Note : "-",
                worst,
                ShapeGateFailed ? "" : (worst > hiGate ? " ⚠存在离群行（表格标红，建议重采）" : ""),
                r.Matrix[0], r.Matrix[1], r.Matrix[2], r.Matrix[3], r.Matrix[4], r.Matrix[5]);
        }

        /// <summary>世界点按拍照位姿规范到法兰系：p_f = R(−U)·(w − t)（T_F→B 的逆映射）</summary>
        private static Tuple<double, double> ToFlangeFrame(ChainPointRow p)
        {
            double rad = p.PhotoU * Math.PI / 180.0;
            double c = Math.Cos(rad), s = Math.Sin(rad);
            double dx = p.WorldX - p.PhotoX;
            double dy = p.WorldY - p.PhotoY;
            return Tuple.Create(c * dx + s * dy, -s * dx + c * dy);
        }

        /// <summary>组装相机节点（未拟合 = null）。Meta 留痕，不参与求值/门禁。</summary>
        public ChainCameraNode ToNode()
        {
            if (!FitOk || Matrix == null) return null;
            var node = new ChainCameraNode
            {
                CameraId = CameraId,
                Mount = IsEih ? ChainCameraMount.EyeInHand : ChainCameraMount.EyeToHand,
                Matrix = (double[])Matrix.Clone(),
                Meta = new ChainCalibMeta
                {
                    Method = IsEih ? "NinePoint-EIH(法兰系规范化)" : "NinePoint-ETH",
                    PointPairs = _pointCount,
                    RmsMm = RmsMm,
                    Version = 1,
                    CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    Operator = "ChainWizardV2",
                    Note = string.Format(CultureInfo.InvariantCulture,
                        "σ1={0:F3} σ2={1:F3} shapeDev={2:F4}", _sigma1, _sigma2, _shapeDev),
                },
            };
            if (double.TryParse((CalibZText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double cz))
                node.Meta.CalibZHeightMm = cz;
            if (HasDeltaRef)
                node.DeltaRefPixel = new[] { DeltaRefCol, DeltaRefRow };
            if (IsEih && HasPhotoPose)
                node.PhotoPose = new[] { PhotoPoseX, PhotoPoseY };
            return node;
        }
    }

    /// <summary>工具节点行（骨架决定主/副与绑定关系；向导只填实测偏移量）</summary>
    public class ChainToolRowViewModel : ViewModelBase
    {
        private double _offsetDx;
        private double _offsetDy;
        private bool _isMaster;
        private bool _isConcentric;     // ★ 三态之一：已测同心 ⇒ 显式落 (0,0) 放行（修复§6.4：值反推会把同心判成未测）
        private string _ready = "待测";

        public string ToolId { get; set; }
        /// <summary>主工具 = IsMaster；副工具自动绑定主（刚性阵列）</summary>
        public bool IsMaster
        {
            get { return _isMaster; }
            set { Set(ref _isMaster, value); }
        }
        /// <summary>副工具绑定的主工具 Id（推导器生成，只读展示）</summary>
        public string BindMasterToolId { get; set; }
        /// <summary>主=对针直量 T_TCP→Flange；副=相对主工具的 Δ（法兰系，U=0 基准，带符号）</summary>
        public double OffsetDx { get { return _offsetDx; } set { Set(ref _offsetDx, value); NotifyReady(); } }
        public double OffsetDy { get { return _offsetDy; } set { Set(ref _offsetDy, value); NotifyReady(); } }

        /// <summary>已测同心：pivoting/直量确认无偏心 ⇒ 显式 (0,0)，不再靠「值非零」反推（§6.4）</summary>
        public bool IsConcentric
        {
            get { return _isConcentric; }
            set { if (Set(ref _isConcentric, value)) NotifyReady(); }
        }

        // ---- 2026-09-30 新增：旋转中心（视觉路径）与 U0 基准角 ----

        private double? _rotationCenterDx;
        private double? _rotationCenterDy;
        private double _offsetBaseUDeg;

        /// <summary>
        /// 旋转中心（法兰系，mm）——由「旋转中心 e 采集步」的像素圆拟合经 H_FC 映射得到，
        /// 落 ChainTcpNode.URotationCenter。null = 未走视觉路径（此时由 pivoting 的 Offset 单独承担）。
        /// </summary>
        public double? RotationCenterDx
        {
            get { return _rotationCenterDx; }
            set { if (Set(ref _rotationCenterDx, value)) OnPropertyChanged(nameof(HasRotationCenter)); }
        }
        public double? RotationCenterDy
        {
            get { return _rotationCenterDy; }
            set { if (Set(ref _rotationCenterDy, value)) OnPropertyChanged(nameof(HasRotationCenter)); }
        }
        public bool HasRotationCenter { get { return _rotationCenterDx.HasValue && _rotationCenterDy.HasValue; } }

        /// <summary>
        /// Offset 的基准法兰角 U0（度）——落 ChainCalibMeta.OffsetBaseU。
        /// 0 = 标定在 U=0 完成（pivoting 解出的 e 本就是法兰系矢量，这是常态，不用改）；
        /// 非零时逆解用 R(U_final − U0)，供「Offset 由姿态相关的直量路径给出」时声明参考姿态
        /// （旧档迁移 / ETH 直量），避免那段旋转被丢掉后生产端系统性偏。
        /// </summary>
        public double OffsetBaseUDeg
        {
            get { return _offsetBaseUDeg; }
            set { if (Set(ref _offsetBaseUDeg, value)) OnPropertyChanged(nameof(OffsetBaseUText)); }
        }
        public string OffsetBaseUText
        {
            get { return _offsetBaseUDeg == 0 ? "0°（法兰系内蕴）" : _offsetBaseUDeg.ToString("F2", CultureInfo.InvariantCulture) + "°"; }
        }

        /// <summary>三态口径：勾同心 或 填了非零偏移 ⇒ 已测可落盘；否则视为未测（fail-closed）</summary>
        public bool OffsetFilled { get { return _isConcentric || _offsetDx != 0 || _offsetDy != 0; } }

        public string Ready
        {
            get { return _ready; }
            private set { Set(ref _ready, value); }
        }

        public string KindHeader
        {
            get
            {
                return IsMaster ? "主工具（对针直量）"
                     : "副工具（Δ 相对 " + (BindMasterToolId ?? "主") + "；同轴工具实测=(0,0)）";
            }
        }

        private void NotifyReady()
        {
            Ready = _isConcentric ? "✓ 已测(同心)"
                  : (_offsetDx != 0 || _offsetDy != 0) ? "✓ 已填" : "待测";
        }
    }

    /// <summary>步骤清单一行（=骨架节点×Workflow；依赖排序在生成时确定）</summary>
    public class ChainStepRow : ViewModelBase
    {
        private string _status = "待办";

        public int StepNo { get; set; }
        /// <summary>序号显示（"1."；绕开 XAML StringFormat 对结尾句点的转义坑）</summary>
        public string StepLabel { get { return StepNo + "."; } }
        /// <summary>目标节点（相机 Id / 工具 Id）</summary>
        public string Target { get; set; }
        /// <summary>Workflow 编号+名称（对齐《框架设计文档 V2.1》体系；WF-12=本仓 DeltaRefPixel）</summary>
        public string Workflow { get; set; }
        /// <summary>物料与前置条件（V2.1 物料层 UI 化：缺什么一目了然）</summary>
        public string Hint { get; set; }
        /// <summary>操作区分类（2026-09-28）：Camera=相机采集页 / Tool=工具偏移页 / Gate=门禁落盘页</summary>
        public StepZone Zone { get; set; }
        /// <summary>跳转提示（点击该行时显示"已切换到…"）</summary>
        public string JumpHint { get; set; }
        private bool _isSelected;
        /// <summary>当前选中行（步骤清单高亮 + 右侧操作区跟随）</summary>
        public bool IsSelected { get { return _isSelected; } set { Set(ref _isSelected, value); } }
        public string Status
        {
            get { return _status; }
            set { Set(ref _status, value); }
        }
    }

    /// <summary>步骤对应的操作区（用于"点步骤 → 切区域"）</summary>
    public enum StepZone { Camera, Tool, Rotation, Gate }

    /// <summary>
    /// 链向导 v2 主 VM：档案→推导器→骨架→步骤清单→采集回填→校验→落盘。
    /// 人工裁决仅两处：主工具指派（SwitchMaster 重推导）＋相机角色覆盖（后续版本；默认推导先行）。
    /// </summary>
    public class ChainWizardViewModel : ViewModelBase
    {
        private readonly StationCalibGraph _draft;      // 推导器骨架（节点+边齐全，矩阵待填）
        private string _stationCode;
        private string _planNotes = "";
        private string _validateResult = "未校验";
        private string _saveResult = "未保存";

        //---------------------------------------------------------------------
        // 相机接入（T10，2026-09-28）：设备池取图替代 OpenFileDialog
        //---------------------------------------------------------------------
        private readonly IDevicePool _devicePool;
        private readonly HalconImageRenderService _renderService = new HalconImageRenderService();
        // 特征识别算子宿主（圆/十字/模板 三路），照搬范式1：算法本体在 HalconWrapper，别在 UI 层重写
        private readonly CalibrationService _calibService = new CalibrationService();
        private readonly System.Threading.AutoResetEvent _frameArrivedEvent = new System.Threading.AutoResetEvent(false);
        private volatile FrameEventArgs _latestFrame;
        private volatile bool _captureWaitActive;
        private int _triggerMode = -1;

        public ChainWizardViewModel(string stationCode) : this(stationCode, null)
        {
        }

        /// <summary>
        /// 带「任务卡直达」的构造（#5，2026-09-30）：focusCard = 卡型键（H/E/T/S），
        /// 打开即把步骤清单定位到该卡对应的第一步——点哪张卡就落在哪个工作面，不用再自己找。
        /// </summary>
        public ChainWizardViewModel(string stationCode, string focusCard)
        {
            _stationCode = stationCode;
            var plan = PlanFromProfile(stationCode);
            _draft = plan.Ok ? plan.Draft : null;
            PlanNotes = plan.Ok
                ? "【系统自动裁决】" + string.Join("；", plan.AutoNotes)
                  + (plan.OpenQuestions.Count > 0 ? " ｜【请人工确认】" + string.Join("；", plan.OpenQuestions) : "")
                : "❌ 拓扑推导失败：" + string.Join("；", plan.Errors);

            try { _devicePool = App.StationHostRuntime?.DevicePool; }
            catch { _devicePool = null; }

            Sections = new ObservableCollection<ChainCameraSectionViewModel>();
            ToolRows = new ObservableCollection<ChainToolRowViewModel>();
            Steps = new ObservableCollection<ChainStepRow>();
            LoadCameraDevices();
            LoadMotionDevices();
            if (_draft != null)
                BuildUiFromDraft();
            SelectStepByCard(focusCard);
        }

        /// <summary>
        /// 按任务卡型把步骤清单定位到对应工作面（#5）。键沿用卡的量名首字母：
        /// H/S（手眼/像素当量）→ 相机九点步；E（旋转中心）→ 旋转采样步（缺则该工具的对针步）；T（对针）→ 工具步。
        /// 认不出的键不猜——保持默认（第一步），宁可多点一下也不要跳错工作面。
        /// </summary>
        private void SelectStepByCard(string focusCard)
        {
            if (string.IsNullOrWhiteSpace(focusCard)) return;
            string k = focusCard.Trim().ToUpperInvariant();
            ChainStepRow target;
            switch (k)
            {
                case "H":
                case "HANDEYE":
                case "S":
                case "PIXELSCALE":
                    target = Steps.FirstOrDefault(s => s.Zone == StepZone.Camera);
                    break;
                case "E":
                case "TOOLROTATION":
                case "ROTATION":
                    target = Steps.FirstOrDefault(s => s.Zone == StepZone.Rotation)
                          ?? Steps.FirstOrDefault(s => s.Zone == StepZone.Tool);
                    break;
                case "T":
                case "TOOLOFFSET":
                    target = Steps.FirstOrDefault(s => s.Zone == StepZone.Tool);
                    break;
                default:
                    return;
            }
            if (target != null) SelectStep(target);
        }

        //---------------------------------------------------------------------
        // 相机接入：枚举 / 连接 / 单帧采集（照抄 CameraTune 成熟模式）
        //---------------------------------------------------------------------

        /// <summary>设备池中的相机列表（ICamera）</summary>
        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();

        private ICamera _selectedCameraDevice;
        /// <summary>当前选择的相机（各相机 Tab 共用同一个选择；如需分别绑定可后续扩展为每 Section 一选择）</summary>
        public ICamera SelectedCameraDevice
        {
            get { return _selectedCameraDevice; }
            set
            {
                if (Set(ref _selectedCameraDevice, value))
                {
                    OnPropertyChanged(nameof(CameraConnectedText));
                    OnPropertyChanged(nameof(IsCameraConnected));
                    OnPropertyChanged(nameof(CameraAccessHint));
                }
            }
        }

        /// <summary>相机是否已连接（据 State 判断）</summary>
        public bool IsCameraConnected
        {
            get { return _selectedCameraDevice != null && _selectedCameraDevice.State == DeviceState.Connected; }
        }

        public string CameraConnectedText
        {
            get
            {
                if (_selectedCameraDevice == null) return "未选择相机";
                return _selectedCameraDevice.DeviceKey + "　" + (IsCameraConnected ? "● 已连接" : "○ 未连接");
            }
        }

        /// <summary>相机接入区提示（无相机/未选/已连）</summary>
        public string CameraAccessHint
        {
            get
            {
                if (_devicePool == null) return "设备池未初始化（请在主程序运行时使用本向导取图）；仍可用『载入图像…』离线选图。";
                if (CameraDeviceList.Count == 0) return "设备池中未发现相机——请到硬件设备页扫描/连接相机；仍可用『载入图像…』离线选图。";
                if (_selectedCameraDevice == null) return "请先选择相机。";
                return IsCameraConnected
                    ? "已连接。点『单帧取图』把当前相机画面送入本机位图像区（走位 → 取图 → 点选）。"
                    : "点『连接相机』建立连接后再取图。";
            }
        }

        private string _cameraLog = "相机未接入。";
        public string CameraLog { get { return _cameraLog; } private set { Set(ref _cameraLog, value); } }

        /// <summary>重新从设备池枚举相机</summary>
        public void LoadCameraDevices()
        {
            CameraDeviceList.Clear();
            try
            {
                var all = _devicePool?.GetAllDevices();
                if (all != null)
                    foreach (var cam in all.OfType<ICamera>()) CameraDeviceList.Add(cam);
            }
            catch (Exception ex) { CameraLog = "枚举相机异常：" + ex.Message; }
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();
            OnPropertyChanged(nameof(CameraAccessHint));
        }

        /// <summary>连接当前相机（已连则直接成功）</summary>
        public void ConnectCamera()
        {
            var cam = _selectedCameraDevice;
            if (cam == null) { CameraLog = "⚠ 未选择相机。"; return; }
            if (cam.State == DeviceState.Connected) { CameraLog = "相机已连接：" + cam.DeviceKey; return; }
            var r = cam.Connect();
            if (r == null || !r.Success) { CameraLog = "⚠ 连接失败：" + (r?.Message ?? "无应答"); }
            else { CameraLog = "相机已连接：" + cam.DeviceKey; }
            OnPropertyChanged(nameof(IsCameraConnected));
            OnPropertyChanged(nameof(CameraConnectedText));
            OnPropertyChanged(nameof(CameraAccessHint));
        }

        /// <summary>切软触发（标定采样纪律：走位 → 软触发 → 本点新帧）</summary>
        private void EnsureTriggered(ICamera cam)
        {
            if (_triggerMode == 1) return;
            var r = cam.ConfigureSoftwareTrigger();
            if (r == null || !r.Success) cam.SetTriggerMode(1);
            _triggerMode = 1;
        }

        /// <summary>
        /// 单帧采集（软触发：武装 → 起流 → 触发 → 等新帧，最多 5 次；失败返回 null 绝不沿用旧帧）。
        /// 照抄 CameraTune 的 CaptureOnce 纪律：走位后旧帧 = 上一位置的坐标。
        /// </summary>
        public FrameEventArgs CaptureOnce()
        {
            var cam = _selectedCameraDevice;
            if (cam == null) { CameraLog = "⚠ 未选择相机。"; return null; }
            if (cam.State != DeviceState.Connected)
            {
                var cr = cam.Connect();
                if (cr == null || !cr.Success) { CameraLog = "⚠ 相机连接失败：" + (cr?.Message ?? "无应答"); return null; }
            }
            EnsureTriggered(cam);
            var sr = cam.StartGrabbing();
            if (sr == null || !sr.Success) { CameraLog = "⚠ 启动采集流失败：" + (sr?.Message ?? "无应答"); return null; }

            _latestFrame = null;
            cam.FrameReceived -= OnCameraFrameReceived;
            cam.FrameReceived += OnCameraFrameReceived;
            const int maxAttempts = 5, firstWaitMs = 2500, retryWaitMs = 800;
            _captureWaitActive = true;
            try
            {
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    _frameArrivedEvent.Reset();
                    _latestFrame = null;
                    var trig = cam.SoftTrigger();
                    if (trig == null || !trig.Success) trig = cam.SoftwareTrigger();
                    if (trig == null || !trig.Success)
                    {
                        System.Threading.Thread.Sleep(150);
                        continue;
                    }
                    int waitMs = attempt == 1 ? firstWaitMs : retryWaitMs;
                    if (_frameArrivedEvent.WaitOne(waitMs) && _latestFrame != null) return _latestFrame;
                    if (attempt < maxAttempts) System.Threading.Thread.Sleep(80);
                }
            }
            finally { _captureWaitActive = false; }
            CameraLog = "⚠ 软触发 " + maxAttempts + " 次均未等到新帧——本次采图放弃（不沿用旧图）。";
            return null;
        }

        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            if (!_captureWaitActive) return;
            _latestFrame = e;
            _frameArrivedEvent.Set();
        }

        //---------------------------------------------------------------------
        // 图像入段（2026-09-30）：相机帧 / 本地文件 → 指定相机段的图像区
        //   ★ Bug 修复：『载入图像…』『单帧取图』两个按钮在采集工具栏里（TabControl 之外），
        //     它们的 DataContext 是窗口 VM，不是 ChainCameraSectionViewModel ⇒ 由视图显式
        //     传 sec 进来，不能靠 sender.DataContext 推断。
        //---------------------------------------------------------------------

        /// <summary>把本地图像文件载入指定相机段（离线选图：无相机时也能练流程）</summary>
        public void LoadImageIntoSection(ChainCameraSectionViewModel sec, string path)
        {
            if (sec == null || string.IsNullOrWhiteSpace(path)) return;
            try
            {
                sec.Image = LoadBitmapFromFile(path);
                // 本地文件 → Halcon 视窗 + 识别上下文：WrapImage 直接认文件路径（无需先转帧）
                var ctx = CreateContextFromFile(path, sec.CameraId);
                if (ctx != null) sec.SetImageContext(ctx);
                CameraLog = "已载入本地图像：" + System.IO.Path.GetFileName(path)
                    + (ctx == null ? "（⚠ 该格式无法转入特征识别视窗，仅预览）" : "");
            }
            catch (Exception ex)
            {
                CameraLog = "⚠ 载入图像失败：" + ex.Message;
            }
        }

        /// <summary>把相机新帧送入指定相机段（走位 → 软触发 → 本点新帧）</summary>
        public void ShowFrameInSection(ChainCameraSectionViewModel sec, FrameEventArgs frame)
        {
            if (sec == null || frame == null) return;
            var bmp = FrameToBitmap(frame);
            if (bmp == null)
            {
                CameraLog = "⚠ 帧格式暂不支持显示（PixelFormat=" + (frame.PixelFormat ?? "?") + "）；当前仅支持 Mono8 / RGB8。";
                return;
            }
            sec.Image = bmp;
            var ctx = _renderService.CreateRenderContextFromFrame(frame, "ChainWizard-" + sec.CameraId, "ChainWiz_" + sec.CameraId);
            if (ctx != null) sec.SetImageContext(ctx);
        }

        /// <summary>
        /// 识别指定相机段图像上的特征点（圆 / 十字 / 模板匹配）：结果叠加到视窗 + 刷新匹配分卡片。
        /// ★ 不另写算法：直接把图像交给 HalconWrapper 的 CalibrationService.ExtractFeaturePreview
        ///   （与生产 ShapeMatch 节点 / 九点采样同一套算子）。识别结论写回 sec（供回填点对表）。
        /// </summary>
        public string RecognizeFeature(ChainCameraSectionViewModel sec)
        {
            if (sec == null) return "\u26a0 请先选择相机 Tab。";
            var img = sec.RecognitionImage;
            if (img == null) return "\u26a0 当前无图像——请先『单帧取图』或『载入图像…』。";

            SyncExtractOptions(sec);
            string name = sec.IsTemplateFeature
                ? "模板匹配（" + (string.IsNullOrWhiteSpace(sec.FeatureTemplateName) ? "未选模板" : sec.FeatureTemplateName) + "）"
                : sec.IsCrossFeature ? "十字 Mark" : "圆形 Mark";
            try
            {
                var res = _calibService.ExtractFeaturePreview(img, sec.FeatureType);
                sec.ApplyMatchReport(_calibService.LastMatchReport);
                if (res != null && res.Success)
                {
                    return "\u2713 " + name + " 识别成功：像素 u="
                         + res.Data.PixelX.ToString("F2", CultureInfo.InvariantCulture)
                         + ", v=" + res.Data.PixelY.ToString("F2", CultureInfo.InvariantCulture)
                         + "（已在视窗叠加标记）";
                }
                return "\u2717 " + name + " 未识别：" + (res == null ? "无结果" : res.Message);
            }
            catch (Exception ex)
            {
                ClearRecognition(sec);
                return "\u26a0 识别异常：" + ex.Message;
            }
        }

        /// <summary>把本段模板参数同步进算子（每次识别前调，避免跨段串参数）</summary>
        private void SyncExtractOptions(ChainCameraSectionViewModel sec)
        {
            var o = _calibService.ExtractOptions;
            if (o == null) return;
            o.TemplateName = sec.FeatureTemplateName;
            o.TemplateMinScore = sec.TemplateMinScore;
            o.TemplateAngleStart = sec.TemplateAngleStart;
            o.TemplateAngleEnd = sec.TemplateAngleEnd;
        }

        private static void ClearRecognition(ChainCameraSectionViewModel sec)
        {
            if (sec != null) sec.ClearRecognition();
        }

        /// <summary>把识别到的像素回填到该段当前选中行（识别 → 点对表 的落点）</summary>
        public string ApplyRecognizedToSelectedRow(ChainCameraSectionViewModel sec)
        {
            if (sec == null) return "\u26a0 请先选择相机 Tab。";
            if (!sec.HasRecognized) return "\u26a0 尚未识别到特征——先点『\ud83c\udfaf 识别特征』。";
            if (sec.SelectedRow == null) return "\u26a0 请先在点对表格中选中要回填的那一行。";
            sec.SetPixelFromClick(sec.RecognizedCol, sec.RecognizedRow);
            return "\u2713 已把识别像素回填到第 " + (sec.Points.IndexOf(sec.SelectedRow) + 1) + " 行。";
        }

        /// <summary>本地文件 → 渲染上下文（WrapImage 直接认文件路径；HImage 生命周期归上下文）</summary>
        private WpfImageRenderContext CreateContextFromFile(string path, string cameraId)
        {
            try
            {
                var renderImage = _renderService.WrapImage(path);
                if (renderImage == null) return null;
                return new WpfImageRenderContext
                {
                    NodeId = "ChainWiz_" + cameraId,
                    NodeName = "ChainWizard-" + cameraId,
                    Image = renderImage,
                    Thumbnail = _renderService.CreateThumbnail(renderImage),
                };
            }
            catch (Exception ex)
            {
                CameraLog = "⚠ 转 Halcon 视窗失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>本地图像读取（缓存到内存，释放文件句柄——现场要反复重拍）</summary>
        private static System.Windows.Media.Imaging.BitmapSource LoadBitmapFromFile(string path)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        /// <summary>帧 → BitmapSource（Mono8 灰度 / RGB8，紧致缓冲直拷；其它格式返回 null 并提示）</summary>
        public static System.Windows.Media.Imaging.BitmapSource FrameToBitmap(FrameEventArgs f)
        {
            if (f == null || f.Buffer == null || f.Width <= 0 || f.Height <= 0) return null;
            var fmt = (f.PixelFormat ?? "Mono8").ToUpperInvariant();
            System.Windows.Media.PixelFormat pf;
            int channels;
            if (fmt.Contains("MONO8") || fmt == "MONO" || fmt == "GRAY8") { pf = System.Windows.Media.PixelFormats.Gray8; channels = 1; }
            else if (fmt.Contains("RGB8") || fmt.Contains("BGR8")) { pf = System.Windows.Media.PixelFormats.Bgr24; channels = 3; }
            else return null;

            int stride = f.Width * channels;
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(
                f.Width, f.Height, 96, 96, pf, null, f.Buffer, stride);
            bmp.Freeze();
            return bmp;
        }

        //---------------------------------------------------------------------
        // 轴操控（T18，2026-09-28）：运动设备连接 / 点动 / 回读位姿
        //   标定必须能"走位"，否则无从采集；位姿回填同一入口（板卡编码器级）
        //---------------------------------------------------------------------

        /// <summary>设备池中的运动设备（IMotionCard）</summary>
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();

        private IMotionCard _selectedMotionDevice;
        public IMotionCard SelectedMotionDevice
        {
            get { return _selectedMotionDevice; }
            set
            {
                if (Set(ref _selectedMotionDevice, value))
                {
                    OnPropertyChanged(nameof(MotionConnectedText));
                    OnPropertyChanged(nameof(IsMotionConnected));
                    OnPropertyChanged(nameof(MotionAccessHint));
                }
            }
        }

        public bool IsMotionConnected
        {
            get { return _selectedMotionDevice != null && _selectedMotionDevice.State == DeviceState.Connected; }
        }

        public string MotionConnectedText
        {
            get
            {
                if (_selectedMotionDevice == null) return "未选择运动设备";
                return _selectedMotionDevice.DeviceKey + "　" + (IsMotionConnected ? "● 已连接" : "○ 未连接");
            }
        }

        public string MotionAccessHint
        {
            get
            {
                if (_devicePool == null) return "设备池未初始化——轴操控不可用（可用示教器走位后手抄位姿）。";
                if (MotionDeviceList.Count == 0) return "设备池中未发现运动设备/机器人——请到硬件设备页连接；此处仍可手抄示教器位姿。";
                if (_selectedMotionDevice == null) return "请先选择运动设备。";
                return IsMotionConnected ? "已连接。可用方向键点动，或点『回读位姿』把当前坐标填入点对表。" : "点『连接运动设备』后可用轴操控。";
            }
        }

        private string _motionLog = "运动设备未接入。";
        public string MotionLog { get { return _motionLog; } private set { Set(ref _motionLog, value); } }

        /// <summary>点动步长（mm）</summary>
        private double _jogStep = 1.0;
        public double JogStep { get { return _jogStep; } set { Set(ref _jogStep, value); } }

        private double _jogSpeed = 30.0;
        public double JogSpeed { get { return _jogSpeed; } set { Set(ref _jogSpeed, value); } }

        /// <summary>
        /// 轴槽位映射。★默认按 EPSON SCARA = X0 / Y1 / Z2 / U3。
        /// ★2026-09-30 修正：原默认 U=2 与 Z=2 **撞车**——撞车时 MoveAbsolute 会把位移写到
        /// 重复的那根轴上且**零报错**（"没下 Z 却动了 U"），是血泪项，故默认值改为互不相同。
        /// </summary>
        public int AxisX { get { return _axisX; } set { if (Set(ref _axisX, value)) RaiseAxisSlotChanged(); } }
        public int AxisY { get { return _axisY; } set { if (Set(ref _axisY, value)) RaiseAxisSlotChanged(); } }
        public int AxisU { get { return _axisU; } set { if (Set(ref _axisU, value)) RaiseAxisSlotChanged(); } }
        private int _axisX = 0, _axisY = 1, _axisU = 3;

        private void RaiseAxisSlotChanged()
        {
            OnPropertyChanged(nameof(AxisSlotCollisionText));
            OnPropertyChanged(nameof(HasAxisSlotCollision));
            OnPropertyChanged(nameof(GridAxisHint));
        }

        /// <summary>轴槽位是否撞车（任意两轴同槽位）</summary>
        public bool HasAxisSlotCollision
        {
            get
            {
                var s = new[] { AxisX, AxisY, AxisZ, AxisU };
                return s.Distinct().Count() != s.Length;
            }
        }

        /// <summary>轴槽位撞车告警（无撞车为空串，UI 用 BoolToVis 控制显隐）</summary>
        public string AxisSlotCollisionText
        {
            get
            {
                if (!HasAxisSlotCollision) return string.Empty;
                return "⚠ 轴槽位撞车：X=" + AxisX + " Y=" + AxisY + " Z=" + AxisZ + " U=" + AxisU
                     + " 存在重复槽位。撞车时点动 Z 会写到重复的那根轴上且不报错——"
                     + "EPSON SCARA 应为 X0/Y1/Z2/U3，请改为互不相同的槽位后再点动。";
            }
        }

        /// <summary>回读的当前位姿（板卡编码器）</summary>
        private string _currentPoseText = "—";
        public string CurrentPoseText { get { return _currentPoseText; } private set { Set(ref _currentPoseText, value); } }

        /// <summary>最近一次成功回读的位姿（供『回填当前点到选中行』使用）。
        /// ★ 保存的是原始数值而不是显示字符串——回填是落盘数据源，不能靠解析 UI 文本。</summary>
        private double _lastPoseX = double.NaN;
        private double _lastPoseY = double.NaN;
        private double _lastPoseU = double.NaN;
        public bool HasLastPose
        {
            get { return !double.IsNaN(_lastPoseX) && !double.IsNaN(_lastPoseY); }
        }
        public bool HasLastPoseU
        {
            get { return HasLastPose && !double.IsNaN(_lastPoseU); }
        }

        /// <summary>取最近一次回读位姿；无有效回读返回 false</summary>
        public bool TryGetLastPose(out double x, out double y)
        {
            x = _lastPoseX;
            y = _lastPoseY;
            return HasLastPose;
        }

        /// <summary>取最近一次回读位姿（含 U）；U 未回读成功返回 false</summary>
        public bool TryGetLastPoseU(out double x, out double y, out double u)
        {
            x = _lastPoseX;
            y = _lastPoseY;
            u = _lastPoseU;
            return HasLastPoseU;
        }

        /// <summary>手工录入位姿（示教器手抄场景）——与回读同一落点，供回填使用</summary>
        public void SetPose(double x, double y)
        {
            SetPose(x, y, double.NaN);
        }

        /// <summary>手工录入位姿（含 U 角，pivoting 场景需要）</summary>
        public void SetPose(double x, double y, double u)
        {
            _lastPoseX = x;
            _lastPoseY = y;
            _lastPoseU = u;
            CurrentPoseText = double.IsNaN(u)
                ? string.Format(CultureInfo.InvariantCulture, "X={0:F3}  Y={1:F3}", x, y)
                : string.Format(CultureInfo.InvariantCulture, "X={0:F3}  Y={1:F3}  U={2:F2}°", x, y, u);
            OnPropertyChanged(nameof(HasLastPose));
            OnPropertyChanged(nameof(HasLastPoseU));
            OnPropertyChanged(nameof(CurrentPoseText));
        }

        public void LoadMotionDevices()
        {
            MotionDeviceList.Clear();
            try
            {
                var all = _devicePool?.GetAllDevices();
                if (all != null)
                    foreach (var m in all.OfType<IMotionCard>()) MotionDeviceList.Add(m);
            }
            catch (Exception ex) { MotionLog = "枚举运动设备异常：" + ex.Message; }
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault();
            OnPropertyChanged(nameof(MotionAccessHint));
        }

        public void ConnectMotion()
        {
            var m = _selectedMotionDevice;
            if (m == null) { MotionLog = "⚠ 未选择运动设备。"; return; }
            if (m.State == DeviceState.Connected) { MotionLog = "运动设备已连接：" + m.DeviceKey; return; }
            var r = m.Connect();
            MotionLog = (r == null || !r.Success) ? "⚠ 连接失败：" + (r?.Message ?? "无应答") : "运动设备已连接：" + m.DeviceKey;
            OnPropertyChanged(nameof(IsMotionConnected));
            OnPropertyChanged(nameof(MotionConnectedText));
            OnPropertyChanged(nameof(MotionAccessHint));
        }

        /// <summary>点动一轴（相对移动）</summary>
        public void Jog(int axis, int dir)
        {
            var m = _selectedMotionDevice;
            if (m == null) { MotionLog = "⚠ 未选择运动设备。"; return; }
            double step = JogStep * dir;
            var r = m.MoveRelative(axis, (float)step, (float)JogSpeed);
            MotionLog = (r != null && r.Success)
                ? string.Format(CultureInfo.InvariantCulture, "轴{0} {1}{2:F3}mm 已下发", axis, dir > 0 ? "+" : "-", Math.Abs(step))
                : "轴" + axis + " 移动失败：" + (r?.Message ?? "无应答");
        }

        /// <summary>回读位姿（X/Y[/U]）并缓存，供后续回填点对表/pivoting</summary>
        public void ReadPose()
        {
            var m = _selectedMotionDevice;
            if (m == null) { MotionLog = "⚠ 未选择运动设备。"; return; }
            try
            {
                var xr = m.GetFeedbackPosition(AxisX);
                var yr = m.GetFeedbackPosition(AxisY);
                double x = (xr != null && xr.Success) ? xr.Data : double.NaN;
                double y = (yr != null && yr.Success) ? yr.Data : double.NaN;
                if (double.IsNaN(x) || double.IsNaN(y))
                {
                    MotionLog = "⚠ 回读未取得有效值——该轴无编码器反馈？可用示教器手抄后手工录入位姿。";
                    return;
                }
                // U 轴尽力读（pivoting 需要；读不到不阻塞 X/Y 回填）
                double u = double.NaN;
                try
                {
                    var ur = m.GetFeedbackPosition(AxisU);
                    if (ur != null && ur.Success) u = ur.Data;
                }
                catch { /* U 轴读不到就算了 */ }
                SetPose(x, y, u);
                MotionLog = double.IsNaN(u)
                    ? "位姿已回读（X/Y；U 轴读不到——pivoting 请手抄 U 角后点『回填对针点』）。"
                    : "位姿已回读并缓存（X/Y/U）。";
            }
            catch (Exception ex) { MotionLog = "回读失败：" + ex.Message; }
        }

        //---------------------------------------------------------------------
        // 九点网格采集与走位（2026-09-30 补）
        //   链向导原有能力只有"方向键点动 + 回读位姿 + 手填表格"——一个九点要人工走位 9 次、
        //   手抄 9 次坐标。本段接成：设基准 → 生成目标 → 逐点走位 → 自动取图 → 人点选回填。
        //   ★ 只做【逐点触发】，不做全自动循环：真机上操作员必须全程可见、可急停。
        //   ★ Epson 必须走 CP 直线（MoveToLinear→LMOVE）：PTP(Go) 在关节空间插补、末端走弧线，
        //     会让"沿世界 X / 沿世界 Y"的像素当量不等（现场实测差 13.78%、夹角偏 15.3°），
        //     九点矩阵各向异性 σ1/σ2≈1.49 直接被形状门拦下。LMOVE 是【同步】的（到位才回 DONE），
        //     返回即可采图；PTP 是异步的，不能直接拿返回值当到位。
        //---------------------------------------------------------------------

        private double _safeZ;
        /// <summary>安全高度 Z（mm）：平移前先抬到此高度（低位横穿会刮碰，血泪项）</summary>
        public double SafeZ { get { return _safeZ; } set { Set(ref _safeZ, value); } }

        private double _wizardMoveSpeed = 30.0;
        /// <summary>标定走位速度</summary>
        public double WizardMoveSpeed { get { return _wizardMoveSpeed; } set { Set(ref _wizardMoveSpeed, value); } }

        private int _axisZ = 2;
        /// <summary>Z 轴槽位（安全高度用）。★ 轴槽撞车会让 MoveAbsolute 写到别的轴且不报错</summary>
        public int AxisZ
        {
            get { return _axisZ; }
            set { if (Set(ref _axisZ, value)) RaiseAxisSlotChanged(); }
        }

        /// <summary>网格面板是否可用（当前为相机步 + 该相机段支持九点）</summary>
        public bool CanUseGrid
        {
            get { return IsCameraStepSelected && SelectedSection != null && SelectedSection.SupportsGrid; }
        }

        public string GridAxisHint
        {
            get
            {
                return "轴槽位：X=" + AxisX + " Y=" + AxisY + " Z=" + AxisZ + " U=" + AxisU
                     + "（EPSON SCARA 为 X0/Y1/Z2/U3；槽位须两两不同）";
            }
        }

        /// <summary>把当前回读位姿设为网格中心基准（须先『回读位姿』）</summary>
        public void SetGridBaseFromPose()
        {
            var sec = SelectedSection;
            if (sec == null) { MotionLog = "⚠ 请先选择相机 Tab。"; return; }
            if (!sec.SupportsGrid) { MotionLog = "本段为下相机纠偏段，无九点网格。"; return; }
            double x, y;
            if (!TryGetLastPose(out x, out y))
            {
                MotionLog = "⚠ 尚无有效位姿——请先『回读位姿』（或示教器手抄后在右栏录入），再设基准。";
                return;
            }
            sec.SetGridBase(x, y);
            MotionLog = string.Format(CultureInfo.InvariantCulture,
                "网格中心基准已设为 ({0:F3}, {1:F3})——接着填步长、点『生成 9 点目标』。", x, y);
        }

        /// <summary>按基准 + 步长生成 9 点目标</summary>
        public void BuildGridTargets()
        {
            var sec = SelectedSection;
            if (sec == null) { MotionLog = "⚠ 请先选择相机 Tab。"; return; }
            int n = sec.BuildGridTargets();
            MotionLog = n > 0
                ? "已生成 " + n + " 个网格目标（" + (sec.IsEih ? "EIH：写入拍照机位 PhotoX/Y" : "ETH：写入世界坐标 WorldX/Y") + "）。"
                : sec.GridStatus;
        }

        /// <summary>走位到下一个未采集点 → 取图（人点选回填）</summary>
        public void MoveToNextPoint()
        {
            var sec = SelectedSection;
            if (sec == null) { MotionLog = "⚠ 请先选择相机 Tab。"; return; }
            if (!sec.SupportsGrid) { MotionLog = "本段为下相机纠偏段，无九点网格走位。"; return; }
            if (!sec.HasGridBase) { MotionLog = "⚠ 请先设网格中心基准，再生成目标。"; return; }

            int orderPos;
            var row = sec.NextUncapturedRow(out orderPos);
            if (row == null) { MotionLog = "✓ 9 点均已有像素（已采完），无需再走位。"; return; }

            double tx, ty;
            if (!sec.TryGetMoveTarget(row, out tx, out ty))
            {
                MotionLog = "⚠ 第 " + (sec.Points.IndexOf(row) + 1) + " 行无目标坐标——请先『生成 9 点目标』。";
                return;
            }

            if (!MoveToPointWithSafeZ(tx, ty)) return;

            // ★ 走位后用【实际反馈】替代指令值回填（旧向导血泪：World 记指令值 = 把到位误差算进矩阵）
            double ax = tx, ay = ty;
            try
            {
                var m = _selectedMotionDevice;
                var fx = m?.GetFeedbackPosition(AxisX);
                var fy = m?.GetFeedbackPosition(AxisY);
                if (fx != null && fx.Success) ax = fx.Data;
                if (fy != null && fy.Success) ay = fy.Data;
            }
            catch { /* 反馈读不到就沿用指令值，不阻断采样 */ }
            if (sec.IsEih) { row.PhotoX = Math.Round(ax, 3); row.PhotoY = Math.Round(ay, 3); }
            else { row.WorldX = Math.Round(ax, 3); row.WorldY = Math.Round(ay, 3); }

            // 取图（软触发新帧；走位后旧帧 = 上一位置的坐标，绝不沿用）
            var frame = CaptureOnce();
            if (frame != null)
            {
                var bmp = FrameToBitmap(frame);
                if (bmp != null) sec.Image = bmp;
            }

            sec.SelectedRow = row;          // 预选该行，点选即回填
            sec.RefreshGridStatus();
            MotionLog = frame != null
                ? string.Format(CultureInfo.InvariantCulture,
                    "已走到第 {0} 点（次序 {1}/9；目标 {2:F3},{3:F3} → 实际 {4:F3},{5:F3}）并取图，请在图上点选该点像素。",
                    sec.Points.IndexOf(row) + 1, orderPos + 1, tx, ty, ax, ay)
                : string.Format(CultureInfo.InvariantCulture,
                    "已走到第 {0} 点（次序 {1}/9）但取图失败——请检查相机后重新『走到下一点』。",
                    sec.Points.IndexOf(row) + 1, orderPos + 1);
        }

        /// <summary>走到指定行（表格里逐点补采用）</summary>
        public void MoveToRow(ChainPointRow row)
        {
            var sec = SelectedSection;
            if (sec == null || row == null) return;
            double tx, ty;
            if (!sec.TryGetMoveTarget(row, out tx, out ty))
            {
                MotionLog = "⚠ 该行无目标坐标——请先『生成 9 点目标』。";
                return;
            }
            if (!MoveToPointWithSafeZ(tx, ty)) return;
            sec.SelectedRow = row;
            var frame = CaptureOnce();
            if (frame != null) { var bmp = FrameToBitmap(frame); if (bmp != null) sec.Image = bmp; }
            MotionLog = string.Format(CultureInfo.InvariantCulture,
                "已走到第 {0} 点 ({1:F3}, {2:F3}) 并取图，请在图上点选像素。", sec.Points.IndexOf(row) + 1, tx, ty);
        }

        /// <summary>抬 SafeZ → 直线走位到 (x,y)。无运动设备=演示模式（不阻塞，提示人工走位）</summary>
        private bool MoveToPointWithSafeZ(double x, double y)
        {
            var m = _selectedMotionDevice;
            if (m == null)
            {
                MotionLog = string.Format(CultureInfo.InvariantCulture,
                    "⚠ 未绑定运动设备（演示模式）——未走位；请人工走到 ({0:F3}, {1:F3}) 后『单帧取图』再点选。", x, y);
                return true;
            }
            if (m.State != DeviceState.Connected)
            {
                var conn = m.Connect();
                if (conn != null && !conn.Success) { MotionLog = "⚠ 运动设备连接失败：" + conn.Message; return false; }
            }

            // 1) 先抬安全 Z（低位横穿 = 刮碰风险）
            try
            {
                var cz = m.GetFeedbackPosition(AxisZ);
                if (cz != null && cz.Success && Math.Abs(cz.Data - SafeZ) > 1e-6)
                {
                    var rz = m.MoveAbsolute(AxisZ, (float)SafeZ, (float)WizardMoveSpeed);
                    if (rz != null && !rz.Success)
                    {
                        MotionLog = "⚠ 抬安全 Z 到 " + SafeZ.ToString("F1") + " 失败：" + rz.Message + "——已中止走位。";
                        return false;
                    }
                    WaitAxesIdle(AxisZ);
                }
            }
            catch (Exception ex) { MotionLog = "抬安全 Z 异常（继续尝试平移）：" + ex.Message; }

            // 2) 直线平移：Epson 必走 LMOVE
            if (m is EpsonRobot epson)
            {
                var cur = epson.GetPositionsAll();
                if (cur == null || !cur.Success)
                {
                    MotionLog = "⚠ 读取 Epson 当前 Z/U 失败：" + (cur?.Message ?? "无应答") + "——已中止走位。";
                    return false;
                }
                var rl = epson.MoveToLinear((float)x, (float)y,
                                            cur.Data[EpsonRobot.AxisZ], cur.Data[EpsonRobot.AxisU],
                                            (float)WizardMoveSpeed);
                if (rl != null && !rl.Success)
                {
                    MotionLog = "⚠ Epson 直线走位被拒：" + rl.Message
                              + "——该点可能在可达域外（4001 内圈空洞 / 4007 够不着 / 4041 背面禁区）；"
                              + "请检查基准与步长，或改小步长，不要反复重试同一点。";
                    return false;
                }
                // LMOVE 同步语义（到位才回 DONE）⇒ 返回即可采图，无需再等到位
                MotionLog = string.Format(CultureInfo.InvariantCulture, "已直线走到 ({0:F3}, {1:F3})。", x, y);
                return true;
            }

            var rx = m.MoveAbsolute(AxisX, (float)x, (float)WizardMoveSpeed);
            var ry = m.MoveAbsolute(AxisY, (float)y, (float)WizardMoveSpeed);
            if ((rx != null && !rx.Success) || (ry != null && !ry.Success))
            {
                MotionLog = "⚠ 走位被拒：" + (rx != null && !rx.Success ? rx.Message : "")
                          + (ry != null && !ry.Success ? " / " + ry.Message : "");
                return false;
            }
            WaitAxesIdle(AxisX, AxisY);
            MotionLog = string.Format(CultureInfo.InvariantCulture, "已走到 ({0:F3}, {1:F3})。", x, y);
            return true;
        }

        /// <summary>等待轴空闲（超时 15s 不抛异常，仅放弃等待）</summary>
        private void WaitAxesIdle(params int[] axes)
        {
            var m = _selectedMotionDevice;
            if (m == null) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 15000)
            {
                bool all = true;
                foreach (var a in axes)
                {
                    try { var r = m.IsAxisIdle(a); if (r != null && r.Success && !r.Data) { all = false; break; } }
                    catch { }
                }
                if (all) return;
                System.Threading.Thread.Sleep(50);
            }
        }

        //---------------------------------------------------------------------
        // 工具 TCP 采集（T14，2026-09-28）：针尖对点 pivoting——多角度扎点 → 最小二乘 → 残差门禁 → 写入
        //   行业标准工作面：每工具一组对针点（U 散开 ≥90°），解出 e=(ex,ey) 与残差，
        //   残差=对针重复精度即验收门；解出 |e|≈0 ⇒ 引导勾『同心』显式落 (0,0)（§6.4 三态）。
        //---------------------------------------------------------------------

        private readonly Dictionary<string, ObservableCollection<ChainPivotRow>> _pivotStore
            = new Dictionary<string, ObservableCollection<ChainPivotRow>>(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<ChainPivotRow> _pivotRows = new ObservableCollection<ChainPivotRow>();
        /// <summary>当前工具的对针点表（切工具自动换表，数据按 ToolId 保留）</summary>
        public ObservableCollection<ChainPivotRow> PivotRows
        {
            get { return _pivotRows; }
            private set { Set(ref _pivotRows, value); }
        }

        private ChainToolRowViewModel _pivotTool;
        /// <summary>pivoting 面板当前操作的工具</summary>
        public ChainToolRowViewModel PivotTool
        {
            get { return _pivotTool; }
            set
            {
                if (Set(ref _pivotTool, value))
                {
                    var t = _pivotTool;
                    if (t != null)
                    {
                        ObservableCollection<ChainPivotRow> list;
                        if (!_pivotStore.TryGetValue(t.ToolId, out list))
                        {
                            list = new ObservableCollection<ChainPivotRow>();
                            for (int i = 0; i < 4; i++) list.Add(new ChainPivotRow("A" + (i + 1)));
                            _pivotStore[t.ToolId] = list;
                        }
                        PivotRows = list;
                        ClearPivotViz();      // 切工具即清图：旧工具的散点留在屏上会被当成新结论
                        PivotResultText = "工具 " + t.ToolId + "：" + t.Ready
                            + (t.IsConcentric ? "（已勾同心，无需再对针）" : "——把法兰转到不同 U 角扎同一针尖，每角回填一次读数");
                    }
                    OnPropertyChanged(nameof(PivotReadyText));
                }
            }
        }

        private ChainPivotRow _selectedPivotRow;
        /// <summary>对针点表选中行（『回填对针点』写入目标）</summary>
        public ChainPivotRow SelectedPivotRow
        {
            get { return _selectedPivotRow; }
            set { Set(ref _selectedPivotRow, value); }
        }

        public string PivotReadyText
        {
            get { return _pivotTool == null ? "" : _pivotTool.Ready; }
        }

        private string _pivotResultText = "选工具 → 转 U 角扎针 → 回读回填 → 求解";
        public string PivotResultText { get { return _pivotResultText; } private set { Set(ref _pivotResultText, value); } }

        /// <summary>最近一次 pivoting 解出的偏移（供『写入工具偏移』）</summary>
        public bool PivotHasResult { get; private set; }
        private double _pivotEx, _pivotEy;

        public void AddPivotPoint()
        {
            PivotRows.Add(new ChainPivotRow("A" + (PivotRows.Count + 1)));
        }

        public void RemovePivotPoint(ChainPivotRow row)
        {
            if (row != null) PivotRows.Remove(row);
        }

        /// <summary>把缓存位姿（X/Y/U）写入对针点表选中行；提示文本同步进结果栏</summary>
        public void FillPivotIntoSelectedRow()
        {
            if (_pivotTool == null) { PivotResultText = "⚠ 请先选择要标定的工具。"; return; }
            double x, y, u;
            if (!TryGetLastPoseU(out x, out y, out u))
            {
                PivotResultText = "⚠ 尚无含 U 角的位姿——请『回读位姿』（U 轴须可读）或手抄后直接改表。";
                return;
            }
            var row = _selectedPivotRow;
            if (row == null) { PivotResultText = "⚠ 请先在对针点表里选中一行。"; return; }
            row.UDeg = u;
            row.FlangeX = x;
            row.FlangeY = y;
            PivotResultText = string.Format(CultureInfo.InvariantCulture,
                "已回填 {0} 第 {1} 行：U={2:F2}°  X={3:F3}  Y={4:F3}——转到下一角度继续扎点，凑够 4 个以上后『求解 pivoting』",
                _pivotTool.ToolId, PivotRows.IndexOf(row) + 1, u, x, y);
        }

        /// <summary>求解 pivoting：解出 e 与 P_ref，残差回填行内并给出验收判定</summary>
        public void SolvePivoting()
        {
            PivotHasResult = false;
            ClearPivotViz();
            if (_pivotTool == null) { PivotResultText = "⚠ 请先选择工具。"; return; }
            if (_pivotTool.IsConcentric) { PivotResultText = "该工具已勾『同心』，无需对针求解。"; return; }

            var filled = PivotRows.Where(p => p.UDeg != 0 || p.FlangeX != 0 || p.FlangeY != 0).ToList();
            foreach (var p in PivotRows) { p.ResidualMm = double.NaN; p.ResidualHigh = false; }
            if (filled.Count < 3)
            {
                PivotResultText = "❌ 有效对针点不足（≥3 可解，推荐 4~8 个角度；当前 " + filled.Count + "）。";
                return;
            }
            // U=0° 是合法起始角；「角度未散开」由 FitPivoting 的跨度门（<30° 硬拦）统一判定

            var r = ChainFitter.FitPivoting(
                filled.Select(p => p.UDeg).ToArray(),
                filled.Select(p => p.FlangeX).ToArray(),
                filled.Select(p => p.FlangeY).ToArray());
            if (!r.Ok)
            {
                PivotResultText = "❌ " + r.Error;
                return;
            }

            // 残差回填 + 离群标红
            double hiGate = Math.Max(r.RmsMm * 2.5, 0.1);
            double worst = 0; int worstIdx = -1;
            for (int i = 0; i < filled.Count; i++)
            {
                filled[i].ResidualMm = r.PerPointResidual[i];
                filled[i].ResidualHigh = r.PerPointResidual[i] > hiGate;
                if (r.PerPointResidual[i] > worst) { worst = r.PerPointResidual[i]; worstIdx = i; }
            }

            _pivotEx = r.Ex;
            _pivotEy = r.Ey;
            PivotHasResult = true;
            string gate = r.RmsMm <= ChainFitter.PivotRmsGateMm ? "≤门0.5mm ✓" : "⚠超门0.5mm";
            string concentricHint = Math.Sqrt(r.Ex * r.Ex + r.Ey * r.Ey) < 0.05
                ? "｜★|e|<0.05mm≈同心：确认后可直接勾『同心』显式落 (0,0)"
                : "";
            PivotResultText = string.Format(CultureInfo.InvariantCulture,
                "{0}求解成功：e=({1:F4}, {2:F4})mm，P_ref=({3:F3}, {4:F3})，U跨度={5:F1}°，残差RMS={6:F4}mm（{7}）{8}，最差点 {9}={10:F4}mm{11}",
                r.Warning == null ? "" : "⚠" + r.Warning + "｜",
                r.Ex, r.Ey, r.RefX, r.RefY, r.USpanDeg, r.RmsMm, gate,
                r.RmsMm <= ChainFitter.PivotRmsGateMm ? "" : "——超对针重复精度门，禁止采用，请重新扎点",
                worstIdx >= 0 ? filled[worstIdx].Note : "-",
                worst,
                concentricHint);
            BuildPivotViz(filled, r);
        }

        /// <summary>把解出的 e 写入当前工具（OffsetDx/Dy），完成 WF-03</summary>
        public string ApplyPivotToTool()
        {
            if (_pivotTool == null) return "⚠ 请先选择工具。";
            if (!PivotHasResult) return "⚠ 尚无有效求解结果——请先『求解 pivoting』且门禁通过。";
            _pivotTool.OffsetDx = _pivotEx;
            _pivotTool.OffsetDy = _pivotEy;
            RefreshSteps();
            OnPropertyChanged(nameof(PivotReadyText));
            return string.Format(CultureInfo.InvariantCulture,
                "已写入 {0}：dx={1:F4}, dy={2:F4}（主=对针直量；副=相对主 Δ）", _pivotTool.ToolId, _pivotEx, _pivotEy);
        }

        //---------------------------------------------------------------------
        // 旋转中心 e 采集（2026-09-30，WF-05）：EIH 法兰绕 U 转、相机始终看同一固定特征
        //   ⇒ 特征在【法兰系】里画圆，圆心即工具尖相对法兰的偏心 e —— 与 pivoting 是同一物理量的
        //     第二条独立路径（不碰针尖/不需工装，代价是需要相机已标 H_FC）。
        //   圆心像素经 H_FC 映到法兰系落 ChainTcpNode.URotationCenter；与 pivoting 的 Offset 交叉校验。
        //---------------------------------------------------------------------

        private readonly Dictionary<string, ObservableCollection<ChainRotationRow>> _rotationStore
            = new Dictionary<string, ObservableCollection<ChainRotationRow>>(StringComparer.OrdinalIgnoreCase);

        private ObservableCollection<ChainRotationRow> _rotationRows = new ObservableCollection<ChainRotationRow>();
        /// <summary>当前工具的旋转采样表（切工具自动换表；数据按 ToolId 保留）</summary>
        public ObservableCollection<ChainRotationRow> RotationRows
        {
            get { return _rotationRows; }
            private set { Set(ref _rotationRows, value); }
        }

        private ChainToolRowViewModel _rotationTool;
        /// <summary>旋转中心面板当前操作的工具</summary>
        public ChainToolRowViewModel RotationTool
        {
            get { return _rotationTool; }
            set
            {
                if (!Set(ref _rotationTool, value)) return;
                var t = _rotationTool;
                if (t != null)
                {
                    ObservableCollection<ChainRotationRow> list;
                    if (!_rotationStore.TryGetValue(t.ToolId, out list))
                    {
                        list = new ObservableCollection<ChainRotationRow>();
                        var def = new[] { 0.0, 90.0, 180.0, 270.0 };   // 行业惯例四点（天然满足 ≥30° 跨度门）
                        for (int i = 0; i < def.Length; i++)
                            list.Add(new ChainRotationRow("R" + (i + 1)) { UDeg = def[i] });
                        _rotationStore[t.ToolId] = list;
                    }
                    RotationRows = list;
                    RotationResultText = "工具 " + t.ToolId
                        + "——按表内 U 角逐角转到位、每角拍同一固定特征，凑够 ≥3 角（推荐 0/90/180/270）后『圆拟合求 e』";
                }
                OnPropertyChanged(nameof(RotationReadyText));
            }
        }

        private ChainRotationRow _selectedRotationRow;
        /// <summary>旋转采样表选中行（『识别回填』/『点选回填』的写入目标）</summary>
        public ChainRotationRow SelectedRotationRow
        {
            get { return _selectedRotationRow; }
            set { Set(ref _selectedRotationRow, value); }
        }

        public string RotationReadyText
        {
            get
            {
                if (_rotationTool == null) return "";
                return _rotationTool.HasRotationCenter
                    ? string.Format(CultureInfo.InvariantCulture, "✓ 已测 e=({0:F4},{1:F4})",
                                    _rotationTool.RotationCenterDx.Value, _rotationTool.RotationCenterDy.Value)
                    : "待测";
            }
        }

        private string _rotationResultText = "选工具 → 逐角转 U 拍特征 → 回填像素 → 圆拟合 → 写入旋转中心";
        public string RotationResultText
        {
            get { return _rotationResultText; }
            private set { Set(ref _rotationResultText, value); }
        }

        /// <summary>最近一次圆拟合有效（供『写入旋转中心』闸门）</summary>
        public bool RotationHasResult { get; private set; }
        private double _rotEx, _rotEy;

        public void AddRotationPoint()
        {
            RotationRows.Add(new ChainRotationRow("R" + (RotationRows.Count + 1)));
        }

        public void RemoveRotationPoint(ChainRotationRow row)
        {
            if (row != null) RotationRows.Remove(row);
        }

        /// <summary>统一像素落点（识别与图点选共用同一入口，避免两套口径）</summary>
        public void SetRotationPixel(ChainRotationRow row, double col, double rowPx)
        {
            if (row == null) row = _selectedRotationRow;
            if (row == null) { RotationResultText = "⚠ 请先在旋转采样表里选中一行（回填目标）。"; return; }
            row.PixelCol = col;
            row.PixelRow = rowPx;
            RotationResultText = string.Format(CultureInfo.InvariantCulture,
                "已回填 {0} 第 {1} 行（U={2:F1}°）：像素=({3:F1},{4:F1})——转下一角继续；凑够 ≥3 角后『圆拟合求 e』",
                _rotationTool == null ? "?" : _rotationTool.ToolId, RotationRows.IndexOf(row) + 1, row.UDeg, col, rowPx);
        }

        /// <summary>识别当前相机图像的特征并回填选中采样行（EIH 间接对针：免人工点选）</summary>
        public void RecognizeForRotation()
        {
            var sec = SelectedSection;
            if (sec == null) { RotationResultText = "⚠ 请先在相机页选中一台相机（旋转采样要用它的图）。"; return; }
            var msg = RecognizeFeature(sec);
            if (!sec.HasRecognized) { RotationResultText = msg; return; }
            SetRotationPixel(_selectedRotationRow, sec.RecognizedCol, sec.RecognizedRow);
            RotationResultText = msg + " ｜ " + RotationResultText;
        }

        /// <summary>
        /// 圆拟合求 e：像素域圆拟合（RotationResidualCalculator.Compute）→ 圆心经相机矩阵 H_FC 映到法兰系。
        /// 相机未拟合时只给像素结论并点名缺什么——像素与物理量之间没有桥，不猜。
        /// </summary>
        public void SolveRotationCircle()
        {
            RotationHasResult = false;
            if (_rotationTool == null) { RotationResultText = "⚠ 请先选择工具。"; return; }

            var filled = RotationRows.Where(p => p.HasPixel).ToList();
            foreach (var p in RotationRows) { p.ResidualPx = double.NaN; p.ResidualHigh = false; }
            if (filled.Count < 3)
            {
                RotationResultText = "❌ 有效采样不足（≥3 可解，推荐 0/90/180/270 四点；当前 " + filled.Count + " 行填了像素）。";
                return;
            }
            // U 跨度门与 pivoting 同尺：角度没散开时圆心解病态（外圈噪声被放大成圆心抖动）
            double uMin = filled.Min(p => p.UDeg), uMax = filled.Max(p => p.UDeg);
            if (uMax - uMin < 30.0)
            {
                RotationResultText = string.Format(CultureInfo.InvariantCulture,
                    "❌ U 角跨度仅 {0:F1}°（<30°）——角度未散开时圆心解病态，请把法兰转过更大范围再采样。", uMax - uMin);
                return;
            }

            var pts = filled.Select(p => new RotationResidualPoint { AngleDeg = p.UDeg, X = p.PixelCol, Y = p.PixelRow }).ToList();
            var rep = RotationResidualCalculator.Compute(pts);
            if (rep.PointCount == 0) { RotationResultText = "❌ 圆拟合失败（点退化/共线，检查各行像素是否填错）。"; return; }

            double centerCol = rep.CenterX, centerRow = rep.CenterY;
            double radiusPx = rep.MeanRadius, rmsPx = rep.RmsResidual;
            double hiGate = Math.Max(rep.RmsResidual * 2.5, 0.5);
            foreach (var r in rep.Rows)
            {
                int k = r.Index - 1;                     // Rows.Index 是 1 基
                if (k < 0 || k >= filled.Count) continue;
                filled[k].ResidualPx = r.Residual;
                filled[k].ResidualHigh = r.Residual > hiGate;
            }

            var sec = SelectedSection;
            var node = sec == null ? null : sec.ToNode();
            if (node == null || node.Matrix == null || node.Matrix.Length != 6)
            {
                RotationResultText = string.Format(CultureInfo.InvariantCulture,
                    "圆心像素=({0:F2},{1:F2})，半径={2:F1}px，残差RMS={3:F2}px（最差点 U={4:F1}°={5:F2}px）"
                    + "｜⚠ 相机未拟合（缺 H_FC）⇒ 像素映不到法兰系，请先完成相机步再写入旋转中心。",
                    centerCol, centerRow, radiusPx, rmsPx, rep.MaxResidualAngleDeg, rep.MaxResidual);
                return;
            }

            var mm = node.Matrix;
            var h = HomMat2D.FromElements(mm[0], mm[1], mm[2], mm[3], mm[4], mm[5]);
            double ex, ey;
            h.Map(centerCol, centerRow, out ex, out ey);
            _rotEx = ex; _rotEy = ey;
            RotationHasResult = true;

            string cross = "";
            if (_rotationTool.OffsetFilled && !_rotationTool.IsConcentric)
            {
                double ddx = ex - _rotationTool.OffsetDx, ddy = ey - _rotationTool.OffsetDy;
                double dev = Math.Sqrt(ddx * ddx + ddy * ddy);
                cross = string.Format(CultureInfo.InvariantCulture,
                    "｜交叉校验：与 pivoting 的 Offset=({0:F4},{1:F4}) 相差 {2:F4}mm{3}",
                    _rotationTool.OffsetDx, _rotationTool.OffsetDy, dev,
                    dev <= 0.1 ? "（两条独立路径一致 ✓）" : "（⚠ >0.1mm：两法结论不一致，先查针尖是否扎稳 / 特征是否真固定）");
            }
            RotationResultText = string.Format(CultureInfo.InvariantCulture,
                "✓ {0} 圆心像素=({1:F2},{2:F2})，半径={3:F1}px，残差RMS={4:F2}px（最差点 U={5:F1}°={6:F2}px）"
                + "⇒ e=({7:F4},{8:F4})mm（相机 {9} 的 H_FC 映射）{10}——点『写入旋转中心』落 URotationCenter",
                _rotationTool.ToolId, centerCol, centerRow, radiusPx, rmsPx,
                rep.MaxResidualAngleDeg, rep.MaxResidual, ex, ey, node.CameraId, cross);
        }

        /// <summary>把解出的 e 写入当前工具的 URotationCenter（WF-05 收口）</summary>
        public string ApplyRotationToTool()
        {
            if (_rotationTool == null) return "⚠ 请先选择工具。";
            if (!RotationHasResult) return "⚠ 尚无有效圆拟合结果——请先『圆拟合求 e』（相机须已拟合）。";
            _rotationTool.RotationCenterDx = _rotEx;
            _rotationTool.RotationCenterDy = _rotEy;
            RefreshSteps();
            OnPropertyChanged(nameof(RotationReadyText));
            return string.Format(CultureInfo.InvariantCulture,
                "已写入 {0} 旋转中心：e=({1:F4}, {2:F4})mm（落 ChainTcpNode.URotationCenter）",
                _rotationTool.ToolId, _rotEx, _rotEy);
        }

        //---------------------------------------------------------------------
        // pivoting 可视化（#3，2026-09-30）：把 SolvePivoting 的结果画出来——
        //   靶心 = P_ref，每个采样角一个点；点到靶心的距离就是该角的残差，
        //   验收门（0.5mm）画成一个圈。原来只有一行长文本，哪个角扎歪要自己在数字堆里找。
        //---------------------------------------------------------------------

        public ObservableCollection<PivotVizDot> PivotVizDots { get; } = new ObservableCollection<PivotVizDot>();

        private bool _hasPivotViz;
        public bool HasPivotViz { get { return _hasPivotViz; } private set { Set(ref _hasPivotViz, value); } }

        private double _pivotVizMaxDevMm;
        /// <summary>本组点的最大偏差（mm）——可视化的满量程参考</summary>
        public double PivotVizMaxDevMm
        {
            get { return _pivotVizMaxDevMm; }
            private set { if (Set(ref _pivotVizMaxDevMm, value)) OnPropertyChanged(nameof(PivotVizMaxDevText)); }
        }
        /// <summary>最大偏差的人读文本（在 VM 里格式化：XAML 的 StringFormat 遇 { 要转义，绕开这个坑）</summary>
        public string PivotVizMaxDevText
        {
            get { return string.Format(CultureInfo.InvariantCulture, "最大偏差 {0:F4} mm", _pivotVizMaxDevMm); }
        }

        private double _pivotVizGateRadius;
        /// <summary>验收门（0.5mm）在画布上的半径（px）</summary>
        public double PivotVizGateRadius { get { return _pivotVizGateRadius; } private set { Set(ref _pivotVizGateRadius, value); } }
        /// <summary>验收门直径（px）——XAML 里 Ellipse 的 Width/Height 直接绑它</summary>
        public double PivotVizGateDiameter { get { return _pivotVizGateRadius * 2.0; } }

        private string _pivotVizScaleText = "";
        public string PivotVizScaleText { get { return _pivotVizScaleText; } private set { Set(ref _pivotVizScaleText, value); } }

        /// <summary>画布内切半径（px）——与 XAML 里 Canvas 的边长保持同一口径（边长 = 2×本值）</summary>
        public const double PivotVizHalfExtent = 78.0;
        /// <summary>画布中心（= P_ref 落点）</summary>
        public double PivotVizCenter { get { return PivotVizHalfExtent; } }

        /// <summary>清空可视化（切工具 / 求解失败时调，避免旧图当作新结论看）</summary>
        private void ClearPivotViz()
        {
            PivotVizDots.Clear();
            HasPivotViz = false;
            PivotVizScaleText = "";
        }

        /// <summary>
        /// 由求解结果生成散点：预测针尖位置（tᵢ + R(Uᵢ)·e）相对 P_ref 的偏差即残差。
        /// 尺度取「最大偏差占内切半径 82%」与「门圈占内切半径 90%」的较小者——两者都要看得见。
        /// </summary>
        private void BuildPivotViz(List<ChainPivotRow> filled, ChainPivotResult r)
        {
            ClearPivotViz();
            if (filled == null || filled.Count == 0 || r == null || !r.Ok) return;

            int n = filled.Count;
            var axs = new double[n];
            var ays = new double[n];
            var dev = new double[n];
            double maxAbs = 0.05;                       // 兜底正尺度，避免全零时除零
            for (int i = 0; i < n; i++)
            {
                double rad = filled[i].UDeg * Math.PI / 180.0;
                double c = Math.Cos(rad), s = Math.Sin(rad);
                double px = filled[i].FlangeX + c * r.Ex - s * r.Ey;
                double py = filled[i].FlangeY + s * r.Ex + c * r.Ey;
                axs[i] = px - r.RefX;
                ays[i] = py - r.RefY;
                dev[i] = Math.Sqrt(axs[i] * axs[i] + ays[i] * ays[i]);
                if (dev[i] > maxAbs) maxAbs = dev[i];
            }

            double scale = Math.Min(PivotVizHalfExtent * 0.82 / maxAbs,
                                    PivotVizHalfExtent * 0.90 / ChainFitter.PivotRmsGateMm);

            for (int i = 0; i < n; i++)
            {
                PivotVizDots.Add(new PivotVizDot
                {
                    CanvasX = PivotVizHalfExtent + axs[i] * scale,
                    CanvasY = PivotVizHalfExtent - ays[i] * scale,      // 屏幕 Y 向下、世界 Y 向上 ⇒ 翻转
                    CenterX = PivotVizHalfExtent,
                    CenterY = PivotVizHalfExtent,
                    IsOutlier = dev[i] > ChainFitter.PivotRmsGateMm,
                    Label = (i + 1).ToString(CultureInfo.InvariantCulture),
                    Tip = string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 行：U={1:F1}°，残差 {2:F4}mm{3}",
                        i + 1, filled[i].UDeg, dev[i],
                        dev[i] > ChainFitter.PivotRmsGateMm ? "（超门 0.5mm，建议重扎该角）" : ""),
                });
            }
            PivotVizMaxDevMm = maxAbs;
            PivotVizGateRadius = ChainFitter.PivotRmsGateMm * scale;
            PivotVizScaleText = string.Format(CultureInfo.InvariantCulture,
                "靶心=P_ref；虚线圈=验收门 {0:F2}mm；满量程≈{1:F2}mm（点越靠靶心越准）",
                ChainFitter.PivotRmsGateMm, PivotVizHalfExtent / scale);
            HasPivotViz = true;
        }

        //---------------------------------------------------------------------
        // 角度流程化（#4，2026-09-30）：行业惯例 0/90/180/270，一键把法兰转到下一采样角。
        //   ★ 用【相对移动】而不是绝对定位：现场 U 轴零点约定不一，相对 +90° 不依赖零点口径。
        //   ★ 只下发不闭环：运动是异步的，到位与否由操作员看机器人，随后『回读位姿』『回填对针点』。
        //---------------------------------------------------------------------

        private double _pivotAngleStepDeg = 90.0;
        /// <summary>一键转角步进（度）——默认 90（行业惯例 0/90/180/270）</summary>
        public double PivotAngleStepDeg { get { return _pivotAngleStepDeg; } set { Set(ref _pivotAngleStepDeg, value); } }

        public void PivotStepNextAngle()
        {
            var m = _selectedMotionDevice;
            if (m == null) { MotionLog = "⚠ 未选择运动设备。"; return; }
            if (_pivotTool == null) { PivotResultText = "⚠ 请先选择要标定的工具。"; return; }

            var r = m.MoveRelative(AxisU, (float)_pivotAngleStepDeg, (float)JogSpeed);
            if (r == null || !r.Success)
            {
                string err = "⚠ 转到下一采样角失败：" + (r == null ? "无应答" : r.Message);
                MotionLog = err; PivotResultText = err;
                return;
            }
            // 顺带把选中行切到"下一个还没填的行"——到位后『回读位姿』+『回填对针点』直接落位，省一次手动选行
            var target = PivotRows.FirstOrDefault(p => !IsPivotRowFilled(p)) ?? PivotRows.LastOrDefault();
            if (target != null) SelectedPivotRow = target;
            PivotResultText = string.Format(CultureInfo.InvariantCulture,
                "U 轴 +{0:F1}° 已下发（相对移动）——等机器人到位后点『回读位姿』→『回填对针点』，会落到第 {1} 行。",
                _pivotAngleStepDeg, target == null ? 0 : PivotRows.IndexOf(target) + 1);
        }

        private static bool IsPivotRowFilled(ChainPivotRow p)
        {
            return p != null && (p.UDeg != 0 || p.FlangeX != 0 || p.FlangeY != 0);
        }

        /// <summary>完成即推进：选中第一个待办步骤（行业向导惯例——做完一步自动带到位）</summary>
        public void AdvanceToNextPendingStep()
        {
            var next = Steps.FirstOrDefault(s => s.Status == "待办");
            if (next != null && next != _selectedStep) SelectStep(next);
        }

        /// <summary>前置缺口逐条点名（fail-closed 但要「说得出缺什么」）</summary>
        public System.Collections.Generic.List<string> MissingPrereqs()
        {
            var miss = new System.Collections.Generic.List<string>();
            foreach (var s in Sections)
            {
                if (!s.FitOk) miss.Add("相机 " + s.CameraId + " 未拟合");
                else if (s.ShapeGateFailed) miss.Add("相机 " + s.CameraId + " 形状门未过（|σ1/σ2−1|>0.03）");
                else if (s.IsEih && !s.HasPhotoPose) miss.Add("相机 " + s.CameraId + " 缺拍照基准位 PhotoPose");
                else if (s.IsDownCorrect && !s.HasDeltaRef) miss.Add("下相机 " + s.CameraId + " 缺 DeltaRefPixel");
            }
            foreach (var t in ToolRows.Where(x => x.IsMaster && !x.OffsetFilled))
                miss.Add("主工具 " + t.ToolId + " 未测偏移（对针 pivoting 或勾同心）");
            foreach (var t in ToolRows.Where(x => !x.IsMaster && !x.OffsetFilled))
                miss.Add("副工具 " + t.ToolId + " 未填 Δ（底拍批量或勾同心）");
            return miss;
        }

        /// <summary>把缓存的当前位姿写入某相机的【选中点行】的世界坐标（X/Y）。
        /// 返回提示文本；失败时不抛。</summary>
        public string FillPoseIntoSelectedRow(ChainCameraSectionViewModel sec)
        {
            if (sec == null) return "⚠ 当前不在相机采集页——请先在左侧步骤里选中一个相机采集步。";
            if (!HasLastPose) return "⚠ 尚无位姿可回填——请先『回读位姿』或手工录入。";
            var row = sec.SelectedRow;
            if (row == null) return "⚠ 该点对表未选中行——请先在表格里点一行。";
            double x, y;
            TryGetLastPose(out x, out y);
            row.WorldX = x;
            row.WorldY = y;
            return string.Format(CultureInfo.InvariantCulture,
                "已回填到 {0} 第 {1} 行：X={2:F3}  Y={3:F3}", sec.CameraId, sec.Points.IndexOf(row) + 1, x, y);
        }

        /// <summary>从工位档案（Config\StationProfiles\*.json）推导链骨架。读不到档案 ⇒ 空骨架+错误说明。</summary>
        private static ChainPlanResult PlanFromProfile(string stationCode)
        {
            var r = new ChainPlanResult();
            try
            {
                var repo = new StationProfileRepository();
                var profile = repo.ListAll().FirstOrDefault(p =>
                    string.Equals(p.StationCode, stationCode, StringComparison.OrdinalIgnoreCase));
                if (profile == null || profile.Requirement == null)
                {
                    r.Errors.Add("未找到工位档案 " + stationCode + "（Config\\StationProfiles）——链形状须以档案事实为源，请先建档案");
                    return r;
                }
                var slots = profile.Requirement.CameraSlots ?? new System.Collections.Generic.List<VisionSlotInfo>();
                var tools = new System.Collections.Generic.List<ChainToolFact>();
                int headCount = ParseToolHeadCount(profile.Requirement.ToolHeadCount);
                for (int i = 1; i <= headCount; i++)
                    tools.Add(new ChainToolFact { ToolId = "Nozzle" + i });

                return ChainTopologyPlanner.Plan(stationCode, slots, tools, null);
            }
            catch (Exception ex)
            {
                r.Errors.Add("档案推导异常：" + ex.Message);
                return r;
            }
        }

        private static int ParseToolHeadCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 1;
            string digits = new string(text.TakeWhile(char.IsDigit).ToArray());
            int n;
            return (int.TryParse(digits, out n) && n >= 1 && n <= 8) ? n : 1;
        }

        private void BuildUiFromDraft()
        {
            foreach (var cam in _draft.Cameras)
            {
                bool down = _draft.Edges.Any(e => e.FromCameraId == cam.CameraId
                                               && e.Usage == ChainUsage.DownCameraCorrect);
                int pickCnt = _draft.Edges.Count(e => e.FromCameraId == cam.CameraId
                                                   && e.Usage == ChainUsage.PickAnchor);
                Sections.Add(new ChainCameraSectionViewModel(cam, down, pickCnt, _renderService));
            }
            foreach (var t in _draft.Tools)
            {
                ToolRows.Add(new ChainToolRowViewModel
                {
                    ToolId = t.ToolId,
                    IsMaster = t.IsMaster,
                    BindMasterToolId = t.BindMasterToolId,
                });
            }
            GenerateSteps();
            BuildVisualization();
        }

        /// <summary>
        /// 步骤生成器（§5）：骨架遍历 + 依赖排序——
        /// 相机九点（WF-01）→ 下相机 DeltaRefPixel（WF-12，依赖下相机矩阵）→ 主工具 TCP（WF-03）→
        /// 副工具 Δ（WF-04）→ 门禁总检与落盘。EIH 相机在 Hint 提示逐点记拍照位姿。
        /// </summary>
        private void GenerateSteps()
        {
            Steps.Clear();
            int no = 1;
            foreach (var s in Sections)
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = s.CameraId,
                    Zone = StepZone.Camera,
                    Workflow = s.IsDownCorrect ? "WF-01 九点标定（下固定）" : (s.IsEih ? "WF-01 九点标定（EIH·随动）" : "WF-01 九点标定（ETH·固定）"),
                    Hint = s.IsEih ? "物料：Mark/标定物＋每点记录拍照位姿（法兰系规范化后拟合）＋填拍照基准位 PhotoPose"
                                   : "物料：Mark/标定物＋机器人走位读世界坐标",
                });
            }
            foreach (var s in Sections.Where(x => x.IsDownCorrect))
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = s.CameraId,
                    Zone = StepZone.Camera,
                    Workflow = "WF-12 DeltaRefPixel 实测（本仓新增）",
                    Hint = "吸嘴停拍照位 → 右键点选 U 轴图像投影中心；缺失将拒绝落盘",
                });
            }
            foreach (var t in ToolRows.Where(x => x.IsMaster))
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = t.ToolId,
                    Zone = StepZone.Tool,
                    Workflow = "WF-03 主工具 TCP（针尖对点 pivoting）",
                    Hint = "物料：固定基准针尖；法兰转过 ≥90°（推荐 0/90/180/270）分点扎同一针尖；中区面板采集→求解→写入",
                });
            }
            foreach (var t in ToolRows.Where(x => !x.IsMaster))
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = t.ToolId,
                    Zone = StepZone.Tool,
                    Workflow = "WF-04 副工具 Δ（底拍批量/手输）",
                    Hint = "所有吸嘴伸到下相机上方一拍取各嘴中心两两差；同轴工具实测=(0,0)——中区面板填数或勾『同心』",
                });
            }
            // WF-05 旋转中心 e（2026-09-30）：EIH 专用——法兰绕 U 转时特征在图像上画圆，圆心即偏心。
            // ETH 下相机固定、特征固定 ⇒ 图像不动，此法不成立，故只在链上有 EIH 相机时才生成该步。
            if (Sections.Any(s => s.IsEih))
            {
                foreach (var t in ToolRows)
                {
                    Steps.Add(new ChainStepRow
                    {
                        StepNo = no++,
                        Target = t.ToolId,
                        Zone = StepZone.Rotation,
                        Workflow = "WF-05 旋转中心 e（EIH 绕 U 圆拟合）",
                        Hint = "物料：固定特征 Mark（不用针尖）；法兰按 0/90/180/270 逐角转到位、每角拍同一特征 → 圆拟合圆心 → 写入 URotationCenter。"
                             + "可选步：与 pivoting 的 Offset 交叉校验（两条独立路径应一致）",
                    });
                }
            }
            Steps.Add(new ChainStepRow
            {
                StepNo = no,
                Target = "整链",
                Zone = StepZone.Gate,
                Workflow = "门禁总检 G0~G4 + 形状门 → 落盘 Chain.json",
                Hint = "任一门不过即拒绝保存（fail-closed 与生产端同一把尺）",
            });
            // 默认选中第一步（打开即是"从哪开始"）
            var first = Steps.FirstOrDefault();
            if (first != null) SelectStep(first);
            RefreshSteps();
        }

        //---------------------------------------------------------------------
        // 步骤 ↔ 操作区 联动（2026-09-28）：点步骤清单 → 右侧切到对应区域
        //---------------------------------------------------------------------

        private ChainStepRow _selectedStep;
        /// <summary>当前选中的步骤（界面高亮 + 驱动右侧操作区切换）</summary>
        public ChainStepRow SelectedStep
        {
            get { return _selectedStep; }
            set
            {
                if (Set(ref _selectedStep, value))
                {
                    foreach (var s in Steps) s.IsSelected = ReferenceEquals(s, value);
                    OnPropertyChanged(nameof(FocusZone));
                    OnPropertyChanged(nameof(FocusCameraId));
                    OnPropertyChanged(nameof(StepGuide));
                    OnPropertyChanged(nameof(IsToolStepSelected));
                    OnPropertyChanged(nameof(IsCameraStepSelected));
                    OnPropertyChanged(nameof(IsRotationStepSelected));
                    OnPropertyChanged(nameof(ShowCameraWorkArea));
                    OnPropertyChanged(nameof(CanUseGrid));
                }
            }
        }

        /// <summary>选中步骤所在区域（界面据此切 Tab / 展开对应组）</summary>
        public StepZone FocusZone { get { return _selectedStep == null ? StepZone.Camera : _selectedStep.Zone; } }
        /// <summary>选中步对应相机 Id（空=不切相机 Tab / 非相机步）</summary>
        public string FocusCameraId
        {
            get { return _selectedStep != null && _selectedStep.Zone == StepZone.Camera ? _selectedStep.Target : null; }
        }
        public bool IsToolStepSelected { get { return FocusZone == StepZone.Tool; } }
        public bool IsCameraStepSelected { get { return FocusZone == StepZone.Camera; } }
        /// <summary>旋转中心步（EIH 绕 U 圆拟合）——中区切到旋转采样面板</summary>
        public bool IsRotationStepSelected { get { return FocusZone == StepZone.Rotation; } }
        /// <summary>
        /// 相机工作区（相机步与旋转中心步都显示）：旋转采样必须能看见图——
        /// 要点选像素、要看识别叠加标记，把视图藏掉这一步就没法做。
        /// </summary>
        public bool ShowCameraWorkArea
        {
            get { return FocusZone == StepZone.Camera || FocusZone == StepZone.Rotation; }
        }

        /// <summary>选中步骤的操作指引（中区顶部"当前该做什么"栏）</summary>
        public string StepGuide
        {
            get
            {
                if (_selectedStep == null) return "请在上方步骤清单点选一步开始。";
                string where = _selectedStep.Zone == StepZone.Tool
                    ? "操作面：中区『工具 TCP 标定』面板"
                    : _selectedStep.Zone == StepZone.Rotation
                        ? "操作面：中区『旋转中心 e』面板（EIH 绕 U 圆拟合）"
                        : "操作面：中间相机区（Tab=" + _selectedStep.Target + "）";
                return "第 " + _selectedStep.StepNo + " 步 · " + _selectedStep.Workflow
                       + "\n" + where + "　目标：" + _selectedStep.Target + "　状态：" + _selectedStep.Status
                       + "\n" + _selectedStep.Hint;
            }
        }

        /// <summary>点选一步（界面点击行时调用；返回是否可聚焦到操作区）</summary>
        public void SelectStep(ChainStepRow step)
        {
            SelectedStep = step;
            // 相机步：同步把 Tab 切到对应相机（"点一步即到位"）
            if (step != null && step.Zone == StepZone.Camera)
            {
                var sec = Sections.FirstOrDefault(x => x.CameraId == step.Target);
                if (sec != null) SelectedSection = sec;
            }
            // 工具/旋转步：把中区面板的「当前工具」切到该步目标（与相机步同待遇，点一步即到位）
            if (step != null && (step.Zone == StepZone.Tool || step.Zone == StepZone.Rotation))
            {
                var row = ToolRows.FirstOrDefault(x => x.ToolId == step.Target);
                if (row != null)
                {
                    PivotTool = row;
                    RotationTool = row;
                }
            }
            RefreshSteps();
            OnPropertyChanged(nameof(StepGuide));
        }

        private ChainCameraSectionViewModel _selectedSection;
        /// <summary>当前相机 Tab（点步骤相机步时自动切换）</summary>
        public ChainCameraSectionViewModel SelectedSection
        {
            get { return _selectedSection; }
            set
            {
                if (Set(ref _selectedSection, value))
                {
                    OnPropertyChanged(nameof(CanUseGrid));
                    if (_selectedSection != null) _selectedSection.RefreshGridStatus();
                }
            }
        }

        /// <summary>按当前采集完成度刷新步骤状态</summary>
        public void RefreshSteps()
        {
            foreach (var st in Steps)
            {
                if (st.Workflow.StartsWith("WF-01"))
                {
                    var sec = Sections.FirstOrDefault(x => x.CameraId == st.Target);
                    st.Status = (sec != null && sec.FitOk && !sec.ShapeGateFailed) ? "✓ 完成" : "待办";
                }
                else if (st.Workflow.StartsWith("WF-12"))
                {
                    var sec = Sections.FirstOrDefault(x => x.CameraId == st.Target);
                    st.Status = (sec != null && sec.HasDeltaRef) ? "✓ 完成" : "待办";
                }
                else if (st.Workflow.StartsWith("WF-03") || st.Workflow.StartsWith("WF-04"))
                {
                    var row = ToolRows.FirstOrDefault(x => x.ToolId == st.Target);
                    st.Status = (row != null && row.OffsetFilled) ? "✓ 完成" : "待办";
                }
                else if (st.Workflow.StartsWith("WF-05"))
                {
                    // 旋转中心 e 是「交叉校验」性质的增强步：不做也能落盘（主线由 pivoting 的 Offset 承担），
                    // 做了就是两条独立路径互相印证。故待办态写作「○ 可选」而不是「待办」——不阻塞推进。
                    var row = ToolRows.FirstOrDefault(x => x.ToolId == st.Target);
                    st.Status = (row != null && row.HasRotationCenter) ? "✓ 完成" : "○ 可选";
                }
                else
                {
                    st.Status = _draft != null && BuildGraph() != null ? "可落盘" : "待前置完成";
                }
            }
            OnPropertyChanged(nameof(StepGuide));
        }

        public string WindowTitle { get { return "链向导 v2（工位驱动）—— " + _stationCode; } }

        public string StationCode { get { return _stationCode; } }

        //---------------------------------------------------------------------
        // 传导链可视化（2026-09-28）：把"这个工位怎么标、怎么用"用拓扑图+矩阵式直接画在界面上
        //---------------------------------------------------------------------

        /// <summary>坐标系传导拓扑（ASCII/Unicode 图，随工位事实动态生成）</summary>
        public string TopologyDiagram { get { return _topology; } private set { Set(ref _topology, value); } }
        private string _topology = "";

        /// <summary>标定阶段：每台相机/工具各解出什么矩阵（含公式）</summary>
        public string CalibMath { get { return _calibMath; } private set { Set(ref _calibMath, value); } }
        private string _calibMath = "";

        /// <summary>生产消费：像素→世界→法兰（含矩阵式）</summary>
        public string ConsumeMath { get { return _consumeMath; } private set { Set(ref _consumeMath, value); } }
        private string _consumeMath = "";

        /// <summary>按当前骨架（工位事实）生成三块可视化文本</summary>
        private void BuildVisualization()
        {
            if (_draft == null)
            {
                TopologyDiagram = "（未能从工位档案推导出链骨架，无法生成传导图）";
                CalibMath = "";
                ConsumeMath = "";
                return;
            }

            var sb = new System.Text.StringBuilder();
            var tools = _draft.Tools.Select(t => t.ToolId + (t.IsMaster ? "(主)" : "(副)")).ToList();
            sb.AppendLine("坐标系传导链（★=需标定量，→=变换方向，⊕=差分）");
            sb.AppendLine();
            sb.AppendLine("  世界系 B ──┬── [法兰/滑台系 F] ── T_FT=<e> ──▶ [工具 TCP] " + string.Join(",", tools));
            sb.AppendLine("             │        ▲");
            sb.AppendLine("             │        │ T_BF(X,Y,U)  机器/板卡实时位姿（读数）");
            sb.AppendLine("             └── [相机系 C] ── H_FC / H_CB ──▶ 像素 (u,v)");
            sb.AppendLine();
            sb.Append("  说明：H 解出「像素↔链上某系」，e 解出「法兰↔工具尖」，两者同在 B 系表达，缺一不可。");
            TopologyDiagram = sb.ToString().TrimEnd();

            var cm = new System.Text.StringBuilder();
            cm.AppendLine("【标定阶段】逐节点解什么（观测方程 → 拟合/求解）");
            foreach (var cam in _draft.Cameras)
            {
                bool down = _draft.Edges.Any(e => e.FromCameraId == cam.CameraId && e.Usage == ChainUsage.DownCameraCorrect);
                bool eih = cam.Mount == ChainCameraMount.EyeInHand;
                cm.AppendLine();
                cm.AppendLine("  ● 相机 " + cam.CameraId + (eih ? "（EIH·随动）" : "（ETH·固定）") + (down ? " ［下相机纠偏］" : ""));
                if (eih)
                    cm.AppendLine("      H_FC :  p_f = H_FC · [u,v,1]ᵀ      （像素 → 法兰系）");
                else
                    cm.AppendLine("      H_CB :  p_B = H_CB · [u,v,1]ᵀ      （像素 → 世界系）");
                cm.AppendLine("      拟合：  min Σ‖ H·pᵢ − qᵢ ‖²  （n≥4，推荐九点；SVD 最小二乘）");
                cm.AppendLine("      门禁：  |σ1/σ2 − 1| ≤ 0.03（真奇异值形状门）  +  RMS 报告");
                if (eih && !down)
                    cm.AppendLine("      规范化：qᵢ = R(−Uᵢ)·(m − tᵢ)  逐点用法兰位姿规范到法兰系");
                if (down)
                    cm.AppendLine("      差分：  δ = H·p_now − H·R_cdown   同位姿两次求值，机位项相消");
            }
            foreach (var t in _draft.Tools)
            {
                cm.AppendLine();
                cm.AppendLine("  ● 工具 " + t.ToolId + (t.IsMaster ? "（主）" : "（副·绑 " + t.BindMasterToolId + "）"));
                cm.AppendLine("      pivoting：  tᵢ + R(Uᵢ)·e = P_ref        （针尖扎住不动）");
                cm.AppendLine("      线性化：    [ R(Uᵢ) | −I₂ ]·[e ; P_ref] = −tᵢ   ⇒ 最小二乘（MathNet QR）");
                cm.AppendLine("      产物：      e=(ex,ey) 落 ChainTcpNode.Offset；残差=对针重复精度（门禁）");
                cm.AppendLine("      同心特例：  |e|<阈值 且 残差小 ⇒ 显式落 (0,0) 并标『已测(同心)』");
                if (!t.IsMaster)
                    cm.AppendLine("      副工具：    oᵢ = o_master + δᵢ （刚性阵列；旋转由主公式统一承担）");
            }
            CalibMath = cm.ToString().TrimEnd();

            var pm = new System.Text.StringBuilder();
            pm.AppendLine("【生产消费】像素 → 世界 → 法兰（逆解）");
            pm.AppendLine();
            pm.AppendLine("  正解：");
            bool anyEih = _draft.Cameras.Any(c => c.Mount == ChainCameraMount.EyeInHand);
            if (_draft.Cameras.Any(c => c.Mount == ChainCameraMount.EyeToHand))
                pm.AppendLine("    ETH：  p_B = H_CB · [u,v,1]ᵀ");
            if (anyEih)
                pm.AppendLine("    EIH：  p_B = T_BF(X,Y,U) · H_FC · [u,v,1]ᵀ");
            if (_draft.Cameras.Any(c => _draft.Edges.Any(e => e.FromCameraId == c.CameraId && e.Usage == ChainUsage.DownCameraCorrect)))
                pm.AppendLine("    纠偏：  δ = H_down·p_now − H_down·R_cdown（差分）");
            pm.AppendLine();
            pm.AppendLine("  逆解（目标 → 法兰）：");
            pm.AppendLine("    t_flange = p_B − R(U_go)·e        （e=工具偏心，随 U 旋转）");
            pm.AppendLine("    展开：  fx = wx − (ex·cosU − ey·sinU)");
            pm.AppendLine("            fy = wy − (ex·sinU + ey·cosU)      ← 与 ChainEngine 逐字一致");
            pm.AppendLine("    同心时：e=(0,0) ⇒ t_flange = p_B（法兰读数直接开到工件点）");
            ConsumeMath = pm.ToString().TrimEnd();
        }

        /// <summary>推导结果说明（自动裁决+人工确认项，拓扑确认页内容）</summary>
        public string PlanNotes { get { return _planNotes; } private set { Set(ref _planNotes, value); } }

        public ObservableCollection<ChainCameraSectionViewModel> Sections { get; private set; }
        public ObservableCollection<ChainToolRowViewModel> ToolRows { get; private set; }
        public ObservableCollection<ChainStepRow> Steps { get; private set; }

        public string ValidateResult { get { return _validateResult; } private set { Set(ref _validateResult, value); } }
        public string SaveResult { get { return _saveResult; } private set { Set(ref _saveResult, value); } }

        /// <summary>
        /// 组装链图：以推导骨架为底（保住 Edges 结构），把拟合成功的相机矩阵/工具偏移回填。
        /// 返回 null = 前置不齐（原因已写入 SaveResult/ValidateResult 由调用方决定提示）。
        /// </summary>
        public StationCalibGraph BuildGraph()
        {
            if (_draft == null) return null;

            // 骨架深拷贝（保 Edges；节点矩阵由采集回填覆盖）
            var graph = new StationCalibGraph { StationCode = _draft.StationCode };
            foreach (var e in _draft.Edges)
                graph.Edges.Add(new ChainEdge { FromCameraId = e.FromCameraId, ToToolId = e.ToToolId, Usage = e.Usage });

            foreach (var sec in Sections)
            {
                var node = sec.ToNode();
                if (node == null) return null;      // 任一相机未拟合 ⇒ 整链不产
                graph.Cameras.Add(node);
            }

            string masterId = null;
            // 基准角 U0 统一取主工具：副工具 Δ 与主工具同基准定义（引擎侧按此校验，不一致直接拒解）
            double baseUDeg = ToolRows.Where(x => x.IsMaster).Select(x => x.OffsetBaseUDeg).FirstOrDefault();
            foreach (var row in ToolRows)
            {
                if (row.IsMaster) masterId = row.ToolId;
                graph.Tools.Add(new ChainTcpNode
                {
                    ToolId = row.ToolId,
                    IsMaster = row.IsMaster,
                    BindMasterToolId = row.IsMaster ? null : row.BindMasterToolId,
                    Offset = row.OffsetFilled ? new[] { row.OffsetDx, row.OffsetDy } : null,
                    // 旋转中心 e（视觉路径，可选步）：未做则 null（生产端不消费该字段，仅留档 + 与 Offset 交叉校验）
                    URotationCenter = row.HasRotationCenter
                        ? new[] { row.RotationCenterDx.Value, row.RotationCenterDy.Value } : null,
                    Meta = new ChainCalibMeta
                    {
                        Method = row.IsConcentric ? "ChainWizard-已测同心"
                               : row.IsMaster ? "ChainWizard-对针直量" : "ChainWizard-刚性阵列Δ",
                        Version = 1,
                        CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                        Operator = "ChainWizardV2",
                        // 0 基准 = 历史形态，写 null 保持旧档案等价（引擎 ReadOffsetBaseU 视 null 为 0）
                        OffsetBaseU = baseUDeg == 0 ? (double?)null : baseUDeg,
                        Note = row.IsConcentric ? "已测同心：显式 (0,0)（§6.4 三态口径，非『未测』）"
                             : row.IsMaster ? "Offset=对针直量 T_TCP→Flange（法兰系，符号内蕴）"
                                            : "相对主工具的 Δ（法兰系）；同轴工具=(0,0) 特例",
                    },
                });
            }
            // 主工具不填 Offset 同样整链不产（G2 结构+G1 完整性会拦，这里提前给一致行为）
            if (!ToolRows.Any(t => t.IsMaster && t.OffsetFilled)) return null;
            return graph;
        }

        /// <summary>主工具人工指派：重跑推导（decisions.MasterToolId），已填采集数据按 Id 迁移。</summary>
        public void SwitchMaster(string toolId)
        {
            if (_draft == null || string.IsNullOrWhiteSpace(toolId)) return;
            var dec = new ChainPlanDecisions { MasterToolId = toolId };
            var plan = ChainTopologyPlanner.Plan(_stationCode, SlotsOf(_stationCode), ToolsOf(_stationCode), dec);
            if (!plan.Ok) { SaveResult = "❌ 重推导失败：" + string.Join("；", plan.Errors); return; }

            // 迁移已填数据（按 Id 匹配；骨架形状变化时未匹配项自然丢弃并提示）
            var oldSections = Sections.ToDictionary(s => s.CameraId);
            var oldTools = ToolRows.ToDictionary(t => t.ToolId);
            _planNotesDirty = "【人工裁决】主工具=" + toolId + " ｜ " + string.Join("；", plan.AutoNotes);
            Sections.Clear();
            ToolRows.Clear();
            foreach (var cam in plan.Draft.Cameras)
            {
                bool down = plan.Draft.Edges.Any(e => e.FromCameraId == cam.CameraId && e.Usage == ChainUsage.DownCameraCorrect);
                int pickCnt = plan.Draft.Edges.Count(e => e.FromCameraId == cam.CameraId && e.Usage == ChainUsage.PickAnchor);
                var sec = new ChainCameraSectionViewModel(cam, down, pickCnt, _renderService);
                ChainCameraSectionViewModel old;
                if (oldSections.TryGetValue(cam.CameraId, out old))
                {
                    sec.Points.Clear();
                    foreach (var p in old.Points) sec.Points.Add(p);
                    sec.SelectedRow = old.SelectedRow;
                    if (old.HasDeltaRef) sec.SetDeltaRefFromClick(old.DeltaRefCol, old.DeltaRefRow);
                    if (old.FitOk) sec.Fit();
                }
                Sections.Add(sec);
            }
            foreach (var t in plan.Draft.Tools)
            {
                var row = new ChainToolRowViewModel { ToolId = t.ToolId, IsMaster = t.IsMaster, BindMasterToolId = t.BindMasterToolId };
                ChainToolRowViewModel old;
                if (oldTools.TryGetValue(t.ToolId, out old))
                {
                    row.OffsetDx = old.OffsetDx;
                    row.OffsetDy = old.OffsetDy;
                }
                ToolRows.Add(row);
            }
            PlanNotes = _planNotesDirty;
            GenerateSteps();
            BuildVisualization();
        }

        private string _planNotesDirty;

        private static System.Collections.Generic.List<VisionSlotInfo> SlotsOf(string code)
        {
            try
            {
                var repo = new StationProfileRepository();
                var profile = repo.ListAll().FirstOrDefault(p =>
                    string.Equals(p.StationCode, code, StringComparison.OrdinalIgnoreCase));
                if (profile != null && profile.Requirement != null && profile.Requirement.CameraSlots != null)
                    return profile.Requirement.CameraSlots;
            }
            catch { }
            return new System.Collections.Generic.List<VisionSlotInfo>();
        }

        private static System.Collections.Generic.List<ChainToolFact> ToolsOf(string code)
        {
            try
            {
                var repo = new StationProfileRepository();
                var profile = repo.ListAll().FirstOrDefault(p =>
                    string.Equals(p.StationCode, code, StringComparison.OrdinalIgnoreCase));
                int n = (profile != null && profile.Requirement != null) ? ParseToolHeadCount(profile.Requirement.ToolHeadCount) : 1;
                var tools = new System.Collections.Generic.List<ChainToolFact>();
                for (int i = 1; i <= n; i++) tools.Add(new ChainToolFact { ToolId = "Nozzle" + i });
                return tools;
            }
            catch { return new System.Collections.Generic.List<ChainToolFact> { new ChainToolFact { ToolId = "Nozzle1" } }; }
        }

        public void Validate()
        {
            var miss = MissingPrereqs();
            if (miss.Count > 0)
            {
                ValidateResult = "❌ 前置不齐（" + miss.Count + " 项）：" + string.Join("；", miss) + "——整链不产（fail-closed）";
                return;
            }
            var graph = BuildGraph();
            if (graph == null)
            {
                ValidateResult = "❌ 前置不齐：任一相机未拟合成功 / 主工具偏移未填，整链不产（fail-closed）";
                return;
            }
            var errs = ChainEngine.Validate(graph, StationCode);
            ValidateResult = errs.Count == 0
                ? "✓ G0~G4 全过：工位=" + graph.StationCode
                  + "，相机=" + graph.Cameras.Count + "，工具=" + graph.Tools.Count
                  + "，边=" + string.Join("；", graph.Edges.Select(e => e.FromCameraId + "--" + e.Usage + "-->" + e.ToToolId))
                : "❌ 门禁未过：" + string.Join("；", errs);
            RefreshSteps();
        }

        public void Save()
        {
            // 形状硬拦在所有门禁之前（血泪：拟合半径可被各向异性拉伸骗过 RMS）
            var bad = Sections.FirstOrDefault(s => s.ShapeGateFailed);
            if (bad != null)
            {
                SaveResult = "❌ 相机 " + bad.CameraId + " 形状失真超门（|σ1/σ2−1|>0.03），禁止保存。请检查点对分布/重新采集。";
                return;
            }

            // EIH 拍照基准位必填（与门禁 G1 同源的前置闸：报错给人看，指路可操作）
            var noPose = Sections.FirstOrDefault(s => s.IsEih && s.FitOk && !s.HasPhotoPose);
            if (noPose != null)
            {
                SaveResult = "❌ EIH 相机 " + noPose.CameraId
                             + " 未填拍照基准位 PhotoPose（X/Y）——EIH 生产求值必须回该机位拍照，拒绝落盘。";
                return;
            }

            var miss = MissingPrereqs();
            if (miss.Count > 0)
            {
                SaveResult = "❌ 前置不齐（" + miss.Count + " 项）：" + string.Join("；", miss) + "——整链不产（fail-closed）";
                return;
            }

            var graph = BuildGraph();
            if (graph == null)
            {
                SaveResult = "❌ 前置不齐：任一相机未拟合成功 / 主工具偏移未填，整链不产（fail-closed）";
                return;
            }
            // 下相机纠偏边存在 ⇒ DeltaRefPixel 必测（推导器 AutoNote 已提示，此处最后一道闸）
            foreach (var e in graph.Edges.Where(x => x.Usage == ChainUsage.DownCameraCorrect))
            {
                var cam = graph.Cameras.FirstOrDefault(c => c.CameraId == e.FromCameraId);
                if (cam == null || cam.DeltaRefPixel == null)
                {
                    SaveResult = "❌ 相机 " + e.FromCameraId + " 有 DownCameraCorrect 边但缺 DeltaRefPixel 实测——缺基准时生产端 δ 只能显式降级（纠偏不生效），拒绝落盘。";
                    return;
                }
            }

            var gateErrs = ChainEngine.Validate(graph, StationCode);
            if (gateErrs.Count > 0)
            {
                SaveResult = "❌ 门禁未过，拒绝保存：" + string.Join("；", gateErrs);
                return;
            }

            string path = Path.Combine(
                CalibrationMatrixStore.GetStationCalibDir(StationCode), "Chain.json");
            string writeErr;
            if (StationCalibGraph.TrySave(graph, path, out writeErr))
            {
                SaveResult = "✓ 已保存：" + path
                    + "（生产端 EnsureChainGraph 将从此处 fail-closed 装载）";
            }
            else
            {
                SaveResult = "❌ " + writeErr;
            }
            RefreshSteps();
        }
    }
}
