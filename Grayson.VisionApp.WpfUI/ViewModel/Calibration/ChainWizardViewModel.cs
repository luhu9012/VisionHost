//===================================================================================
// 文件名: ChainWizardViewModel.cs
// 说 明: 范式2 链向导（R3，首站 ST_002）——采集点对 → ChainFitter 拟合 → 组装
//        StationCalibGraph → G0~G4 校验 → 写 Recipes\Workstations\{工位}\Calib\Chain.json。
//
// 纪律（迁移设计附录A 修订版）：
//   · Edges 由本向导按采集路径生成（PickAnchor / DownCameraCorrect），禁止手填；
//   · 下相机必须实测 DeltaRefPixel（吸嘴 U 轴图像投影 R_cdown）才允许产出
//     DownCameraCorrect 边——缺基准时消费端 δ 只能显式降级（fail-closed 精神）；
//   · 形状硬拦：|σ1/σ2 − 1| > 0.03 的相机禁止保存（RMS 抓不到形状失真）；
//   · 本向导不产范式1 档案、不碰 CalibrationApply/发布链。
//===================================================================================
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Chain;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;

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

        public double WorldX { get { return _worldX; } set { Set(ref _worldX, value); } }
        public double WorldY { get { return _worldY; } set { Set(ref _worldY, value); } }
        public double PixelCol { get { return _pixelCol; } set { Set(ref _pixelCol, value); } }
        public double PixelRow { get { return _pixelRow; } set { Set(ref _pixelRow, value); } }
        public double PhotoX { get { return _photoX; } set { Set(ref _photoX, value); } }
        public double PhotoY { get { return _photoY; } set { Set(ref _photoY, value); } }
        public double PhotoU { get { return _photoU; } set { Set(ref _photoU, value); } }
        public string Note { get { return _note; } set { Set(ref _note, value); } }
    }

    /// <summary>一个相机节点的采集与拟合（吸点引导相机 / 下相机 共用）</summary>
    public class ChainCameraSectionViewModel : ViewModelBase
    {
        private string _title;
        private string _cameraId = "Cam_A";
        private bool _isEih;
        private ChainPointRow _selectedRow;
        private double _deltaRefCol;        // 下相机专属：差分基准像素（R_cdown）
        private double _deltaRefRow;
        private bool _hasDeltaRef;          // 已实测基准像素（图像点选或手输后置位）

        private double[] _matrix;           // 拟合产物 [a11,a12,tx,a21,a22,ty]
        private string _fitResult = "未拟合";
        private bool _fitOk;
        private bool _shapeGateFailed;
        private double _rmsMm, _sigma1, _sigma2, _shapeDev;
        private int _pointCount;

        public ChainCameraSectionViewModel(string title, string defaultCameraId)
        {
            _title = title;
            _cameraId = defaultCameraId;
            Points = new ObservableCollection<ChainPointRow>();
        }

        public string Title { get { return _title; } }
        public ObservableCollection<ChainPointRow> Points { get; private set; }

        public string CameraId
        {
            get { return _cameraId; }
            set { Set(ref _cameraId, value); }
        }

        /// <summary>true=眼在手（EIH，矩阵=T_Cam→Flange）；false=眼在手外（ETH，矩阵=T_Cam→Robot）</summary>
        public bool IsEih
        {
            get { return _isEih; }
            set { Set(ref _isEih, value); OnPropertyChanged("MountHeader"); }
        }

        public string MountHeader { get { return IsEih ? "EIH（随动）" : "ETH（固定）"; } }

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
        /// </summary>
        public void Fit()
        {
            var px = Points.Select(p => p.PixelCol).ToArray();
            var py = Points.Select(p => p.PixelRow).ToArray();
            double[] tx, ty;
            if (!IsEih)
            {
                tx = Points.Select(p => p.WorldX).ToArray();
                ty = Points.Select(p => p.WorldY).ToArray();
            }
            else
            {
                var norm = Points.Select(p => ToFlangeFrame(p)).ToArray();
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

            Matrix = r.Matrix;
            FitOk = true;
            FitResult = string.Format(CultureInfo.InvariantCulture,
                "✓ 拟合成功：n={0}, RMS={1:F4}, σ1={2:F3}, σ3比={3:F4}, 形状偏差={4:F4} ({5}), 矩阵=[{6:G9},{7:G9},{8:G9},{9:G9},{10:G9},{11:G9}]",
                r.PointCount, r.RmsMm, r.Sigma1, r.Sigma2, r.ShapeDeviation,
                ShapeGateFailed ? "⚠超门0.03禁止保存" : "≤0.03 ✓",
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
                    CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    Operator = "ChainWizard",
                    Note = string.Format(CultureInfo.InvariantCulture,
                        "σ1={0:F3} σ2={1:F3} shapeDev={2:F4}", _sigma1, _sigma2, _shapeDev),
                },
            };
            if (HasDeltaRef)
                node.DeltaRefPixel = new[] { DeltaRefCol, DeltaRefRow };
            return node;
        }
    }

    /// <summary>链向导主 VM：两相机 + 双吸嘴工具 + Edges 组装 + 校验 + 落盘</summary>
    public class ChainWizardViewModel : ViewModelBase
    {
        public const string PickAnchorUsage = "PickAnchor";
        public const string DownCorrectUsage = "DownCameraCorrect";
        public const string Nozzle1ToolId = "Nozzle1";
        public const string Nozzle2ToolId = "Nozzle2";

        private string _stationCode = "ST_002";
        private bool _enableDownCamera = true;
        private double _nozzle1Dx, _nozzle1Dy;      // 主工具 T_TCP→Flange（法兰系，U=0 基准，带符号）
        private bool _enableNozzle2 = true;
        private double _nozzle2Dx, _nozzle2Dy;      // 副工具相对主工具的 Δ（法兰系）
        private string _validateResult = "未校验";
        private string _saveResult = "未保存";

        public ChainWizardViewModel()
        {
            PickCamera = new ChainCameraSectionViewModel("吸点引导相机（上相机）", "Cam_A");
            DownCamera = new ChainCameraSectionViewModel("下相机（纠偏）", "Cam_C");
            if (PickCamera.Points.Count == 0) PickCamera.AddPoint();
            if (DownCamera.Points.Count == 0) DownCamera.AddPoint();
        }

        public string WindowTitle { get { return "链向导（范式2 · 坐标系传导矩阵链）—— " + StationCode; } }

        public string StationCode
        {
            get { return _stationCode; }
            set { Set(ref _stationCode, value); OnPropertyChanged("WindowTitle"); }
        }

        public ChainCameraSectionViewModel PickCamera { get; private set; }
        public ChainCameraSectionViewModel DownCamera { get; private set; }

        /// <summary>是否产出下相机节点与 DownCameraCorrect 边</summary>
        public bool EnableDownCamera
        {
            get { return _enableDownCamera; }
            set { Set(ref _enableDownCamera, value); }
        }

        public double Nozzle1Dx { get { return _nozzle1Dx; } set { Set(ref _nozzle1Dx, value); } }
        public double Nozzle1Dy { get { return _nozzle1Dy; } set { Set(ref _nozzle1Dy, value); } }
        public bool EnableNozzle2 { get { return _enableNozzle2; } set { Set(ref _enableNozzle2, value); } }
        public double Nozzle2Dx { get { return _nozzle2Dx; } set { Set(ref _nozzle2Dx, value); } }
        public double Nozzle2Dy { get { return _nozzle2Dy; } set { Set(ref _nozzle2Dy, value); } }

        public string ValidateResult { get { return _validateResult; } private set { Set(ref _validateResult, value); } }
        public string SaveResult { get { return _saveResult; } private set { Set(ref _saveResult, value); } }

        /// <summary>组装链图（相机/工具/边）。返回 null 表示前置不齐（原因已写入 SaveResult）。</summary>
        public StationCalibGraph BuildGraph(out string error)
        {
            error = null;
            var pickNode = PickCamera.ToNode();
            if (pickNode == null)
            {
                error = "吸点引导相机未拟合成功，不能产出链图";
                return null;
            }

            var graph = new StationCalibGraph { StationCode = StationCode };
            graph.Cameras.Add(pickNode);
            graph.Edges.Add(new ChainEdge
            {
                FromCameraId = pickNode.CameraId,
                ToToolId = Nozzle1ToolId,
                Usage = PickAnchorUsage,
            });

            if (EnableDownCamera)
            {
                if (!DownCamera.HasDeltaRef)
                {
                    error = "下相机已启用但缺差分基准像素 DeltaRefPixel（吸嘴 U 轴图像投影 R_cdown）——"
                          + "缺基准时生产端 δ 只能显式降级（纠偏不生效）。请实测后填写，或取消勾选下相机。";
                    return null;
                }
                var downNode = DownCamera.ToNode();
                if (downNode == null)
                {
                    error = "下相机未拟合成功，不能产出 DownCameraCorrect 边";
                    return null;
                }
                graph.Cameras.Add(downNode);
                graph.Edges.Add(new ChainEdge
                {
                    FromCameraId = downNode.CameraId,
                    ToToolId = Nozzle1ToolId,
                    Usage = DownCorrectUsage,
                });
            }

            // 主工具：Offset=向导实测带符号矢量（符号内蕴，无来源分级/符号声明——原生范式2）
            graph.Tools.Add(new ChainTcpNode
            {
                ToolId = Nozzle1ToolId,
                IsMaster = true,
                Offset = new[] { Nozzle1Dx, Nozzle1Dy },
                Meta = new ChainCalibMeta
                {
                    Method = "ChainWizard-对针直量",
                    CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    Operator = "ChainWizard",
                },
            });

            if (EnableNozzle2)
            {
                graph.Tools.Add(new ChainTcpNode
                {
                    ToolId = Nozzle2ToolId,
                    IsMaster = false,
                    BindMasterToolId = Nozzle1ToolId,
                    Offset = new[] { Nozzle2Dx, Nozzle2Dy },
                    Meta = new ChainCalibMeta
                    {
                        Method = "ChainWizard-刚性阵列Δ",
                        CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                        Operator = "ChainWizard",
                        Note = "相对主工具的 Δ（法兰系），旋转由主公式 R(Uf) 统一承担",
                    },
                });
            }
            return graph;
        }

        public void Validate()
        {
            string err;
            var graph = BuildGraph(out err);
            if (graph == null)
            {
                ValidateResult = "❌ " + err;
                return;
            }
            var errs = ChainEngine.Validate(graph, StationCode);
            if (errs.Count == 0)
            {
                ValidateResult = "✓ G0~G4 全过：工位=" + graph.StationCode
                    + "，相机=" + graph.Cameras.Count + "，工具=" + graph.Tools.Count
                    + "，边=" + string.Join("；", graph.Edges.Select(e => e.FromCameraId + "--" + e.Usage + "-->" + e.ToToolId));
            }
            else
            {
                ValidateResult = "❌ 门禁未过：" + string.Join("；", errs);
            }
        }

        public void Save()
        {
            // 形状硬拦在所有门禁之前（血泪：拟合半径可被各向异性拉伸骗过 RMS）
            if (PickCamera.ShapeGateFailed)
            {
                SaveResult = "❌ 吸点相机形状失真超门（|σ1/σ2−1|>0.03），禁止保存。请检查点对分布/重新采集。";
                return;
            }
            if (EnableDownCamera && DownCamera.ShapeGateFailed)
            {
                SaveResult = "❌ 下相机形状失真超门（|σ1/σ2−1|>0.03），禁止保存。请检查点对分布/重新采集。";
                return;
            }

            string err;
            var graph = BuildGraph(out err);
            if (graph == null)
            {
                SaveResult = "❌ " + err;
                return;
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
        }
    }
}
