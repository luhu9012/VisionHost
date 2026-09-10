//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationVerifierViewModel.Geometry.cs
// 说 明: 校验台「分步几何校验」扩展（CalibrationVerifierViewModel 的 partial 部分）。
//        动机：H+e+t 一次性闭环出偏差时，无法判断是 H 错、e 错还是 t 错。本模块把
//        几何链拆成可独立证伪的步骤，每一步都提供「图上可视化 + 肉眼对照 + 数值判据」。
//
//        ★ 核心判据（纯 H，与 e/t 完全无关）——务必理解，否则会误判：
//          九点标定真值 = 机械手走位命令位 P（回转中心），相机随动拍【固定特征】，
//          拟合 H 使 H(u)=P。故对任意机位 P_now 拍同一未移动特征，恒有 H(u) ≈ P_now。
//          ⟹ 残差 Δ = H(u_click) − P_now 就是纯 H 误差（应 ≤0.5mm）。
//          ⟹ 若特征/工件相对标定时移动了 d，则 Δ = H 误差 + d（混入位移，不是 H 的锅）。
//
//        ★ 反向同理：给机位 P，特征应成像在 H⁻¹(P)。走位前先在图上画出预测十字，
//          走到位抓拍后肉眼看特征是否落在十字上——这是最直观的"像素↔机械"体检。
//
//        ★ 关键边界（写在 UI 上防误判）：只要工件不动，H(u')=P_now 恒成立（定义使然），
//          因此"走位后重新拍照比对"【证伪不了任何落点量】——只能靠吸嘴尖物理压住特征目视验证。
//
//        步骤：
//          ⓪ 矩阵体检：像素当量/两轴比/正交偏差/行列式(镜像)/轴向 —— 一眼看出 X/Y 是否反转
//          ① H 正向（像素→机械）：抓拍 → 点特征 → Δ=H(u)−机位（纯 H 残差）→ 多点统计
//          ② H 反向（机械→像素）：走位到目标 → 抓拍 → 图上画预测十字 → 肉眼比对 → 点选量偏差
//          ③ 九点回放：把标定采样点全画在图上（黄=采样像素 青=真值反投影）+ 可走到任一点复现
//          ④ H+e+t 全量：点特征 → X_obj=P_photo+O−H(u) → P_go=X_obj−R(U_go−U0)·e → 走位目视
//
//        ★ 2026-09-08 消费定案（唯一真源 Contracts/Calibration/CalibrationGeometry.cs）：
//            X_obj = P_photo + O − H(u)  （O=旋转中心，三点定圆只取圆心，半径丢弃）
//            P_go  = X_obj − R(U_go − U0)·e   （e = O − H(p_tip)，物理对针一次即得）
//          已证伪禁用：v5 R_go=w−TCO（仿真 451mm）、v4 P_photo+H(p_tip)−H(u)（缺旋转项 27.8mm）
//          另：求 O 必须"先逐点 H 映射再定圆"，像素域定圆再映射会因 H 非相似而偏心 8.46mm
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public partial class CalibrationVerifierViewModel
    {
        // ==================== ⓿~② 纯 H 校验区 ====================

        private bool _geoHasPick;
        /// <summary>分步校验区是否已点选（影响反向偏差与全量走位可用性）</summary>
        public bool GeoHasPick
        {
            get => _geoHasPick;
            set { if (Set(ref _geoHasPick, value)) RaiseGeometryCanExecutes(); }
        }

        private double _geoPickCol;
        private double _geoPickRow;
        private double _geoPickWorldX;
        private double _geoPickWorldY;

        private string _matrixHealthText = "未体检";
        /// <summary>⓪ 矩阵体检结论（像素当量/正交/镜像/轴向）</summary>
        public string MatrixHealthText
        {
            get => _matrixHealthText;
            set => Set(ref _matrixHealthText, value);
        }

        private string _fwdInfoText = "抓拍后在图上点选【与标定同一特征】→ 显示 Δ=H(u)−机位（纯 H 残差）。";
        /// <summary>① 正向校验结论（点选后更新）</summary>
        public string FwdInfoText
        {
            get => _fwdInfoText;
            set => Set(ref _fwdInfoText, value);
        }

        private string _fwdSummaryText = "尚无记录";
        /// <summary>① 正向多点统计（点数/RMS/MAX）</summary>
        public string FwdSummaryText
        {
            get => _fwdSummaryText;
            set => Set(ref _fwdSummaryText, value);
        }

        /// <summary>① 正向校验记录表</summary>
        public ObservableCollection<GeoCheckRow> FwdRows { get; } = new ObservableCollection<GeoCheckRow>();

        private GeoCheckRow _selectedFwdRow;
        public GeoCheckRow SelectedFwdRow
        {
            get => _selectedFwdRow;
            set { if (Set(ref _selectedFwdRow, value)) RaiseGeometryCanExecutes(); }
        }

        // ---- ② 反向：目标机位输入 ----

        private string _targetXText;
        /// <summary>② 反向目标机位 X（mm；可手输，也可【读当前位】回填）
        /// ★ 手输优先：改变即实时重画预测十字（若已画过一次），不必重新抓拍。</summary>
        public string TargetXText
        {
            get => _targetXText;
            set
            {
                if (Set(ref _targetXText, value))
                {
                    RaiseGeometryCanExecutes();
                    RedrawPredictOnInputChanged();
                }
            }
        }

        private string _targetYText;
        /// <summary>② 反向目标机位 Y（mm）</summary>
        public string TargetYText
        {
            get => _targetYText;
            set
            {
                if (Set(ref _targetYText, value))
                {
                    RaiseGeometryCanExecutes();
                    RedrawPredictOnInputChanged();
                }
            }
        }

        /// <summary>输入框变更 → 若已画过预测十字则立即重画（不抓拍），让"改数字→十字动"立即可见。</summary>
        private void RedrawPredictOnInputChanged()
        {
            if (IsBusy || !IsMatrixReady) return;
            if (double.IsNaN(_revPredictCol)) return;   // 还没画过 → 不乱画
            DrawPredictionMarker("输入变更自动重画");
        }

        private string _revInfoText = "填目标机位(可【读当前位】)→【走位】→【抓拍并标注预测点】→ 肉眼看特征是否落在绿色十字上。";
        /// <summary>② 反向校验结论</summary>
        public string RevInfoText
        {
            get => _revInfoText;
            set => Set(ref _revInfoText, value);
        }

        private double _revPredictCol = double.NaN;
        private double _revPredictRow = double.NaN;
        private double _revPoseX = double.NaN;   // 画预测十字时所用的机位（比对基准）
        private double _revPoseY = double.NaN;

        // ---- ③ 九点回放 ----

        /// <summary>③ 九点采样点回放行</summary>
        public ObservableCollection<SampleReplayRow> ReplayRows { get; } = new ObservableCollection<SampleReplayRow>();

        private SampleReplayRow _selectedReplayRow;
        public SampleReplayRow SelectedReplayRow
        {
            get => _selectedReplayRow;
            set { if (Set(ref _selectedReplayRow, value)) RaiseGeometryCanExecutes(); }
        }

        private string _replaySummaryText = "未回放 —— 点【📊 回放采样点】把标定时的采样位画到图上。";
        public string ReplaySummaryText
        {
            get => _replaySummaryText;
            set => Set(ref _replaySummaryText, value);
        }

        // ---- ④ H+e+t 全量 ----

        private string _fullInfoText = "抓拍后点选特征 → 这里会展开 P_photo、w=H(u)、O（旋转中心）、e（真吸嘴偏心）、X_obj 与最终 P_go，走位后目视吸嘴尖是否压中特征。";
        /// <summary>④ 全量链路结论（中间量全展开，便于定位是哪一项错了）</summary>
        public string FullInfoText
        {
            get => _fullInfoText;
            set => Set(ref _fullInfoText, value);
        }

        private string _fullTargetText = "尚未定案";
        /// <summary>④ 全量走位目标 R_go 文本</summary>
        public string FullTargetText
        {
            get => _fullTargetText;
            set => Set(ref _fullTargetText, value);
        }

        // ---- 图上标记（VM 产出 → 视图层宿主绘制） ----

        /// <summary>分步校验区要绘制的标记（视图层订阅 MarkersInvalidated 后按此集合重画）</summary>
        public ObservableCollection<GeoMarkerItem> GeometryMarkers { get; } = new ObservableCollection<GeoMarkerItem>();

        /// <summary>标记集合已变更 → 视图层应清空宿主标记并按集合重画</summary>
        public event EventHandler MarkersInvalidated;

        private void InvalidateMarkers()
        {
            var h = MarkersInvalidated;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ==================== 命令 ====================

        public ICommand RunMatrixHealthCommand { get; private set; }
        public ICommand ReadCurrentPoseCommand { get; private set; }
        public ICommand MoveToTargetXYCommand { get; private set; }
        public ICommand CaptureAndPredictCommand { get; private set; }
        /// <summary>② 只重画预测十字（不抓拍）——改数字后立刻看"特征该在哪"，无需重新取流</summary>
        public ICommand PredictOnlyCommand { get; private set; }
        public ICommand GeoRecordCommand { get; private set; }
        public ICommand ReplaySamplesCommand { get; private set; }
        public ICommand MoveToSampleCommand { get; private set; }
        public ICommand FullMoveCommand { get; private set; }
        public ICommand ClearGeoMarkersCommand { get; private set; }

        /// <summary>分步区命令初始化（主文件构造函数末尾调用一次）</summary>
        private void InitGeometrySection()
        {
            RunMatrixHealthCommand = new RelayCommand(_ => RunMatrixHealth(), _ => IsMatrixReady);
            ReadCurrentPoseCommand = new RelayCommand(_ => ReadCurrentPoseIntoTarget(), _ => SelectedMotion != null);
            MoveToTargetXYCommand = new RelayCommand(_ => MoveToTargetXY(), _ => CanMoveGeometry());
            CaptureAndPredictCommand = new RelayCommand(_ => CaptureAndPredict(), _ => CanCamera() && !IsBusy);
            PredictOnlyCommand = new RelayCommand(_ => DrawPredictionMarker("仅重画（不抓拍）"), _ => IsMatrixReady && !IsBusy);
            GeoRecordCommand = new RelayCommand(_ => RecordFwdPoint(), _ => GeoHasPick && !IsBusy);
            ReplaySamplesCommand = new RelayCommand(_ => ReplaySamples(), _ => IsMatrixReady);
            MoveToSampleCommand = new RelayCommand(_ => MoveToSelectedSample(), _ => CanMoveGeometry() && SelectedReplayRow != null);
            FullMoveCommand = new RelayCommand(_ => MoveFullTarget(), _ => GeoHasPick && !IsBusy && _facade != null);
            ClearGeoMarkersCommand = new RelayCommand(_ => { GeometryMarkers.Clear(); InvalidateMarkers(); });

            RunMatrixHealth();

            // 分步排查引导：有采样快照 ⇒ 可离线回放九点，直接给出排查顺序
            bool hasSamples = Profile.Samples?.Any(s => s.Points != null && s.Points.Any(p => p.IsCaptured)) ?? false;
            if (IsMatrixReady)
            {
                AppendLog("分步排查顺序：先到右侧「🧮 H 单验」用 Δ=H(u)−机位 证伪 H（与 e/t 无关）→ H 过了再去「🎯 H+e+t 全量」查 O（旋转中心）与 e（真吸嘴偏心）。"
                          + (hasSamples ? "（本档案有采样快照，可直接【📊 回放采样点】离线体检）" : "（本档案无采样快照，九点回放不可用）"));
            }
        }

        private void RaiseGeometryCanExecutes()
        {
            (RunMatrixHealthCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ReadCurrentPoseCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveToTargetXYCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CaptureAndPredictCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (PredictOnlyCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GeoRecordCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ReplaySamplesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (MoveToSampleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FullMoveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        private bool CanMoveGeometry() => !IsBusy && _facade != null;

        /// <summary>
        /// 取点选/记点的参考机位（与在线打点同口径）：优先用【抓拍定格瞬间的快照 _photoPose】
        /// —— 否则"抓拍后手工 JOG 了一下再点选"会算出假残差，把人引向错误的排查方向。
        /// 未定格时退化为实时位并标注来源；两者偏离 >0.5mm 额外告警。
        /// </summary>
        private (double X, double Y, double Z, double U)? ResolvePickPose(out string srcTag)
        {
            srcTag = "无机位";
            var now = TryReadCurrentPose();
            if (_photoPose.HasValue)
            {
                if (now.HasValue && (Math.Abs(now.Value.X - _photoPose.Value.X) > 0.5
                                     || Math.Abs(now.Value.Y - _photoPose.Value.Y) > 0.5))
                {
                    AppendLog($"  ⚠ 定格后机械手已移动 (Δ={now.Value.X - _photoPose.Value.X:F2},{now.Value.Y - _photoPose.Value.Y:F2})mm" +
                              " —— 残差按定格瞬间机位计算；若是有意移动，请重新抓拍后再点选。");
                    srcTag = "定格快照(⚠定格后已移位)";
                }
                else
                {
                    srcTag = "定格快照(成像时刻)";
                }
                return _photoPose;
            }
            if (now.HasValue)
            {
                srcTag = "实时(未定格)";
                return now;
            }
            return null;
        }

        // ==================== ⓪ 矩阵体检 ====================

        /// <summary>
        /// 三点差分反解 H 的线性部分（不依赖矩阵文件解析，只用正向映射，最稳）：
        ///   a11,a21 = ∂(X,Y)/∂col ；a12,a22 = ∂(X,Y)/∂row
        /// 输出：像素当量(mm/px)、两轴比例、正交偏差、行列式(镜像)、两轴方向(是否反转)。
        /// 直接回答"X/Y 是否镜像/反转"——det&lt;0 为 EIH 固有镜像（正常）；单轴反向看轴向文字。
        /// </summary>
        private void RunMatrixHealth()
        {
            if (!IsMatrixReady)
            {
                MatrixHealthText = "无矩阵，无法体检。";
                return;
            }

            // 在图像上取 3 个相距很远的点分别求雅可比。真实 HomMat2D 是仿射 → 三点结果必须一致；
            // 若不一致说明 MapPixelToWorld 里含畸变/非线性环节（那种情况下"当量"要按工作区取值，
            // 不能拿 (0,0) 角上的数当全局当量——2026-09-09 实机复盘踩过这个坑）。
            var probes = new (double Col, double Row, string Tag)[]
            {
                (0.0, 0.0, "左上角"),
                (1295.0, 971.0, "图像中心"),
                (2200.0, 1600.0, "右下区"),
            };
            var rowsJs = new System.Collections.Generic.List<(string Tag, double SCol, double SRow, double Ratio, double Ortho, double Det, bool Ok)>();
            foreach (var p in probes)
            {
                var r00 = _calibService.MapPixelToWorld(MatrixPath, p.Col, p.Row);
                var r10 = _calibService.MapPixelToWorld(MatrixPath, p.Col + 1, p.Row);
                var r01 = _calibService.MapPixelToWorld(MatrixPath, p.Col, p.Row + 1);
                if (!r00.Success || !r10.Success || !r01.Success)
                {
                    rowsJs.Add((p.Tag, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false));
                    continue;
                }
                double a11 = r10.Data.WorldX - r00.Data.WorldX;   // ∂X/∂col
                double a21 = r10.Data.WorldY - r00.Data.WorldY;   // ∂Y/∂col
                double a12 = r01.Data.WorldX - r00.Data.WorldX;   // ∂X/∂row
                double a22 = r01.Data.WorldY - r00.Data.WorldY;   // ∂Y/∂row
                rowsJs.Add(JacobRow(p.Tag, a11, a21, a12, a22));
            }

            var ok = rowsJs.Where(r => r.Ok).ToList();
            if (ok.Count == 0)
            {
                MatrixHealthText = "矩阵换算失败（文件损坏或不是 HomMat2D）。";
                return;
            }

            var mid = ok.FirstOrDefault(r => r.Tag == "图像中心");
            if (mid.Ok == false) mid = ok[0];
            double spread = 0.0;
            if (ok.Count > 1)
            {
                spread = (ok.Max(r => r.SCol) - ok.Min(r => r.SCol)) / Math.Max(1e-12, ok.Average(r => r.SCol)) * 100.0;
            }

            string mirror = mid.Det < 0
                ? "det<0 = 含镜像（EIH 眼在手固有，正常）"
                : (mid.Det > 0 ? "det>0 = 无镜像（纯旋转+缩放）" : "det=0 奇异矩阵（标定失败）");

            // 判据（平面成像 + 方形像素 ⇒ 必须是「相似 + 镜像」）：
            //   · 两轴比必须 ≈1        —— 差太多说明 nine 点数据错位/某一軸线性度坏
            //   · 正交偏差必须 ≈0°      —— 说明 col/row 两轴在世界里不垂直（镜像/斜视/畸变）
            double ratioDev = Math.Abs(mid.Ratio - 1.0);
            bool nonlinear = ok.Count > 1 && spread > 1.0;
            bool badRatio = ratioDev > 0.03;
            bool badOrtho = mid.Ortho > 1.0;

            string verdict;
            if (nonlinear)
                verdict = $"🔴 非线性：不同位置当量差 {spread:F1}%（左上角/中心/右下区不一致）→ 映射含畸变或非仿射环节，不能按单一当量外推。";
            else if (badRatio || badOrtho)
                verdict = "🔴 该 J 不是「相似+镜像」→ 九点数据有污染（某点走位没到位/点对错位/采样时 Z 不一致），建议重做九点标定。";
            else
                verdict = "✅ 形状合法（比例≈1、正交≈90°、只看缩放下:—";

            MatrixHealthText =
                $"当量【{mid.Tag}】 {mid.SCol:F5}/{mid.SRow:F5} mm/px · 两轴比 {mid.Ratio:F4} · 正交偏差 {mid.Ortho:F2}° · {mirror}\n" +
                $"三点一致性误差 {spread:F2}% ｜ 轴向：{(mid.Det < 0 ? "EIH 镜像" : "无镜像")}\n" +
                verdict;
            foreach (var r in rowsJs)
            {
                if (r.Ok)
                    AppendLog($"[矩阵体检·{r.Tag}] 当量 {r.SCol:F5}/{r.SRow:F5} 比 {r.Ratio:F4} 正交 {r.Ortho:F2}° det {r.Det:F6}");
                else
                    AppendLog($"[矩阵体检·{r.Tag}] 换算失败");
            }
            AppendLog("[矩阵体检] " + MatrixHealthText.Replace("\n", " ｜ "));
        }

        private static (string Tag, double SCol, double SRow, double Ratio, double Ortho, double Det, bool Ok)
            JacobRow(string tag, double a11, double a21, double a12, double a22)
        {
            double sCol = Math.Sqrt(a11 * a11 + a21 * a21);
            double sRow = Math.Sqrt(a12 * a12 + a22 * a22);
            double ratio = (sRow > 1e-12) ? sCol / sRow : double.NaN;
            double det = a11 * a22 - a12 * a21;
            double dot = a11 * a12 + a21 * a22;
            double cosv = (sCol > 1e-12 && sRow > 1e-12) ? dot / (sCol * sRow) : 0.0;
            cosv = Math.Max(-1.0, Math.Min(1.0, cosv));
            double ortho = Math.Abs(Math.Acos(cosv) * 180.0 / Math.PI - 90.0);
            return (tag, sCol, sRow, ratio, ortho, det, true);
        }

        /// <summary>
        /// 矩阵形状（落点误差的放大器）——2026-09-09 实机复盘新增。
        ///
        /// 为什么必须单独看它：落点用的是【差分】P_go − P_photo = H(p_tip) − H(u)，
        /// 九点自残差（RMS）小并不代表差分准。残差只说明"9 个采样点彼此自洽"，
        /// 而差分要的是"参考点 p_tip 到目标 u 这一段"的增量也准。
        /// 若 H 的形状不是「相似+镜像」（各向异性≠1 / 正交≠90°），
        /// 增量误差 = 失真因子 × 参考点到目标的世界距离 —— 距离越远偏得越多，
        /// 这正是"九点 RMS 只有 0.5mm、实拍却偏 20mm"的成因。
        ///
        /// 物理约束：平面成像 + 方形像素 ⇒ H 必须是「相似 + 镜像」⇒ σ1/σ2≈1、正交偏差≈0°。
        /// </summary>
        /// <param name="col">求值位置（一般取图像中心或参考点附近）</param>
        /// <param name="row">求值位置</param>
        /// <returns>
        /// Aniso = σ1/σ2（各向异性，理想 1.000）；
        /// Ortho = 正交偏差（°，理想 0）；
        /// Distort = 最坏方向的相对失真（0.227 表示 100mm 最多差 22.7mm）；
        /// Ok = 形状是否合法。
        /// </returns>
        private (double Aniso, double Ortho, double Distort, bool Ok) ProbeMatrixShape(double col, double row)
        {
            var r00 = _calibService.MapPixelToWorld(MatrixPath, col, row);
            var r10 = _calibService.MapPixelToWorld(MatrixPath, col + 1, row);
            var r01 = _calibService.MapPixelToWorld(MatrixPath, col, row + 1);
            if (!r00.Success || !r10.Success || !r01.Success)
                return (double.NaN, double.NaN, double.NaN, false);

            double a11 = r10.Data.WorldX - r00.Data.WorldX;   // ∂X/∂col
            double a21 = r10.Data.WorldY - r00.Data.WorldY;   // ∂Y/∂col
            double a12 = r01.Data.WorldX - r00.Data.WorldX;   // ∂X/∂row
            double a22 = r01.Data.WorldY - r00.Data.WorldY;   // ∂Y/∂row
            double det = a11 * a22 - a12 * a21;

            // A^T·A 的两个特征值开方 = 奇异值 σ1≥σ2（col/row 基不正交时比 sCol/sRow 严谨）
            double tr = a11 * a11 + a21 * a21 + a12 * a12 + a22 * a22;
            double disc = tr * tr - 4.0 * det * det;
            if (disc < 0) disc = 0;
            double s1 = Math.Sqrt(Math.Max(0.0, (tr + Math.Sqrt(disc)) / 2.0));
            double s2 = Math.Sqrt(Math.Max(1e-18, (tr - Math.Sqrt(disc)) / 2.0));
            if (s2 < 1e-12) return (double.NaN, double.NaN, double.NaN, false);

            double aniso = s1 / s2;
            // 以「等面积的相似变换」为参照：最坏方向相对失真 = sqrt(aniso) − 1
            double distort = Math.Sqrt(aniso) - 1.0;

            double dot = a11 * a12 + a21 * a22;
            double sCol = Math.Sqrt(a11 * a11 + a21 * a21);
            double sRow = Math.Sqrt(a12 * a12 + a22 * a22);
            double cosv = (sCol > 1e-12 && sRow > 1e-12) ? dot / (sCol * sRow) : 0.0;
            cosv = Math.Max(-1.0, Math.Min(1.0, cosv));
            double ortho = Math.Abs(Math.Acos(cosv) * 180.0 / Math.PI - 90.0);

            bool ok = Math.Abs(aniso - 1.0) <= 0.03 && ortho <= 1.0;
            return (aniso, ortho, distort, ok);
        }

        // ==================== 点选分流（①② 共用） ====================

        /// <summary>
        /// 分步校验区点选回调（由主文件 ApplyPick 按当前 Tab 分流进入）。
        /// 计算 w=H(u)、读当前机位、给出 Δ=H(u)−机位（纯 H 残差）；并按当前 Tab 更新 ② 或 ④ 面板。
        /// </summary>
        private void ApplyGeometryPick(double row, double col)
        {
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵，无法换算。");
                return;
            }
            var fwd = _calibService.MapPixelToWorld(MatrixPath, col, row);
            if (!fwd.Success)
            {
                AppendLog("换算失败: " + (fwd.Message ?? "未知原因"));
                return;
            }
            _geoPickCol = col;
            _geoPickRow = row;
            _geoPickWorldX = fwd.Data.WorldX;
            _geoPickWorldY = fwd.Data.WorldY;
            GeoHasPick = true;

            string srcTag;
            var pose = ResolvePickPose(out srcTag);
            string poseTag = "未读(无运动卡)";
            double dx = double.NaN, dy = double.NaN, err = double.NaN;
            if (pose.HasValue)
            {
                dx = _geoPickWorldX - pose.Value.X;
                dy = _geoPickWorldY - pose.Value.Y;
                err = Math.Sqrt(dx * dx + dy * dy);
                poseTag = $"机位 ({pose.Value.X:F3},{pose.Value.Y:F3}) [{srcTag}]";
            }

            string verdict;
            if (double.IsNaN(err)) verdict = "⚪ 无机位可读（只显示 H 输出）";
            else if (err <= ResidualToleranceMm) verdict = $"✅ |Δ|={err:F3}mm ≤ {ResidualToleranceMm:0.0}mm —— H 自洽";
            else verdict = $"⚠ |Δ|={err:F3}mm > {ResidualToleranceMm:0.0}mm —— 先确认：①点的是不是标定那个特征 ②工件是否移动过（Δ 里会混入位移）";

            FwdInfoText =
                $"像素 (col={col:F1}, row={row:F1}) → H(u) = ({_geoPickWorldX:F3}, {_geoPickWorldY:F3}) mm\n" +
                (double.IsNaN(err)
                    ? $"{poseTag}\n{verdict}"
                    : $"{poseTag}  Δ=H(u)−机位 = ({dx:+0.000;-0.000}, {dy:+0.000;-0.000}) mm  |Δ|={err:F3}mm\n{verdict}");

            AppendLog($"[H正向] 像素({col:F1},{row:F1}) → H(u)=({_geoPickWorldX:F3},{_geoPickWorldY:F3}) " +
                      (double.IsNaN(err) ? "(无机位)" : $"机位差 Δ=({dx:+0.000;-0.000},{dy:+0.000;-0.000}) |Δ|={err:F3}mm"));

            // ② 反向模式下：把点选位与预测十字比对（像素差 + 折算 mm）
            if (ActiveTabIndex == 2 && !double.IsNaN(_revPredictCol))
            {
                double dCol = col - _revPredictCol;
                double dRow = row - _revPredictRow;
                double dPx = Math.Sqrt(dCol * dCol + dRow * dRow);
                double mm = double.NaN;
                if (!double.IsNaN(_revPoseX))
                {
                    mm = Math.Sqrt(Math.Pow(_geoPickWorldX - _revPoseX, 2) + Math.Pow(_geoPickWorldY - _revPoseY, 2));
                }
                RevInfoText =
                    $"预测十字 (col={_revPredictCol:F1}, row={_revPredictRow:F1}) ← 机位 ({_revPoseX:F3},{_revPoseY:F3})\n" +
                    $"你点选的实际位 (col={col:F1}, row={row:F1}) → 像素差 ({dCol:+0.0;-0.0}, {dRow:+0.0;-0.0}) = {dPx:F1}px" +
                    (double.IsNaN(mm) ? "" : $" ≈ {mm:F3}mm（折算基准=上面预测所用机位）") +
                    $"\n{(dPx <= 3.0 ? "✅ 特征落在预测十字上（≤3px）—— H 反向自洽" : "⚠ 特征偏离预测十字 —— H 与机械不自洽，或机位读数/特征选错")}";
                AppendLog($"[H反向] 点选与预测十字差 {dPx:F1}px" + (double.IsNaN(mm) ? "" : $" ≈ {mm:F3}mm"));

                // 图上补一个洋红十字=实际点选位，与绿色预测十字形成对照
                GeometryMarkers.Add(new GeoMarkerItem { Row = row, Col = col, Size = 22, Color = "magenta", Label = "实际" });
                InvalidateMarkers();
            }

            if (ActiveTabIndex == 3)
            {
                RefreshFullChain();
            }
        }

        /// <summary>① 把当前点选记入统计表（多点后看 RMS/MAX 是否稳定）</summary>
        private void RecordFwdPoint()
        {
            if (!GeoHasPick) return;
            string srcTag;
            var pose = ResolvePickPose(out srcTag);
            double dx = 0, dy = 0, err = double.NaN;
            if (pose.HasValue)
            {
                dx = _geoPickWorldX - pose.Value.X;
                dy = _geoPickWorldY - pose.Value.Y;
                err = Math.Sqrt(dx * dx + dy * dy);
            }
            FwdRows.Add(new GeoCheckRow
            {
                Order = FwdRows.Count + 1,
                Col = _geoPickCol,
                Row = _geoPickRow,
                PoseX = pose.HasValue ? pose.Value.X : double.NaN,
                PoseY = pose.HasValue ? pose.Value.Y : double.NaN,
                Hx = _geoPickWorldX,
                Hy = _geoPickWorldY,
                Dx = dx,
                Dy = dy,
                AbsErr = err,
                StatusText = double.IsNaN(err) ? "⚪" : (err <= ResidualToleranceMm ? "✅" : "⚠ 超差")
            });
            RefreshFwdSummary();
            AppendLog($"[H正向] 已记入第 {FwdRows.Count} 点。");
        }

        private void RefreshFwdSummary()
        {
            var vals = FwdRows.Where(r => !double.IsNaN(r.AbsErr)).Select(r => r.AbsErr).ToList();
            if (vals.Count == 0)
            {
                FwdSummaryText = $"已记 {FwdRows.Count} 点（无机位读数，无法统计残差）";
                return;
            }
            double rms = Math.Sqrt(vals.Sum(v => v * v) / vals.Count);
            FwdSummaryText = $"已记 {FwdRows.Count} 点 · RMS {rms:F3}mm · MAX {vals.Max():F3}mm" +
                             $" · 超差(>{ResidualToleranceMm:0.0}mm) {vals.Count(v => v > ResidualToleranceMm)} 点";
        }

        // ==================== ② 反向：机械→像素 ====================

        private void ReadCurrentPoseIntoTarget()
        {
            var pose = TryReadCurrentPose();
            if (!pose.HasValue)
            {
                AppendLog("读当前位失败：未选运动卡或轴反馈读取失败。");
                return;
            }
            TargetXText = pose.Value.X.ToString("F3");
            TargetYText = pose.Value.Y.ToString("F3");
            AppendLog($"已读取当前机位 → 目标 ({TargetXText}, {TargetYText})");
        }

        /// <summary>② 走位到目标机位（XY，Z 不动）</summary>
        private void MoveToTargetXY()
        {
            double tx, ty;
            if (!double.TryParse(TargetXText, out tx) || !double.TryParse(TargetYText, out ty))
            {
                AppendLog("目标机位不是合法数字 —— 先【读当前位】或手工填写。");
                return;
            }
            if (_facade == null)
            {
                AppendLog("未绑定运动卡，无法走位。");
                return;
            }
            IsBusy = true;
            try
            {
                AppendLog($"② 走位 → X={tx:F3}, Y={ty:F3} ...");
                bool ok = _facade.MoveToXY(tx, ty);
                AppendLog(ok ? "走位完成 —— 接着点【抓拍并标注预测点】。"
                             : "走位被拒: " + (_facade.LastError ?? "未知原因"));
                if (ok)
                {
                    var pose = TryReadCurrentPose();
                    if (pose.HasValue)
                    {
                        AppendLog($"  到位反馈 ({pose.Value.X:F3},{pose.Value.Y:F3}) —— 与命令差 " +
                                  $"{Math.Sqrt(Math.Pow(pose.Value.X - tx, 2) + Math.Pow(pose.Value.Y - ty, 2)):F3}mm");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("走位异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                RaiseGeometryCanExecutes();
            }
        }

        /// <summary>
        /// ② 抓拍 + 在图上画出"特征应该出现的位置"：u_pred = H⁻¹(机位)。
        /// ⚠ 不得在调用 CaptureFrame 前置 IsBusy=true —— CaptureFrame 首行 `if (IsBusy) return;`
        ///    会直接静默返回，导致"点了抓拍但画面没更新、也没任何日志"（2026-09-08 修复的自锁 bug）。
        ///    这里只在开头做一次忙检查，抓拍的忙状态由 CaptureFrame 自己管理。
        /// </summary>
        private async void CaptureAndPredict()
        {
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵，无法反投影。");
                return;
            }
            if (IsBusy)
            {
                AppendLog("正在忙（取流/走位中），请稍候再点。");
                return;
            }
            try
            {
                await CaptureFrame();                       // 复用主文件自愈抓拍（定格当前画面）
                await Task.Delay(400);                      // 等首帧上屏（宿主在 Background 优先级刷）
                DrawPredictionMarker("抓拍后标注");
            }
            catch (Exception ex)
            {
                AppendLog("抓拍/反投影异常: " + ex.Message);
            }
            finally
            {
                RaiseGeometryCanExecutes();
            }
        }

        /// <summary>
        /// ② 解析"用哪个机位做反投影"：**手输目标位优先**，退化为当前机位。
        /// 手输优先是刻意的——用户改了输入框却看到十字不动（旧的当前机位优先）会被误判为"点了没反应"。
        /// 若机位读数与手输目标偏离 >0.5mm，额外告警：说明机械手实际并不在输入的目标位。
        /// </summary>
        private bool TryResolvePredictPose(out double px, out double py, out string srcTag)
        {
            px = double.NaN; py = double.NaN; srcTag = string.Empty;
            double mx = 0, my = 0;   // 必须初始化：&& 短路时编译器不保证 out 参数已赋值(CS0165)
            bool hasManual = double.TryParse(TargetXText, out mx) && double.TryParse(TargetYText, out my);
            var pose = TryReadCurrentPose();

            if (hasManual)
            {
                px = mx; py = my;
                srcTag = "手输目标位";
                if (pose.HasValue)
                {
                    double d = Math.Sqrt(Math.Pow(pose.Value.X - mx, 2) + Math.Pow(pose.Value.Y - my, 2));
                    if (d > 0.5)
                    {
                        srcTag = $"手输目标位（⚠ 实际机位偏离 {d:F2}mm，未走位时仅供预览）";
                        AppendLog($"  ⚠ 手输目标 ({mx:F3},{my:F3}) 与当前机位 ({pose.Value.X:F3},{pose.Value.Y:F3}) 相差 {d:F2}mm —— " +
                                  "十字按手输位画（预览）；若要实物比对请先【🚗 走位】。");
                    }
                }
                return true;
            }
            if (pose.HasValue)
            {
                px = pose.Value.X; py = pose.Value.Y;
                srcTag = "当前机位（输入框为空/非数字）";
                return true;
            }
            return false;
        }

        /// <summary>② 反投影 + 画绿色预测十字 + 更新结论文本与日志（抓拍后与仅重画共用）</summary>
        private void DrawPredictionMarker(string why)
        {
            if (!IsMatrixReady)
            {
                AppendLog("无矩阵，无法反投影。");
                return;
            }
            if (!TryResolvePredictPose(out double bx, out double by, out string srcTag))
            {
                AppendLog("无法反投影：输入框不是合法数字，且读不到当前机位（先【📍 读当前位】或手填目标位）。");
                RevInfoText = "⚠ 无法反投影：请在上方填目标机位 X/Y（或点【📍 读当前位】）。";
                return;
            }
            var bwd = _calibService.MapWorldToPixel(MatrixPath, bx, by);
            if (!bwd.Success)
            {
                AppendLog("反投影失败: " + (bwd.Message ?? "未知原因"));
                RevInfoText = "⚠ 反投影失败：" + (bwd.Message ?? "未知原因");
                return;
            }
            _revPredictCol = bwd.Data.PixelX;
            _revPredictRow = bwd.Data.PixelY;
            _revPoseX = bx;
            _revPoseY = by;

            GeometryMarkers.Clear();
            GeometryMarkers.Add(new GeoMarkerItem
            {
                Row = _revPredictRow,
                Col = _revPredictCol,
                Size = 40,
                Color = "green",
                Label = "特征应在此"
            });
            InvalidateMarkers();

            RevInfoText =
                $"[{why}] 基准机位 ({bx:F3},{by:F3}) ← {srcTag}\n" +
                $"→ 预测像素 (col={_revPredictCol:F1}, row={_revPredictRow:F1})（绿十字）\n" +
                "★ 肉眼看：特征中心是否落在绿色十字上？\n" +
                "  改上方 X/Y 数字会立即重画；再在特征实际位置点一下 → 显示像素差与折算 mm。";
            AppendLog($"[H反向] 基准({bx:F3},{by:F3})[{srcTag}] → 预测像素(col={_revPredictCol:F1},row={_revPredictRow:F1})，" +
                      "已画绿色十字；请目视特征是否落在十字上。");
        }

        // ==================== ③ 九点回放 ====================

        /// <summary>
        /// ③ 把标定采样快照全画到图上：黄十字=采样时特征像素位，青十字=机械真值反投影 H⁻¹(P)。
        /// 两十字间距即该点残差；并可【走到该点】复现：走到真值位抓拍，特征应回到采样像素。
        /// </summary>
        private void ReplaySamples()
        {
            ReplayRows.Clear();
            if (!IsMatrixReady)
            {
                ReplaySummaryText = "无矩阵，无法回放。";
                return;
            }
            var pts = Profile.Samples?
                .SelectMany(s => s.Points ?? new ObservableCollection<CalibrationPointModel>())
                .Where(p => p.IsCaptured)
                .ToList();
            if (pts == null || pts.Count == 0)
            {
                ReplaySummaryText = "档案里没有采样点快照（Samples 为空）—— 请用标定向导重新标定并保存。";
                AppendLog("九点回放: 无采样快照。");
                return;
            }

            GeometryMarkers.Clear();
            int order = 0;
            double sumSq = 0, maxErr = 0; int n = 0, over = 0;
            foreach (var p in pts)
            {
                order++;
                var fwd = _calibService.MapPixelToWorld(MatrixPath, p.PixelX, p.PixelY);
                var bwd = _calibService.MapWorldToPixel(MatrixPath, p.WorldX, p.WorldY);
                double hx = fwd.Success ? fwd.Data.WorldX : double.NaN;
                double hy = fwd.Success ? fwd.Data.WorldY : double.NaN;
                double bx = bwd.Success ? bwd.Data.PixelX : double.NaN;
                double by = bwd.Success ? bwd.Data.PixelY : double.NaN;
                double err = (fwd.Success)
                    ? Math.Sqrt(Math.Pow(p.WorldX - hx, 2) + Math.Pow(p.WorldY - hy, 2))
                    : double.NaN;
                double backPx = (bwd.Success)
                    ? Math.Sqrt(Math.Pow(bx - p.PixelX, 2) + Math.Pow(by - p.PixelY, 2))
                    : double.NaN;
                if (!double.IsNaN(err))
                {
                    n++; sumSq += err * err;
                    if (err > maxErr) maxErr = err;
                    if (err > ResidualToleranceMm) over++;
                }

                var rowModel = new SampleReplayRow
                {
                    Order = order,
                    PixelX = p.PixelX,
                    PixelY = p.PixelY,
                    TrueX = p.WorldX,
                    TrueY = p.WorldY,
                    Hx = hx,
                    Hy = hy,
                    BackX = bx,
                    BackY = by,
                    AbsErrMm = err,
                    BackErrPx = backPx,
                    Reliable = p.IsReliable,
                    StatusText = double.IsNaN(err) ? "⚪" : (err <= ResidualToleranceMm ? "✅" : "⚠ 超差")
                };
                ReplayRows.Add(rowModel);

                // 2026-09-09：逐点落盘日志。只给 RMS/MAX 汇总时，"哪一个点错了、错在哪个方向"
                // 无从查证；而各向异性/数据错位恰恰要看逐点残差的【分布形态】才能判断。
                AppendLog($"  [九点回放 P{order}] 像素(col={p.PixelX:F1},row={p.PixelY:F1}) 真值P=({p.WorldX:F3},{p.WorldY:F3})"
                          + $" → H(u)=({hx:F3},{hy:F3}) 反投影(col={bx:F1},row={by:F1})"
                          + $" | 残差 {err:F3}mm ({backPx:F1}px) {(double.IsNaN(err) ? "⚪" : (err <= ResidualToleranceMm ? "✅" : "⚠超差"))}");

                // 黄十字=采样像素（标定当时特征在哪）
                GeometryMarkers.Add(new GeoMarkerItem
                {
                    Row = p.PixelY,
                    Col = p.PixelX,
                    Size = 16,
                    Color = "yellow",
                    Label = "P" + order
                });
                // 青十字=机械真值反投影（H 认为它该在哪）；差>2px 才画，避免同屏噪声
                if (!double.IsNaN(bx) && !double.IsNaN(by) && backPx > 2.0)
                {
                    GeometryMarkers.Add(new GeoMarkerItem { Row = by, Col = bx, Size = 11, Color = "cyan", Label = null });
                }
            }
            InvalidateMarkers();

            double rms = n > 0 ? Math.Sqrt(sumSq / n) : 0;
            ReplaySummaryText = $"采样 {order} 点 · 有效 {n} · RMS {rms:F3}mm · MAX {maxErr:F3}mm · 超差 {over} 点" +
                                $"（黄=采样像素 青=真值反投影，两十字间距=残差）";
            AppendLog("[九点回放] " + ReplaySummaryText + " —— 选中行可【走到该点】复现。");
        }

        /// <summary>③ 走到选中采样点的机械真值位（复现：抓拍后特征应回到该行记录的像素位）</summary>
        private void MoveToSelectedSample()
        {
            var row = SelectedReplayRow;
            if (row == null || _facade == null) return;
            IsBusy = true;
            try
            {
                AppendLog($"③ 复现走位 → 采样点 P{row.Order} 真值 ({row.TrueX:F3},{row.TrueY:F3}) ...");
                bool ok = _facade.MoveToXY(row.TrueX, row.TrueY);
                AppendLog(ok
                    ? $"到位 —— 抓拍后看特征是否回到 P{row.Order} 的黄十字 (col={row.PixelX:F0}, row={row.PixelY:F0})。"
                    : "走位被拒: " + (_facade.LastError ?? "未知原因"));
            }
            catch (Exception ex)
            {
                AppendLog("复现走位异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                RaiseGeometryCanExecutes();
            }
        }

        // ==================== ④ H+e+t 全量 ====================

        /// <summary>
        /// ④ 全量链路（2026-09-08 定案，与生产引擎/发布链同一个真源 CalibrationGeometry）：
        ///   X_obj = P_photo + O − H(u)　　P_go = X_obj − R(U_go − U0)·e
        /// 中间量全展开，哪一步错了直接看得到。
        /// 退路：无 O/e 但 U≈U0 → P_go = P_photo + H(p_tip) − H(u)；都没有 → 视觉直吸并明确提示。
        /// </summary>
        public void RefreshFullChain()
        {
            if (!GeoHasPick)
            {
                FullInfoText = "先在图上点选特征。";
                return;
            }
            double u0 = Profile.CalibU0 ?? 0.0;
            var pose = TryReadCurrentPose();
            double uGo = pose.HasValue && !double.IsNaN(pose.Value.U) ? pose.Value.U : u0;

            // 拍照瞬间机位 P_photo（优先抓拍定格快照，与在线打点同口径）
            var photo = _photoPose ?? pose;
            bool hasPhoto = photo.HasValue && !double.IsNaN(photo.Value.X) && !double.IsNaN(photo.Value.Y);
            double px = hasPhoto ? photo.Value.X : 0.0;
            double py = hasPhoto ? photo.Value.Y : 0.0;

            bool hasO = Profile.HasRotationCenter;
            bool hasE = Profile.IsNozzleEccCalibrated
                        && (Math.Abs(Profile.ToolOffsetPureWx) > 1e-9
                            || Math.Abs(Profile.ToolOffsetPureWy) > 1e-9);
            bool hasTip = Profile.IsToolOffsetCalibrated
                          && Profile.ToolOffsetMethod == ToolOffsetMethod.EyeInHandIndirect;

            string mode;
            double objX = _geoPickWorldX, objY = _geoPickWorldY;

            if (hasO && hasE && hasPhoto)
            {
                var obj = CalibrationGeometry.ObjectBase(_geoPickWorldX, _geoPickWorldY, px, py,
                    Profile.ToolCenterWx, Profile.ToolCenterWy, eih: true);
                objX = obj.X; objY = obj.Y;
                var cmd = CalibrationGeometry.CommandFor(objX, objY,
                    Profile.ToolOffsetPureWx, Profile.ToolOffsetPureWy, uGo, u0);
                _geoFullTargetX = cmd.X;
                _geoFullTargetY = cmd.Y;
                mode = "定案式（任意 U 角精确）";
            }
            else if (hasTip && hasPhoto && Math.Abs(uGo - u0) < 2.0)
            {
                var tp = _calibService.MapPixelToWorld(MatrixPath, Profile.ToolAlignPixelX, Profile.ToolAlignPixelY);
                if (tp.Success)
                {
                    _geoFullTargetX = px + tp.Data.WorldX - _geoPickWorldX;
                    _geoFullTargetY = py + tp.Data.WorldY - _geoPickWorldY;
                    objX = _geoFullTargetX; objY = _geoFullTargetY;
                    mode = "差分退路（仅 U≈U0；缺 O 或 e）";
                }
                else
                {
                    _geoFullTargetX = _geoPickWorldX;
                    _geoFullTargetY = _geoPickWorldY;
                    mode = "视觉直吸（p_tip 映射失败）";
                }
            }
            else
            {
                _geoFullTargetX = _geoPickWorldX;
                _geoFullTargetY = _geoPickWorldY;
                mode = "视觉直吸（缺 O/e 或 P_photo，落点不可信）";
            }

            FullInfoText =
                $"w = H(u) = ({_geoPickWorldX:F3}, {_geoPickWorldY:F3})\n" +
                $"P_photo = {(hasPhoto ? $"({px:F3}, {py:F3})" : "未读到")}　" +
                $"O（旋转中心）= {(hasO ? $"({Profile.ToolCenterWx:F3}, {Profile.ToolCenterWy:F3})" : "未标")}\n" +
                $"e（真吸嘴偏心）= {(hasE ? $"({Profile.ToolOffsetPureWx:F3}, {Profile.ToolOffsetPureWy:F3})" : "未标")}　" +
                $"U0={u0:F1}° U_go={uGo:F1}°\n" +
                $"X_obj = P_photo + O − w = ({objX:F3}, {objY:F3})\n" +
                $"P_go = X_obj − R(U_go−U0)·e = ({_geoFullTargetX:F3}, {_geoFullTargetY:F3})\n" +
                $"【{mode}】";
            FullTargetText = $"({_geoFullTargetX:F3}, {_geoFullTargetY:F3}) mm";
            AppendLog($"[全量] w=({_geoPickWorldX:F3},{_geoPickWorldY:F3}) → X_obj=({objX:F3},{objY:F3}) → P_go=({_geoFullTargetX:F3},{_geoFullTargetY:F3})　[{mode}]");
        }

        private double _geoFullTargetX;
        private double _geoFullTargetY;

        /// <summary>④ 走位到 R_go（到位后目视吸嘴尖是否压住特征 —— TCO 只能这样证伪）</summary>
        private void MoveFullTarget()
        {
            if (!GeoHasPick) return;
            RefreshFullChain();
            if (_facade == null)
            {
                AppendLog("未绑定运动卡，无法走位。");
                return;
            }
            IsBusy = true;
            try
            {
                AppendLog($"④ 全量走位 → R_go=({_geoFullTargetX:F3}, {_geoFullTargetY:F3}) ...");
                bool ok = _facade.MoveToXY(_geoFullTargetX, _geoFullTargetY);
                if (ok)
                {
                    AppendLog("到位 —— ★目视：吸嘴尖应正对点选特征（下压到工件面高度再判断，避免投影视差）。");
                    AppendLog("  ⚠ 提醒：工件不动时 H(u)=机位恒成立，'再拍一张比对'证伪不了任何落点量，只能靠吸嘴尖物理压中判断。");
                }
                else
                {
                    AppendLog("走位被拒: " + (_facade.LastError ?? "未知原因"));
                }
            }
            catch (Exception ex)
            {
                AppendLog("全量走位异常: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                RaiseGeometryCanExecutes();
            }
        }
    }

    /// <summary>① 正向校验行（H：像素→机械，残差= H(u) − 拍照机位）</summary>
    public sealed class GeoCheckRow
    {
        public int Order { get; set; }
        public double Col { get; set; }
        public double Row { get; set; }
        public double PoseX { get; set; }
        public double PoseY { get; set; }
        public double Hx { get; set; }
        public double Hy { get; set; }
        public double Dx { get; set; }
        public double Dy { get; set; }
        public double AbsErr { get; set; }
        public string StatusText { get; set; }
    }

    /// <summary>③ 九点回放行（采样快照重投影）</summary>
    public sealed class SampleReplayRow
    {
        public int Order { get; set; }
        public double PixelX { get; set; }
        public double PixelY { get; set; }
        public double TrueX { get; set; }
        public double TrueY { get; set; }
        public double Hx { get; set; }
        public double Hy { get; set; }
        public double BackX { get; set; }
        public double BackY { get; set; }
        public double AbsErrMm { get; set; }
        public double BackErrPx { get; set; }
        public bool Reliable { get; set; }
        public string StatusText { get; set; }
    }

    /// <summary>图上标记项（VM 产出 → 视图层调宿主 AddMarkerCross 绘制）</summary>
    public sealed class GeoMarkerItem
    {
        public double Row { get; set; }
        public double Col { get; set; }
        public double Size { get; set; }
        public string Color { get; set; }
        public string Label { get; set; }
    }
}
