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
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.WpfUI.Service;

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

        public ChainCameraSectionViewModel(ChainCameraNode draftNode, bool isDownCorrect, int pickEdgeCount)
        {
            _isEih = draftNode.Mount == ChainCameraMount.EyeInHand;
            _isDownCorrect = isDownCorrect;
            CameraId = draftNode.CameraId;
            // 边数决定标题语义：引导相机显示服务几个工具，纠偏相机显示纠偏角色
            Title = CameraId + (isDownCorrect ? " ｜ 下相机纠偏（ETH·固定）"
                                              : " ｜ 吸点引导 ×" + pickEdgeCount + (IsEih ? "（EIH·随动）" : "（ETH·固定）"));
            Points = new ObservableCollection<ChainPointRow>();
            for (int i = 0; i < 9; i++)
                Points.Add(new ChainPointRow { Note = "P" + (i + 1) });
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

        public void AddPoint()
        {
            Points.Add(new ChainPointRow { Note = "P" + (Points.Count + 1) });
        }

        public void RemovePoint(ChainPointRow row)
        {
            if (row != null) Points.Remove(row);
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
    public enum StepZone { Camera, Tool, Gate }

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
        private readonly System.Threading.AutoResetEvent _frameArrivedEvent = new System.Threading.AutoResetEvent(false);
        private volatile FrameEventArgs _latestFrame;
        private volatile bool _captureWaitActive;
        private int _triggerMode = -1;

        public ChainWizardViewModel(string stationCode)
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
                return _selectedCameraDevice.DeviceName + "　" + (IsCameraConnected ? "● 已连接" : "○ 未连接");
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
            if (cam.State == DeviceState.Connected) { CameraLog = "相机已连接：" + cam.DeviceName; return; }
            var r = cam.Connect();
            if (r == null || !r.Success) { CameraLog = "⚠ 连接失败：" + (r?.Message ?? "无应答"); }
            else { CameraLog = "相机已连接：" + cam.DeviceName; }
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
                return _selectedMotionDevice.DeviceName + "　" + (IsMotionConnected ? "● 已连接" : "○ 未连接");
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

        /// <summary>轴映射：X=0,Y=1,U=2（默认；EPSON 经示教器手抄时不用）</summary>
        public int AxisX { get { return _axisX; } set { Set(ref _axisX, value); } }
        public int AxisY { get { return _axisY; } set { Set(ref _axisY, value); } }
        public int AxisU { get { return _axisU; } set { Set(ref _axisU, value); } }
        private int _axisX = 0, _axisY = 1, _axisU = 2;

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
            if (m.State == DeviceState.Connected) { MotionLog = "运动设备已连接：" + m.DeviceName; return; }
            var r = m.Connect();
            MotionLog = (r == null || !r.Success) ? "⚠ 连接失败：" + (r?.Message ?? "无应答") : "运动设备已连接：" + m.DeviceName;
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
                Sections.Add(new ChainCameraSectionViewModel(cam, down, pickCnt));
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

        /// <summary>选中步骤的操作指引（中区顶部"当前该做什么"栏）</summary>
        public string StepGuide
        {
            get
            {
                if (_selectedStep == null) return "请在上方步骤清单点选一步开始。";
                string where = _selectedStep.Zone == StepZone.Tool
                    ? "操作面：右栏『工具偏移』"
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
            RefreshSteps();
            OnPropertyChanged(nameof(StepGuide));
        }

        private ChainCameraSectionViewModel _selectedSection;
        /// <summary>当前相机 Tab（点步骤相机步时自动切换）</summary>
        public ChainCameraSectionViewModel SelectedSection
        {
            get { return _selectedSection; }
            set { Set(ref _selectedSection, value); }
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
            foreach (var row in ToolRows)
            {
                if (row.IsMaster) masterId = row.ToolId;
                graph.Tools.Add(new ChainTcpNode
                {
                    ToolId = row.ToolId,
                    IsMaster = row.IsMaster,
                    BindMasterToolId = row.IsMaster ? null : row.BindMasterToolId,
                    Offset = row.OffsetFilled ? new[] { row.OffsetDx, row.OffsetDy } : null,
                    Meta = new ChainCalibMeta
                    {
                        Method = row.IsConcentric ? "ChainWizard-已测同心"
                               : row.IsMaster ? "ChainWizard-对针直量" : "ChainWizard-刚性阵列Δ",
                        Version = 1,
                        CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                        Operator = "ChainWizardV2",
                        Note = row.IsConcentric ? "已测同心：显式 (0,0)（§6.4 三态口径，非『未测』）"
                             : row.IsMaster ? "Offset=对针直量 T_TCP→Flange（U=0 基准，符号内蕴）"
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
                var sec = new ChainCameraSectionViewModel(cam, down, pickCnt);
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
