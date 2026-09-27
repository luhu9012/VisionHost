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
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Grayson.Vision.Contracts.Calibration.Chain;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
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

        public double WorldX { get { return _worldX; } set { Set(ref _worldX, value); } }
        public double WorldY { get { return _worldY; } set { Set(ref _worldY, value); } }
        public double PixelCol { get { return _pixelCol; } set { Set(ref _pixelCol, value); } }
        public double PixelRow { get { return _pixelRow; } set { Set(ref _pixelRow, value); } }
        public double PhotoX { get { return _photoX; } set { Set(ref _photoX, value); } }
        public double PhotoY { get { return _photoY; } set { Set(ref _photoY, value); } }
        public double PhotoU { get { return _photoU; } set { Set(ref _photoU, value); } }
        public string Note { get { return _note; } set { Set(ref _note, value); } }
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
                "✓ 拟合成功：n={0}, RMS={1:F4}, σ1={2:F3}, σ2={3:F3}, 形状偏差={4:F4} ({5}), 矩阵=[{6:G9},{7:G9},{8:G9},{9:G9},{10:G9},{11:G9}]",
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
                    Version = 1,
                    CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                    Operator = "ChainWizardV2",
                    Note = string.Format(CultureInfo.InvariantCulture,
                        "σ1={0:F3} σ2={1:F3} shapeDev={2:F4}", _sigma1, _sigma2, _shapeDev),
                },
            };
            if (HasDeltaRef)
                node.DeltaRefPixel = new[] { DeltaRefCol, DeltaRefRow };
            return node;
        }
    }

    /// <summary>工具节点行（骨架决定主/副与绑定关系；向导只填实测偏移量）</summary>
    public class ChainToolRowViewModel : ViewModelBase
    {
        private double _offsetDx;
        private double _offsetDy;
        private bool _isMaster;
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
            Ready = OffsetFilled ? "✓ 已填" : "待测";
        }

        public bool OffsetFilled { get { return !(OffsetDx == 0 && OffsetDy == 0); } }
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
        public string Status
        {
            get { return _status; }
            set { Set(ref _status, value); }
        }
    }

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

        public ChainWizardViewModel(string stationCode)
        {
            _stationCode = stationCode;
            var plan = PlanFromProfile(stationCode);
            _draft = plan.Ok ? plan.Draft : null;
            PlanNotes = plan.Ok
                ? "【系统自动裁决】" + string.Join("；", plan.AutoNotes)
                  + (plan.OpenQuestions.Count > 0 ? " ｜【请人工确认】" + string.Join("；", plan.OpenQuestions) : "")
                : "❌ 拓扑推导失败：" + string.Join("；", plan.Errors);

            Sections = new ObservableCollection<ChainCameraSectionViewModel>();
            ToolRows = new ObservableCollection<ChainToolRowViewModel>();
            Steps = new ObservableCollection<ChainStepRow>();
            if (_draft != null)
                BuildUiFromDraft();
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
                    Workflow = s.IsDownCorrect ? "WF-01 九点标定（下固定）" : (s.IsEih ? "WF-01 九点标定（EIH·随动）" : "WF-01 九点标定（ETH·固定）"),
                    Hint = s.IsEih ? "物料：Mark/标定物＋每点记录拍照位姿（法兰系规范化后拟合）"
                                   : "物料：Mark/标定物＋机器人走位读世界坐标",
                });
            }
            foreach (var s in Sections.Where(x => x.IsDownCorrect))
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = s.CameraId,
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
                    Workflow = "WF-03 主工具 TCP 偏移（对针直量）",
                    Hint = "物料：固定基准针尖；Offset=对针直量的 T_TCP→Flange 矢量（U=0 基准）",
                });
            }
            foreach (var t in ToolRows.Where(x => !x.IsMaster))
            {
                Steps.Add(new ChainStepRow
                {
                    StepNo = no++,
                    Target = t.ToolId,
                    Workflow = "WF-04 副工具 Δ（底拍批量/手输）",
                    Hint = "所有吸嘴伸到下相机上方一拍取各嘴中心两两差；同轴工具实测=(0,0)",
                });
            }
            Steps.Add(new ChainStepRow
            {
                StepNo = no,
                Target = "整链",
                Workflow = "门禁总检 G0~G4 + 形状门 → 落盘 Chain.json",
                Hint = "任一门不过即拒绝保存（fail-closed 与生产端同一把尺）",
            });
            RefreshSteps();
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
        }

        public string WindowTitle { get { return "链向导 v2（工位驱动）—— " + _stationCode; } }

        public string StationCode { get { return _stationCode; } }

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
                        Method = row.IsMaster ? "ChainWizard-对针直量" : "ChainWizard-刚性阵列Δ",
                        Version = 1,
                        CapturedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                        Operator = "ChainWizardV2",
                        Note = row.IsMaster ? "Offset=对针直量 T_TCP→Flange（U=0 基准，符号内蕴）"
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
