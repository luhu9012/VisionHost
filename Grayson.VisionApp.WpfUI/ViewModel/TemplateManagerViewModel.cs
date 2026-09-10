//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TemplateManagerViewModel.cs
// 说 明: 视觉模板管理 VM —— 左侧模板列表（增删）+ 右侧创建向导
//        （本地图片 / 相机采集 → 框选 ROI → 设角度 → 创建落盘）。
//        模板被 ShapeMatch / NccMatch 节点按名称（TemplateName）引用，
//        与标定管理（CalibrationProfileName）的引用模式一致。
//===================================================================================
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.Services;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 模板工作台 VM（TemplateManagerView 的 DataContext；页面缓存单例=编辑现场跨导航保活）。
    /// 2026-09-05 合并：原 TemplateWorkbenchViewModel（壳层）的页头 Scope/导航职责下沉至此，
    /// Workbench 壳文件删除——模板页只剩本 View + 本 VM 单层，消除 Manager/Workbench 双命名困惑。
    /// 相机采集只取一帧（快照模式），避免长期占用取流所有权。
    /// </summary>
    public class TemplateManagerViewModel : ViewModelBase, INavigationAware
    {
        private readonly TemplateManager _templateManager;
        private readonly HalconImageRenderService _renderService;

        /// <summary>当前展示/创建用的图像上下文（持有显示帧句柄，替换前释放旧帧）</summary>
        private WpfImageRenderContext _currentContext;
        /// <summary>
        /// 引擎真源帧（ShowImage 传入的原 wrapper，拥有 raw HImage，恒持有直到 ResetWizard）。
        /// 显示帧与真源分离（P1 学习域预览，2026-09-09）：
        ///   · _currentContext.Image = 显示帧（显示拷贝 / 学习域预览合成帧），可随时替换/释放；
        ///   · 创建模板一律从 _rawFrame 取图 —— 掩膜预览把画面切成灰度预览帧时，创建仍学原图。
        /// </summary>
        private HalconRenderImage _rawFrame;
        /// <summary>当前 _currentContext.Image 是否为"学习域预览帧"（掩膜可视化；决定恢复路径）</summary>
        private bool _previewActive;
        /// <summary>学习域预览合成请求序号：并发合成只认最新，过期帧丢弃（防快涂时旧帧覆盖新状态）</summary>
        private int _previewSeq;
        private bool _previewBusy;
        private ICamera _pendingCamera;
        private bool _pendingAcquire;
        private bool _weStartedGrab;
        /// <summary>快照前若相机处于软/硬触发模式，记录原模式，收帧/超时后恢复</summary>
        private int? _triggerModeToRestore;
        private DispatcherTimer _acquireTimeoutTimer;
        /// <summary>快照采集完成信号（相机帧到达/超时后置位），供 AcquireSnapshotAsync 可等待化</summary>
        private TaskCompletionSource<bool> _snapshotTcs;

        /// <summary>实拍验证的匹配可视化叠加（轮廓/十字/分数标注），随当前 context 显示</summary>
        private List<ImageOverlay> _verifyOverlays = new List<ImageOverlay>();
        /// <summary>ROI 常驻框叠加（基底 ROI 显示：默认黄矩形 Region；有形状基底时=用户形状 XLD 描边）</summary>
        private ImageOverlay _roiOverlay;

        /// <summary>
        /// 基底 ROI 形状（v2，2026-09-09）：用户在图上画的【圆/椭圆/旋转矩形/多边形】等学习框，
        /// null = 矩形窗口语义（旧模板/默认）。
        /// · 创建/覆盖重学 → 随 TemplateInfo.RoiShape 落盘；
        /// · 编辑载入 → 从资产读回，经 EditorRoiReady 回注宿主（ReplaceRois）→ 可点选/拖动/手柄编辑；
        /// · 学习域起算基底：掩膜预览帧 / 整域轮廓 / 创建链引擎全部按"形状∩ROI"（非外接矩形）。
        /// </summary>
        private TemplateMaskShape _baseRoiShape;

        /// <summary>模板载入编辑器完成、基底形状就绪后触发（宿主已异步清空旧 ROI 集合）——
        /// View 订阅后把 BuildHostBaseRoi() 回注宿主（ReplaceRois），使 ROI 可点选编辑。</summary>
        public event EventHandler EditorRoiReady;

        /// <summary>编辑场景真正切换/重置（换源图取景、新建向导清场）时触发 ——
        /// View 订阅后调宿主 ClearRoisSilently()（不触发掩膜/特征清除语义）：
        /// 宿主 ROI 集合里的形状按旧图像素锚定，留在新场景上会悬浮/误命中。基底形状本体由 VM 持有不受影响。</summary>
        public event EventHandler EditorSceneReset;

        /// <summary>学习掩膜笔画叠加（2026-09-09 版图化后 = 学习域整域轮廓一条：lime；
        /// 取代旧"逐笔描边"——整域与灰化预览帧/创建链同源，避免多笔堆叠观感）</summary>
        private readonly List<ImageOverlay> _maskOverlays = new List<ImageOverlay>();

        /// <summary>当前向导/编辑器里的掩膜笔画（按绘制顺序），创建/重学时序列化进 TemplateInfo.LearnMask 落盘</summary>
        private List<TemplateMaskShape> _maskStrokes = new List<TemplateMaskShape>();

        /// <summary>🖌 涂抹"未收笔轨迹"（宿主 BrushSketchChanged 广播，图像坐标 X=Col/Y=Row）：
        /// 按住时实时并入学习域预览/整域轮廓（∪ 版图实时扩 / ∖ 实时抠洞），收笔/取消后清空</summary>
        private Point[] _sketchPoints;
        private double _sketchRadius = 14;
        private bool _sketchDown;
        /// <summary>整域轮廓重建节流（涂抹新采样点高频广播；region 布尔运算 μs~ms 级，40ms 足够平滑）</summary>
        private DateTime _lastContourRebuild = DateTime.MinValue;
        private static readonly TimeSpan ContourRebuildMinInterval = TimeSpan.FromMilliseconds(40);

        /// <summary>最近一次载入的本地源图路径（文件模式创建模板时写入 SourceImagePath，供【✏️ 编辑选中】回读重载原图继续编辑）</summary>
        private string _lastSourceImagePath = "";

        /// <summary>模板特征叠加（青色：特征点=十字 / 特征面=轮廓，编辑器静态显示在 ROI 中心周围）</summary>
        private readonly List<ImageOverlay> _featureOverlays = new List<ImageOverlay>();

        /// <summary>当前向导/编辑器里的模板特征（点/面，相对 ROI 中心偏移），创建/重学时随模板落盘</summary>
        private List<TemplateFeature> _features = new List<TemplateFeature>();

        #region 模板列表

        public ObservableCollection<TemplateInfo> Templates { get; } = new ObservableCollection<TemplateInfo>();

        private TemplateInfo _selectedTemplate;
        public TemplateInfo SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (Set(ref _selectedTemplate, value))
                {
                    // 列表选中项变化后刷新删除/验证/编辑按钮（未选中时禁用）
                    DeleteTemplateCommand?.RaiseCanExecuteChanged();
                    VerifyTemplateCommand?.RaiseCanExecuteChanged();
                    EditTemplateCommand?.RaiseCanExecuteChanged();
                    // 切换模板后，上一次验证的"位置基准"对新模板无意义，清空锁死检测缓存
                    _lastVerifyRow = null;
                    _lastVerifyCol = null;
                }
            }
        }

        #endregion

        #region 图像显示

        /// <summary>Halcon 图像显示 VM（HalconImageDisplayHost 绑定）</summary>
        public ImageDisplayVm DisplayVm { get; }

        private bool _hasImage;
        public bool HasImage
        {
            get => _hasImage;
            private set
            {
                if (Set(ref _hasImage, value))
                {
                    // 依赖 HasImage 的命令必须显式刷新（RelayCommand 脱离 CommandManager，不监听属性变化）
                    CreateTemplateCommand?.RaiseCanExecuteChanged();
                    VerifyTemplateCommand?.RaiseCanExecuteChanged();
                    LearnTemplateCommand?.RaiseCanExecuteChanged();
                    // 换了新图（如目标挪位后重新采集），上一次验证位置不再可比，清空锁死检测缓存
                    _lastVerifyRow = null;
                    _lastVerifyCol = null;
                }
            }
        }

        #endregion

        #region 源图模式与相机

        private bool _isFileMode = true;
        public bool IsFileMode
        {
            get => _isFileMode;
            set { if (Set(ref _isFileMode, value) && value) OnPropertyChanged(nameof(IsCameraMode)); }
        }

        public bool IsCameraMode
        {
            get => !_isFileMode;
            set { if (Set(ref _isFileMode, !value)) OnPropertyChanged(nameof(IsFileMode)); }
        }

        public ObservableCollection<ICamera> Cameras { get; } = new ObservableCollection<ICamera>();

        private ICamera _selectedCamera;
        public ICamera SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                if (Set(ref _selectedCamera, value))
                {
                    // 相机下拉切换后刷新采集/验证按钮（无相机时禁用）
                    AcquireImageCommand?.RaiseCanExecuteChanged();
                    VerifyTemplateCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region P1 分步引导（模板创建 5 步：源图→学习框→掩膜→特征→参数与创建）
        // 轻量向导：不强制分步（不锁死各区块显隐），以"步骤条 + 当前步引导横幅 + 快捷动作"
        // 指导新手按序完成；老手可直接操作任意区块，切步只换引导不毁状态。
        // 0=选源图 1=框学习框 2=掩膜∪/∖(可选) 3=特征/基准点(可选) 4=参数与创建

        private int _wizardStep;
        public int WizardStep
        {
            get => _wizardStep;
            set
            {
                int v = Math.Max(0, Math.Min(4, value));
                if (Set(ref _wizardStep, v))
                {
                    OnPropertyChanged(nameof(Step0Active));  // 步骤条高亮
                    OnPropertyChanged(nameof(Step1Active));
                    OnPropertyChanged(nameof(Step2Active));
                    OnPropertyChanged(nameof(Step3Active));
                    OnPropertyChanged(nameof(Step4Active));
                    OnPropertyChanged(nameof(WizardStepHint));
                    OnPropertyChanged(nameof(IsFinalStep));
                    OnPropertyChanged(nameof(IsFirstStep));
                    GoNextStepCommand?.RaiseCanExecuteChanged();
                    GoPrevStepCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool Step0Active => WizardStep == 0;
        public bool Step1Active => WizardStep == 1;
        public bool Step2Active => WizardStep == 2;
        public bool Step3Active => WizardStep == 3;
        public bool Step4Active => WizardStep == 4;
        public bool IsFirstStep => WizardStep == 0;
        public bool IsFinalStep => WizardStep == 4;

        public RelayCommand GoNextStepCommand { get; private set; }
        public RelayCommand GoPrevStepCommand { get; private set; }

        private void GoNextStep() => WizardStep++;
        private void GoPrevStep() => WizardStep--;

        /// <summary>当前步骤引导横幅（每步做什么 + 完成判据/可跳过提示）</summary>
        public string WizardStepHint
        {
            get
            {
                switch (WizardStep)
                {
                    case 0:
                        return HasImage
                            ? "✅ 源图已就绪。点【2】框学习框，或直接在图上看清工件后进入下一步。"
                            : "① 请先在上方选【📁 本地图片】载入源图，或切【📷 相机采集】抓一帧工件图像（目标要清晰、光照稳定）。";
                    case 1:
                        return RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1
                            ? "✅ 学习框已框选：只包住目标本体（目标占框 50% 以上，四边留 5~8px）。可拖动黄框微调，或进入【3】用掩膜剔除框内干扰。"
                            : "② 框选学习框：点【✏️ 框选学习框】后在图上按住左键拖出矩形（只包目标本体，框心对准关键点），松开再右键完成。";
                    case 2:
                        return _maskStrokes.Count > 0
                            ? $"✅ 掩膜已生效（{_maskStrokes.Count} 笔）。可继续涂或点【↩ 撤销】；不需要可直接去【4】。"
                            : "③ 掩膜（可选）：点【🖌 涂抹】开启后按住左键在图上刷——模式=保留∪ 涂哪学哪；模式=排除∖ 抠掉字符/划痕/背景。右侧滑杆调画笔粗细。不掩膜=学整框。";
                    case 3:
                        return _features.Count > 0
                            ? $"✅ 已标注 {_features.Count} 个特征。识别时特征随模板位姿显示；不需要可直接去【5】。"
                            : "④ 特征/基准点（可选）：点【📍 标注】后画小形状取中心=特征点（定位/量测用），或切 ▨面 画特征面。不需要特征直接去【5】。";
                    default:
                        return HasImage && RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1
                            ? "⑤ 最后一步：起好模板名（节点引用键）→ 核对角度范围 → 点【🚀 创建模板】。创建后按体检报告用【🔍 实拍验证】两步法复核。"
                            : "⑤ 最后一步：请先回【2】框选学习框，才能创建模板。";
                }
            }
        }

        #endregion

        #region 创建参数表单

        private string _templateName;
        public string TemplateName
        {
            get => _templateName;
            set => Set(ref _templateName, value);
        }

        private TemplateMatchType _templateType = TemplateMatchType.Shape;
        public TemplateMatchType TemplateType
        {
            get => _templateType;
            set => Set(ref _templateType, value);
        }

        /// <summary>模板类型下拉数据源（Shape / Ncc）</summary>
        public Array TemplateTypeOptions => Enum.GetValues(typeof(TemplateMatchType));

        // 默认全角度 -180~180°：工件/工件在工台上可能任意旋转，训练时直接覆盖完整圆周，
        // 节点运行期再用 AngleStart/AngleEnd 按需收窄，避免大角度摆放无法识别。
        private double _angleStart = -180.0;
        public double AngleStart
        {
            get => _angleStart;
            set => Set(ref _angleStart, value);
        }

        private double _angleEnd = 180.0;
        public double AngleEnd
        {
            get => _angleEnd;
            set => Set(ref _angleEnd, value);
        }

        private string _remark;
        public string Remark
        {
            get => _remark;
            set => Set(ref _remark, value);
        }

        private double _roiRow1;
        public double RoiRow1
        {
            get => _roiRow1;
            set => Set(ref _roiRow1, value);
        }

        private double _roiCol1;
        public double RoiCol1
        {
            get => _roiCol1;
            set => Set(ref _roiCol1, value);
        }

        private double _roiRow2;
        public double RoiRow2
        {
            get => _roiRow2;
            set => Set(ref _roiRow2, value);
        }

        private double _roiCol2;
        public double RoiCol2
        {
            get => _roiCol2;
            set => Set(ref _roiCol2, value);
        }

        #endregion

        #region 搜索框 SearchRoi（模板资产：限制"在哪找"，与学习框分离；引擎 MatchWithDatum/生产节点自动裁剪）

        /// <summary>搜索框叠加（橙色 Region 描边，与学习框黄框/特征青/掩膜 lime 区分）</summary>
        private ImageOverlay _searchRoiOverlay;

        private bool _searchRoiEnabled;
        /// <summary>启用搜索框：模板匹配只在搜索框内找目标（标定期建议≈整图、生产期=工件+来料偏差 1.5~2 倍）</summary>
        public bool SearchRoiEnabled
        {
            get => _searchRoiEnabled;
            set
            {
                if (Set(ref _searchRoiEnabled, value))
                {
                    if (value && (SearchRoiRow2 <= SearchRoiRow1 || SearchRoiCol2 <= SearchRoiCol1))
                    {
                        // 首次启用且还没有有效范围 → 默认整图（随后可框选/手填收紧）
                        SearchRoiSetToFull(silent: true);
                    }
                    UpdateSearchRoiOverlay();
                }
            }
        }

        private double _searchRoiRow1;
        public double SearchRoiRow1
        {
            get => _searchRoiRow1;
            set { if (Set(ref _searchRoiRow1, value)) UpdateSearchRoiOverlay(); }
        }

        private double _searchRoiCol1;
        public double SearchRoiCol1
        {
            get => _searchRoiCol1;
            set { if (Set(ref _searchRoiCol1, value)) UpdateSearchRoiOverlay(); }
        }

        private double _searchRoiRow2;
        public double SearchRoiRow2
        {
            get => _searchRoiRow2;
            set { if (Set(ref _searchRoiRow2, value)) UpdateSearchRoiOverlay(); }
        }

        private double _searchRoiCol2;
        public double SearchRoiCol2
        {
            get => _searchRoiCol2;
            set { if (Set(ref _searchRoiCol2, value)) UpdateSearchRoiOverlay(); }
        }

        private bool _searchRoiDrawMode;
        /// <summary>框选搜索框模式（与掩膜/特征编辑互斥）：开启后宿主下一笔闭合形状=搜索框（仅 ▭ 矩形），收笔自动回填并退出</summary>
        public bool SearchRoiDrawMode
        {
            get => _searchRoiDrawMode;
            set
            {
                if (Set(ref _searchRoiDrawMode, value))
                {
                    if (value)
                    {
                        // 与掩膜/特征编辑互斥：搜索框与学习语义只能有一种"绘制去向"
                        if (_maskEditVisible) MaskEditVisible = false;
                        if (_featureEditVisible) FeatureEditVisible = false;
                        if (_caliperDrawMode) CaliperDrawMode = false; // 批3：卡尺拖绘让位
                        StatusText = "🔷 框选搜索框模式：用视图工具条 📐▾ 的 ▭ 矩形在图上拖出搜索范围（左键拖出→右键完成）；收笔自动回填橙色框并退出。搜索框=匹配时'在哪找'（建议 ⊇ 学习框）";
                    }
                    else
                    {
                        StatusText = "搜索框框选已退出";
                    }
                }
            }
        }

        /// <summary>搜索框设为整图（首次启用/一键恢复）；silent=true 不刷状态栏（属性 setter 内部调用）</summary>
        public void SearchRoiSetToFull(bool silent = false)
        {
            double h = 0, w = 0;
            if (_currentContext?.Image != null)
            {
                try { h = _currentContext.Image.Height; w = _currentContext.Image.Width; } catch { }
            }
            if (h < 2 || w < 2)
            {
                if (!silent) StatusText = "暂无图像，无法设为整图：请先载入源图";
                return;
            }
            SearchRoiRow1 = 0;
            SearchRoiCol1 = 0;
            SearchRoiRow2 = h - 1;
            SearchRoiCol2 = w - 1;
            if (!silent) StatusText = $"搜索框已设为整图（0,0 ~ {h - 1:0},{w - 1:0}）：匹配在全图找目标，不限制范围";
            LogBus.Info("TemplateSearchRoi", $"搜索框设为整图 {w:0}x{h:0}");
        }

        /// <summary>清空搜索框（不启用）：数值归零 + 移除橙色叠加</summary>
        public void SearchRoiClear()
        {
            _searchRoiEnabled = false;
            OnPropertyChanged(nameof(SearchRoiEnabled));
            SearchRoiRow1 = 0; SearchRoiCol1 = 0;
            SearchRoiRow2 = 0; SearchRoiCol2 = 0;
            (_searchRoiOverlay?.NativeHandle as IDisposable)?.Dispose();
            _searchRoiOverlay = null;
            RebuildDisplayOverlays();
            StatusText = "搜索框已清除（匹配将不做范围限制）";
        }

        /// <summary>由视图搜索框框选收笔调用：把 ▭ 矩形（已钳到图像内）写入搜索框数值并刷新橙色叠加。
        /// 返回是否接受（非法矩形/未启用会拒绝并提示）。</summary>
        public bool ApplySearchRoiBounds(double r1, double c1, double r2, double c2)
        {
            if (r2 - r1 < 2 || c2 - c1 < 2)
            {
                StatusText = "搜索框太小（需 ≥2px 宽高），已忽略该次框选";
                return false;
            }
            _searchRoiEnabled = true;
            OnPropertyChanged(nameof(SearchRoiEnabled));
            SearchRoiRow1 = r1; SearchRoiCol1 = c1;
            SearchRoiRow2 = r2; SearchRoiCol2 = c2;
            LogBus.Info("TemplateSearchRoi", $"搜索框更新: R({r1:F1},{c1:F1})-({r2:F1},{c2:F1})");
            return true;
        }

        /// <summary>搜索框叠加刷新（Region 描边，橙；数值/开关变化即调用；无图/未启用/无效=移除）</summary>
        private void UpdateSearchRoiOverlay()
        {
            if (_searchRoiOverlay != null)
            {
                (_searchRoiOverlay.NativeHandle as IDisposable)?.Dispose();
                _searchRoiOverlay = null;
            }
            bool valid = _searchRoiEnabled
                && SearchRoiRow2 > SearchRoiRow1 && SearchRoiCol2 > SearchRoiCol1;
            if (valid && _currentContext != null && HasImage)
            {
                try
                {
                    _searchRoiOverlay = new ImageOverlay
                    {
                        Kind = OverlayKind.Region,
                        Color = "orange",
                        NativeHandle = TemplateCreationBridge.CreateRoiRectangle(
                            SearchRoiRow1, SearchRoiCol1, SearchRoiRow2, SearchRoiCol2)
                    };
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateSearchRoi", $"搜索框叠加生成失败: {ex.Message}");
                }
            }
            RebuildDisplayOverlays();
        }

        #endregion

        #region 资产卡尺 + 基准点（P1 2026-09-09：工件形状选型 → 按形状生成建议卡尺 → 逐条微调；创建/编辑落盘回读）

        /// <summary>卡尺静态预览叠加（magenta 测量带，锚点@0° 编辑器显示）</summary>
        private ImageOverlay _caliperOverlay;
        /// <summary>基准点标记叠加（红色大十字；仅显式指定非 (0,0) 时画）</summary>
        private ImageOverlay _datumOverlay;

        public static readonly string[] WorkpieceShapeOptions = { "Generic", "Rectangle", "Circle", "Cross" };

        private string _workpieceShape = "Generic";
        /// <summary>工件形状选型（记忆字段；Rectangle/Circle 可一键生成建议卡尺）</summary>
        public string WorkpieceShape
        {
            get => _workpieceShape;
            set => Set(ref _workpieceShape, value);
        }

        /// <summary>卡尺行集合（每行编辑实时写回底层 TemplateCaliper，创建快照直接取用）</summary>
        public ObservableCollection<CaliperRowItem> CaliperRows { get; } = new ObservableCollection<CaliperRowItem>();

        private string _caliperStatusText = "未配置测量卡尺（基准点=模型锚点直出）";
        /// <summary>卡尺资产状态行（数量/作用说明）</summary>
        public string CaliperStatusText
        {
            get => _caliperStatusText;
            private set => Set(ref _caliperStatusText, value);
        }

        private double _datumRow;
        /// <summary>基准点绝对行坐标（0,0=自动：创建时锚点=学习域重心；矩形学习框=ROI 中心）</summary>
        public double DatumRow
        {
            get => _datumRow;
            set { if (Set(ref _datumRow, value)) RebuildDatumOverlay(); }
        }

        private double _datumCol;
        /// <summary>基准点绝对列坐标（0,0=自动）</summary>
        public double DatumCol
        {
            get => _datumCol;
            set { if (Set(ref _datumCol, value)) RebuildDatumOverlay(); }
        }

        /// <summary>把基准点复位为 0,0（=创建时自动钉学习域重心/ROI 中心）</summary>
        public void DatumResetToAuto()
        {
            DatumRow = 0;
            DatumCol = 0;
            StatusText = "基准点已复位 0,0：创建时将自动钉在【学习域重心】（矩形学习框=ROI 中心），find 输出即该点";
        }

        // ===== 基准点精测方式（P1 收尾 2026-09-09）：固定点 / 圆卡尺拟心 / 两线交点 =====
        // 引擎 TemplateCaliperRunner 已支持 CircleCenter/LineIntersection 精测覆盖锚点，
        // 这里补编辑器放置 UI：选方式 + 选来源卡尺；创建快照把 Kind/SourceCaliper 一并落盘。

        /// <summary>精测方式文案（顺序与 TemplateDatumKind 枚举序一致：0 固定点 / 1 圆心 / 2 交点）</summary>
        public static readonly string[] DatumKindNames =
        {
            "固定点(锚点直出)",
            "圆心精测(圆卡尺)",
            "两线交点精测"
        };

        public string[] DatumKindOptions => DatumKindNames;

        private int _datumKindIndex;

        /// <summary>基准点精测方式（0=Point 固定点 / 1=CircleCenter 圆卡尺拟心 / 2=LineIntersection 两线交点）</summary>
        public int DatumKindIndex
        {
            get => _datumKindIndex;
            set
            {
                if (!Set(ref _datumKindIndex, value)) return;
                switch (_datumKindIndex)
                {
                    case 1: // 圆心精测只需 1 条圆卡尺
                        DatumSource2 = string.Empty;
                        break;
                    case 2: // 两线交点只需 2 条线卡尺
                        DatumSource1 = string.Empty;
                        break;
                    default: // 固定点：来源不适用
                        DatumSource1 = string.Empty;
                        DatumSource2 = string.Empty;
                        break;
                }
                RaiseDatumSourceOptions();
                UpdateDatumKindHint();
                StatusText = "🎯 基准点方式已切换为「" + (value >= 0 && value < DatumKindNames.Length ? DatumKindNames[value] : "?") + "」" +
                             (value == 0 ? "：find 输出=模型锚点；0,0=自动钉学习域中心，填坐标=该绝对点为锚点" :
                              (value == 1 ? "：运行时由所选圆卡尺圆周边缘点拟合圆心（亚像素），覆盖锚点输出" :
                               "：运行时由两条所选线卡尺拟合直线求交点（亚像素），覆盖锚点输出"));
                RebuildDatumOverlay(); // 固定点↔精测方式切换：红大十字（显式坐标标记）仅固定点方式显示
            }
        }

        private string _datumSource1 = string.Empty;

        /// <summary>精测来源卡尺 1（CircleCenter=圆/弧卡尺名；LineIntersection=线卡尺名 A）</summary>
        public string DatumSource1
        {
            get => _datumSource1;
            set { if (Set(ref _datumSource1, value)) UpdateDatumKindHint(); }
        }

        private string _datumSource2 = string.Empty;

        /// <summary>精测来源卡尺 2（仅 LineIntersection 第二线卡尺名）</summary>
        public string DatumSource2
        {
            get => _datumSource2;
            set { if (Set(ref _datumSource2, value)) UpdateDatumKindHint(); }
        }

        /// <summary>圆心精测可选圆/弧卡尺（启用中）</summary>
        public string[] DatumSourceCircleOptions => CaliperRows
            .Where(r => r.Enabled && r.Cal != null && r.Cal.Kind != TemplateCaliperKind.Line)
            .Select(r => r.Cal.Name).ToArray();

        /// <summary>两线交点可选线卡尺（启用中）</summary>
        public string[] DatumSourceLineOptions => CaliperRows
            .Where(r => r.Enabled && r.Cal != null && r.Cal.Kind == TemplateCaliperKind.Line)
            .Select(r => r.Cal.Name).ToArray();

        private string _datumKindHint = "固定点=模型锚点（find 输出即其位置）；0,0=自动学习域中心/重心，填坐标=该绝对点为锚点（红大十字标记）";

        /// <summary>基准点方式/来源操作提示（UI 状态行）</summary>
        public string DatumKindHint
        {
            get => _datumKindHint;
            private set => Set(ref _datumKindHint, value);
        }

        private void UpdateDatumKindHint()
        {
            switch (_datumKindIndex)
            {
                case 1:
                    DatumKindHint = string.IsNullOrEmpty(DatumSource1)
                        ? "圆心精测：请选择一条圆/弧卡尺作来源（没有可先 ✨ 生成建议选圆形，或开卡尺拖绘画 ⭘ 圆）——运行时其圆周边缘点拟合的圆心=基准点，亚像素覆盖锚点"
                        : $"圆心精测 ← 圆卡尺 [{DatumSource1}]：运行时拟该圆圆心为基准点（锚点仅作刚性参照）";
                    break;
                case 2:
                    if (string.IsNullOrEmpty(DatumSource1) || string.IsNullOrEmpty(DatumSource2))
                        DatumKindHint = "两线交点精测：请为线1/线2 选两条不同边线卡尺（没有可先 ✨ 生成建议选矩形，或开卡尺拖绘画 〰 线）——两拟合直线交点=基准点，应相交于目标角点/交叉点";
                    else if (string.Equals(DatumSource1, DatumSource2, StringComparison.Ordinal))
                        DatumKindHint = "⚠ 两条来源线卡尺不能相同：请为「线 1 / 线 2」选两条不同边卡尺";
                    else
                        DatumKindHint = $"两线交点精测 ← 线卡尺 [{DatumSource1}] 与 [{DatumSource2}]：两拟合直线求交点为基准点（亚像素）";
                    break;
                default:
                    DatumKindHint = "固定点=模型锚点（find 输出即其位置）；0,0=自动学习域中心/重心，填坐标=该绝对点为锚点（红大十字标记）";
                    break;
            }
        }

        /// <summary>卡尺集合变化后刷新来源下拉并清理失效选择（UpdateCaliperStatusText 尾部/换方式时调用）</summary>
        private void RaiseDatumSourceOptions()
        {
            OnPropertyChanged(nameof(DatumSourceCircleOptions));
            OnPropertyChanged(nameof(DatumSourceLineOptions));
            bool ok1 = false, ok2 = false;
            foreach (var r in CaliperRows)
            {
                if (r.Cal == null || !r.Enabled) continue;
                if (!string.IsNullOrEmpty(DatumSource1) && string.Equals(r.Cal.Name, DatumSource1, StringComparison.Ordinal)
                    && ((_datumKindIndex == 1 && r.Cal.Kind != TemplateCaliperKind.Line)
                        || (_datumKindIndex == 2 && r.Cal.Kind == TemplateCaliperKind.Line)))
                {
                    ok1 = true;
                }
                if (!string.IsNullOrEmpty(DatumSource2) && string.Equals(r.Cal.Name, DatumSource2, StringComparison.Ordinal)
                    && _datumKindIndex == 2 && r.Cal.Kind == TemplateCaliperKind.Line)
                {
                    ok2 = true;
                }
            }
            if (!ok1 && !string.IsNullOrEmpty(DatumSource1)) DatumSource1 = string.Empty; // 失效来源自动清
            if (!ok2 && !string.IsNullOrEmpty(DatumSource2)) DatumSource2 = string.Empty;
        }

        /// <summary>创建快照：按当前方式/来源构建 TemplateDatum（精测基准点坐标交给引擎回填学习域中心；
        /// 固定点 0,0=null 自动锚点）。软失败（来源失效）返回 null 并已提示，消费端按锚点直出。</summary>
        private TemplateDatum BuildDatumSnapshot()
        {
            if (_datumKindIndex == 1)
            {
                bool hit = false;
                foreach (var r in CaliperRows)
                {
                    if (r.Enabled && r.Cal != null && r.Cal.Kind != TemplateCaliperKind.Line
                        && string.Equals(r.Cal.Name, DatumSource1, StringComparison.Ordinal))
                    {
                        hit = true;
                        break;
                    }
                }
                if (!hit)
                {
                    StatusText = "基准点=圆心精测但来源圆卡尺无效：本次按【锚点直出】创建（请先画/选圆卡尺再切换方式）";
                    return null;
                }
                return new TemplateDatum { Kind = TemplateDatumKind.CircleCenter, SourceCaliper1 = DatumSource1 };
            }
            if (_datumKindIndex == 2)
            {
                int hit1 = 0, hit2 = 0;
                foreach (var r in CaliperRows)
                {
                    if (!r.Enabled || r.Cal == null || r.Cal.Kind != TemplateCaliperKind.Line) continue;
                    if (string.Equals(r.Cal.Name, DatumSource1, StringComparison.Ordinal)) hit1++;
                    if (string.Equals(r.Cal.Name, DatumSource2, StringComparison.Ordinal)) hit2++;
                }
                if (hit1 == 0 || hit2 == 0 || string.Equals(DatumSource1, DatumSource2, StringComparison.Ordinal))
                {
                    StatusText = "基准点=两线交点但来源线卡尺缺失/相同：本次按【锚点直出】创建（请选两条不同边卡尺）";
                    return null;
                }
                return new TemplateDatum { Kind = TemplateDatumKind.LineIntersection, SourceCaliper1 = DatumSource1, SourceCaliper2 = DatumSource2 };
            }
            // 固定点：显式坐标 → 钉锚点；0,0 → 自动（引擎钉学习域中心/重心）
            if (Math.Abs(DatumRow) > 0.5 || Math.Abs(DatumCol) > 0.5)
            {
                return new TemplateDatum { Kind = TemplateDatumKind.Point, Row = DatumRow, Col = DatumCol };
            }
            return null;
        }

        /// <summary>当前有效锚点（基准点显式坐标或 ROI 中心）；无有效 ROI 返回 false</summary>
        private bool TryGetAnchor(out double anchorRow, out double anchorCol)
        {
            anchorRow = 0;
            anchorCol = 0;
            if (RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1) return false;
            if (Math.Abs(DatumRow) > 0.5 || Math.Abs(DatumCol) > 0.5)
            {
                anchorRow = DatumRow;
                anchorCol = DatumCol;
            }
            else
            {
                anchorRow = (RoiRow1 + RoiRow2) / 2.0;
                anchorCol = (RoiCol1 + RoiCol2) / 2.0;
            }
            return true;
        }

        /// <summary>是否存在随模板落盘的卡尺/基准点资产：卡尺行；或固定点显式非 0 坐标；或精测方式已选有效来源（用于基底 ROI 清空/重框后的连带处理判断）</summary>
        private bool HasCaliperOrDatumAssets => CaliperRows.Count > 0
            || Math.Abs(_datumRow) > 0.5 || Math.Abs(_datumCol) > 0.5
            || (_datumKindIndex == 1 && !string.IsNullOrEmpty(_datumSource1))
            || (_datumKindIndex == 2 && (!string.IsNullOrEmpty(_datumSource1) || !string.IsNullOrEmpty(_datumSource2)));

        /// <summary>
        /// 按工件形状生成建议卡尺（Rectangle→四边 4×Line；Circle→整圆环 1×Circle；Cross/Generic→无，给指引）。
        /// 几何=相对当前锚点的偏移（锚点=显式基准点或 ROI 中心）。重复调用=覆盖重建（保留人工微调请勿再点）。
        /// </summary>
        public void GenerateCaliperSuggest()
        {
            if (!TryGetAnchor(out double ar, out double ac))
            {
                StatusText = "请先框选学习框（ROI），才能按形状生成卡尺";
                return;
            }
            double r1 = RoiRow1, c1 = RoiCol1, r2 = RoiRow2, c2 = RoiCol2;
            double midRow = (r1 + r2) / 2.0, midCol = (c1 + c2) / 2.0;
            double w = c2 - c1, h = r2 - r1;
            double pad = Math.Min(6.0, Math.Max(1.0, Math.Min(w, h) * 0.08));

            var list = new List<TemplateCaliper>();
            string shape = (WorkpieceShape ?? "").Trim();
            if (string.Equals(shape, "Rectangle", StringComparison.OrdinalIgnoreCase))
            {
                double hw = Math.Max(2.0, w / 2.0 - pad);   // 顶/底边探针沿边散布半跨距
                double hh = Math.Max(2.0, h / 2.0 - pad);   // 左/右边
                list.Add(MakeLine("C1", "顶边", r1 - ar, midCol - ac, 0.0, hw));
                list.Add(MakeLine("C2", "底边", r2 - ar, midCol - ac, 0.0, hw));
                list.Add(MakeLine("C3", "左边", midRow - ar, c1 - ac, Math.PI / 2.0, hh));
                list.Add(MakeLine("C4", "右边", midRow - ar, c2 - ac, Math.PI / 2.0, hh));
            }
            else if (string.Equals(shape, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new TemplateCaliper
                {
                    Name = "C1",
                    Kind = TemplateCaliperKind.Circle,
                    DRow = midRow - ar,
                    DCol = midCol - ac,
                    Length1 = Math.Max(4.0, Math.Min(w, h) / 2.0 - pad), // 目标圆半径（近似：ROI 内切）
                    Length2 = 10.0,
                    NumPoints = 1,
                    Threshold = 30.0,
                    Sigma = 1.0,
                    FitEnabled = true,
                    MinEdgePoints = 8
                });
            }
            else
            {
                // Cross/Generic 无通用建议（十字臂厚/Generic 形状未知，自动生成必错）；给出指引
                StatusText = shape + " 形状暂无通用卡尺建议：先用 Rectangle/Circle 或手动规划（十字可拆 2 条线卡尺测臂边求交点，需知道臂宽）";
            }

            ReplaceCaliperRows(list, shape);
            RebuildCaliperOverlay();
            if (string.Equals(shape, "Rectangle", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = $"已按矩形生成 4 条边卡尺（C1 顶/C2 底/C3 左/C4 右）：紫色测量带横跨四边，拖动 ROI 后建议重新生成；L1/L2/点数可逐条微调";
            }
            else if (string.Equals(shape, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "已按圆形生成整圆环卡尺 C1（目标半径≈ROI 内切）：紫色环带=将采样区域，可调 L2 环宽/点数/阈值";
            }
        }

        private static TemplateCaliper MakeLine(string name, string role, double dRow, double dCol, double phi, double halfSpan)
        {
            return new TemplateCaliper
            {
                Name = name,
                Kind = TemplateCaliperKind.Line,
                DRow = dRow,
                DCol = dCol,
                Phi = phi,
                Length1 = halfSpan,     // 沿边散布半跨距
                Length2 = 10.0,          // 跨边探测半长
                ProbeWidth = 2.0,        // 沿边平均半宽
                NumPoints = 7,
                Threshold = 30.0,
                Sigma = 1.0,
                FitEnabled = true,
                MinEdgePoints = 3
            };
        }

        /// <summary>模板卡尺深拷贝（编辑载入/创建快照用：编辑器行对象与库内缓存对象不直接交引擎，防引擎补默认值/行清空互相污染）</summary>
        private static TemplateCaliper CloneCaliper(TemplateCaliper c)
        {
            if (c == null) return null;
            return new TemplateCaliper
            {
                Name = c.Name,
                Enabled = c.Enabled,
                Kind = c.Kind,
                DRow = c.DRow,
                DCol = c.DCol,
                Phi = c.Phi,
                Length1 = c.Length1,
                Length2 = c.Length2,
                ProbeWidth = c.ProbeWidth,
                Sigma = c.Sigma,
                Threshold = c.Threshold,
                Transition = c.Transition,
                Select = c.Select,
                NumPoints = c.NumPoints,
                ArcStart = c.ArcStart,
                ArcExtent = c.ArcExtent,
                FitEnabled = c.FitEnabled,
                MinEdgePoints = c.MinEdgePoints
            };
        }

        /// <summary>用给定卡尺列表重建行集合（编辑/生成/回读共用；订阅行属性变化→叠加跟随）</summary>
        private void ReplaceCaliperRows(IEnumerable<TemplateCaliper> calipers, string shapeHint)
        {
            if (_caliperDrawMode)
            {
                // 整组重建（生成建议/编辑载入）与"宿主 ROI=行"的拖绘现场冲突：先退出拖绘模式
                // （setter 会 RebuildCaliperOverlay + 通知 View 清理宿主卡尺形状），新行下次进入拖绘时重新回注。
                CaliperDrawMode = false;
            }
            foreach (var row in CaliperRows)
            {
                row.PropertyChanged -= OnCaliperRowPropertyChanged;
            }
            CaliperRows.Clear();
            if (calipers != null)
            {
                foreach (var cal in calipers)
                {
                    if (cal == null) continue;
                    var row = new CaliperRowItem(cal);
                    row.PropertyChanged += OnCaliperRowPropertyChanged;
                    CaliperRows.Add(row);
                }
            }
            UpdateCaliperStatusText(shapeHint);
        }

        private void OnCaliperRowPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RaiseDatumSourceOptions();      // 启用/参数变化 → 基准点精测来源下拉同步（失效自动清）
            RebuildCaliperOverlay(); // 行参数编辑实时更新静态测量带预览（region/XLD 级微秒运算，UI 线程安全）
        }

        private void UpdateCaliperStatusText(string shapeHint)
        {
            if (CaliperRows.Count == 0)
            {
                CaliperStatusText = $"未配置测量卡尺（{shapeHint ?? WorkpieceShape}）；基准点=模型锚点直出";
                return;
            }
            int line = 0, ring = 0, arc = 0;
            foreach (var r in CaliperRows)
            {
                if (r.Cal == null) continue;
                if (r.Cal.Kind == TemplateCaliperKind.Line) line++;
                else if (r.Cal.Kind == TemplateCaliperKind.Arc || r.ArcExtentDeg < 359.5) arc++;
                else ring++;
            }
            CaliperStatusText = $"卡尺 {CaliperRows.Count} 条（线{line}/圆环{ring}/弧{arc}）——匹配后按位姿仿射精测；逐条可调 L1/L2/点数/阈值，紫色测量带=将采样区域";
            RaiseDatumSourceOptions(); // 卡尺集合变化（增/删/启停/重排）→ 基准点精测来源下拉同步
        }

        /// <summary>移除单条卡尺（行内 ✖）</summary>
        public void RemoveCaliperRow(CaliperRowItem row)
        {
            if (row == null || !CaliperRows.Remove(row)) return;
            row.PropertyChanged -= OnCaliperRowPropertyChanged;
            UpdateCaliperStatusText(WorkpieceShape);
            RebuildCaliperOverlay();
            StatusText = $"已移除卡尺 [{row.Cal.Name}]，剩余 {CaliperRows.Count} 条";
        }

        /// <summary>清空全部卡尺行（UI「🧹 清空卡尺」；保留显式基准点——基准点独立于卡尺，仍作锚点输出）</summary>
        public void ClearCalipersAll()
        {
            if (CaliperRows.Count == 0)
            {
                StatusText = "当前没有卡尺可清空";
                return;
            }
            foreach (var row in CaliperRows)
            {
                row.PropertyChanged -= OnCaliperRowPropertyChanged;
            }
            CaliperRows.Clear();
            UpdateCaliperStatusText(WorkpieceShape);
            RebuildCaliperOverlay(); // 带/基准点一起重绘（基准点保留）
            StatusText = "已清空全部测量卡尺：回到【基准点=模型锚点直出】（显式基准点十字仍保留，如需删除点「基准点复位 0,0」）";
        }

        /// <summary>工件形状下拉数据源（绑定 ItemsSource；见静态 WorkpieceShapeOptions）</summary>
        public string[] WorkpieceShapeItems => WorkpieceShapeOptions;

        /// <summary>清空全部卡尺与基准点显式值（内部：ClearRoi/ResetWizard）</summary>
        private void ClearCaliperAssets()
        {
            if (_caliperDrawMode)
            {
                // 基底没了，拖绘现场（宿主卡尺形状依赖锚点）一并退出；View 经 PropertyChanged 清理宿主
                CaliperDrawMode = false;
            }
            foreach (var row in CaliperRows)
            {
                row.PropertyChanged -= OnCaliperRowPropertyChanged;
            }
            CaliperRows.Clear();
            _datumRow = 0;
            _datumCol = 0;
            OnPropertyChanged(nameof(DatumRow));
            OnPropertyChanged(nameof(DatumCol));
            _datumKindIndex = 0;
            OnPropertyChanged(nameof(DatumKindIndex));
            _datumSource1 = string.Empty;
            _datumSource2 = string.Empty;
            OnPropertyChanged(nameof(DatumSource1));
            OnPropertyChanged(nameof(DatumSource2));
            UpdateDatumKindHint();
            RaiseDatumSourceOptions();
            DisposeCaliperOverlays();
            UpdateCaliperStatusText(WorkpieceShape);
            // 叠加被移除后立即刷新显示（有图时；ResetWizard 尾部会整体拆 context，无需这里强刷）
            if (HasImage && _currentContext != null)
            {
                RebuildDisplayOverlays();
            }
        }

        private void DisposeCaliperOverlays()
        {
            (_caliperOverlay?.NativeHandle as IDisposable)?.Dispose();
            _caliperOverlay = null;
            (_datumOverlay?.NativeHandle as IDisposable)?.Dispose();
            _datumOverlay = null;
        }

        /// <summary>卡尺静态预览（magenta 测量带 @ 锚点0°）+ 基准点标记（红大十字）刷新；无 ROI/无行则不画</summary>
        private void RebuildCaliperOverlay()
        {
            // 批3：图上拖绘模式开启时宿主直接画卡尺形状（可点选拖动），静态紫带让位避免双画；
            // 行参数编辑/拖绘回写均经 OnCaliperRowPropertyChanged 进来，模式内直接跳过。
            if (_caliperDrawMode) return;
            DisposeCaliperOverlays();
            if (!HasImage || _currentContext == null) return;
            if (CaliperRows.Count > 0 && TryGetAnchor(out double ar, out double ac))
            {
                try
                {
                    var band = TemplateCreationBridge.CreateCaliperBandsAtPose(
                        CaliperRows.Select(r => r.Cal).ToList(), ar, ac, 0.0);
                    if (band != null)
                    {
                        _caliperOverlay = new ImageOverlay { Kind = OverlayKind.Xld, Color = "magenta", NativeHandle = band };
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateCaliper", $"卡尺静态预览失败: {ex.Message}");
                }
            }
            RebuildDatumOverlayCore();
        }

        /// <summary>基准点标记（红大十字；显式非 0 时画在基准点绝对坐标）</summary>
        private void RebuildDatumOverlay()
        {
            DisposeDatumOnly();
            if (!HasImage || _currentContext == null) return;
            RebuildDatumOverlayCore();
        }

        private void DisposeDatumOnly()
        {
            (_datumOverlay?.NativeHandle as IDisposable)?.Dispose();
            _datumOverlay = null;
        }

        private void RebuildDatumOverlayCore()
        {
            (_datumOverlay?.NativeHandle as IDisposable)?.Dispose();
            _datumOverlay = null;
            // 红大十字=固定点方式的显式锚点；圆心/交点精测=运行时由卡尺覆盖，编辑器内不预画（位置=匹配+采样后才定）
            bool explicitDatum = _datumKindIndex == 0 && (Math.Abs(_datumRow) > 0.5 || Math.Abs(_datumCol) > 0.5);
            if (explicitDatum && HasImage && _currentContext != null)
            {
                try
                {
                    _datumOverlay = new ImageOverlay
                    {
                        Kind = OverlayKind.Xld,
                        Color = "red",
                        NativeHandle = TemplateCreationBridge.CreateDatumCross(_datumRow, _datumCol)
                    };
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateCaliper", $"基准点标记生成失败: {ex.Message}");
                }
            }
            RebuildDisplayOverlays();
        }

        #endregion

        #region 卡尺图上拖绘/编辑（批3 2026-09-09：宿主 ROI 形状 ↔ 卡尺行双向）

        private bool _caliperDrawMode;

        /// <summary>
        /// 卡尺图上拖绘/编辑模式（与掩膜/特征/搜索框框选互斥，同"绘制去向"语义）。
        /// 开启 = 视图把现有卡尺行以宿主 ROI（〰线/⭘圆）回注 → 原生可见、🖱 可点选拖动/拉手柄；
        ///        宿主下一笔 〰线=线卡尺、⭘圆=圆卡尺、▭▣旋转矩形=四边卡尺，收笔即时入行并留在宿主可再编辑；
        ///        行参数/几何修改实时回写 Cal（紫带在拖绘期让位，宿主画形状）。
        /// 关闭 = 宿主卡尺形状退场，恢复静态紫带预览（行仍在，语义不变）。
        /// </summary>
        public bool CaliperDrawMode
        {
            get => _caliperDrawMode;
            set
            {
                if (!Set(ref _caliperDrawMode, value)) return;
                if (value)
                {
                    // 拖绘与其它"绘制去向"互斥：进来先让位
                    if (_maskEditVisible) MaskEditVisible = false;
                    if (_featureEditVisible) FeatureEditVisible = false;
                    if (_searchRoiDrawMode) SearchRoiDrawMode = false;
                    DisposeCaliperOverlays();      // 紫带让位（宿主画形状）；基准点红十字保留
                    RebuildDatumOverlayCore();     // 内部会 RebuildDisplayOverlays
                    StatusText = "📏 卡尺拖绘/编辑已开启：用视图工具条画 〰 线(左键拖出→右键完成)=线卡尺、⭘ 圆=圆卡尺、▭▣ 旋转矩形=四边卡尺；青色形状可点选拖动/拉手柄微调；行内 ✖ 或 Delete 删除；再点开关退出";
                }
                else
                {
                    // 退出：静态紫带恢复（含拖绘/回写期间增改的行）
                    RebuildCaliperOverlay();
                    StatusText = "卡尺拖绘已退出：形状已收为卡尺行（紫色测量带预览）；可继续 ✨ 生成建议 / 逐条微调，或用新开关再进入微调几何";
                }
            }
        }

        /// <summary>学习框 ROI 窗口是否就绪（拖绘卡尺偏移相对锚点=显式基准点或 ROI 中心，须有基底）</summary>
        public bool HasRoiWindow => RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1;

        private string NextCaliperName()
        {
            int max = 0;
            foreach (var r in CaliperRows)
            {
                if (r.Cal != null && r.Cal.Name != null && r.Cal.Name.Length > 1
                    && int.TryParse(r.Cal.Name.Substring(1), out int n) && n > max)
                {
                    max = n;
                }
            }
            return "C" + (max + 1);
        }

        /// <summary>新增一条卡尺行并订阅属性变化（拖绘/建议共用追加路径；ReplaceCaliperRows 仍负责整组重建）</summary>
        private CaliperRowItem AppendCaliperRow(TemplateCaliper cal)
        {
            var row = new CaliperRowItem(cal);
            row.PropertyChanged += OnCaliperRowPropertyChanged;
            CaliperRows.Add(row);
            return row;
        }

        /// <summary>
        /// 把宿主一笔提交的几何收为卡尺行（拖绘模式，由 View 分发）。
        /// Line → 线卡尺：中心=线段中点、Phi=线段方向、L1=半长；Circle → 圆卡尺：圆心+L1=半径（整环 2π）。
        /// 坐标一律转相对当前锚点（显式基准点或 ROI 中心）。返回新行；null=拒绝（已给状态文案）。
        /// </summary>
        public CaliperRowItem AddCaliperFromDrawn(RoiShape s)
        {
            if (s == null || !HasRoiWindow)
            {
                StatusText = "请先框选学习框（ROI），拖绘卡尺需要锚点基准";
                return null;
            }
            if (!TryGetAnchor(out double ar, out double ac))
            {
                StatusText = "无法确定锚点：请先框选学习框再拖绘卡尺";
                return null;
            }
            TemplateCaliper cal;
            if (s.Kind == RoiShapeKind.Line)
            {
                double dr = s.Row2 - s.Row, dc = s.Col2 - s.Col;
                double len = Math.Sqrt(dr * dr + dc * dc);
                if (len < 2.0)
                {
                    StatusText = "线段太短（<2px），已忽略：请拖出足够长度的卡尺线段";
                    return null;
                }
                cal = new TemplateCaliper
                {
                    Name = NextCaliperName(),
                    Kind = TemplateCaliperKind.Line,
                    DRow = (s.Row + s.Row2) / 2.0 - ar,
                    DCol = (s.Col + s.Col2) / 2.0 - ac,
                    Phi = Math.Atan2(dr, dc),          // 边缘方向（HALCON 像素系：0=水平向右）
                    Length1 = Math.Max(0.5, len / 2.0), // 沿边散布半跨距=线段半长
                    Length2 = 10.0,
                    ProbeWidth = 2.0,
                    NumPoints = 7,
                    Threshold = 30.0,
                    Sigma = 1.0,
                    FitEnabled = true,
                    MinEdgePoints = 3
                };
            }
            else if (s.Kind == RoiShapeKind.Circle)
            {
                if (s.Radius1 < 2.0)
                {
                    StatusText = "圆太小（半径<2px），已忽略：请拖出覆盖目标圆环的卡尺";
                    return null;
                }
                cal = new TemplateCaliper
                {
                    Name = NextCaliperName(),
                    Kind = TemplateCaliperKind.Circle,
                    DRow = s.Row - ar,
                    DCol = s.Col - ac,
                    Length1 = Math.Max(1.0, s.Radius1), // 目标半径
                    Length2 = 10.0,                      // 环形半宽（采样带厚度）
                    NumPoints = 1,
                    Threshold = 30.0,
                    Sigma = 1.0,
                    ArcStart = 0.0,
                    ArcExtent = 2.0 * Math.PI,           // 整圆环（行内可改起/跨角变圆弧）
                    FitEnabled = true,
                    MinEdgePoints = 8
                };
            }
            else
            {
                StatusText = $"卡尺拖绘只收 〰线 / ⭘圆 / ▭▣旋转矩形；{s.Kind} 形状请退出拖绘后按学习框/掩膜语义处理";
                return null;
            }
            var row = AppendCaliperRow(cal);
            UpdateCaliperStatusText(WorkpieceShape);
            StatusText = $"已加卡尺 [{cal.Name}]（{(cal.Kind == TemplateCaliperKind.Line ? "线" : "圆")}，相对锚点偏移 Δ({cal.DRow:0.0},{cal.DCol:0.0})）：青色形状留在图上可拖动/拉手柄微调";
            LogBus.Info("TemplateCaliper", $"图上拖绘新增卡尺 {cal.Name} kind={cal.Kind} L1={cal.Length1:F1}");
            return row;
        }

        /// <summary>
        /// 把宿主一笔 ▭▣ 旋转矩形收为"矩形四边 4×Line"卡尺（批3：Rectangle2 二维带 → 四边精测）。
        /// 以矩形中心/角度/半长半宽展开四条边（顶/底沿主轴方向、左/右垂直主轴），逐边成线卡尺。
        /// 返回新增行表；空表=拒绝（已给状态文案）。
        /// </summary>
        public List<CaliperRowItem> AddRect2CalipersFromDrawn(RoiShape s)
        {
            if (s == null || s.Kind != RoiShapeKind.Rectangle2 || !HasRoiWindow)
            {
                StatusText = "请先框选学习框（ROI），再拖 ▭▣ 旋转矩形生成四边卡尺";
                return null;
            }
            if (!TryGetAnchor(out double ar, out double ac)) return null;
            if (s.Length1 < 2.0 || s.Length2 < 2.0)
            {
                StatusText = "矩形太小（半长/半宽需 ≥2px），已忽略";
                return null;
            }
            double phi = s.Phi;
            // 主轴 u=(cosφ 列, sinφ 行)，副轴 v=(-sinφ 列, cosφ 行)（与宿主 ComputeOutlinePoints 同约定）
            double ux = Math.Cos(phi) * s.Length1, uy = Math.Sin(phi) * s.Length1;
            double vx = -Math.Sin(phi) * s.Length2, vy = Math.Cos(phi) * s.Length2;
            double pad = 0.92; // 边卡尺 L1 收缩 8%，避开角点圆弧过渡区
            var rows = new List<CaliperRowItem>();
            TemplateCaliper cal;
            // 顶边（+v 侧，沿主轴方向）
            cal = new TemplateCaliper
            {
                Name = NextCaliperName(),
                Kind = TemplateCaliperKind.Line,
                DRow = (s.Row + vy) - ar,
                DCol = (s.Col + vx) - ac,
                Phi = phi,
                Length1 = Math.Max(1.0, s.Length1 * pad),
                Length2 = 10.0, ProbeWidth = 2.0, NumPoints = 7,
                Threshold = 30.0, Sigma = 1.0, FitEnabled = true, MinEdgePoints = 3
            };
            rows.Add(AppendCaliperRow(cal));
            // 底边（-v 侧）
            cal = new TemplateCaliper
            {
                Name = NextCaliperName(),
                Kind = TemplateCaliperKind.Line,
                DRow = (s.Row - vy) - ar,
                DCol = (s.Col - vx) - ac,
                Phi = phi,
                Length1 = Math.Max(1.0, s.Length1 * pad),
                Length2 = 10.0, ProbeWidth = 2.0, NumPoints = 7,
                Threshold = 30.0, Sigma = 1.0, FitEnabled = true, MinEdgePoints = 3
            };
            rows.Add(AppendCaliperRow(cal));
            // 右边（+u 侧，垂直主轴方向）
            cal = new TemplateCaliper
            {
                Name = NextCaliperName(),
                Kind = TemplateCaliperKind.Line,
                DRow = (s.Row + uy) - ar,
                DCol = (s.Col + ux) - ac,
                Phi = phi + Math.PI / 2.0,
                Length1 = Math.Max(1.0, s.Length2 * pad),
                Length2 = 10.0, ProbeWidth = 2.0, NumPoints = 7,
                Threshold = 30.0, Sigma = 1.0, FitEnabled = true, MinEdgePoints = 3
            };
            rows.Add(AppendCaliperRow(cal));
            // 左边（-u 侧）
            cal = new TemplateCaliper
            {
                Name = NextCaliperName(),
                Kind = TemplateCaliperKind.Line,
                DRow = (s.Row - uy) - ar,
                DCol = (s.Col - ux) - ac,
                Phi = phi + Math.PI / 2.0,
                Length1 = Math.Max(1.0, s.Length2 * pad),
                Length2 = 10.0, ProbeWidth = 2.0, NumPoints = 7,
                Threshold = 30.0, Sigma = 1.0, FitEnabled = true, MinEdgePoints = 3
            };
            rows.Add(AppendCaliperRow(cal));
            UpdateCaliperStatusText(WorkpieceShape);
            StatusText = $"已把旋转矩形展开为 4 条边卡尺（{rows[0].Cal.Name}~{rows[rows.Count - 1].Cal.Name}，顶/底沿长轴、左/右沿短轴）：矩形笔迹已收走，四边紫带即采样带，可逐条微调";
            return rows;
        }

        /// <summary>拖绘模式下宿主 ROI 被拖动/变形 → 按新几何回写卡尺行（转相对锚点偏移）</summary>
        public void UpdateCaliperFromHostRoi(CaliperRowItem row, RoiShape s)
        {
            if (row == null || s == null || row.Cal == null) return;
            if (!TryGetAnchor(out double ar, out double ac)) return;
            if (row.Cal.Kind == TemplateCaliperKind.Line && s.Kind == RoiShapeKind.Line)
            {
                double dr = s.Row2 - s.Row, dc = s.Col2 - s.Col;
                double len = Math.Sqrt(dr * dr + dc * dc);
                if (len < 0.5) return;
                row.Cal.DRow = (s.Row + s.Row2) / 2.0 - ar;
                row.Cal.DCol = (s.Col + s.Col2) / 2.0 - ac;
                row.Cal.Phi = Math.Atan2(dr, dc);
                row.Cal.Length1 = Math.Max(0.5, len / 2.0);
                row.NotifyGeometryChanged();
            }
            else if (row.Cal.Kind != TemplateCaliperKind.Line && s.Kind == RoiShapeKind.Circle)
            {
                if (s.Radius1 < 0.5) return;
                row.Cal.DRow = s.Row - ar;
                row.Cal.DCol = s.Col - ac;
                row.Cal.Length1 = s.Radius1;
                row.NotifyGeometryChanged();
            }
        }

        /// <summary>把一条卡尺转成宿主可拖动 ROI（拖绘模式回注；Line→线段，Circle/Arc→圆）。无有效锚点返回 null。</summary>
        public RoiShape BuildCaliperDrawRoi(TemplateCaliper cal)
        {
            if (cal == null) return null;
            if (!TryGetAnchor(out double ar, out double ac)) return null;
            if (cal.Kind == TemplateCaliperKind.Line)
            {
                double c0 = ac + cal.DCol, r0 = ar + cal.DRow;
                double ux = Math.Cos(cal.Phi) * cal.Length1;
                double uy = Math.Sin(cal.Phi) * cal.Length1;
                return new RoiShape(RoiShapeKind.Line)
                {
                    Row = r0 - uy, Col = c0 - ux,
                    Row2 = r0 + uy, Col2 = c0 + ux
                };
            }
            return new RoiShape(RoiShapeKind.Circle)
            {
                Row = ar + cal.DRow,
                Col = ac + cal.DCol,
                Radius1 = Math.Max(1.0, cal.Length1)
            };
        }

        #endregion

        /// <summary>卡尺行编辑项：包一层 TemplateCaliper，界面字段编辑实时写回 Cal（创建快照直接取用）。
        /// 几何（DRow/DCol/Phi）由"按形状生成"给出并只读展示（tooltip）；这里只开放常用微调参数。</summary>
        public sealed class CaliperRowItem : System.ComponentModel.INotifyPropertyChanged
        {
            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

            public TemplateCaliper Cal { get; }

            public CaliperRowItem(TemplateCaliper cal)
            {
                Cal = cal;
            }

            public string Title
            {
                get
                {
                    string kindText;
                    if (Cal.Kind == TemplateCaliperKind.Line) kindText = "直线边";
                    else if (ArcExtentDeg < 359.5) kindText = $"圆弧 {ArcStartDeg:0}°→{ArcStartDeg + ArcExtentDeg:0}°";
                    else kindText = "整圆环";
                    return $"{Cal.Name} · {kindText}";
                }
            }

            /// <summary>几何只读提示（tooltip）：测量带画在哪、各参数含义</summary>
            public string GeometryHint
            {
                get
                {
                    if (Cal.Kind == TemplateCaliperKind.Circle)
                    {
                        string arcPart = ArcExtentDeg >= 359.5
                            ? "整圆环"
                            : $"弧段 {ArcStartDeg:0}°→{ArcStartDeg + ArcExtentDeg:0}°";
                        return $"圆心偏移 (Δ行 {Cal.DRow:F0}, Δ列 {Cal.DCol:F0})｜L1=目标半径｜L2=环形半宽（采样带在半径±L2 内）｜{arcPart}";
                    }
                    if (Cal.Kind == TemplateCaliperKind.Arc)
                        return $"圆心偏移 (Δ行 {Cal.DRow:F0}, Δ列 {Cal.DCol:F0})｜L1=弧半径｜L2=环形半宽｜弧段 {ArcStartDeg:0}°→{ArcStartDeg + ArcExtentDeg:0}°";
                    return $"中心偏移 (Δ行 {Cal.DRow:F0}, Δ列 {Cal.DCol:F0})｜边缘方向 {Cal.Phi / Math.PI * 180.0:F0}°（测量带横跨边缘）｜L1=探针沿边散布半跨距｜L2=跨边探测半长";
                }
            }

            /// <summary>几何被宿主拖绘改动后的界面刷新（Title/GeometryHint 变化通知）</summary>
            public void NotifyGeometryChanged()
            {
                Raise(nameof(Title));
                Raise(nameof(GeometryHint));
            }

            /// <summary>圆/弧卡尺才有弧段参数（行内角段编辑可见）</summary>
            public bool IsArcEditable => Cal.Kind != TemplateCaliperKind.Line;

            /// <summary>圆/弧卡尺：弧段起始角（°；0=右，HALCON 数学正方向）</summary>
            public double ArcStartDeg
            {
                get => Cal.ArcStart / Math.PI * 180.0;
                set
                {
                    double v = ((value % 360.0) + 360.0) % 360.0;
                    if (Math.Abs(Cal.ArcStart - v * Math.PI / 180.0) > 1e-6)
                    {
                        Cal.ArcStart = v * Math.PI / 180.0;
                        Raise(nameof(ArcStartDeg));
                        Raise(nameof(Title));
                        Raise(nameof(GeometryHint));
                    }
                }
            }

            /// <summary>圆/弧卡尺：弧段跨度（°；360=整圆环，<360=圆弧）</summary>
            public double ArcExtentDeg
            {
                get => Cal.ArcExtent / Math.PI * 180.0;
                set
                {
                    double v = Math.Min(360.0, Math.Max(0.5, value));
                    if (Math.Abs(Cal.ArcExtent - v * Math.PI / 180.0) > 1e-6)
                    {
                        Cal.ArcExtent = v * Math.PI / 180.0;
                        Raise(nameof(ArcExtentDeg));
                        Raise(nameof(Title));
                        Raise(nameof(GeometryHint));
                    }
                }
            }

            public bool Enabled
            {
                get => Cal.Enabled;
                set
                {
                    if (Cal.Enabled != value)
                    {
                        Cal.Enabled = value;
                        Raise(nameof(Enabled));
                    }
                }
            }

            /// <summary>Line：探针沿边散布半跨距；Circle：目标半径</summary>
            public double Length1
            {
                get => Cal.Length1;
                set
                {
                    double v = Math.Max(0.5, value);
                    if (Math.Abs(Cal.Length1 - v) > 1e-6)
                    {
                        Cal.Length1 = v;
                        Raise(nameof(Length1));
                    }
                }
            }

            /// <summary>Line：跨边探测半长；Circle：环形半宽</summary>
            public double Length2
            {
                get => Cal.Length2;
                set
                {
                    double v = Math.Max(0.5, value);
                    if (Math.Abs(Cal.Length2 - v) > 1e-6)
                    {
                        Cal.Length2 = v;
                        Raise(nameof(Length2));
                    }
                }
            }

            /// <summary>Line：单探针沿边平均半宽</summary>
            public double ProbeWidth
            {
                get => Cal.ProbeWidth;
                set
                {
                    double v = Math.Max(0.5, value);
                    if (Math.Abs(Cal.ProbeWidth - v) > 1e-6)
                    {
                        Cal.ProbeWidth = v;
                        Raise(nameof(ProbeWidth));
                    }
                }
            }

            public int NumPoints
            {
                get => Cal.NumPoints;
                set
                {
                    int v = Math.Max(1, value);
                    if (Cal.NumPoints != v)
                    {
                        Cal.NumPoints = v;
                        Raise(nameof(NumPoints));
                    }
                }
            }

            public double Threshold
            {
                get => Cal.Threshold;
                set
                {
                    double v = Math.Max(1.0, value);
                    if (Math.Abs(Cal.Threshold - v) > 1e-6)
                    {
                        Cal.Threshold = v;
                        Raise(nameof(Threshold));
                    }
                }
            }
        }

        #region 学习掩膜（区域涂抹 ∪/∖，P1 模板资产包）

        /// <summary>掩膜编辑开关：开启后宿主每次提交的 ROI 形状都作为"掩膜笔画"收入 _maskStrokes，
        /// 不再当作基底 ROI（与模板页 RoiCommitted 事件分发联动，见 TemplateManagerView.xaml.cs）</summary>
        private bool _maskEditVisible;
        public bool MaskEditVisible
        {
            get => _maskEditVisible;
            set
            {
                if (Set(ref _maskEditVisible, value))
                {
                    if (value && _featureEditVisible)
                    {
                        // 与特征标注互斥：开掩膜编辑时自动关特征标注
                        FeatureEditVisible = false;
                    }
                    if (value && _searchRoiDrawMode)
                    {
                        // 与搜索框框选互斥：掩膜语义优先
                        SearchRoiDrawMode = false;
                    }
                    if (value && _caliperDrawMode)
                    {
                        // 批3：卡尺拖绘让位（掩膜优先）
                        CaliperDrawMode = false;
                    }
                    OnPropertyChanged(nameof(MaskModeHint));
                    StatusText = value
                        ? "✎ 掩膜编辑已开启：点工具条 🖌 涂抹、或 📐▾ 画形状——∪=版图(涂哪学哪) / ∖=从版图抠洞，学习域实时预览"
                        : "掩膜编辑已关闭：新画的闭合形状将作为模板学习框（▭ 矩形直用，⭘ 圆/椭圆/⬠ 多边形按所画形状学习）";
                    if (value)
                    {
                        // 开掩膜编辑 → 学习域预览（域外灰化，"学什么"所见即所得）+ 整域版图轮廓
                        RequestLearnDomainPreview();
                        RebuildMaskOverlays();
                    }
                    else
                    {
                        // 关掩膜编辑 → 恢复原图显示（预览帧退役）；清未收笔轨迹、移除版图轮廓
                        _sketchDown = false;
                        _sketchPoints = null;
                        RestoreOriginalDisplayFrame();
                        RebuildMaskOverlays();
                    }
                }
            }
        }

        /// <summary>笔画作用模式：true=保留(∪ 并集) / false=排除(∖ 差集)。默认排除（最常用：剔除 ROI 内干扰）</summary>
        private bool _maskAddMode;
        public bool MaskAddMode
        {
            get => _maskAddMode;
            set
            {
                if (Set(ref _maskAddMode, value))
                {
                    OnPropertyChanged(nameof(MaskRemoveMode));
                    OnPropertyChanged(nameof(MaskModeHint));
                }
            }
        }

        public bool MaskRemoveMode
        {
            get => !_maskAddMode;
            set => MaskAddMode = !value;
        }

        /// <summary>界面操作提示（随编辑开关/模式变化刷新）</summary>
        public string MaskModeHint
        {
            get
            {
                if (!MaskEditVisible) return "（未开启掩膜编辑时，绘制形状只作用于基底 ROI）";
                return MaskAddMode
                    ? "模式=保留 ∪：所画区域将并入学习域（涂哪学哪）"
                    : "模式=排除 ∖：所画区域将从学习域中抠除（最常见：剔除字符/划痕/背景纹理）";
            }
        }

        private string _maskStatusText = "无掩膜：将学习整块 ROI";
        /// <summary>掩膜笔画统计（界面状态行）：笔数 + 保留/排除分布</summary>
        public string MaskStatusText
        {
            get => _maskStatusText;
            private set => Set(ref _maskStatusText, value);
        }

        /// <summary>归属工位（StationCode，可空；写入 TemplateInfo.OwnerStation，资产归位过渡字段）</summary>
        private string _templateOwnerStation;
        public string TemplateOwnerStation
        {
            get => _templateOwnerStation;
            set => Set(ref _templateOwnerStation, value);
        }

        public RelayCommand UndoMaskCommand { get; private set; }
        public RelayCommand ClearMaskCommand { get; private set; }
        public RelayCommand EditTemplateCommand { get; private set; }

        #endregion

        #region 模板特征（P2：特征点/面，随匹配位姿仿射可视化）

        /// <summary>特征标注开关：开启后宿主提交的闭合形状=一笔特征（点或面，由 FeatureAddIsPoint 决定），
        /// 与掩膜编辑互斥（ROI 基底只能有一种编辑语义）。</summary>
        private bool _featureEditVisible;
        public bool FeatureEditVisible
        {
            get => _featureEditVisible;
            set
            {
                if (Set(ref _featureEditVisible, value))
                {
                    if (value)
                    {
                        // 与掩膜编辑互斥：开特征标注时自动关掩膜编辑
                        if (_maskEditVisible) MaskEditVisible = false;
                        if (_searchRoiDrawMode) SearchRoiDrawMode = false; // 与搜索框框选互斥
                        if (_caliperDrawMode) CaliperDrawMode = false; // 批3：卡尺拖绘让位
                        StatusText = _features.Count == 0
                            ? "📍 特征标注已开启：先用 📐▾ 画一个小形状确定【特征点】位置（取其中心），或用 ⭕点/▨面 模式切换画特征面"
                            : $"📍 特征标注已开启（已有 {_features.Count} 个）：继续画形状追加特征";
                    }
                    else
                    {
                        StatusText = "特征标注已关闭";
                    }
                }
            }
        }

        /// <summary>特征类型：true=特征点（画任意小形状取中心）/ false=特征面（保留形状做闭合区域）</summary>
        private bool _featureAddIsPoint = true;
        public bool FeatureAddIsPoint
        {
            get => _featureAddIsPoint;
            set
            {
                if (Set(ref _featureAddIsPoint, value))
                {
                    OnPropertyChanged(nameof(FeatureAddIsFace));
                    StatusText = value
                        ? "特征类型=⭕ 点：画形状将取其几何中心作为特征点（越小越贴近目标角点/圆点）"
                        : "特征类型=▨ 面：画形状将作为特征面（识别后画其轮廓，用于量测/看区域）";
                }
            }
        }

        public bool FeatureAddIsFace
        {
            get => !_featureAddIsPoint;
            set => FeatureAddIsPoint = !value;
        }

        private string _featureStatusText = "未标注特征：识别时只画模板轮廓";
        /// <summary>特征统计文本（笔数 + 点/面分布 + 可视化语义提示）</summary>
        public string FeatureStatusText
        {
            get => _featureStatusText;
            private set => Set(ref _featureStatusText, value);
        }

        public RelayCommand UndoFeatureCommand { get; private set; }
        public RelayCommand ClearFeatureCommand { get; private set; }

        #endregion

        #region 状态

        private string _statusText = "就绪：请选择源图像并框选 ROI";
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        /// <summary>视图层（code-behind）状态反馈入口：StatusText setter 为 private，宿主事件回调
        /// （如 ROI 形状折算提示）经此方法写入状态栏。</summary>
        public void SetStatusText(string text)
        {
            StatusText = text ?? "";
        }

        #endregion

        #region 模板体检报告（创建后质量评估 + 实拍验证结果，常驻界面供随时查看）

        private string _qualityReportText;
        /// <summary>体检报告正文（多行）：分数/诊断原因/下一步操作引导</summary>
        public string QualityReportText
        {
            get => _qualityReportText;
            private set => Set(ref _qualityReportText, value);
        }

        private bool _qualityReportVisible;
        /// <summary>报告卡可见性（创建或验证后置 true，新建模板时清掉）</summary>
        public bool QualityReportVisible
        {
            get => _qualityReportVisible;
            private set => Set(ref _qualityReportVisible, value);
        }

        private System.Windows.Media.Brush _qualityReportBrush = System.Windows.Media.Brushes.Orange;
        /// <summary>报告主色（绿=合格 / 橙=可用需优化 / 红=不合格），直接绑定前景色</summary>
        public System.Windows.Media.Brush QualityReportBrush
        {
            get => _qualityReportBrush;
            private set => Set(ref _qualityReportBrush, value);
        }

        /// <summary>上一次实拍验证的匹配位置（锁死检测：两次验证目标挪过位而匹配位置不变 = 模板学了背景）</summary>
        private double? _lastVerifyRow;
        private double? _lastVerifyCol;

        #endregion

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    // IsBusy 同时影响创建/学习/采集/验证等命令，刷新后按钮状态才恢复（如采集完成、创建完成）
                    CreateTemplateCommand?.RaiseCanExecuteChanged();
                    LearnTemplateCommand?.RaiseCanExecuteChanged();
                    AcquireImageCommand?.RaiseCanExecuteChanged();
                    VerifyTemplateCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #region 命令

        public RelayCommand LoadImageCommand { get; }
        public RelayCommand AcquireImageCommand { get; }
        public RelayCommand CreateTemplateCommand { get; }
        /// <summary>🧠 模板学习（不落盘预览）：内存学一次，轮廓+自测上屏；可反复点击（编辑掩膜/几何后刷新）</summary>
        public RelayCommand LearnTemplateCommand { get; }
        /// <summary>模板实拍验证：对当前图像低门槛匹配，配合"两步法"判定模板是否合格</summary>
        public RelayCommand VerifyTemplateCommand { get; }
        public RelayCommand DeleteTemplateCommand { get; }
        public RelayCommand NewTemplateCommand { get; }
        public RelayCommand RefreshCommand { get; }

        #endregion

        public TemplateManagerViewModel()
        {
            _templateManager = new TemplateManager();
            _renderService = new HalconImageRenderService();
            DisplayVm = new ImageDisplayVm(_renderService);

            LoadImageCommand = new RelayCommand(_ => LoadLocalImage());
            AcquireImageCommand = new RelayCommand(_ => AcquireCameraSnapshot(), _ => SelectedCamera != null && !IsBusy);
            CreateTemplateCommand = new RelayCommand(async _ => await CreateTemplateAsync(), _ => HasImage && !IsBusy);
            // 🧠 模板学习（2026-09-09 学习/落盘分离）：内存学一次+轮廓/自测上屏，不写盘；可反复点（编辑后刷新）
            LearnTemplateCommand = new RelayCommand(async _ => await LearnTemplateAsync(), _ => HasImage && !IsBusy);
            // 相机模式下允许无图直接点（会自动重采一帧）；文件模式要求先载入图片
            VerifyTemplateCommand = new RelayCommand(async _ => await VerifyTemplateAsync(),
                _ => SelectedTemplate != null && !IsBusy && (HasImage || SelectedCamera != null));
            DeleteTemplateCommand = new RelayCommand(_ => DeleteSelectedTemplate(), _ => SelectedTemplate != null);
            NewTemplateCommand = new RelayCommand(_ => ResetWizard());
            RefreshCommand = new RelayCommand(_ => RefreshTemplates());
            BackToGlobalCommand = new RelayCommand(_ => OnBackToGlobal());
            // 掩膜编辑：撤销上笔 / 清空全部 / 载入选中模板到编辑器（模板编辑·覆盖重学入口）
            UndoMaskCommand = new RelayCommand(_ => UndoMaskStroke(), _ => _maskStrokes.Count > 0);
            ClearMaskCommand = new RelayCommand(_ => ClearMaskStrokes(true), _ => _maskStrokes.Count > 0);
            EditTemplateCommand = new RelayCommand(_ => LoadTemplateForEdit(), _ => SelectedTemplate != null);
            // 特征标注：撤销 / 清空（P2）
            UndoFeatureCommand = new RelayCommand(_ => UndoFeatureStroke(), _ => _features.Count > 0);
            ClearFeatureCommand = new RelayCommand(_ => ClearFeatureStrokes(true), _ => _features.Count > 0);
            // P1 分步引导导航
            GoNextStepCommand = new RelayCommand(_ => GoNextStep(), _ => WizardStep < 4);
            GoPrevStepCommand = new RelayCommand(_ => GoPrevStep(), _ => WizardStep > 0);

            LoadCameras();
            RefreshTemplates();
        }

        /// <summary>视图加载完成时调用（刷新列表）</summary>
        public void OnViewLoaded()
        {
            RefreshTemplates();
        }

        #region 页面导航与工位定位（原 TemplateWorkbenchViewModel 壳职责，合并下沉）

        private string _scopeStationCode = string.Empty;

        /// <summary>当前定位工位代码（空=全库）；同 Workbench 壳语义：决定页头胶囊/回全库显隐</summary>
        public string ScopeStationCode
        {
            get => _scopeStationCode;
            private set
            {
                if (Set(ref _scopeStationCode, value))
                {
                    OnPropertyChanged(nameof(IsStationScope));
                    OnPropertyChanged(nameof(ScopeChipText));
                    OnPropertyChanged(nameof(ScopeHintText));
                }
            }
        }

        /// <summary>是否处于"某工位"定位态（决定「回全库」按钮显隐）</summary>
        public bool IsStationScope => !string.IsNullOrEmpty(ScopeStationCode);

        /// <summary>页头定位胶囊文案</summary>
        public string ScopeChipText => IsStationScope ? $"工位 {ScopeStationCode}" : "全库";

        /// <summary>页头副标题：当前模式的一句话说明</summary>
        public string ScopeHintText => IsStationScope
            ? $"以下展示/新建的模板自动归属工位 [{ScopeStationCode}]；点右上角「回全库」可浏览全局模板。"
            : "全局视觉模板资产库：编辑中途离开本页再回来不丢现场；从工位装配旅程第 6 步进入会自动定位该工位。";

        /// <summary>回全库浏览（解除工位钉）</summary>
        public RelayCommand BackToGlobalCommand { get; private set; }

        public void OnNavigatedFrom() { }

        /// <summary>
        /// 导航进入：parameter=null → 全库；string=StationCode / StationNavigationContext → 定位该工位。
        /// 同上下文再进入直接返回：不打断编辑现场。
        /// </summary>
        public void OnNavigatedTo(object parameter)
        {
            string code = ResolveStationCode(parameter);
            if (string.Equals(code ?? string.Empty, ScopeStationCode, StringComparison.Ordinal))
            {
                return;
            }
            ScopeStationCode = code ?? string.Empty;
            ApplyOwnerContext(ScopeStationCode);
        }

        private void OnBackToGlobal()
        {
            ScopeStationCode = string.Empty;
            ApplyOwnerContext(null);
        }

        private static string ResolveStationCode(object parameter)
        {
            if (parameter is string s) return s;
            if (parameter is StationNavigationContext ctx) return ctx.StationCode;
            return null;
        }

        #endregion

        #region 模板列表管理

        /// <summary>
        /// 页级"钉住的归属工位"（独立模板工作台页在导航时写入；ResetWizard 据此把新建模板继续归属当前工位，
        /// 避免"从工位 A 进入 → 新建模板"被 ResetWizard 清成全局。空 = 全库浏览）。
        /// </summary>
        private string _pinnedOwnerStation = string.Empty;

        /// <summary>应用页面级工位上下文：切换归属钉 → 刷新列表 → 定位该工位已有模板（无则提示新建即归属）</summary>
        public void ApplyOwnerContext(string stationCode)
        {
            stationCode = (stationCode ?? string.Empty).Trim();
            _pinnedOwnerStation = stationCode;
            TemplateOwnerStation = stationCode;
            RefreshTemplates();

            if (string.IsNullOrEmpty(stationCode))
            {
                StatusText = "全库浏览模式（新模板默认归属=全局）。";
                return;
            }

            var hit = Templates.FirstOrDefault(t =>
                string.Equals(t.OwnerStation ?? string.Empty, stationCode, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                SelectedTemplate = hit;
                StatusText = $"已定位到工位 [{stationCode}] 的模板：【{hit.Name}】。";
            }
            else
            {
                SelectedTemplate = null;
                StatusText = $"工位 [{stationCode}] 尚无模板资产 —— 新建模板时将自动归属该工位。";
            }
        }

        private void RefreshTemplates()
        {
            var res = _templateManager.GetAll();
            Templates.Clear();
            if (res.Success)
            {
                foreach (var t in res.Data)
                {
                    Templates.Add(t);
                }
            }
            else
            {
                StatusText = "加载模板列表失败: " + res.Message;
            }

            if (SelectedTemplate == null)
            {
                // 钉了工位时优先选中该工位模板；否则选列表第一项
                SelectedTemplate = !string.IsNullOrEmpty(_pinnedOwnerStation)
                    ? Templates.FirstOrDefault(t =>
                        string.Equals(t.OwnerStation ?? string.Empty, _pinnedOwnerStation, StringComparison.OrdinalIgnoreCase))
                      ?? Templates.FirstOrDefault()
                    : Templates.FirstOrDefault();
            }
        }

        private void DeleteSelectedTemplate()
        {
            if (SelectedTemplate == null) return;

            var result = MessageBox.Show(
                $"确定要删除模板【{SelectedTemplate.Name}】吗？\n" +
                "删除后，引用该模板的 ShapeMatch / NccMatch 节点将无法匹配（需在节点参数中改选其他模板）！",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var name = SelectedTemplate.Name;
            var res = _templateManager.Delete(name);
            if (res.Success)
            {
                StatusText = $"模板 [{name}] 已删除";
            }
            else
            {
                MessageBox.Show("删除模板失败: " + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshTemplates();
        }

        #endregion

        #region 源图像获取

        private void LoadCameras()
        {
            Cameras.Clear();
            try
            {
                var pool = App.StationHostRuntime?.DevicePool;
                if (pool != null)
                {
                    foreach (var cam in pool.GetAllDevices().OfType<ICamera>())
                    {
                        Cameras.Add(cam);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TemplateManager] 加载相机列表失败: {ex.Message}");
            }
            SelectedCamera = Cameras.FirstOrDefault();
        }

        /// <summary>本地图片：OpenFileDialog → WrapImage(filePath) → 上屏</summary>
        private void LoadLocalImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择模板源图像",
                Filter = "图像文件 (*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff)|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _lastSourceImagePath = dlg.FileName;
                var renderImage = _renderService.WrapImage(dlg.FileName) as HalconRenderImage;
                if (renderImage == null)
                {
                    MessageBox.Show("图像加载失败：无法解析该文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ShowImage(renderImage, "模板源图");
                StatusText = $"已加载图片: {dlg.FileName}（{renderImage.Width}x{renderImage.Height}）";
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载图片失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 【📸 采集一帧】按钮入口：IsBusy 由本方法管理，采集流程见 AcquireSnapshotAsync。
        /// </summary>
        private async void AcquireCameraSnapshot()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                await AcquireSnapshotAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 相机快照（可等待）：连接 → 订阅帧事件 → 确保连续采集模式 → 开启取流 → 收到第一帧即显示并停流。
        /// 5 秒无帧按失败返回（不长期占用取流）。【📸 采集一帧】与【🔍 实拍验证】（相机模式自动重采）共用。
        /// 注意：软触发/硬触发模式下 StartGrabbing 不会自动出帧，必须先 SetTriggerMode(0) 切连续
        /// （与 CameraLiveWindowViewModel.TryStartGrabbing 的约定一致），收帧/超时后恢复原模式。
        /// IsBusy 由调用方管理（本方法不触碰，避免嵌套清忙）。
        /// </summary>
        private async Task<bool> AcquireSnapshotAsync()
        {
            var camera = SelectedCamera;
            if (camera == null)
            {
                StatusText = "设备池中未发现相机设备";
                return false;
            }

            StatusText = "正在采集相机图像...";
            _pendingAcquire = true;
            _weStartedGrab = false;
            _triggerModeToRestore = null;
            _pendingCamera = camera;
            var tcs = new TaskCompletionSource<bool>();
            _snapshotTcs = tcs;

            // 确保已连接
            if (camera.State != DeviceState.Connected)
            {
                var connectRes = camera.Connect();
                if (!connectRes.Success)
                {
                    _pendingAcquire = false;
                    _pendingCamera = null;
                    _snapshotTcs = null;
                    StatusText = "相机连接失败: " + connectRes.Message;
                    LogBus.Warn("TemplateSnapshot", $"相机连接失败: {camera.DeviceKey} - {connectRes.Message}");
                    return false;
                }
                LogBus.Info("TemplateSnapshot", $"相机已连接: {camera.DeviceKey}");
            }

            camera.FrameReceived += OnAcquireFrameReceived;

            // 开流前确保连续采集模式：软/硬触发模式下 SDK 不会自动出帧
            var modeRes = camera.GetParam("TriggerModeSelect");
            if (modeRes.Success && int.TryParse(modeRes.Data?.ToString(), out int triggerMode) && triggerMode != 0)
            {
                var setRes = camera.SetTriggerMode(0);
                if (setRes.Success)
                {
                    _triggerModeToRestore = triggerMode;
                    StatusText = $"相机处于触发模式({triggerMode})，已临时切换连续采集...";
                    LogBus.Info("TemplateSnapshot", $"相机原触发模式={triggerMode}，已临时切换为连续采集（收帧后恢复）");
                }
                else
                {
                    StatusText = "切换连续采集模式失败: " + setRes.Message;
                    LogBus.Warn("TemplateSnapshot", $"切换连续采集模式失败: {setRes.Message} —— 触发模式下可能收不到帧");
                }
            }

            // 优先连续取流（快照一帧后立即停止）
            var res = camera.StartContinuousGrab();
            if (res.Success)
            {
                _weStartedGrab = true;
            }
            else
            {
                // 已被其他页面取流 → 搭车等待下一帧
                StatusText = "相机已在取流，等待帧...";
                LogBus.Info("TemplateSnapshot", $"StartContinuousGrab 未开新流（{res.Message}）—— 已有取流，搭车等待下一帧");
            }

            // 5 秒超时保护（长曝光/首帧延迟兜底）
            _acquireTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _acquireTimeoutTimer.Tick += (s, e) =>
            {
                _acquireTimeoutTimer.Stop();
                _acquireTimeoutTimer = null;
                if (!_pendingAcquire) return;
                _pendingAcquire = false;
                bool switchedTriggerMode = _triggerModeToRestore.HasValue;
                CompleteAcquire();
                StatusText = switchedTriggerMode
                    ? "未收到相机图像：已自动切换连续采集仍无帧，请检查曝光时间与相机连接。"
                    : "未收到相机图像：请检查相机触发模式、曝光与连接。";
                LogBus.Warn("TemplateSnapshot",
                    $"5秒未收到相机帧（超时）。开流={(_weStartedGrab ? "本页新开" : "搭车既有流")} 曾切触发模式={switchedTriggerMode} —— 检查曝光时间/触发信号/相机连接");
                tcs.TrySetResult(false);
            };
            _acquireTimeoutTimer.Start();

            return await tcs.Task;
        }

        private void OnAcquireFrameReceived(object sender, FrameEventArgs e)
        {
            if (!_pendingAcquire || sender != _pendingCamera) return;
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;

            // 只取一帧：回调线程（相机 SDK 线程）仅置标志位，
            // DispatcherTimer 等 UI 对象操作与收尾必须 marshal 到 UI 线程，
            // 否则后台线程访问 DispatcherObject 抛 InvalidOperationException
            //（此前 _acquireTimeoutTimer?.Stop() 直接写在回调线程 → 跨线程异常被 HikCamera 插件捕获）。
            _pendingAcquire = false;

            var frame = e;
            var tcs = _snapshotTcs; // 局部捕获：即使下一次采集已开始也不影响本次收尾
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                bool ok = false;
                try
                {
                    // 停止超时保护并统一收尾（停流/恢复触发模式/解绑事件）
                    _acquireTimeoutTimer?.Stop();
                    _acquireTimeoutTimer = null;
                    CompleteAcquire();

                    var renderImage = _renderService.WrapImage(frame) as HalconRenderImage;
                    if (renderImage != null)
                    {
                        ShowImage(renderImage, "相机快照");
                        _lastSourceImagePath = string.Empty; // 相机帧创建的模板源为实采，SourceImagePath 记空
                        StatusText = $"已采集相机图像（{renderImage.Width}x{renderImage.Height}）";
                        ok = true;
                    }
                    else
                    {
                        StatusText = "相机帧转换失败。";
                        LogBus.Warn("TemplateSnapshot", "相机帧转换失败：WrapImage 未返回 HalconRenderImage");
                    }
                }
                catch (Exception ex)
                {
                    StatusText = "相机帧处理异常: " + ex.Message;
                    LogBus.Warn("TemplateSnapshot", "相机帧处理异常: " + ex.Message);
                }
                finally
                {
                    tcs?.TrySetResult(ok);
                }
            }), DispatcherPriority.Render);
        }

        /// <summary>收帧或超时后的统一收尾：停自己发起的流 + 恢复触发模式 + 解绑事件。
        /// （IsBusy 由各调用方管理，此处不再触碰——避免嵌套清忙把验证流程的忙态提前清掉）</summary>
        private void CompleteAcquire()
        {
            var camera = _pendingCamera;
            _pendingCamera = null;
            if (camera == null) return;

            camera.FrameReceived -= OnAcquireFrameReceived;
            if (_weStartedGrab)
            {
                try { camera.StopContinuousGrab(); } catch { }
                _weStartedGrab = false;
            }
            if (_triggerModeToRestore.HasValue)
            {
                try { camera.SetTriggerMode(_triggerModeToRestore.Value); } catch { }
                _triggerModeToRestore = null;
            }
        }

        /// <summary>
        /// 把渲染图像上屏（P1 重构：显示帧与引擎真源分离，支撑学习域预览可视化）。
        /// · _rawFrame = 传入原 wrapper（引擎真源，创建模板用；恒持有到 ResetWizard）；
        /// · _currentContext.Image = 显示拷贝（可被预览合成帧替换/释放，与真源互不牵连）。
        /// </summary>
        private void ShowImage(HalconRenderImage renderImage, string nodeName)
        {
            // 换源图取景 = 宿主 ROI 集合作废（旧形状按旧图像素锚定，留在新场景会悬浮/误命中）；
            // 静默清（不触发 RoisCleared→掩膜清除语义）；LoadTemplateForEdit 流程随后经 EditorRoiReady 回注新形状
            EditorSceneReset?.Invoke(this, EventArgs.Empty);

            var old = _currentContext;
            // 旧帧的匹配叠加/ROI 框/搜索框/掩膜/特征/卡尺带/基准点 native 句柄已登记在旧 context.Overlays，随 old.Dispose 统一释放；引用先清空
            _verifyOverlays = new List<ImageOverlay>();
            _roiOverlay = null;
            _searchRoiOverlay = null;
            _caliperOverlay = null;
            _datumOverlay = null;
            _maskOverlays.Clear();
            _featureOverlays.Clear();

            // 引擎真源：接管调用方 wrapper（旧真源释放）
            _rawFrame?.Dispose();
            _rawFrame = renderImage;
            _previewActive = false;
            ++_previewSeq;          // 作废任何在途的预览合成
            _previewBusy = false;

            // 显示帧：真源的一份深拷贝（独立生命周期，可随时被预览帧替换）
            HalconRenderImage display = TemplateCreationBridge.CreateDisplayCopy(renderImage);

            var context = new WpfImageRenderContext
            {
                NodeId = "template-editor",
                NodeName = nodeName,
                Image = display
            };
            _currentContext = context;
            DisplayVm.ActiveImageContext = context;
            old?.Dispose();

            HasImage = display != null;

            // ROI 数值仍有效（同一取景）：新帧上重建常驻 ROI 框显示
            if (RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1)
            {
                UpdateRoiOverlay();
            }
            // 掩膜笔画（同一取景几何仍有效）：重建描边显示，所见即所得继续编辑
            if (_maskStrokes.Count > 0)
            {
                RebuildMaskOverlays();
            }
            // 特征（相对 ROI 中心偏移）：ROI 仍在取景内时重建"钉在模板上"的静态显示
            if (_features.Count > 0 && RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1)
            {
                RebuildFeatureOverlays();
            }
            // 掩膜编辑态新帧上屏（编辑载入/相机重采）→ 学习域预览跟随 + 整域版图轮廓
            if (MaskEditVisible)
            {
                RequestLearnDomainPreview();
                RebuildMaskOverlays();
            }
            // 搜索框（资产字段在新帧仍有效同取景）→ 橙色叠加重建（内部自检启用/有效/有图）
            UpdateSearchRoiOverlay();
            // 卡尺带/基准点（资产字段/行集合在新帧仍有效同取景）→ 紫色测量带+红大十字重建（锚点=显式基准点或 ROI 中心@0°）
            RebuildCaliperOverlay();
        }

        #endregion

        #region 创建模板

        /// <summary>
        /// 创建模板（后台线程跑 HALCON 算子，避免 UI 卡顿 —— 铁律：算子绝不能在 UI 线程同步跑）。
        /// </summary>
        /// <summary>
        /// 🧠 【模板学习】——学习与落盘分离（2026-09-09）：
        /// 按当前 ROI/掩膜/卡尺/基准点/参数在【内存】学习一次（同创建链：灰度/平场/学习域/锚点钉定/自测），
        /// 不写盘不登记；把"将落盘模板"的贴合轮廓画到图上（青色）+ 自测分报告上屏。
        /// 编辑掩膜/几何/参数后【可再次点击】重新学习刷新预览；满意后点【🚀 创建模板】才真正写盘。
        /// 刻意不做同名覆盖删除/源图留档——未落盘即无资产副作用。
        /// </summary>
        private async Task LearnTemplateAsync()
        {
            if (!HasImage || _currentContext?.Image == null)
            {
                MessageBox.Show("请先加载本地图片或从相机采集一帧！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1)
            {
                MessageBox.Show("ROI 无效：请先在图上框选目标区域（▭ 矩形 / ⭘ 圆 / 椭圆 / ⬠ 多边形等闭合形状均可；线段不能作为学习框）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (TemplateType != TemplateMatchType.Shape)
            {
                StatusText = "灰度(Ncc)模板无轮廓预览：参数已就绪，直接点【🚀 创建模板】落盘（落盘后自动贴合回显）。";
                return;
            }

            IsBusy = true;
            string name = string.IsNullOrWhiteSpace(TemplateName) ? "(预览)" : TemplateName.Trim();
            StatusText = $"正在学习模板轮廓（未落盘）[{name}] ...";

            // 捕获参数（与 CreateTemplateAsync 同链同源：_rawFrame 真源；快照克隆防后台改写）
            var image = _rawFrame ?? _currentContext.Image;
            double r1 = RoiRow1, c1 = RoiCol1, r2 = RoiRow2, c2 = RoiCol2;
            double aStart = AngleStart, aEnd = AngleEnd;
            var remark = Remark ?? "";
            bool srEnabled = SearchRoiEnabled;
            double srR1 = SearchRoiRow1, srC1 = SearchRoiCol1, srR2 = SearchRoiRow2, srC2 = SearchRoiCol2;
            var maskSnapshot = BuildCurrentLearnMask();
            var owner = (TemplateOwnerStation ?? "").Trim();
            var featuresSnapshot = (_features.Count > 0) ? new List<TemplateFeature>(_features) : null;
            string shapeSnapshot = string.IsNullOrWhiteSpace(WorkpieceShape) ? "Generic" : WorkpieceShape.Trim();
            var caliperSnapshot = CaliperRows.Count > 0
                ? CaliperRows.Where(r => r.Enabled).Select(r => CloneCaliper(r.Cal)).ToList()
                : null;
            TemplateDatum datumSnapshot = BuildDatumSnapshot();

            var result = await Task.Run(() => TemplateCreationBridge.CreateShapeTemplatePreview(
                image, name, r1, c1, r2, c2, aStart, aEnd, remark, _lastSourceImagePath,
                maskSnapshot, owner, featuresSnapshot, _baseRoiShape,
                datum: datumSnapshot, calipers: caliperSnapshot,
                searchRoiEnabled: srEnabled, searchRoiRow1: srR1, searchRoiCol1: srC1,
                searchRoiRow2: srR2, searchRoiCol2: srC2,
                workpieceShape: shapeSnapshot));

            IsBusy = false;
            if (!result.Success)
            {
                StatusText = "模板学习失败: " + result.Message;
                MessageBox.Show("模板学习失败：" + result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var preview = result.Data;
            var learned = preview?.Template;
            double score = learned?.QualityScore ?? -1;
            bool hasMask = maskSnapshot != null && maskSnapshot.Enabled && (maskSnapshot.Shapes?.Count ?? 0) > 0;

            // 轮廓上屏（青色=将学内容，与实拍验证的绿色轮廓区分）；掩膜灰化预览时先恢复原图显示
            if (_previewActive)
            {
                RestoreOriginalDisplayFrame();
            }
            foreach (var o in _verifyOverlays)
            {
                (o?.NativeHandle as IDisposable)?.Dispose();
            }
            var overlays = new List<ImageOverlay>();
            if (preview?.ContourXld != null && preview.ContourXld.IsInitialized())
            {
                overlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = "cyan", NativeHandle = preview.ContourXld });
            }
            _verifyOverlays = overlays;
            RebuildDisplayOverlays();

            string tip = score >= 0.9 ? "特征质量良好" : score >= 0.7 ? "分数偏低：检查 ROI 截断/光照对比度" : "分数过低：建议收紧 ROI 或调光源重建";
            StatusText = $"🧠 已学习（未落盘）：青色轮廓=将学到的模板内容，自测分 {score:F2}（{tip}）" +
                         (hasMask ? " · 带学习掩膜" : "") +
                         "。改掩膜/几何/参数后【再点模板学习】刷新；满意后点【🚀 创建模板】写盘登记";
            LogBus.Info("TemplateLearn",
                $"【模板学习·预览】[{name}] 内存学习完成: 自测分={score:F3} 轮廓={(preview?.ContourXld != null ? "已上屏" : "无(自测未命中)")} " +
                $"学习域={TemplateMaskRegionBuilder.Describe(maskSnapshot)} 归属工位={(owner.Length > 0 ? owner : "全局")}");

            ShowQualityReport(BuildLearnReport(name, score, r1, c1, r2, c2));
        }

        /// <summary>模板学习（预览）体检报告：强调"未落盘"，提示下一步动作</summary>
        private string BuildLearnReport(string name, double score, double r1, double c1, double r2, double c2)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🧠 模板学习预览 [{name}]：");
            sb.AppendLine($"  自测分 {score:F3}" + (score >= 0.9 ? " —— 特征质量良好，可直接落盘" : score >= 0.7
                ? " —— 分数偏低：检查 ROI 是否截断特征/光照对比度，或收紧 ROI 重建" : " —— 分数过低：模板特征不可靠，强烈建议调整后重建"));
            sb.AppendLine($"  青色轮廓 = 将落盘模板学到的内容（当前【未落盘】，模板库无此资产）");
            sb.AppendLine($"  ROI=({r1:F0},{c1:F0})~({r2:F0},{c2:F0})，角度覆盖已含旋转自测诊断（见日志）");
            sb.AppendLine($"  → 满意：点【🚀 创建模板】写盘登记（同参数同链，结果与本次预览一致）");
            sb.AppendLine($"  → 想改：调掩膜/卡尺/基准点/参数后【再点 🧠 模板学习】刷新预览，确认后再落盘");
            return sb.ToString();
        }

        private async Task CreateTemplateAsync()
        {
            if (!HasImage || _currentContext?.Image == null)
            {
                MessageBox.Show("请先加载本地图片或从相机采集一帧！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(TemplateName))
            {
                MessageBox.Show("请填写模板名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1)
            {
                MessageBox.Show("ROI 无效：请先在图上框选目标区域（▭ 矩形 / ⭘ 圆 / 椭圆 / ⬠ 多边形等闭合形状均可——学习框=所画形状本身，线段不能作为学习框。也可用右侧数值手动修正）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusText = $"正在创建模板 [{TemplateName}] ...";

            // 捕获参数，后台执行（P1：一律用引擎真源 _rawFrame —— 掩膜预览把显示帧切成灰度预览时，
            // 创建/重学仍从原图学习；_rawFrame 在操作期间不会被替换）
            var image = _rawFrame ?? _currentContext.Image;
            var name = TemplateName.Trim();
            var type = TemplateType;
            double r1 = RoiRow1, c1 = RoiCol1, r2 = RoiRow2, c2 = RoiCol2;
            double aStart = AngleStart, aEnd = AngleEnd;
            var remark = Remark ?? "";
            // 搜索框资产（模板一部分；运行时 MatchWithDatum/节点匹配按此裁剪搜索区，UI 只负责定义与可视化）
            bool srEnabled = SearchRoiEnabled;
            double srR1 = SearchRoiRow1, srC1 = SearchRoiCol1, srR2 = SearchRoiRow2, srC2 = SearchRoiCol2;
            // 掩膜快照 + 归属工位：UI 在创建/重学时一次性落盘（掩膜编辑开关只控制"收笔"，不影响已收笔画）
            var maskSnapshot = BuildCurrentLearnMask();
            var owner = (TemplateOwnerStation ?? "").Trim();
            // 特征快照（点/面，相对 ROI 中心偏移）：随模板一起落盘，匹配可视化按 pose 仿射
            var featuresSnapshot = (_features.Count > 0) ? new List<TemplateFeature>(_features) : null;
            // 卡尺/基准点/工件形状快照（P1）：行集合是编辑器活对象，克隆后交后台引擎（防引擎改写/ResetWizard 清行）；
            // 显式基准点(非 0,0) → Point 基准；0,0=null → 引擎把锚点自动钉学习域中心/ROI 中心（同一语义默认值）
            string shapeSnapshot = string.IsNullOrWhiteSpace(WorkpieceShape) ? "Generic" : WorkpieceShape.Trim();
            var caliperSnapshot = CaliperRows.Count > 0
                ? CaliperRows.Where(r => r.Enabled).Select(r => CloneCaliper(r.Cal)).ToList()
                : null;
            TemplateDatum datumSnapshot = BuildDatumSnapshot();

            // 同名覆盖重学：模板名是节点引用键，先征求用户同意再覆盖（引用该名的节点无需改动）
            bool allowOverwrite = false;
            if (_templateManager.GetByName(name).Success)
            {
                var ask = MessageBox.Show(
                    $"模板 [{name}] 已存在。\n\n点【是】将删除旧模板并用当前参数/掩膜覆盖重学（引用该模板的 ShapeMatch/NccMatch 节点无需修改）；" +
                    $"点【否】取消本次创建。\n\n当前学习域：{TemplateMaskRegionBuilder.Describe(maskSnapshot)}",
                    "模板覆盖重学", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes)
                {
                    IsBusy = false;
                    StatusText = "已取消（未覆盖已有模板）";
                    return;
                }
                allowOverwrite = true;
                LogBus.Info("TemplateLearn", $"模板 [{name}] 已存在，用户确认覆盖重学");
            }

            var result = await Task.Run(() =>
                type == TemplateMatchType.Shape
                    ? TemplateCreationBridge.CreateShapeTemplate(image, name, r1, c1, r2, c2, aStart, aEnd, remark, _lastSourceImagePath,
                        maskSnapshot, owner, allowOverwrite, featuresSnapshot, _baseRoiShape,
                        datum: datumSnapshot, calipers: caliperSnapshot,
                        searchRoiEnabled: srEnabled, searchRoiRow1: srR1, searchRoiCol1: srC1,
                        searchRoiRow2: srR2, searchRoiCol2: srC2,
                        workpieceShape: shapeSnapshot)
                    : TemplateCreationBridge.CreateNccTemplate(image, name, r1, c1, r2, c2, aStart, aEnd, remark, _lastSourceImagePath,
                        maskSnapshot, owner, allowOverwrite, featuresSnapshot, _baseRoiShape,
                        datum: datumSnapshot, calipers: caliperSnapshot,
                        searchRoiEnabled: srEnabled, searchRoiRow1: srR1, searchRoiCol1: srC1,
                        searchRoiRow2: srR2, searchRoiCol2: srC2,
                        workpieceShape: shapeSnapshot));

            IsBusy = false;

            if (result.Success)
            {
                bool hasMask = result.Data.LearnMask != null && result.Data.LearnMask.Enabled
                               && (result.Data.LearnMask.Shapes?.Count ?? 0) > 0;
                bool hasFeat = result.Data.Features != null && result.Data.Features.Count > 0;
                StatusText = $"模板 [{name}] 创建成功（自测分 {result.Data.QualityScore:F2}{(hasMask ? " · 带学习掩膜" : "")}{(hasFeat ? " · 带特征标注" : "")}）";
                LogBus.Info("TemplateLearn",
                    $"模板[{name}] 创建成功: 自测分={result.Data.QualityScore:F3} " +
                    $"学习域={TemplateMaskRegionBuilder.Describe(maskSnapshot)} " +
                    $"特征={TemplateFeatureBuilder.Describe(featuresSnapshot)} " +
                    $"归属工位={(!string.IsNullOrEmpty(owner) ? owner : "全局")} 覆盖重学={allowOverwrite}");

                // 体检报告直接常驻界面（不再只弹一次 MessageBox 就消失）：
                // 分数只是"模板在源图上认得自己"，真正合格要用【实拍验证】两步法复核。
                ShowQualityReport(BuildCreationReport(name, result.Data.QualityScore, r1, c1, r2, c2,
                    result.Data.RoiRow2 - result.Data.RoiRow1, result.Data.RoiCol2 - result.Data.RoiCol1));

                // 🌟 创建成功【现场保留 + 贴合回显】（2026-09-09，替代旧的"ResetWizard 全清场"）：
                //   · 编辑器现场（源图/ROI/掩膜笔画/卡尺/基准点）不清 —— 图上一键可见"刚学到的模板轮廓+卡尺贴合"；
                //   · 左栏【+ 新建】保留 ResetWizard 全清场语义（新建另一模板从选图重新开始）；
                //   · 自动选中新模板 → 【🔍 实拍验证】按钮直接可用，无需先手动点选；
                //   · 两步法基准清零：贴合回显是"创建图自证"，不算验证基线，从创建后第一次实拍重采起算。
                _lastVerifyRow = null;
                _lastVerifyCol = null;
                // 退出掩膜/特征/搜索框框选编辑态并恢复原图显示（笔画/标注本体保留，可随时重新开启继续改）
                if (MaskEditVisible) { MaskEditVisible = false; }
                if (FeatureEditVisible) { FeatureEditVisible = false; }
                SearchRoiDrawMode = false;
                RefreshTemplates();
                var created = _templateManager.GetByName(name);
                if (created.Success)
                {
                    SelectedTemplate = created.Data;
                }
                StatusText = $"✅ 模板 [{name}] 创建成功（自测分 {result.Data.QualityScore:F2}{(hasMask ? " · 带学习掩膜" : "")}{(hasFeat ? " · 带特征标注" : "")}）：" +
                             "图上已回显轮廓/卡尺贴合（确认学到的内容与摆位）——下一步点【🔍 实拍验证】做两步法判定（相机模式自动重采一帧）；新建另一模板→左栏【+ 新建】";
                await EchoCreatedTemplateAsync(result.Data);
            }
            else
            {
                StatusText = "创建失败: " + result.Message;
                MessageBox.Show("模板创建失败：" + result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        //===================================================================================
        // 模板体检：报告生成 + 实拍验证
        //===================================================================================

        /// <summary>
        /// 生成"创建后体检报告"：按自测分三档给结论与对应处理办法。
        /// 注意：自测分是"模板在自己源图上的匹配分"，>=0.9 才说明特征足够独特；
        /// 它不能证明换个场景（光照微变/目标挪位）后仍能匹配——那要用实拍验证两步法。
        /// </summary>
        private string BuildCreationReport(string name, double score,
            double r1, double c1, double r2, double c2, double roiH, double roiW)
        {
            string verdict;
            if (score < 0)
            {
                verdict = "⚠ 自测未执行/失败：请确认 ROI 正确框选了目标特征";
                QualityReportBrush = System.Windows.Media.Brushes.DarkOrange;
            }
            else if (score >= 0.9)
            {
                verdict = "✅ 合格：特征质量良好";
                QualityReportBrush = System.Windows.Media.Brushes.Green;
            }
            else if (score >= 0.7)
            {
                verdict = "🟡 可用但不稳：光照/角度一变就可能掉分，建议优化后重建";
                QualityReportBrush = System.Windows.Media.Brushes.DarkOrange;
            }
            else
            {
                verdict = "❌ 不合格：特征不可靠，请按下方原因清单重建模板";
                QualityReportBrush = System.Windows.Media.Brushes.Red;
            }

            string causes = "";
            if (score >= 0 && score < 0.9)
            {
                causes = "\n\n常见原因（按概率排序）：\n" +
                    "  1. ROI 框得太大，背景占比高 → 模板学了桌面/工台纹理，收紧 ROI 只框目标本体\n" +
                    "  2. 目标图案对比度不足 → 调光源角度/亮度，让边缘轮廓清晰锐利\n" +
                    "  3. 目标在 ROI 里偏一边/被裁掉一角 → 框心对准目标中心，四边留 5~8px";
            }

            return $"模板 [{name}] 创建成功，自测分 {score:F3}\n" +
                   $"ROI 尺寸 {roiW:F0}×{roiH:F0} px（Row {r1:F0}~{r2:F0}, Col {c1:F0}~{c2:F0}）\n" +
                   $"{verdict}{causes}" +
                   "\n\n下一步（实拍验证两步法，确认模板真的合格）：\n" +
                   "  ① 目标放在 A 位置 → 点【🔍 实拍验证】（相机模式会自动重采一帧，无需先点采集）\n" +
                   "  ② 把目标挪开 ≥20mm（或明显转个角度）→ 再点【🔍 实拍验证】\n" +
                   "  判定：两次分数 ≥0.7 且匹配位置跟着目标走 → 模板合格；位置纹丝不动 → 模板锁死背景，必须重建";
        }

        /// <summary>显示体检报告（正文 + 主色）</summary>
        private void ShowQualityReport(string report)
        {
            QualityReportText = report;
            QualityReportVisible = true;
        }

        /// <summary>关闭体检报告卡（视图"✕"按钮调用）</summary>
        public void HideQualityReport()
        {
            QualityReportVisible = false;
        }

        /// <summary>
        /// 实拍验证：相机模式下点按钮先自动重采一帧（实拍反映目标当前位置，避免拿旧图验证），
        /// 再按 0.4 低门槛匹配选中模板（比生产阈值低，暴露真实分数水平），并把匹配效果
        /// ——模型轮廓（贴合目标）+ 中心十字 + 分数/角度标注——画到视图上（缩放平移不丢失）。
        /// 同时做"锁死检测"：上次验证后目标挪过位而匹配位置几乎没变，说明模板学的是背景（ROI 过大），必须重建。
        /// </summary>
        private async Task VerifyTemplateAsync()
        {
            var template = SelectedTemplate;
            if (template == null)
            {
                MessageBox.Show("请先在左侧列表选择要验证的模板。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 相机模式：验证前自动重采一帧（点按钮即拿当前实时画面，不用先手动采集）
            if (IsCameraMode && SelectedCamera != null)
            {
                IsBusy = true;
                StatusText = "正在采集验证图像...";
                var sw = Stopwatch.StartNew();
                bool got = await AcquireSnapshotAsync();
                sw.Stop();
                IsBusy = false;
                LogBus.Info("TemplateVerify",
                    $"实拍验证 [{template.Name}] 相机重采{(got ? "成功" : "失败")}（耗时 {sw.ElapsedMilliseconds}ms，相机={SelectedCamera.DeviceKey}）");
                if (!got)
                {
                    QualityReportBrush = System.Windows.Media.Brushes.Red;
                    ShowQualityReport($"❌ 模板 [{template.Name}] 验证图像采集失败：未在 5 秒内收到相机帧。\n" +
                        "请检查相机连接、触发模式与曝光后重试。");
                    StatusText = "验证图像采集失败";
                    return;
                }
            }

            if (!HasImage || _currentContext?.Image == null)
            {
                MessageBox.Show("请先载入图片或从相机采集一帧作为验证图（相机模式下点验证会自动采集）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusText = $"正在验证模板 [{template.Name}]（门槛 0.4）...";

            var image = _currentContext.Image;
            var name = template.Name;
            // 锁死检测基准在重采之后取：跨"重采→验证"保留，两次验证之间可比
            var lastRow = _lastVerifyRow;
            var lastCol = _lastVerifyCol;

            LogBus.Info("TemplateVerify",
                $"实拍验证开始: 模板[{name}]({template.Type}) 门槛=0.4 模式={(IsCameraMode ? "相机实采帧" : "本地/既有图")} " +
                $"上次验证位置={(lastRow.HasValue ? $"({lastRow:F0},{lastCol:F0})" : "无（首次）")}");

            // v2 统一口径：模板资产内建【搜索框】自动生效；输出基准点（无卡尺=锚点直出；有卡尺=亚像素精测覆盖）
            var result = await Task.Run(() => TemplateCreationBridge.MatchWithDatum(image, name, 0.4));

            IsBusy = false;

            if (!result.Success)
            {
                ClearMatchOverlay();
                LogBus.Warn("TemplateVerify", $"实拍验证 [{name}] 匹配调用失败: {result.Message}");
                QualityReportBrush = System.Windows.Media.Brushes.Red;
                ShowQualityReport($"❌ 模板 [{name}] 验证失败：{result.Message}\n" +
                    "请确认左侧选中的模板与图像内容匹配（模板是背面图案就采背面图）。");
                StatusText = "验证失败: " + result.Message;
                return;
            }

            var mo = result.Data;
            if (mo?.Match == null)
            {
                ClearMatchOverlay();
                LogBus.Warn("TemplateVerify", $"实拍验证 [{name}] 无候选：0.4 门槛未过（目标不在视野/超出搜索框/角度超范围/光照差异/特征弱）");
                QualityReportBrush = System.Windows.Media.Brushes.Red;
                ShowQualityReport($"❌ 模板 [{name}] 在当前图中无候选（0.4 门槛都没过）。\n" +
                    "可能原因：目标不在视野内（或不在模板【搜索框】内）/ 角度超出训练范围 / 光照差异过大 / 模板特征太弱 → 建议重建模板。");
                StatusText = "验证未找到目标";
                return;
            }
            var best = mo.Match;

            // 分数档位（验证门槛 0.4，生产建议 ≥0.7 才稳）
            string scoreVerdict;
            string verdictColor;
            if (best.Score >= 0.7) { scoreVerdict = "✅ 分数达标（≥0.7，可投生产）"; QualityReportBrush = System.Windows.Media.Brushes.Green; verdictColor = "green"; }
            else if (best.Score >= 0.5) { scoreVerdict = "🟡 分数偏低（0.5~0.7），光照一变可能失配，建议优化模板"; QualityReportBrush = System.Windows.Media.Brushes.DarkOrange; verdictColor = "orange"; }
            else { scoreVerdict = "❌ 分数过低（<0.5），不可靠，请重建模板"; QualityReportBrush = System.Windows.Media.Brushes.Red; verdictColor = "red"; }

            // 锁死检测：与上一次验证对比位置
            string lockCheck = "";
            if (lastRow.HasValue && lastCol.HasValue)
            {
                double dRow = best.PixelRow - lastRow.Value;
                double dCol = best.PixelCol - lastCol.Value;
                if (System.Math.Abs(dRow) < 5 && System.Math.Abs(dCol) < 5)
                {
                    lockCheck = "\n\n⚠️ 锁死警报：这次匹配位置与上次几乎相同（Δ=" +
                        $"({dRow:F1},{dCol:F1})px)。如果你确实挪动过目标，说明模板锁死了背景" +
                        "（ROI 框太大，模型学的是桌面纹理）——无论目标在哪都输出模板原位，必须重建模板！\n" +
                        "（若你还没挪过目标：请把目标挪开 ≥20mm 后再点一次验证）";
                    LogBus.Warn("TemplateVerify",
                        $"实拍验证 [{name}] 锁死警报：位置与上次几乎相同 Δ=({dRow:F1},{dCol:F1})px，Score={best.Score:F3} —— 若已挪动目标则模板锁死背景，须重建");
                    if (QualityReportBrush == System.Windows.Media.Brushes.Green)
                    {
                        QualityReportBrush = System.Windows.Media.Brushes.Red;
                    }
                }
                else
                {
                    lockCheck = $"\n\n✅ 跟手检查：匹配位置较上次移动了 ({dRow:F1},{dCol:F1})px —— 位置跟着目标走，未锁死背景。";
                    LogBus.Info("TemplateVerify",
                        $"实拍验证 [{name}] 跟手检查通过：位置较上次移动 ({dRow:F1},{dCol:F1})px，Score={best.Score:F3}");
                }
            }
            else
            {
                lockCheck = "\n\n（下一步：把目标挪开 ≥20mm 或转个角度 → 再点【🔍 实拍验证】，位置应跟着目标走）";
            }

            _lastVerifyRow = best.PixelRow;
            _lastVerifyCol = best.PixelCol;

            // 🌟 匹配效果上屏：模型轮廓贴合目标 + 中心十字 + 基准点(精测) + 卡尺测量带/边缘点 + 分数/角度标注。
            // 轮廓生成含模板文件磁盘加载，放后台线程（铁律：算子不在 UI 线程同步跑）。
            try
            {
                var overlays = await Task.Run(() => BuildMatchOverlays(template, best, mo, verdictColor));
                foreach (var o in _verifyOverlays)
                {
                    (o?.NativeHandle as IDisposable)?.Dispose();
                }
                _verifyOverlays = overlays;
                RebuildDisplayOverlays();
            }
            catch (Exception ex)
            {
                StatusText = "匹配效果绘制失败: " + ex.Message;
                LogBus.Warn("TemplateVerify", $"实拍验证 [{name}] 匹配效果绘制失败（不影响验证结论）: {ex.Message}");
            }

            LogBus.Info("TemplateVerify",
                $"实拍验证完成 [{name}]: Score={best.Score:F3} 锚点=({best.PixelRow:F1},{best.PixelCol:F1}) " +
                $"{best.RotateDegree:F2}° → 基准点=({mo.DatumRow:F1},{mo.DatumCol:F1})" +
                (mo.RefinedByCaliper ? $"（{mo.RefineKind} 精测覆盖）" : "（锚点直出）") +
                $" 卡尺={mo.Measurements?.Count ?? 0} 条 verdict={verdictColor}");

            ShowQualityReport(BuildVerifyReport(template, mo, scoreVerdict, lockCheck));

            StatusText = $"验证完成：Score={best.Score:F3} 基准点@({mo.DatumRow:F0},{mo.DatumCol:F0})";
        }

        /// <summary>
        /// 创建成功后的【贴合回显】：在创建图（引擎真源 _rawFrame，避开掩膜预览灰帧）上对刚落盘的模板
        /// 跑一次低门槛匹配，复用实拍验证同款叠加（轮廓/特征/中心十字/卡尺测量带/精测基准点）上屏——
        /// 确认"学到的东西、卡尺摆位、基准点钉点"都正确。与实拍验证的区别：本回显用创建图（必中，
        /// 视觉确认为主）；实拍验证用新图/新采帧（真实考验，两步法判定合格）。回显失败不弹窗不阻塞。
        /// </summary>
        private async Task EchoCreatedTemplateAsync(TemplateInfo template)
        {
            if (template == null || string.IsNullOrWhiteSpace(template.Name)) return;
            // 掩膜预览把显示帧切成了灰化帧 → 先恢复真源原图显示（编辑态开关已在调用方关闭并恢复）
            if (_previewActive)
            {
                RestoreOriginalDisplayFrame();
            }
            var image = _rawFrame ?? _currentContext?.Image;
            if (image == null) return;
            try
            {
                var match = await Task.Run(() => TemplateCreationBridge.MatchWithDatum(image, template.Name, 0.4));
                var mo = match?.Data;
                if (!match.Success || mo?.Match == null)
                {
                    // 创建图上都匹配不到（自测分极低/掩膜域太小）→ 清掉可能残留的旧验证轮廓，软提示不弹窗
                    ClearMatchOverlay();
                    LogBus.Warn("TemplateEcho", $"创建后贴合回显无候选 [{template.Name}]: {match?.Message ?? "0.4 门槛未过"}（自测分低，请按体检报告原因重建）");
                    StatusText = StatusText + "（回显无候选：自测分过低，建议按体检报告原因重建）";
                    return;
                }
                var overlays = await Task.Run(() => BuildMatchOverlays(template, mo.Match, mo, "green"));
                foreach (var o in _verifyOverlays)
                {
                    (o?.NativeHandle as IDisposable)?.Dispose();
                }
                _verifyOverlays = overlays;
                RebuildDisplayOverlays();
                LogBus.Info("TemplateEcho",
                    $"创建后贴合回显完成 [{template.Name}]: Score={mo.Match.Score:F3} " +
                    $"基准点=({mo.DatumRow:F1},{mo.DatumCol:F1}){(mo.RefinedByCaliper ? "（卡尺精测）" : "（锚点直出）")} 卡尺={mo.Measurements?.Count ?? 0} 条");
            }
            catch (Exception ex)
            {
                LogBus.Warn("TemplateEcho", $"创建后贴合回显异常（不影响创建结果）[{template.Name}]: {ex.Message}");
            }
        }

        /// <summary>实拍验证报告文本：分数 + 锚点/基准点口径 + 卡尺逐条摘要 + 搜索框状态 + 结论</summary>
        private string BuildVerifyReport(TemplateInfo template, TemplateMeasureOutput mo,
            string scoreVerdict, string lockCheck)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"模板 [{template?.Name}] 实拍验证结果：");
            var match = mo.Match;
            sb.AppendLine($"  匹配分数 {match.Score:F3}（门槛 0.4）｜角度 {match.RotateDegree:F2}°");
            if (template != null && template.SearchRoiEnabled)
            {
                sb.AppendLine($"  🔍 搜索框=启用：只在 R({template.SearchRoiRow1:F0},{template.SearchRoiCol1:F0})-(" +
                    $"{template.SearchRoiRow2:F0},{template.SearchRoiCol2:F0}) 内找目标（目标出框即无候选）");
            }
            sb.AppendLine(mo.RefinedByCaliper
                ? $"  🎯 基准点=({mo.DatumRow:F2},{mo.DatumCol:F2})【{mo.RefineKind} 卡尺精测覆盖（亚像素）】锚点=({match.PixelRow:F2},{match.PixelCol:F2})"
                : $"  🎯 基准点=锚点直出 ({mo.DatumRow:F2},{mo.DatumCol:F2})（模板未绑精测卡尺）");
            if (mo.Measurements != null && mo.Measurements.Count > 0)
            {
                sb.AppendLine($"  📏 卡尺 {mo.Measurements.Count} 条：");
                foreach (var m in mo.Measurements)
                {
                    if (m == null) continue;
                    if (m.Ok)
                    {
                        int n = m.Points?.Count ?? 0;
                        string fit = "";
                        if (m.CircleFit != null) fit = $"圆心({m.CircleFit.CenterRow:F2},{m.CircleFit.CenterCol:F2}) R={m.CircleFit.Radius:F2} RMS={m.CircleFit.RmsError:F3}";
                        else if (m.LineFit != null) fit = $"线 RMS={m.LineFit.RmsError:F3}";
                        sb.AppendLine($"     [{m.Caliper?.Name}] 边缘{n}点 {(fit.Length > 0 ? fit : "（未拟合）")}");
                    }
                    else
                    {
                        sb.AppendLine($"     [{m.Caliper?.Name}] ⚠ {m.Error}");
                    }
                }
            }
            if (mo.Issues != null && mo.Issues.Count > 0)
            {
                sb.AppendLine("  ⚠ " + string.Join("；", mo.Issues));
            }
            sb.AppendLine($"  {scoreVerdict}{lockCheck}");
            return sb.ToString();
        }

        /// <summary>清掉上一次验证的匹配可视化（验证失败/无候选时不留旧轮廓误导）</summary>
        private void ClearMatchOverlay()
        {
            foreach (var o in _verifyOverlays)
            {
                (o?.NativeHandle as IDisposable)?.Dispose();
            }
            _verifyOverlays = new List<ImageOverlay>();
            RebuildDisplayOverlays();
        }

        /// <summary>
        /// 构造匹配可视化叠加：模型轮廓（Shape=特征轮廓逐边贴合目标 / Ncc=ROI 外接旋转矩形）
        /// + 模板特征仿射（P2：青色十字=特征点 / 轮廓=特征面，验证"特征随位姿跟得准不准"）
        /// + 中心十字 + 分数/角度标注 + [v2] 卡尺测量带(橙)/边缘点(lime 小十字)/精测基准点(红大十字)。
        /// 句柄生成涉及磁盘读模板，须在后台线程调用。
        /// </summary>
        private List<ImageOverlay> BuildMatchOverlays(TemplateInfo template,
            Grayson.Vision.HalconWrapper.Match2D.TemplateMatchResult best,
            TemplateMeasureOutput measure, string color)
        {
            var overlays = new List<ImageOverlay>();
            string templateName = template?.Name;
            try
            {
                var contourRes = TemplateCreationBridge.CreateMatchOverlay(templateName, best);
                if (contourRes.Success && contourRes.Data != null)
                {
                    overlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = color, NativeHandle = contourRes.Data });
                }
            }
            catch { /* 轮廓生成失败不影响其余标注 */ }

            // 🌟 特征仿射叠加（P2 验收点）：识别出的 pose 就是模型原点（ROI 中心）位置，
            // 特征存的是相对该中心的偏移 → 同一 hom_mat2d 把点/面整体搬到目标上并随角度旋转。
            if (template?.Features != null && template.Features.Count > 0)
            {
                try
                {
                    var featOverlay = TemplateCreationBridge.CreateFeatureOverlayForTarget(
                        template.Features, best.PixelRow, best.PixelCol, best.RotateDegree);
                    if (featOverlay != null)
                    {
                        overlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = "cyan", NativeHandle = featOverlay });
                    }
                }
                catch (Exception fex)
                {
                    LogBus.Warn("TemplateVerify", $"模板 [{templateName}] 特征仿射叠加生成失败（不影响验证结论）: {fex.Message}");
                }
            }

            try
            {
                overlays.Add(new ImageOverlay
                {
                    Kind = OverlayKind.Xld,
                    Color = color,
                    NativeHandle = TemplateCreationBridge.CreateCross(best.PixelRow, best.PixelCol, 60)
                });
            }
            catch { }
            overlays.Add(new ImageOverlay
            {
                Kind = OverlayKind.Text,
                Color = color,
                Text = $"{best.Score:F2} / {best.RotateDegree:F1}°",
                Row = Math.Max(2, best.PixelRow - 50),
                Column = Math.Max(2, best.PixelCol - 30)
            });
            // —— v2 精测可视化：卡尺测量带(橙,随位姿) → 边缘点(lime 小十字) → 精测基准点(红大十字)
            if (template?.Calipers != null && template.Calipers.Count > 0)
            {
                try
                {
                    var bands = TemplateCreationBridge.CreateCaliperBandsAtPose(
                        template.Calipers, best.PixelRow, best.PixelCol, best.RotateDegree);
                    if (bands != null)
                    {
                        overlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = "orange", NativeHandle = bands });
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateVerify", $"卡尺测量带叠加失败（不影响结论）: {ex.Message}");
                }
            }
            if (measure?.Measurements != null && measure.Measurements.Count > 0)
            {
                try
                {
                    var marks = TemplateCreationBridge.CreateEdgePointMarkers(measure.Measurements);
                    if (marks != null)
                    {
                        overlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = "lime", NativeHandle = marks });
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateVerify", $"卡尺边缘点叠加失败（不影响结论）: {ex.Message}");
                }
            }
            if (measure != null && measure.RefinedByCaliper)
            {
                try
                {
                    // 精测覆盖的最终基准点（CircleCenter/LineIntersection）→ 红大十字
                    overlays.Add(new ImageOverlay
                    {
                        Kind = OverlayKind.Xld,
                        Color = "red",
                        NativeHandle = TemplateCreationBridge.CreateDatumCross(measure.DatumRow, measure.DatumCol)
                    });
                }
                catch { }
            }

            return overlays;
        }

        /// <summary>
        /// 合并 ROI 常驻框 + 掩膜笔画描边 + 特征标注 + 匹配可视化叠加到当前 context 并刷新显示。
        /// 绘制次序：ROI（基底黄框）→ 掩膜笔画（保留=lime / 排除=red）→ 特征标注（cyan）→ 验证效果（最上层）。
        /// 句柄释放约定：各字段替换时自行 Dispose 旧句柄（UpdateRoiOverlay/RebuildMaskOverlays/RebuildFeatureOverlays/验证路径），
        /// 最终未替换的由 WpfImageRenderContext.Dispose 兜底——本方法只重组列表不释放。
        /// </summary>
        private void RebuildDisplayOverlays()
        {
            var ctx = _currentContext;
            if (ctx == null) return;

            ctx.Overlays = BuildOverlaySnapshot();

            DisplayVm.RefreshActiveImage();
        }

        /// <summary>清空创建向导表单（保留当前源图？不 —— 新建模板从选图重新开始）</summary>
        private void ResetWizard()
        {
            WizardStep = 0;
            TemplateName = string.Empty;
            TemplateType = TemplateMatchType.Shape;
            AngleStart = -180.0;
            AngleEnd = 180.0;
            Remark = string.Empty;
            ClearRoi();          // 同时清掉基底 ROI、掩膜笔画与特征标注
            MaskEditVisible = false;
            FeatureEditVisible = false;
            if (_caliperDrawMode) CaliperDrawMode = false; // 批3：拖绘现场随向导重置退出
            // 搜索框随向导重置：不启用 + 数值清零（叠加句柄随下方 context.Dispose 统一释放，引用先断）
            _searchRoiEnabled = false;
            OnPropertyChanged(nameof(SearchRoiEnabled));
            _searchRoiDrawMode = false;
            OnPropertyChanged(nameof(SearchRoiDrawMode));
            _searchRoiOverlay = null;
            _searchRoiRow1 = _searchRoiCol1 = 0;
            _searchRoiRow2 = _searchRoiCol2 = 0;
            OnPropertyChanged(nameof(SearchRoiRow1)); OnPropertyChanged(nameof(SearchRoiCol1));
            OnPropertyChanged(nameof(SearchRoiRow2)); OnPropertyChanged(nameof(SearchRoiCol2));
            // 卡尺/基准点已随 ClearRoi 清空（ROI 基底没了）；工件形状选型随向导回到默认
            if (_workpieceShape != "Generic")
            {
                _workpieceShape = "Generic";
                OnPropertyChanged(nameof(WorkpieceShape));
                UpdateCaliperStatusText("Generic");
            }
            // 新建归属：沿用页面钉住的工位（无钉=全局），不随向导重置丢失
            TemplateOwnerStation = _pinnedOwnerStation;
            _lastSourceImagePath = string.Empty;

            var old = _currentContext;
            _currentContext = null;
            DisplayVm.ActiveImageContext = null;
            old?.Dispose();
            _rawFrame?.Dispose();
            _rawFrame = null;
            _previewActive = false;
            ++_previewSeq;
            _previewBusy = false;
            HasImage = false;

            SelectedTemplate = null;
            StatusText = "就绪：请选择源图像并框选 ROI";
            HideQualityReport();      // 全新向导：清掉上一张模板的体检报告
            _lastVerifyRow = null;    // 两步法基准清零：验证从新模板的第一次实拍起算
            _lastVerifyCol = null;

            // 全清场：宿主 ROI 集合一并静默清（即便显示帧已空，防下一张新图残留旧 ROI 形状）
            EditorSceneReset?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>清空 ROI（供视图"清除 ROI"按钮调用）：数值清零 + 移除常驻 ROI 框显示。
        /// ROI 是掩膜的基底，基底清空后掩膜几何一并清掉（否则掩膜悬空无意义）。</summary>
        public void ClearRoi()
        {
            RoiRow1 = 0;
            RoiCol1 = 0;
            RoiRow2 = 0;
            RoiCol2 = 0;
            _baseRoiShape = null; // 基底形状随 ROI 一并清空
            if (_roiOverlay != null)
            {
                (_roiOverlay.NativeHandle as IDisposable)?.Dispose();
                _roiOverlay = null;
                if (HasImage)
                {
                    RebuildDisplayOverlays();
                }
            }
            if (_maskStrokes.Count > 0)
            {
                ClearMaskStrokes(false);
            }
            if (_features.Count > 0)
            {
                ClearFeatureStrokes(false);
            }
            // 卡尺/基准点同样以 ROI 为基底（偏移相对锚点=显式基准点或 ROI 中心）：基底没了一并清掉
            if (HasCaliperOrDatumAssets)
            {
                ClearCaliperAssets();
            }
        }

        /// <summary>视图框选 ROI 完成后回调（更新状态栏反馈 + 常驻 ROI 框显示）。
        /// color：跟随宿主 ROI 编辑器当前轮廓色（右键改色后黄框同步变色，默认黄）。</summary>
        public void NotifyRoiSelected(string color = "yellow")
        {
            StatusText = $"ROI 已框选: Row {RoiRow1:0.0} ~ {RoiRow2:0.0}, Col {RoiCol1:0.0} ~ {RoiCol2:0.0}";
            UpdateRoiOverlay(color);
            // ROI 中心=特征/掩膜的原点：基底被拖动后特征要重新钉到新中心
            if (_features.Count > 0)
            {
                RebuildFeatureOverlays();
            }
            // 学习域预览依赖 ROI：掩膜编辑中重框/拖动基底 → 立即刷新预览与整域版图轮廓
            if (MaskEditVisible)
            {
                RequestLearnDomainPreview();
                RebuildMaskOverlays();
            }
            // 卡尺几何偏移/基准点均相对锚点（显式基准点或 ROI 中心）：ROI 被重框/拖动后按新锚点重摆测量带
            if (HasCaliperOrDatumAssets)
            {
                RebuildCaliperOverlay();
            }
        }

        /// <summary>
        /// 基底 ROI 形状更新入口（模板页 RoiCommitted/RoiEdited 调用）：保存"用户所画形状"为学习框
        /// （圆/旋转矩形/多边形等原样保留，不再只留外接矩形数值）。重绘由 NotifyRoiSelected
        /// （UpdateRoiOverlay 画形状黄框 + 掩膜版图重建）统一收敛，本方法只存储。
        /// </summary>
        public void SetRoiShape(RoiShape shape)
        {
            // 掩膜几何模型=引擎可直接消费的形状载体（Kind/坐标/多边形顶点同语义）
            _baseRoiShape = ConvertRoiToMaskShape(shape);
        }

        /// <summary>供 View 回注宿主用：把基底形状（或退化的 AABB 矩形）转成宿主 RoiShape（黄色，含图像尺寸）</summary>
        public RoiShape BuildHostBaseRoi()
        {
            double w = 0, h = 0;
            var img = _currentContext?.Image;
            if (img != null)
            {
                try { w = img.Width; h = img.Height; } catch { }
            }
            var t = _baseRoiShape;
            RoiShape r;
            if (t == null || string.Equals(t.Kind, "Rectangle1", StringComparison.OrdinalIgnoreCase))
            {
                r = new RoiShape(RoiShapeKind.Rectangle1)
                {
                    Row = RoiRow1, Col = RoiCol1, Row2 = RoiRow2, Col2 = RoiCol2
                };
            }
            else
            {
                switch ((t.Kind ?? "").ToLowerInvariant())
                {
                    case "circle":
                        r = new RoiShape(RoiShapeKind.Circle) { Row = t.Row, Col = t.Col, Radius1 = t.Radius1 };
                        break;
                    case "rectangle2":
                        r = new RoiShape(RoiShapeKind.Rectangle2)
                        { Row = t.Row, Col = t.Col, Phi = t.Phi, Length1 = t.Length1, Length2 = t.Length2 };
                        break;
                    case "ellipse":
                        r = new RoiShape(RoiShapeKind.Ellipse)
                        { Row = t.Row, Col = t.Col, Phi = t.Phi, Radius1 = t.Radius1, Radius2 = t.Radius2 };
                        break;
                    case "polygon":
                    case "freehand":
                        r = new RoiShape(RoiShapeKind.Polygon);
                        if (t.Points != null && t.Points.Length >= 6)
                        {
                            var pts = new Point[t.Points.Length / 2];
                            for (int i = 0; i < pts.Length; i++)
                            {
                                pts[i] = new Point(t.Points[i * 2], t.Points[i * 2 + 1]); // X=col,Y=row
                            }
                            r.Polygon = pts;
                        }
                        break;
                    default:
                        r = new RoiShape(RoiShapeKind.Rectangle1)
                        { Row = RoiRow1, Col = RoiCol1, Row2 = RoiRow2, Col2 = RoiCol2 };
                        break;
                }
            }
            r.ImageWidth = w;
            r.ImageHeight = h;
            r.ColorName = "yellow";
            r.Filled = false;
            return r;
        }

        /// <summary>载入编辑流程尾部调用：通知 View 把基底 ROI 回注宿主（渲染线程清空后排队，保证不被误清）</summary>
        private void RaiseEditorRoiReady()
        {
            try
            {
                // 宿主 Display 换帧为异步(Dispatcher.Normal)；用 Background 优先级排队 → 必在其后执行，
                // 否则回注集合会立刻被"底图更换清空 ROI"再次清掉
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(() => EditorRoiReady?.Invoke(this, EventArgs.Empty)));
            }
            catch { /* 视图未就绪时静默，宿主回注由下一次编辑载入补齐 */ }
        }

        /// <summary>
        /// 生成/更新常驻 ROI 叠加：有形状基底（圆/旋转矩形等）→ 画"用户形状"描边（黄 XLD）；
        /// 无形状/平行矩形 → 黄色矩形 Region（原行为）。随缩放平移重放。
        /// 宿主 HALCON 窗口层也会同色同几何绘制该形状（两者重叠不可辨，用于保证任意页面可见）；
        /// VM 数值清除（ClearRoi/换图）时此处同步移除黄框、值归零，保持"框选→数值"联动语义。
        /// GenRectangle1/取边界为微秒级句柄生成（无图像处理），UI 线程直接执行不违背算子铁律。
        /// </summary>
        private void UpdateRoiOverlay(string color = "yellow")
        {
            if (_currentContext == null || RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1)
            {
                if (_roiOverlay != null)
                {
                    (_roiOverlay.NativeHandle as IDisposable)?.Dispose();
                    _roiOverlay = null;
                    RebuildDisplayOverlays();
                }
                return;
            }

            (_roiOverlay?.NativeHandle as IDisposable)?.Dispose();
            _roiOverlay = null;

            bool shapeShown = false;
            var bs = _baseRoiShape;
            if (bs != null && !string.Equals(bs.Kind, "Rectangle1", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(bs.Kind, "Line", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 形状基底：XLD 描边显示"用户所画的学习框"（引擎同源 BuildShapeRegion 边界）
                    var contour = TemplateCreationBridge.CreateMaskContourOverlay(bs);
                    if (contour != null)
                    {
                        _roiOverlay = new ImageOverlay
                        {
                            Kind = OverlayKind.Xld,
                            Color = string.IsNullOrEmpty(color) ? "yellow" : color,
                            NativeHandle = contour
                        };
                        shapeShown = true;
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateRoi", $"基底形状描边生成失败(退矩形框): {ex.Message}");
                }
            }
            if (!shapeShown)
            {
                _roiOverlay = new ImageOverlay
                {
                    Kind = OverlayKind.Region,
                    Color = string.IsNullOrEmpty(color) ? "yellow" : color,
                    NativeHandle = TemplateCreationBridge.CreateRoiRectangle(RoiRow1, RoiCol1, RoiRow2, RoiCol2)
                };
            }
            RebuildDisplayOverlays();
        }

        #endregion

        #region 学习掩膜操作（模板编辑：区域涂抹 ∪/∖）

        //===================================================================================
        // 学习域预览可视化（P1 2026-09-09）：掩膜编辑开启且 ROI 有效时，把显示帧换成
        // "学习域预览帧"（域外灰化）——用户涂抹/调框时一眼看清模板到底学哪块。
        // 显示帧与引擎真源分离（_rawFrame），预览切换不污染创建取图。
        //===================================================================================

        /// <summary>掩膜画笔半径(px)：掩膜面板滑杆绑定，收笔时写入掩膜笔画（IsBrush）</summary>
        private double _maskBrushRadius = 12.0;
        public double MaskBrushRadius
        {
            get => _maskBrushRadius;
            set
            {
                var v = Math.Max(2.0, Math.Min(80.0, value));
                if (Set(ref _maskBrushRadius, v))
                {
                    OnPropertyChanged(nameof(MaskBrushRadiusText));
                }
            }
        }

        public string MaskBrushRadiusText => $"画笔半径 {_maskBrushRadius:0} px";

        private bool _previewDirty;
        private bool _previewUrgent;
        private static readonly TimeSpan PreviewDebounce = TimeSpan.FromMilliseconds(120);
        /// <summary>涂抹按住中的紧急防抖（短）：采样广播每 ~24ms 触发一次新合成起点，
        /// 使"按住涂 → 学习域灰化帧实时扩/缩"；收笔/撤销等低频操作仍走 PreviewDebounce 长防抖合并</summary>
        private static readonly TimeSpan PreviewSketchDebounce = TimeSpan.FromMilliseconds(24);

        /// <summary>掩膜可视化当前状态（供界面状态行显示：预览中/原图/条件不满足）</summary>
        private string _learnPreviewStatus = "";
        public string LearnPreviewStatus
        {
            get => _learnPreviewStatus;
            private set => Set(ref _learnPreviewStatus, value);
        }

        /// <summary>
        /// 由掩膜状态变化驱动的预览刷新入口（收笔/撤销/清空/ROI 变化/🖌 涂抹采样后调用）：
        /// 掩膜编辑开启且 ROI 有效 → 防抖合成预览帧（urgent=涂抹按住中，走短防抖让版图跟手）；否则恢复原图。
        /// </summary>
        private void RequestLearnDomainPreview(bool urgent = false)
        {
            if (_rawFrame == null || !HasImage)
            {
                LearnPreviewStatus = "";
                return;
            }
            if (!MaskEditVisible || RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1)
            {
                LearnPreviewStatus = "";
                RestoreOriginalDisplayFrame();
                return;
            }
            if (_previewBusy)
            {
                _previewDirty = true; // 在途合成完成后补一次（快涂连笔不丢最终状态）
                _previewUrgent |= urgent;
                return;
            }
            LearnPreviewStatus = "学习域预览生成中…";
            _previewBusy = true;
            _previewDirty = false;
            _previewUrgent = urgent;
            int seq = ++_previewSeq;
            var raw = _rawFrame;
            double r1 = RoiRow1, c1 = RoiCol1, r2 = RoiRow2, c2 = RoiCol2;
            var mask = BuildCurrentLearnMask(includeSketch: urgent || _sketchDown);
            _ = ComposePreviewAsync(seq, raw, r1, c1, r2, c2, mask, urgent ? PreviewSketchDebounce : PreviewDebounce);
        }

        private async Task ComposePreviewAsync(int seq, HalconRenderImage raw,
            double r1, double c1, double r2, double c2, TemplateLearnMask mask, TimeSpan debounce)
        {
            try
            {
                await Task.Delay(debounce);
                if (seq != _previewSeq || !MaskEditVisible || raw == null || raw.HImage == null || !raw.HImage.IsInitialized())
                {
                    _previewBusy = false;
                    return;
                }
                var preview = await Task.Run(() =>
                    TemplateCreationBridge.ComposeLearnDomainPreviewFrame(raw, r1, c1, r2, c2, _baseRoiShape, mask));
                if (seq != _previewSeq || !MaskEditVisible || preview == null)
                {
                    preview?.Dispose();
                    _previewBusy = false;
                    LearnPreviewStatus = "";
                    return;
                }
                _previewBusy = false;
                SwapDisplayFrame(preview);
                _previewActive = true;
                LearnPreviewStatus = (_maskStrokes.Count == 0 && !_sketchDown)
                    ? "学习域预览：高亮区=ROI 内（无掩膜，整框学习）"
                    : "学习域预览：高亮区=引擎将学习的内容（🖌∪=版图/涂哪学哪 · ∖=抠洞），其余灰化；涂抹实时跟手";
                if (_previewDirty)
                {
                    bool wasUrgent = _previewUrgent;
                    _previewDirty = false;
                    _previewUrgent = false;
                    RequestLearnDomainPreview(wasUrgent);
                }
            }
            catch (Exception ex)
            {
                _previewBusy = false;
                _previewUrgent = false;
                LearnPreviewStatus = "学习域预览失败（仍按原图编辑）";
                LogBus.Warn("TemplateMask", $"学习域预览合成异常: {ex.Message}");
            }
        }

        /// <summary>恢复显示帧为引擎真源原图（退出掩膜预览）</summary>
        private void RestoreOriginalDisplayFrame()
        {
            if (_rawFrame == null || _currentContext == null) return;
            if (!_previewActive) return;
            ++_previewSeq; // 使在途合成失效
            var disp = TemplateCreationBridge.CreateDisplayCopy(_rawFrame);
            if (disp != null)
            {
                SwapDisplayFrame(disp);
                _previewActive = false;
            }
        }

        /// <summary>替换当前显示帧（新 wrapper 接管所有权；Overlays 从 owner 列表重建，旧 ctx 只清壳）</summary>
        private void SwapDisplayFrame(HalconRenderImage frame)
        {
            var old = _currentContext;
            if (old == null)
            {
                frame?.Dispose();
                return;
            }
            var ctx = new WpfImageRenderContext
            {
                NodeId = "template-editor",
                NodeName = old.NodeName,
                Image = frame
            };
            ctx.Overlays = BuildOverlaySnapshot(); // 黄框/掩膜描边/特征随新帧保留
            _currentContext = ctx;
            if (old.Overlays != null) old.Overlays = null; // owner 列表已接管，旧 ctx 不再重复释放
            old.Dispose();                                 // 释放旧显示帧（真源 _rawFrame 不受影响）
            DisplayVm.ActiveImageContext = ctx;            // 触发渲染（新实例，setter 必触发）
            HasImage = frame != null;
        }

        /// <summary>从 owner 列表组装当前 Overlays 快照（ROI 框 → 搜索框 → 掩膜版图 → 特征 → 验证叠加）</summary>
        private List<ImageOverlay> BuildOverlaySnapshot()
        {
            var list = new List<ImageOverlay>();
            if (_roiOverlay != null) list.Add(_roiOverlay);          // 黄 学习框/基底形状
            if (_searchRoiOverlay != null) list.Add(_searchRoiOverlay); // 橙 搜索框
            if (_caliperOverlay != null) list.Add(_caliperOverlay);  // 紫 卡尺测量带（锚点@0°静态预览）
            list.AddRange(_maskOverlays);                            // lime 掩膜版图
            list.AddRange(_featureOverlays);                         // cyan 特征标注
            if (_datumOverlay != null) list.Add(_datumOverlay);      // 红 显式基准点大十字
            list.AddRange(_verifyOverlays);                          // 实拍验证结果
            return list;
        }

        /// <summary>
        /// 由当前笔画列表构造落盘/合成用 TemplateLearnMask。
        /// includeSketch=true 且 🖌 涂抹按住中 → 追加"未收笔临时轨迹"为一笔 Brush（Op=当前模式）：
        /// 学习域预览帧/整域轮廓实时反映"正在涂的部分"（∪ 版图扩 / ∖ 抠洞），收笔后走正式 _maskStrokes。
        /// 无有效笔画 → null = 全 ROI 学习。
        /// </summary>
        private TemplateLearnMask BuildCurrentLearnMask(bool includeSketch = false)
        {
            bool hasSketch = includeSketch && _sketchDown && _sketchPoints != null && _sketchPoints.Length > 0;
            if (_maskStrokes.Count == 0 && !hasSketch) return null;
            var shapes = new List<TemplateMaskShape>(_maskStrokes);
            if (hasSketch)
            {
                var tmp = new TemplateMaskShape
                {
                    Kind = "Brush",
                    IsBrush = true,
                    Op = MaskAddMode ? TemplateMaskOp.Add : TemplateMaskOp.Subtract,
                    BrushRadius = _sketchRadius > 1 ? _sketchRadius : _maskBrushRadius,
                    Points = new double[_sketchPoints.Length * 2]
                };
                for (int i = 0; i < _sketchPoints.Length; i++)
                {
                    tmp.Points[i * 2] = _sketchPoints[i].X;      // x = Col
                    tmp.Points[i * 2 + 1] = _sketchPoints[i].Y;  // y = Row
                }
                shapes.Add(tmp);
            }
            return new TemplateLearnMask
            {
                Enabled = true,
                Shapes = shapes
            };
        }

        /// <summary>
        /// 掩膜收笔入口：宿主 RoiCommitted 事件在"掩膜编辑模式"下把刚绘制的形状交给本方法
        /// （TemplateManagerView.xaml.cs 分发：MaskEditVisible=true 时不再当作基底 ROI）。
        /// 视图侧随后 ImageHost.RemoveRoi(shape) 回收宿主内笔画，宿主只保留基底 ROI。
        /// </summary>
        public bool AddMaskStroke(RoiShape shape)
        {
            if (shape == null || !HasImage) return false;
            var ms = ConvertRoiToMaskShape(shape);
            if (ms == null) return false;
            _maskStrokes.Add(ms);
            UpdateMaskStatus();
            RebuildMaskOverlays();
            RequestLearnDomainPreview(); // 学习域预览刷新（域外灰化反映最新笔画）
            StatusText = $"掩膜 +1（{TemplateMaskRegionBuilder.Describe(BuildCurrentLearnMask())}）——继续画下一笔，画完关闭掩膜编辑点【🚀 创建模板】";
            LogBus.Info("TemplateMask", $"掩膜收笔: {ms.Kind} 作用={(ms.Op == TemplateMaskOp.Add ? "保留∪" : "排除∖")} 累计{_maskStrokes.Count}笔");
            return true;
        }

        /// <summary>宿主形状 → 掩膜笔画模型（线段非闭合区域不支持，忽略并提示）</summary>
        private TemplateMaskShape ConvertRoiToMaskShape(RoiShape shape)
        {
            var ms = new TemplateMaskShape
            {
                Op = MaskAddMode ? TemplateMaskOp.Add : TemplateMaskOp.Subtract
            };
            switch (shape.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    ms.Kind = "Rectangle1"; ms.Row = shape.Row; ms.Col = shape.Col;
                    ms.Row2 = shape.Row2; ms.Col2 = shape.Col2; return ms;
                case RoiShapeKind.Brush:
                    // 涂抹画笔：轨迹点(绝对坐标，X=Col,Y=Row) + 半径 → 引擎按圆盘带膨胀
                    if (shape.Polygon == null || shape.Polygon.Length < 1)
                    {
                        StatusText = "涂抹轨迹无效（无采样点）";
                        return null;
                    }
                    ms.Kind = "Brush";
                    ms.IsBrush = true;
                    ms.BrushRadius = shape.BrushRadius > 1 ? shape.BrushRadius : _maskBrushRadius;
                    ms.Points = new double[shape.Polygon.Length * 2];
                    for (int i = 0; i < shape.Polygon.Length; i++)
                    {
                        ms.Points[i * 2] = shape.Polygon[i].X;      // x = Col
                        ms.Points[i * 2 + 1] = shape.Polygon[i].Y;  // y = Row
                    }
                    return ms;
                case RoiShapeKind.Rectangle2:
                    ms.Kind = "Rectangle2"; ms.Row = shape.Row; ms.Col = shape.Col;
                    ms.Phi = shape.Phi; ms.Length1 = shape.Length1; ms.Length2 = shape.Length2; return ms;
                case RoiShapeKind.Circle:
                    ms.Kind = "Circle"; ms.Row = shape.Row; ms.Col = shape.Col;
                    ms.Radius1 = shape.Radius1; return ms;
                case RoiShapeKind.Ellipse:
                    ms.Kind = "Ellipse"; ms.Row = shape.Row; ms.Col = shape.Col;
                    ms.Phi = shape.Phi; ms.Radius1 = shape.Radius1; ms.Radius2 = shape.Radius2; return ms;
                case RoiShapeKind.Polygon:
                    if (shape.Polygon == null || shape.Polygon.Length < 3)
                    {
                        StatusText = "多边形掩膜顶点不足（≥3 点）";
                        return null;
                    }
                    ms.Kind = "Polygon";
                    ms.Points = new double[shape.Polygon.Length * 2];
                    for (int i = 0; i < shape.Polygon.Length; i++)
                    {
                        ms.Points[i * 2] = shape.Polygon[i].X;      // x = Col
                        ms.Points[i * 2 + 1] = shape.Polygon[i].Y;  // y = Row
                    }
                    return ms;
                default:
                    StatusText = "该形状（线段等）不是封闭区域，不能作为学习掩膜，请改用矩形/圆/椭圆/多边形/手绘";
                    LogBus.Warn("TemplateMask", $"掩膜拒绝非封闭形状: {shape.Kind}");
                    return null;
            }
        }

        /// <summary>撤销最后一笔掩膜（画错了直接回退）</summary>
        private void UndoMaskStroke()
        {
            if (_maskStrokes.Count == 0) return;
            var last = _maskStrokes[_maskStrokes.Count - 1];
            _maskStrokes.RemoveAt(_maskStrokes.Count - 1);
            UpdateMaskStatus();
            RebuildMaskOverlays();
            RequestLearnDomainPreview();
            StatusText = $"已撤销上一笔掩膜（{last.Kind}）——剩余 {_maskStrokes.Count} 笔";
            LogBus.Info("TemplateMask", $"撤销掩膜一笔: {last.Kind} 剩余{_maskStrokes.Count}笔");
        }

        /// <summary>清空全部掩膜笔画（notify=true：界面"清空掩膜"按钮/掩膜模式下宿主 🧹；false：内部连带清空）</summary>
        public void ClearMaskStrokes(bool notify = true)
        {
            _maskStrokes.Clear();
            UpdateMaskStatus();
            RebuildMaskOverlays();
            RequestLearnDomainPreview();
            if (notify)
            {
                StatusText = "掩膜已清空：将学习整块 ROI";
            }
        }

        /// <summary>刷新掩膜统计文本 + 撤销/清空按钮可用态</summary>
        private void UpdateMaskStatus()
        {
            if (_maskStrokes.Count == 0)
            {
                MaskStatusText = "无掩膜：将学习整块 ROI";
            }
            else
            {
                int add = 0, sub = 0;
                foreach (var s in _maskStrokes)
                {
                    if (s == null) continue;
                    if (s.Op == TemplateMaskOp.Add) add++; else sub++;
                }
                MaskStatusText = $"掩膜 {_maskStrokes.Count} 笔：保留∪ {add} · 排除∖ {sub}（创建时按此裁剪学习域）";
            }
            UndoMaskCommand?.RaiseCanExecuteChanged();
            ClearMaskCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// 🖌 涂抹草绘广播入口（宿主 BrushSketchChanged → TemplateManagerView 转发）。
        /// 掩膜编辑开启时把"未收笔轨迹"并入学习域：按住中(down=true)每采样实时重算灰化帧(紧急防抖)
        /// 与整域轮廓（∪ 版图实时扩 / ∖ 实时抠洞）；收笔/放弃(down=false)清临时候选，落定正式笔画。
        /// 只在掩膜编辑态消费（宿主其它页面画 Brush 与模板掩膜无关，忽略）。
        /// </summary>
        public void NotifyBrushSketch(Point[] points, double radius, bool down)
        {
            if (!MaskEditVisible || !HasImage) return;
            if (down)
            {
                if (points == null || points.Length == 0) return;
                _sketchPoints = points;
                _sketchRadius = radius > 1 ? radius : _maskBrushRadius;
                _sketchDown = true;
                RequestLearnDomainPreview(urgent: true); // 灰化帧：涂哪亮哪/抠哪灰哪（短防抖跟手）
                RebuildMaskOverlaysThrottled();          // 整域轮廓：版图边界实时扩/缩
            }
            else
            {
                bool hadLive = _sketchDown && _sketchPoints != null && _sketchPoints.Length > 0;
                _sketchDown = false;
                _sketchPoints = null;
                if (hadLive)
                {
                    // 收笔：正式笔画已由 RoiCommitted→AddMaskStroke 收入并重建，这里再兜底一次（幂等）
                    RequestLearnDomainPreview();
                    RebuildMaskOverlays();
                }
            }
        }

        /// <summary>涂抹采样高频广播下的整域轮廓节流重建（region 布尔 μs~ms 级；40ms 档避免每采样全量算）</summary>
        private void RebuildMaskOverlaysThrottled()
        {
            var now = DateTime.UtcNow;
            if (now - _lastContourRebuild < ContourRebuildMinInterval) return;
            _lastContourRebuild = now;
            RebuildMaskOverlays();
        }

        /// <summary>
        /// 重建掩膜叠加 —— 2026-09-09 版图化：从"逐笔 lime/red 描边"改为"学习域整域轮廓一条(lime)"，
        /// 口径 = BuildLearnMaskRegion（与灰化预览帧/创建链同源）：整域 = (有保留∪? ∪保留∩ROI : ROI) − ∪排除。
        /// 用户看到的掩膜是一整块"版图"：∪ 定义/扩大学习域、∖ 从版图抠洞；涂抹中临时轨迹实时并入。
        /// 掩膜编辑关闭/无笔画/ROI 无效时不画（空=全 ROI 学习，ROI 黄框即界）。
        /// 无像素级运算（region 布尔+取边界，μs~ms 级句柄运算），UI 线程直接执行不违背算子铁律。
        /// </summary>
        private void RebuildMaskOverlays()
        {
            foreach (var o in _maskOverlays)
            {
                try { (o?.NativeHandle as IDisposable)?.Dispose(); } catch { }
            }
            _maskOverlays.Clear();

            if (HasImage && _currentContext != null && MaskEditVisible &&
                RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1)
            {
                var mask = BuildCurrentLearnMask(includeSketch: true); // 正式笔画 + 涂抹中临时轨迹
                if (mask != null)
                {
                    object contour = null;
                    try
                    {
                        contour = TemplateCreationBridge.CreateLearnDomainContourOverlay(
                            RoiRow1, RoiCol1, RoiRow2, RoiCol2, _baseRoiShape, mask);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn("TemplateMask", $"学习域整域轮廓生成失败: {ex.Message}");
                    }
                    if (contour != null)
                    {
                        _maskOverlays.Add(new ImageOverlay
                        {
                            Kind = OverlayKind.Xld,
                            Color = "lime", // 版图边界恒用 lime（与引擎"学习域"同源，∪/∖ 过程由灰化帧明暗表达）
                            NativeHandle = contour
                        });
                    }
                }
            }
            RebuildDisplayOverlays();
        }

        /// <summary>
        /// 【✏️ 编辑选中模板】：把左侧选中模板载入右侧编辑器（参数 + 源图 + 掩膜），
        /// 改 ROI/掩膜/角度后点【🚀 创建模板】即"同名覆盖重学"——节点引用不变，旧模板自动退役。
        /// 模板编辑闭环（P1）：列表选中 → 编辑 → 覆盖重学 → 列表刷新（新记录，同 Name）。
        /// </summary>
        private void LoadTemplateForEdit()
        {
            var t = SelectedTemplate;
            if (t == null) return;

            // 丢弃当前向导未保存内容需确认
            if (HasImage && !string.IsNullOrWhiteSpace(TemplateName)
                && !string.Equals(TemplateName.Trim(), t.Name, StringComparison.OrdinalIgnoreCase))
            {
                var ask = MessageBox.Show("右侧向导还有未创建的模板内容，载入选中模板将丢弃这些内容。\n继续载入？",
                    "模板编辑", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes) return;
            }

            try
            {
                // 参数回填（破坏式约定：新模板统一 ROI 中心=模型原点，读回数值即可继续编辑）
                TemplateName = t.Name;
                TemplateType = t.Type;
                AngleStart = t.AngleStart;
                AngleEnd = t.AngleEnd;
                Remark = t.Remark ?? string.Empty;
                RoiRow1 = t.RoiRow1; RoiCol1 = t.RoiCol1;
                RoiRow2 = t.RoiRow2; RoiCol2 = t.RoiCol2;
                TemplateOwnerStation = t.OwnerStation ?? string.Empty;
                _baseRoiShape = t.RoiShape; // v2 形状基底：编辑回注宿主后可点选/拖动原形状（圆/旋转矩形等）
                // 搜索框资产回填（引擎 MatchWithDatum 自动裁剪；橙色叠加在源图/当前帧就绪后刷新）
                _searchRoiEnabled = t.SearchRoiEnabled;
                OnPropertyChanged(nameof(SearchRoiEnabled));
                _searchRoiRow1 = t.SearchRoiRow1; _searchRoiCol1 = t.SearchRoiCol1;
                _searchRoiRow2 = t.SearchRoiRow2; _searchRoiCol2 = t.SearchRoiCol2;
                OnPropertyChanged(nameof(SearchRoiRow1)); OnPropertyChanged(nameof(SearchRoiCol1));
                OnPropertyChanged(nameof(SearchRoiRow2)); OnPropertyChanged(nameof(SearchRoiCol2));
                // 卡尺/基准点/工件形状回填（模板资产 P1）：行对象包着模板本体卡尺的克隆（不污染库内缓存对象）；
                // 显式基准点非 (0,0) → 红大十字标记；0,0 → 自动（创建时锚点=学习域中心/ROI 中心）。
                // 叠加统一交给后续 ShowImage 尾部 / !srcDisplayed 分支的 RebuildCaliperOverlay 重建。
                string shapeVal = string.IsNullOrWhiteSpace(t.WorkpieceShape) ? "Generic" : t.WorkpieceShape.Trim();
                if (_workpieceShape != shapeVal)
                {
                    _workpieceShape = shapeVal;
                    OnPropertyChanged(nameof(WorkpieceShape));
                }
                ReplaceCaliperRows((t.Calipers != null) ? t.Calipers.Select(CloneCaliper) : null, shapeVal);
                var tDatum = t.Datum;
                bool datumExplicit = tDatum != null && (Math.Abs(tDatum.Row) > 0.5 || Math.Abs(tDatum.Col) > 0.5);
                _datumRow = datumExplicit ? tDatum.Row : 0;
                _datumCol = datumExplicit ? tDatum.Col : 0;
                OnPropertyChanged(nameof(DatumRow));
                OnPropertyChanged(nameof(DatumCol));
                // 基准点精测方式/来源回填（CircleCenter/LineIntersection 由引擎按卡尺名精测覆盖锚点）
                int kindIdx = tDatum != null ? (int)tDatum.Kind : 0;
                if (kindIdx < 0 || kindIdx > 2) kindIdx = 0;
                _datumKindIndex = kindIdx;
                OnPropertyChanged(nameof(DatumKindIndex));
                _datumSource1 = (tDatum != null && (kindIdx == 1 || kindIdx == 2)) ? (tDatum.SourceCaliper1 ?? string.Empty) : string.Empty;
                _datumSource2 = (tDatum != null && kindIdx == 2) ? (tDatum.SourceCaliper2 ?? string.Empty) : string.Empty;
                OnPropertyChanged(nameof(DatumSource1));
                OnPropertyChanged(nameof(DatumSource2));
                UpdateDatumKindHint();
                RaiseDatumSourceOptions(); // 失效来源（卡尺被删/改名）自动清，用户按当前卡尺重选
                _maskStrokes = MaskFromTemplate(t.LearnMask);
                _features = FeaturesFrom(t.Features);
                UpdateMaskStatus();
                UpdateFeatureStatus();
                MaskEditVisible = _maskStrokes.Count > 0;    // 掩膜优先开启（与特征互斥）
                FeatureEditVisible = _maskStrokes.Count == 0 && _features.Count > 0;

                // 源图重载：模板有 SourceImagePath 且文件在 → 回读原图继续编辑。
                // （新模板创建时相机/实采帧会自动留档到 Config/Templates/Sources，编辑必然有图；
                //   仅历史旧模板/留档失败/用户原图被移动时 SourceImagePath 不可用。）
                bool hasSrc = !string.IsNullOrEmpty(t.SourceImagePath) && File.Exists(t.SourceImagePath);
                bool srcDisplayed = false;
                if (hasSrc)
                {
                    var renderImage = _renderService.WrapImage(t.SourceImagePath) as HalconRenderImage;
                    if (renderImage != null)
                    {
                        _lastSourceImagePath = t.SourceImagePath;
                        // ShowImage 尾部会按上方已回填的 ROI/掩膜/特征数值重建叠加 → 图上直接可编辑
                        ShowImage(renderImage, "模板源图(编辑)");
                        srcDisplayed = true;
                    }
                }
                if (!srcDisplayed && HasImage && _currentContext?.Image != null)
                {
                    // 源图不可回读但视图上已有一帧（本会话先前采集/载入、与旧模板同一取景）：
                    // 按模板参数重建 ROI 框/搜索框/掩膜/特征/卡尺带/基准点叠加，直接在这帧上继续编辑
                    UpdateRoiOverlay();
                    UpdateSearchRoiOverlay();
                    RebuildCaliperOverlay();
                    if (_maskStrokes.Count > 0)
                    {
                        RebuildMaskOverlays();
                    }
                    if (_features.Count > 0 && RoiRow2 > RoiRow1 && RoiCol2 > RoiCol1)
                    {
                        RebuildFeatureOverlays();
                    }
                }

                StatusText = srcDisplayed
                    ? $"已载入模板 [{t.Name}] 到编辑器（源图已回读）：修改 ROI/掩膜/参数后点【🚀 创建模板】即同名覆盖重学"
                    : HasImage
                        ? $"已载入模板 [{t.Name}] 参数（源图未留档，在当前图上编辑）：修改 ROI/掩膜后点【🚀 创建模板】同名覆盖重学"
                        : $"已载入模板 [{t.Name}] 参数（源图未留档）：请先采集/载入与创建时同一取景的图，才能编辑 ROI/掩膜";
                WizardStep = 4; // 编辑态直达"参数与创建"步（参数表单/创建按钮可见）
                LogBus.Info("TemplateEdit",
                    $"载入模板 [{t.Name}] 到编辑器: 类型={t.Type} 角度[{t.AngleStart:F0}~{t.AngleEnd:F0}] " +
                    $"ROI=({t.RoiRow1:F0},{t.RoiCol1:F0})~({t.RoiRow2:F0},{t.RoiCol2:F0}) " +
                    $"基底形状={(t.RoiShape != null ? t.RoiShape.Kind : "矩形(旧)")} " +
                    $"掩膜={TemplateMaskRegionBuilder.Describe(t.LearnMask)} 源图回读={hasSrc} 归属={t.OwnerStation ?? "全局"}");

                // 基底 ROI 回注宿主（可点选/拖动编辑）：图已就绪才排队 —— 宿主换帧为异步，
                // 用 Background 优先级保证在"底图更换清空 ROI"之后执行
                if (HasImage)
                {
                    RaiseEditorRoiReady();
                }

                // 视图上仍无图可依（源图未留档/留档损坏且本会话没采过帧）→
                // 主动引导取一张同一取景的图像（编辑 ROI/掩膜的前提）
                if (!srcDisplayed && !HasImage)
                {
                    var ask = MessageBox.Show(
                        $"模板 [{t.Name}] 创建时未留档源图（相机实采或旧版本创建），原图已不可回读。\n" +
                        "编辑 ROI/掩膜前需要一张与创建时同一取景的图像。\n\n" +
                        "点【是】→ 用相机采集一帧\n点【否】→ 从本地图片载入\n点【取消】→ 暂不编辑",
                        "模板编辑 · 需先载入取景图", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
                    if (ask == MessageBoxResult.Yes)
                    {
                        StatusText = "正在采集取景图（用于编辑模板 ROI/掩膜）...";
                        AcquireCameraSnapshot();   // 带 IsBusy 闸的按钮同款入口，防与其它采集并发踩踏取流
                    }
                    else if (ask == MessageBoxResult.No)
                    {
                        LoadLocalImage();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("载入模板到编辑器失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                LogBus.Error("TemplateEdit", "载入模板到编辑器异常", ex);
            }
        }

        /// <summary>模板落盘的掩膜 → 编辑器笔画列表（无掩膜/未启用 → 空列表）</summary>
        private static List<TemplateMaskShape> MaskFromTemplate(TemplateLearnMask mask)
        {
            var list = new List<TemplateMaskShape>();
            if (mask != null && mask.Enabled && mask.Shapes != null)
            {
                foreach (var s in mask.Shapes)
                {
                    if (s != null) list.Add(s);
                }
            }
            return list;
        }

        #endregion

        #region 模板特征操作（P2：特征点/面标注 + 编辑/识别可视化）

        /// <summary>模板落盘特征 → 编辑器列表（null/空 → 空列表）</summary>
        private static List<TemplateFeature> FeaturesFrom(List<TemplateFeature> features)
        {
            var list = new List<TemplateFeature>();
            if (features != null)
            {
                foreach (var f in features)
                {
                    if (f != null) list.Add(f);
                }
            }
            return list;
        }

        /// <summary>ROI 中心（模型原点，T1 约定）——特征偏移与编辑器显示的基准</summary>
        private bool TryGetOriginCenter(out double originRow, out double originCol)
        {
            originRow = 0;
            originCol = 0;
            if (RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1) return false;
            originRow = (RoiRow1 + RoiRow2) / 2.0;
            originCol = (RoiCol1 + RoiCol2) / 2.0;
            return true;
        }

        /// <summary>
        /// 特征收笔入口：宿主 RoiCommitted 在"特征标注编辑中"把形状交给本方法
        /// （TemplateManagerView.xaml.cs 分发；与掩膜收笔互斥，见 FeatureEditVisible）。
        /// 点模式取形状几何中心；面模式保留闭合形状（线段不支持）。
        /// </summary>
        public bool AddFeatureStroke(RoiShape shape)
        {
            if (shape == null || !HasImage)
            {
                return false;
            }
            if (!TryGetOriginCenter(out double or, out double oc))
            {
                StatusText = "请先框选基底 ROI（特征以 ROI 中心为原点）";
                LogBus.Warn("TemplateFeature", "特征收笔被拒：ROI 无效（需先框选基底矩形）");
                return false;
            }
            var f = ConvertRoiToFeature(shape, or, oc);
            if (f == null) return false;
            if (string.IsNullOrEmpty(f.Name)) f.Name = $"F{_features.Count + 1}";
            _features.Add(f);
            UpdateFeatureStatus();
            RebuildFeatureOverlays();
            StatusText = $"特征 +1：{f.Name}（{(f.IsPoint ? "⭕ 点" : "▨ 面")}）——继续标注或关闭标注后点【🚀 创建模板】落盘";
            LogBus.Info("TemplateFeature",
                $"特征收笔: {f.Name} 类型={(f.IsPoint ? "点" : "面 " + f.Kind)} 偏移=({f.DRow:F1},{f.DCol:F1}) 累计{_features.Count}个");
            return true;
        }

        /// <summary>宿主形状 → 模板特征（几何转"相对 ROI 中心偏移"坐标，T1 铁律）</summary>
        private TemplateFeature ConvertRoiToFeature(RoiShape shape, double originRow, double originCol)
        {
            if (FeatureAddIsPoint)
            {
                // 点：取几何中心
                GetShapeCenterAbs(shape, out double cr, out double cc);
                return new TemplateFeature { Name = "", IsPoint = true, DRow = cr - originRow, DCol = cc - originCol };
            }

            var f = new TemplateFeature { Name = "", IsPoint = false };
            switch (shape.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    f.Kind = "Rectangle1";
                    f.DRow1 = shape.Row - originRow; f.DCol1 = shape.Col - originCol;
                    f.DRow2 = shape.Row2 - originRow; f.DCol2 = shape.Col2 - originCol;
                    break;
                case RoiShapeKind.Circle:
                    f.Kind = "Circle";
                    f.DRow = shape.Row - originRow; f.DCol = shape.Col - originCol;
                    f.Radius1 = shape.Radius1;
                    break;
                case RoiShapeKind.Ellipse:
                    f.Kind = "Ellipse";
                    f.DRow = shape.Row - originRow; f.DCol = shape.Col - originCol;
                    f.Phi = shape.Phi; f.Radius1 = shape.Radius1; f.Radius2 = shape.Radius2;
                    break;
                case RoiShapeKind.Rectangle2:
                    f.Kind = "Rectangle2";
                    f.DRow = shape.Row - originRow; f.DCol = shape.Col - originCol;
                    f.Phi = shape.Phi; f.Length1 = shape.Length1; f.Length2 = shape.Length2;
                    break;
                case RoiShapeKind.Polygon:
                    if (shape.Polygon == null || shape.Polygon.Length < 3)
                    {
                        StatusText = "特征面多边形顶点不足（≥3 点）";
                        return null;
                    }
                    f.Kind = "Polygon";
                    f.Points = new double[shape.Polygon.Length * 2];
                    for (int i = 0; i < shape.Polygon.Length; i++)
                    {
                        f.Points[i * 2] = shape.Polygon[i].X - originCol;      // dx=DCol
                        f.Points[i * 2 + 1] = shape.Polygon[i].Y - originRow;  // dy=DRow
                    }
                    break;
                default:
                    StatusText = "线段等非闭合形状不能作为特征面，请改用矩形/圆/椭圆/多边形（或切 ⭕点 模式取其中心）";
                    LogBus.Warn("TemplateFeature", $"特征面拒绝非闭合形状: {shape.Kind}");
                    return null;
            }
            return f;
        }

        /// <summary>取任意闭合形状的几何中心（点特征用；矩形/圆/椭圆/旋转矩形用几何参数，多边形取顶点平均）</summary>
        private static void GetShapeCenterAbs(RoiShape shape, out double centerRow, out double centerCol)
        {
            switch (shape.Kind)
            {
                case RoiShapeKind.Rectangle1:
                    centerRow = (shape.Row + shape.Row2) / 2.0;
                    centerCol = (shape.Col + shape.Col2) / 2.0;
                    return;
                case RoiShapeKind.Polygon:
                    {
                        double sr = 0, sc = 0;
                        if (shape.Polygon != null && shape.Polygon.Length > 0)
                        {
                            foreach (var p in shape.Polygon)
                            {
                                sr += p.Y;
                                sc += p.X;
                            }
                            sr /= shape.Polygon.Length;
                            sc /= shape.Polygon.Length;
                        }
                        centerRow = sr;
                        centerCol = sc;
                        return;
                    }
                default: // Circle/Ellipse/Rectangle2：中心字段直读
                    centerRow = shape.Row;
                    centerCol = shape.Col;
                    return;
            }
        }

        /// <summary>撤销最后一个特征</summary>
        private void UndoFeatureStroke()
        {
            if (_features.Count == 0) return;
            var last = _features[_features.Count - 1];
            _features.RemoveAt(_features.Count - 1);
            UpdateFeatureStatus();
            RebuildFeatureOverlays();
            StatusText = $"已撤销特征 {last.Name}——剩余 {_features.Count} 个";
            LogBus.Info("TemplateFeature", $"撤销特征: {last.Name} 剩余{_features.Count}个");
        }

        /// <summary>清空全部特征（notify=true：界面按钮/特征编辑下宿主 🧹；false：内部连带清空）</summary>
        public void ClearFeatureStrokes(bool notify = true)
        {
            _features.Clear();
            UpdateFeatureStatus();
            RebuildFeatureOverlays();
            if (notify)
            {
                StatusText = "特征已清空";
            }
        }

        /// <summary>刷新特征统计文本 + 撤销/清空按钮可用态</summary>
        private void UpdateFeatureStatus()
        {
            if (_features.Count == 0)
            {
                FeatureStatusText = "未标注特征：识别时只画模板轮廓";
            }
            else
            {
                int pts = 0;
                foreach (var f in _features)
                {
                    if (f != null && f.IsPoint) pts++;
                }
                FeatureStatusText = $"特征 {_features.Count} 个：⭕点 {pts} · ▨面 {_features.Count - pts}（青色描边；识别时随位姿仿射显示）";
            }
            UndoFeatureCommand?.RaiseCanExecuteChanged();
            ClearFeatureCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>重建特征静态叠加（编辑器预览）：把特征按"目标位姿=ROI 中心@0°"仿射显示，钉在模板上。
        /// XLD 微秒级句柄运算，UI 线程直接执行（无像素处理，不违背算子铁律）。</summary>
        private void RebuildFeatureOverlays()
        {
            foreach (var o in _featureOverlays)
            {
                try { (o?.NativeHandle as IDisposable)?.Dispose(); } catch { }
            }
            _featureOverlays.Clear();

            if (HasImage && _currentContext != null && _features.Count > 0
                && TryGetOriginCenter(out double or, out double oc))
            {
                try
                {
                    var obj = TemplateCreationBridge.CreateFeatureOverlayForTarget(_features, or, oc, 0);
                    if (obj != null)
                    {
                        _featureOverlays.Add(new ImageOverlay { Kind = OverlayKind.Xld, Color = "cyan", NativeHandle = obj });
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Warn("TemplateFeature", $"特征静态叠加生成失败（跳过）: {ex.Message}");
                }
            }
            RebuildDisplayOverlays();
        }

        #endregion
    }
}
