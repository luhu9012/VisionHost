using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;
using VisualCalibTool.Infrastructure.Mvvm;
using VisualCalibTool.Services;

namespace VisualCalibTool.ViewModels
{
    /// <summary>
    /// 第 1 步的一个可选项（界面直接绑定）。
    ///
    /// ★★ 这些成员必须是<b>属性</b>，不能是字段 —— 这一条踩过一次大坑：
    ///   WPF 的数据绑定<b>只认 public 属性</b>。写成 <c>public string PlainTitle;</c>（字段）时，
    ///   <c>{Binding PlainTitle}</c> 既不是编译错误、也不抛异常，而是<b>静默失败</b>：
    ///   TextBlock 保持空文本。现场表现就是 —— 打开程序，第 1 步的选项卡片
    ///   <b>框都在、一个字都没有</b>，而所有离线断言（查的是视图模型里有没有 5 个目标）全绿。
    ///   失败的唯一痕迹是 WPF 数据绑定 TraceSource 里的一行
    ///   「BindingExpression path error: 'PlainTitle' property not found on ...」，
    ///   不挂监听器就谁也不知道。护栏见自检的「界面：第 1 步的目标列表真的画出来了」。
    /// </summary>
    public sealed class WizardGoalItem
    {
        public WizardGoal Goal { get; set; }

        public string PlainTitle { get; set; }

        public string PlainCaption { get; set; }

        /// <summary>能不能跑（由 WizardCoordinator 判定，界面据此置灰）。★ 必须能绑到 ListBoxItem.IsEnabled 上。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>为什么不能跑（非空 = 置灰并显示这句）。</summary>
        public string DisabledReason { get; set; }

        public WizardGoalItem()
        {
            IsEnabled = true;
        }

        public override string ToString()
        {
            return PlainTitle;
        }
    }

    /// <summary>
    /// 第 2 步回显的一行（界面直接绑定）。
    /// ★ 同 <see cref="WizardGoalItem"/>：被绑定的成员一律用属性，别用字段。
    ///   <see cref="Source"/>（"我凭什么这么认为"）与 <see cref="Known"/>（未知要变警示色）
    ///   都绑在界面上，写成字段就会变成"来源空白 + 未知不报警"——两个都不会报错。
    /// </summary>
    public sealed class WizardSceneItem
    {
        public string Label { get; set; }

        public string Value { get; set; }

        public string Source { get; set; }

        /// <summary>false = 未知（界面用警示色，并显示保守处理方式）。</summary>
        public bool Known { get; set; }

        public string Caution { get; set; }

        /// <summary>是否允许用户改（第 2 步的"不对，我改"）。</summary>
        public bool Editable { get; set; }

        /// <summary>
        /// 这一行"该不该由你定"的人话说明。
        ///
        /// ★★ 它存在的理由：<see cref="Editable"/> 修前是**只写不读**的 ——
        ///   算法层给它赋了值、视图模型逐行传递，而**没有任何控件读它**。
        ///   后果是"这一项本该你拍板、但现在没有入口"这个状态被彻底藏了起来，
        ///   用户只能自己去界面上找一个不存在的入口（"懵"的典型来源之一）。
        ///   ⇒ 把它变成一行可见的小字，这个状态就再也不会被静默吞掉。
        /// </summary>
        public string Hint { get; set; }

        public WizardSceneItem()
        {
            Known = true;
            Editable = true;
        }

        public string Display
        {
            get
            {
                string s = Label + "：" + Value;
                if (!string.IsNullOrEmpty(Caution))
                {
                    s += "　⚠ " + Caution;
                }

                return s;
            }
        }

        public override string ToString()
        {
            return Display;
        }
    }

    /// <summary>第 2.5 步的一个特征类型选项。★ 同 <see cref="WizardGoalItem"/>：绑定成员用属性。</summary>
    public sealed class WizardFeatureItem
    {
        public FeatureKind Kind { get; set; }

        public string PlainTitle { get; set; }

        public string PlainCaption { get; set; }

        public bool IsEnabled { get; set; }

        public string DisabledReason { get; set; }

        public WizardFeatureItem()
        {
            IsEnabled = true;
        }

        public override string ToString()
        {
            return PlainTitle;
        }
    }

    /// <summary>
    /// 步骤导轨（界面左侧竖排的步骤条）上的一项。
    ///
    /// ★★ 为什么由视图模型给这份列表、而不是在 XAML 里写死 5 行：
    ///   「当前在哪一步 / 哪些走完了 / 哪些现在点不动」都是**状态**，写死就是两份状态必然分叉。
    ///
    /// ★ 绑定成员一律用属性 —— 同 <see cref="WizardGoalItem"/> 的教训：
    ///   字段绑定静默失败，导轨会变成一排"框在、字没有"的死项。
    /// </summary>
    public sealed class WizardStepRailItem
    {
        public int Index { get; set; }

        public string ShortTitle { get; set; }

        /// <summary>是不是当前所在步（界面据此高亮）。</summary>
        public bool IsCurrent { get; set; }

        /// <summary>是不是已经走过（线性向导里 = 序号小于当前步，界面据此打 ✔）。</summary>
        public bool IsDone { get; set; }

        /// <summary>点击导轨项的命令。★ 只对「往回走」放行，见 <c>RequestGoToStep</c>。</summary>
        public ICommand GoCommand { get; set; }
    }

    /// <summary>
    /// 第 2 步里<b>一个可点的答案</b>（界面直接绑定成按钮）。
    ///
    /// ★ 为什么要在视图模型层再包一层，而不是直接用 <see cref="WizardChoiceOption"/>：
    ///   Domain 层是零依赖的（连 <c>ICommand</c> 都不能出现），而按钮需要命令。
    ///   包一层的代价是多一个类，收益是"点击 → 改拓扑 → 重新推导"这条链路
    ///   全部发生在视图模型里、可离线单测。
    /// </summary>
    public sealed class WizardAnswerOption
    {
        /// <summary>稳定标识（回写拓扑时按它分派）。</summary>
        public string Key { get; set; }

        /// <summary>按钮上的字。</summary>
        public string Label { get; set; }

        /// <summary>选它的后果（小字）。</summary>
        public string Note { get; set; }

        /// <summary>★ 是不是<b>当前生效</b>的那一项（界面据此高亮）。</summary>
        public bool IsCurrent { get; set; }

        public ICommand ChooseCommand { get; set; }

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>
    /// 第 2 步里<b>要用户拍板的一个问题</b>。
    ///
    /// ★★ 修前第 2 步是"10 行结论一次性铺开、整个面板零个可交互控件"，
    ///   而标题写着「承诺：不对就说」—— 用户唯一能做的动作是"按下一步"，那就是自问自答。
    ///   现在每一项要么给一组答案（<see cref="Options"/>），要么明确标成"只读"（<see cref="IsReadOnly"/>）。
    /// </summary>
    public sealed class WizardQuestionItem
    {
        /// <summary>Domain 层那一行的 Label —— 回写时按它分派（改文案要连回写逻辑一起改）。</summary>
        public string LineLabel { get; set; }

        /// <summary>问句（人话）。</summary>
        public string Prompt { get; set; }

        /// <summary>这个结论从哪来（"工位拓扑" / "控制器反馈" / "未设定"）。</summary>
        public string Source { get; set; }

        /// <summary>当前值的人话描述（即使没有选项也要显示，用户得知道现在是什么）。</summary>
        public string CurrentText { get; set; }

        /// <summary>风险提示（非空时界面用警示色）。</summary>
        public string Caution { get; set; }

        /// <summary>
        /// ★ false = 这一项<b>不该问</b>用户（例如控制器能力是设备决定），候选按钮整体置灰。
        ///
        /// ★ 它直接绑在候选区那一块的 <c>IsEnabled</c> 上，是 <see cref="WizardSceneLine.Editable"/>
        ///   唯一的消费点。修前 `Editable` 写进视图模型却没有任何控件读它 ——
        ///   那意味着"标着不可改"和"真的不可改"是两回事，全靠没人发现。
        /// </summary>
        public bool CanEdit { get; set; }

        /// <summary>
        /// 当前生效那一项的说明。
        /// ★ 只显示这一条、不把所有选项的说明都铺出来：五句长说明并排会变成一堵墙，
        ///   而用户当下只需要知道"我选中的这个意味着什么"。
        /// </summary>
        public string CurrentNote { get; set; }

        /// <summary>可选项（IsReadOnly = false 且非空时才是真问题）。</summary>
        public ObservableCollection<WizardAnswerOption> Options { get; private set; }

        public WizardQuestionItem()
        {
            Options = new ObservableCollection<WizardAnswerOption>();
        }
    }

    /// <summary>
    /// 特征候选预览的结果载荷（第 3 步「候选预览」→ 宿主画叠加）。
    /// ★ 不复用 <see cref="CalibVisualizationRequest"/>：那是"链路结果回挂"通道，
    ///   预览只是"找一次给你看"，两者画的东西、语义都不同 —— 混进同一份载荷迟早分叉。
    /// ★ 由原模板预览载荷泛化而来：圆点 / 十字 / 模板三类共用一个入口，
    ///   <see cref="FeaturePreviewPayload.FeatureTitle"/> 说明这次找的是什么。
    /// </summary>
    public sealed class FeaturePreviewPayload
    {
        /// <summary>这次预览找的是什么（选项的 PlainTitle，用于叠加标注与人话状态）。</summary>
        public string FeatureTitle;

        public bool Matched;

        /// <summary>命中位置（图像坐标；col = X，row = Y，与 Pixel 约定一致）。</summary>
        public double PixelX;
        public double PixelY;

        /// <summary>匹配分（0~100，提取器的口径）。</summary>
        public double ScorePercent;

        /// <summary>true = 这次走了降级路径（预览没有期望位置 → 全图搜必然算降级），结果仅供参考。</summary>
        public bool FellBack;

        /// <summary>人话说明（命中=详情，未命中=原因）。</summary>
        public string Message;

        /// <summary>模板训练框（模板预览时画出来让人知道"找的是这个东西"）。</summary>
        public bool HasRoi;
        public double RoiR1, RoiC1, RoiR2, RoiC2;

        /// <summary>参考半径（&gt;1 时宿主画参考圆，让人看见"它认为这个特征有多大"）。</summary>
        public double ReferenceRadiusPx;
    }

    /// <summary>
    /// ★★ 强引导向导（第 1~4 步 + 验收）。
    ///
    /// 三条降低心智模型的手段都落在这个类里（设计文档 §9.1）：
    ///   ① <b>术语换算</b>：界面文案只有"相机看得准不准""吸嘴转到哪"，
    ///      不出现 H / e / t / O / σ1 —— 换算表在 <see cref="WizardGoalCatalog"/>，
    ///      这里只负责选词，不负责判断；
    ///   ② <b>回显代替提问</b>：第 2 步把"我推导出的场景"整行列出（含"凭什么这么认为"），
    ///      用户只需点"对"或"不对，我改"，不问任何一个数值参数；
    ///   ③ <b>数字孪生</b>：第 3 步全程有进度 + 采样计数 + 最后一点的像素，
    ///      跑完立刻把 AR 反投影叠加画出来 —— "看见才算通过"。
    ///
    /// ★ 它不知道 HALCON 存在，也不知道显示面长什么样：
    ///   要画什么都通过 <see cref="VisualizationRequested"/> 交出去。
    /// </summary>
    public sealed class CalibWizardViewModel : ObservableObject
    {
        /// <summary>
        /// 向导自建模板的键（第 2.5 步示教产物）。
        /// ★ 固定键而不是每次换新键：重训 = 覆盖同一个键，"用模板"选项与 MarkSpec 都不用跟着换。
        /// </summary>
        public const string WizardTemplateKey = "WIZARD_TEMPLATE_1";

        private readonly IVisualCalibEnvironment _env;
        private readonly WizardCoordinator _coordinator;
        private readonly Dispatcher _dispatcher;

        /// <summary>模板训练门面（null = 这套装配里没有训练器，示教整块置灰并说明原因）。</summary>
        private readonly ITemplateTrainer _trainer;

        /// <summary>特征提取门面（预览用：sampleIndex 0 = 纯预览，不进任何采样集）。</summary>
        private readonly IFeatureExtractor _extractor;

        private bool _cancelRequested;
        private bool _isRunning;

        // ── 模板示教状态（框选 → 训练 → 预览）──
        //   ★ 帧在「框选」时就抓好并一路带着：训练必须用"画框那一帧"的原始数据，
        //     不能等点「训练」再重新抓一帧 —— 场景只要动过，练出来的就不是框里那个东西。
        private byte[] _templateFrameRaw;
        private int _templateFrameWidth;
        private int _templateFrameHeight;
        private bool _templateRoiPending;
        private double _templateR1, _templateC1, _templateR2, _templateC2;
        private string _templateStatusText =
            "① 按「框选」抓一帧 → ② 在左边图上拖一个框 → 按「训练」。训练完成后「用模板」选项自动启用。";
        private WizardFeatureItem _templateFeatureItem;

        /// <summary>候选预览卡的状态行（圆点/十字/模板共用一张卡）。</summary>
        private string _previewStatusText =
            "还没预览过。点「候选预览」在最新一帧上按当前特征与极性试找一次：命中画绿十字（青圈=参考半径），未命中给原因。";

        private int _stepIndex = 1;
        private WizardGoalItem _selectedGoal;
        private WizardFeatureItem _selectedFeature;
        private WizardScene _scene;

        /// <summary>
        /// 特征极性（Run 与预览构造 MarkSpec 都从这里取 —— 唯一来源）。
        /// ★ 默认亮标记 = 圆点主路径的出厂世界，与主项目行为对等。
        /// </summary>
        private MarkPolarity _selectedPolarity = MarkPolarity.BrightMarkOnDarkBackground;

        /// <summary>
        /// 第 2 步的<b>工作副本</b>拓扑。
        ///
        /// ★★ 为什么必须用副本：<c>ReadTopology()</c> 返回的是环境内部那一个实例（不是拷贝），
        ///   直接改它的话，用户**只是看了看、还没点确认**改动就已经生效了 ——
        ///   中途放弃就把环境悄悄改脏了。改动一律落在这里，确认时才整体写回。
        /// </summary>
        private CalibTopology _draft;

        private string _sceneSummary = "（进入第 2 步后自动推导）";
        private bool _sceneConfirmed;
        private string _planCardText = "（进入第 2 步后自动推导）";
        private string _chainPlanText = "—";
        private string _vaultText = "已经标好的：（还没跑过任何一条链）";
        private string _blockedReason = string.Empty;
        private bool _canRun;

        private double _progressPercent;
        private string _progressText = "尚未开始";
        private string _progressStep = "—";
        private string _lastPointText = "—";

        /// <summary>
        /// ★★ 操作反馈（"刚才那一下为什么没生效"）。**必须显示在动作区旁边、每步都看得见的地方**。
        ///
        /// 修前这些文字全都写进 <see cref="OutcomeText"/>，而 OutcomeText 只绑在第 5 步（验收）
        /// 那个面板里 ⇒ 用户在第 1~4 步点错按钮时，**界面一个字都不变**，看起来就是"点了没反应"。
        /// 这正是"用起来很懵"最直接的技术原因：反馈写了，但写在一个当前不可见的控件上。
        /// </summary>
        private string _noticeText = string.Empty;

        private string _outcomeText = "（尚未运行）";
        private string _acceptanceText = "（跑完第 4 步后在这里给结论）";
        private string _issueText = string.Empty;

        private WizardRunOutcome _outcome;

        /// <summary>★ 要画什么交出去（"画在哪"是宿主的决定）。</summary>
        public Action<CalibVisualizationRequest> VisualizationRequested;

        /// <summary>★ 采样期间"刚采到的这一帧"的通知（宿主去把它显示出来，实现"边跑边看"）。</summary>
        public Action<int, CalibObservation> FramePlanted;

        /// <summary>
        /// ★ 示教帧已抓好（宿主去显示）。训练 / 框选 / 预览看的都该是这一帧 —— 见字段注释。
        /// </summary>
        public Action<byte[], int, int> TemplateFrameCaptured;

        /// <summary>★ 进入 / 退出框选模式（宿主把它翻译成显示面的交互模式切换）。</summary>
        public Action<bool> TemplateRoiPickRequested;

        /// <summary>★ 特征候选预览结果就绪（宿主把它画成叠加：橙框=模板来源，绿十字=命中，青圈=参考半径）。</summary>
        public Action<FeaturePreviewPayload> FeaturePreviewReady;

        public CalibWizardViewModel(IVisualCalibEnvironment env, WizardCoordinator coordinator,
            ITemplateTrainer trainer, IFeatureExtractor extractor, Dispatcher dispatcher = null)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            if (coordinator == null)
            {
                throw new ArgumentNullException("coordinator");
            }

            _env = env;
            _coordinator = coordinator;
            _trainer = trainer;
            _extractor = extractor;
            _dispatcher = dispatcher;

            Goals = new ObservableCollection<WizardGoalItem>();
            SceneItems = new ObservableCollection<WizardSceneItem>();
            Questions = new ObservableCollection<WizardQuestionItem>();
            Features = new ObservableCollection<WizardFeatureItem>();
            MarkOverrides = new ObservableCollection<string>();
            StepRail = new ObservableCollection<WizardStepRailItem>();

            // ★★ 每个按钮都要能回答"我现在该不该亮"。
            //   修前这里 7 个命令**全部**用无 canExecute 的构造 ⇒ 任何时候都是亮的，
            //   包括"第 1 步就能点开始标定""没跑过就能点采用结果""没在跑也能点取消"。
            //   用户面对一排永远亮着的按钮，只能靠试错猜哪个是现在该点的 —— 这就是"懵"。
            //   ★ 判别逻辑一律复用下面那几个 Can* 属性（与 XAML 的 IsEnabled 是同一个判断，
            //     不会出现"按钮亮着但点了被拒"的两套口径）。
            NextStepCommand = new RelayCommand(() => GoNext(), () => CanGoNext);
            PrevStepCommand = new RelayCommand(() => GoPrev(), () => CanGoPrev);
            RunCommand = new RelayCommand(() => Run(), () => CanRun);
            CancelCommand = new RelayCommand(() => RequestCancel(), () => CanCancel);
            AdoptCommand = new RelayCommand(() => Adopt(), () => CanAdopt);
            ResetCommand = new RelayCommand(() => ResetAll(), () => CanReset);
            VisualizeCommand = new RelayCommand(() => RaiseVisualization(), () => CanVisualize);

            // ★ 模板示教：没有训练器时框选/训练 canExecute 恒假（界面置灰 + 状态栏说明原因）。
            TemplatePickCommand = new RelayCommand(() => TemplatePick(), () => CanTemplatePick);
            TemplateTrainCommand = new RelayCommand(() => TemplateTrain(), () => CanTemplateTrain);
            // ★ 候选预览与极性切换是通用能力（圆点/十字/模板共用同一个预览入口）。
            FeaturePreviewCommand = new RelayCommand(() => FeaturePreview(), () => CanFeaturePreview);
            UseBrightMarkCommand = new RelayCommand(
                () => SwitchPolarity(MarkPolarity.BrightMarkOnDarkBackground), () => CanUseBrightMarkButton);
            UseDarkMarkCommand = new RelayCommand(
                () => SwitchPolarity(MarkPolarity.DarkMarkOnBrightBackground), () => CanUseDarkMarkButton);

            BuildGoals();
            BuildFeatures();
            EnterStep(1);
            RefreshCommands();
        }

        #region 绑定属性

        public ObservableCollection<WizardGoalItem> Goals { get; private set; }

        /// <summary>
        /// ★★ 第 2 步"要用户拍板的问题"（有候选答案的那些）。
        ///
        /// 与 <see cref="SceneItems"/> 的分工：
        ///   · <c>Questions</c> = 会影响方案的、且**有选项可以选**的 → 渲染成一组可点按钮；
        ///   · <c>SceneItems</c> = 其余（设备能力、系统已知量）→ 只读展示，写明来源。
        /// 两者加起来就是推导器给出的全部行。修前只有一个集合，于是"该问的"和"只该看的"
        /// 混在同一片纯文本里，用户无从分辨哪些能改、哪些改了也没用。
        /// </summary>
        public ObservableCollection<WizardQuestionItem> Questions { get; private set; }

        public ObservableCollection<WizardSceneItem> SceneItems { get; private set; }

        public ObservableCollection<WizardFeatureItem> Features { get; private set; }

        /// <summary>预留：把"用户手改的拓扑字段"记下来（第 2 步的"不对，我改"）。</summary>
        public ObservableCollection<string> MarkOverrides { get; private set; }

        /// <summary>
        /// ★★ 步骤导轨的数据源（宿主界面左侧竖排的步骤条）。
        ///
        /// 为什么要有它：修前的"我在第几步"只存在于内容卡标题的一行小字里，
        /// 而屏幕的大头是图像 —— 用户随时都回答不出"我在哪、还剩几步"。
        /// 原型图（工业视觉标定向导桌面端.png）把这一条做成**常驻左侧的竖排导轨**：
        /// 位置感不随滚动丢失。这里提供数据 + 点击命令，画法交给宿主。
        /// </summary>
        public ObservableCollection<WizardStepRailItem> StepRail { get; private set; }

        public ICommand NextStepCommand { get; private set; }

        public ICommand PrevStepCommand { get; private set; }

        public ICommand RunCommand { get; private set; }

        public ICommand CancelCommand { get; private set; }

        public ICommand AdoptCommand { get; private set; }

        public ICommand ResetCommand { get; private set; }

        public ICommand VisualizeCommand { get; private set; }

        /// <summary>模板示教①：抓一帧并进入框选模式。</summary>
        public ICommand TemplatePickCommand { get; private set; }

        /// <summary>模板示教②：用画框那一帧训练模板。</summary>
        public ICommand TemplateTrainCommand { get; private set; }

        /// <summary>
        /// 通用「候选预览」：按当前选中的特征 + 当前极性，在最新一帧上真跑一次提取并交出去画。
        /// ★ 原来只有模板有预览（TemplatePreviewCommand），圆点/十字的提示只能写"还没做"；
        ///   现在三类共用这一个入口，口径在 <see cref="CanFeaturePreview"/>。
        /// </summary>
        public ICommand FeaturePreviewCommand { get; private set; }

        /// <summary>极性切换：暗底亮点（默认主路径，不反色）。</summary>
        public ICommand UseBrightMarkCommand { get; private set; }

        /// <summary>极性切换：亮底暗点（提取前整图反色，只对阈值类路径生效）。</summary>
        public ICommand UseDarkMarkCommand { get; private set; }

        /// <summary>当前是第几步（1~5）。</summary>
        public int StepIndex
        {
            get { return _stepIndex; }
            private set
            {
                if (SetProperty(ref _stepIndex, value))
                {
                    OnPropertyChanged("StepTitleText");
                    OnPropertyChanged("StepProgressText");
                    OnPropertyChanged("NextActionText");
                    OnPropertyChanged("IsStep1");
                    OnPropertyChanged("IsStep2");
                    OnPropertyChanged("IsStep3");
                    OnPropertyChanged("IsStep4");
                    OnPropertyChanged("IsStep5");
                    RefreshCommands();
                }
            }
        }

        // 分步可见性。★ 用 5 个布尔而不是"值转换器 + 比较"：WPF 内置转换器不支持
        // 带参数的比较，自写一个转换器又要在 XAML 里多挂一份资源；
        // 而"哪个面板现在可见"本来就是视图模型该回答的问题。

        public bool IsStep1
        {
            get { return _stepIndex == 1; }
        }

        public bool IsStep2
        {
            get { return _stepIndex == 2; }
        }

        public bool IsStep3
        {
            get { return _stepIndex == 3; }
        }

        public bool IsStep4
        {
            get { return _stepIndex == 4; }
        }

        public bool IsStep5
        {
            get { return _stepIndex >= 5; }
        }

        /// <summary>有没有"现在跑不了"的原因（界面据此决定要不要显示那行警示）。</summary>
        public bool HasBlockedReason
        {
            get { return !string.IsNullOrEmpty(_blockedReason); }
        }

        /// <summary>
        /// 当前步骤名（**故意不带"第几步"**）。
        /// ★★ 修前这里写"第 2.5 步：确认我盯的是哪个特征"，而右上角 <see cref="StepProgressText"/>
        ///   同时显示"3 / 5" —— 同一个界面上两个数字互相矛盾（2.5 还是 3？一共 4 步还是 5 步？）。
        ///   根因是步骤机后来插了一步却只改了标题、没改总数。
        ///   现在编号只在 StepProgressText 出现一处，标题只回答"这一步要做什么"。
        /// </summary>
        public string StepTitleText
        {
            get
            {
                switch (_stepIndex)
                {
                    case 1: return "告诉我在标定什么";
                    case 2: return "确认我理解的场景";
                    case 3: return "确认我盯的是哪个特征";
                    case 4: return "自动跑（边跑边看）";
                    default: return "验收（看见才算通过）";
                }
            }
        }

        /// <summary>「第 N 步 / 共 5 步」—— 全界面唯一给出步骤编号的地方。</summary>
        public string StepProgressText
        {
            get { return string.Format(CultureInfo.InvariantCulture, "第 {0} 步 / 共 5 步", _stepIndex); }
        }

        /// <summary>
        /// ★★ 「现在该做什么」——**每一步都显示、永远有内容**的一句话。
        ///
        /// 为什么必须有：这一版的向导有 7 个按钮，而它们修好之前全是亮的；
        /// 即使现在会按状态置灰，用户仍然要自己推断"剩下的按钮里先点哪个"。
        /// 把"这一步的目标动作"直接写出来，是把"猜"变成"读"。
        /// （它不替代按钮置灰：一个说"该做什么"，一个说"现在能不能做"。）
        /// </summary>
        public string NextActionText
        {
            get
            {
                if (_isRunning)
                {
                    return "正在跑。全程可以看进度；要中止就按「取消」（会收尾，不会留下悬空运动）。";
                }

                switch (_stepIndex)
                {
                    case 1:
                        return "现在该做：在下面选一个目标。第一次用就选「相机看得准不准」——"
                             + "它是其他几项的前置，也是判断工具本身有没有问题的第一关。"
                             + "（选完按「下一步」，中间要过一遍场景确认才能开跑。）";
                    case 2:
                        return "现在该做：对下面几个问题各选一项（带「当前」标记的就是系统里的值，"
                             + "没问题就直接按「下一步」）。按「下一步」= 确认，改过的值会写回去。";
                    case 3:
                        return "现在该做：确认我盯的是哪种特征，然后按「下一步」。";
                    case 4:
                        return SceneConfirmed
                            ? "现在该做：按「开始标定」。需要真机（或先按顶栏「初始化仿真环境」）。"
                            : "现在还不能开跑：先回第 2 步确认场景 —— 那是开跑前的最后一道确认。"
                              + "（按「上一步」就能回去。）";
                    default:
                        return "现在该做：先在左边看叠加图（绿十字与红圈重不重合），再决定要不要「采用结果」。";
                }
            }
        }

        /// <summary>操作反馈（"刚才那一下为什么没生效"）；空 = 没出过问题。</summary>
        public string NoticeText
        {
            get { return _noticeText; }
            private set
            {
                if (SetProperty(ref _noticeText, value))
                {
                    OnPropertyChanged("HasNotice");
                }
            }
        }

        public bool HasNotice
        {
            get { return !string.IsNullOrEmpty(_noticeText); }
        }

        /// <summary>把一条"这次操作没生效"的原因写进<b>每步都看得见</b>的那行提示里。</summary>
        private void Notice(string text)
        {
            NoticeText = text;
        }

        /// <summary>
        /// 按钮可用性 —— 全项目**唯一**的"现在能不能点"口径：
        ///   XAML 绑 <c>IsEnabled</c>、命令的 <c>canExecute</c> 也用它，两边不可能分叉。
        /// </summary>
        public bool CanGoPrev
        {
            get { return StepIndex > 1 && !IsRunning; }
        }

        public bool CanGoNext
        {
            get { return StepIndex < 5 && !IsRunning; }
        }

        /// <summary>只有正在跑的时候才谈得上"取消"。</summary>
        public bool CanCancel
        {
            get { return IsRunning; }
        }

        /// <summary>跑过（不论成没成）才有东西可重画。</summary>
        public bool CanVisualize
        {
            get { return _outcome != null; }
        }

        /// <summary>只有<b>跑通了</b>才谈得上采用 —— 失败的结果没有可复用的量。</summary>
        public bool CanAdopt
        {
            get { return _outcome != null && _outcome.Success; }
        }

        public bool CanReset
        {
            get { return !IsRunning; }
        }

        /// <summary>状态一变就把所有"能不能点"重新广播一遍（与 <see cref="RefreshRunReadiness"/> 分工：那个算"能不能跑"，这里广播全部）。</summary>
        private void RefreshCommands()
        {
            OnPropertyChanged("CanGoPrev");
            OnPropertyChanged("CanGoNext");
            OnPropertyChanged("CanCancel");
            OnPropertyChanged("CanVisualize");
            OnPropertyChanged("CanAdopt");
            OnPropertyChanged("CanReset");
            OnPropertyChanged("NextActionText");
            OnPropertyChanged("ShowGenericNext");

            // ★ 分步可见性随状态一起广播（步骤切换 / 开跑 / 跑完都会走到这里）。
            OnPropertyChanged("ShowNextButton");
            OnPropertyChanged("ShowRunButton");
            OnPropertyChanged("ShowCancelButton");
            OnPropertyChanged("ShowVisualizeButton");
            OnPropertyChanged("ShowAdoptButton");
            OnPropertyChanged("ShowResetButton");

            // ★ IsRunning 变了导轨项的"能不能点"也要跟上（EnterStep 只覆盖步骤切换）。
            RebuildStepRail();

            // ★ 极性按钮的可用性跟着 IsRunning / 选中特征走 —— 状态一变一起广播，
            //   否则"跑起来之后极性按钮还亮着"（点了会被 SwitchPolarity 拦，但界面该先灰掉）。
            OnPropertyChanged("CanUseBrightMarkButton");
            OnPropertyChanged("CanUseDarkMarkButton");
            OnPropertyChanged("PolarityText");
        }

        /// <summary>导轨上的短标题（一到四个字，详见原型图：导轨只回答"我在哪"）。</summary>
        private static string ShortTitleOf(int step)
        {
            switch (step)
            {
                case 1: return "选目标";
                case 2: return "确认场景";
                case 3: return "确认特征";
                case 4: return "自动跑";
                default: return "验收";
            }
        }

        /// <summary>
        /// 重建步骤导轨。
        ///
        /// ★ IsCurrent / IsDone 是项上的**普通属性**（不是依赖属性），
        ///   步骤一变就必须整体重建 —— 只改字段不发通知，界面会停在上一次的位置。
        /// ★ 每次都给**新的命令实例**：RelayCommand 的 canExecute 闭包虽然读的是
        ///   实时属性（CommandManager 会自动重查），但重建能保证项状态与命令口径同源。
        /// </summary>
        private void RebuildStepRail()
        {
            if (StepRail == null)
            {
                return;
            }

            StepRail.Clear();
            for (int step = 1; step <= 5; step++)
            {
                // ★ 闭包捕获局部变量（不是循环变量）：每个命令各自记着自己的序号，
                //   不会被下一轮循环改掉 —— 按 3 跳去 5 的经典成因。
                int index = step;
                StepRail.Add(new WizardStepRailItem
                {
                    Index = index,
                    ShortTitle = ShortTitleOf(index),
                    IsCurrent = index == _stepIndex,
                    IsDone = index < _stepIndex,

                    // ★★ 导轨只放行「往回走」。往前的步骤必须经「下一步」/
                    //   「确认，就这样跑」逐步走 —— 中间的确认（把工作副本写回环境）
                    //   不能被导轨绕过去，否则"确认后再动手"就成了摆设。
                    //   点不动的项会被界面置灰（canExecute 是唯一口径）。
                    GoCommand = new RelayCommand(
                        () => RequestGoToStep(index),
                        () => index < _stepIndex && !IsRunning)
                });
            }
        }

        /// <summary>
        /// 导轨点击：只放行「往回走」，并如实说出为什么不能往前点。
        ///
        /// ★★ "能往回、不能往前跳"必须**说出来**：静默忽略 = 点了没反应，
        ///   正是这个项目反复修的那类缺陷。往前走的正确路径写在提示里。
        /// </summary>
        public void RequestGoToStep(int step)
        {
            if (IsRunning)
            {
                Notice("正在跑。要离开这一步，先按「取消」（会安全收尾）。");
                return;
            }

            if (step >= _stepIndex)
            {
                Notice("还没走过的步骤不能从导轨直接跳过去 —— 按「下一步」"
                     + "（第 2 步是「确认，就这样跑」）一步步走，中间的确认不能省。");
                return;
            }

            NoticeText = string.Empty;
            EnterStep(step);
        }

        /// <summary>
        /// 第 2 步要不要显示通用的「下一步」。
        ///
        /// ★ 第 2 步的"往下走"由方案卡上的「确认，就这样跑」承担 ——
        ///   同屏放两个都能往下走、名字却不同的按钮，用户一定会问"这俩有啥区别"，
        ///   然后挑一个点，心里没底。只留一个含义明确的。
        /// </summary>
        public bool ShowGenericNext
        {
            get { return _stepIndex != 2; }
        }

        /// <summary>
        /// ★★ 分步可见性 —— 动作区只出现「这一步用得上」的按钮。
        ///
        /// 修前 7 个按钮全部常驻（只是置灰）：用户每一屏都要在 7 个里找 1 个能点的，
        /// 「焦点」就是被这一排按钮和图像一起分掉的。原型图给的原则是
        /// **每一步一个主操作** —— 落到界面就是按步骤收敛可见性，
        /// 置灰继续保留（那是「能不能点」的口径，可见性是「要不要出现」的口径，两层不冲突）。
        ///
        /// ★ 这些属性与 IsStepN 同源（都从 _stepIndex 算），不另存状态 —— 不会分叉。
        /// </summary>
        public bool ShowNextButton
        {
            get { return ShowGenericNext && !IsStep5; }
        }

        public bool ShowRunButton
        {
            get { return IsStep4; }
        }

        public bool ShowCancelButton
        {
            get { return IsStep4; }
        }

        public bool ShowVisualizeButton
        {
            get { return IsStep5; }
        }

        public bool ShowAdoptButton
        {
            get { return IsStep5; }
        }

        /// <summary>清空重来：第 1 步（没跑就要从头来）与第 5 步（跑完想推倒重来）。</summary>
        public bool ShowResetButton
        {
            get { return IsStep1 || IsStep5; }
        }

        public WizardGoalItem SelectedGoal
        {
            get { return _selectedGoal; }
            set
            {
                if (SetProperty(ref _selectedGoal, value))
                {
                    OnPropertyChanged("GoalHintText");

                    // ★ 选完目标**立刻**把"这次会跑哪几步 + 能不能跑"算出来。
                    //   修前这几项只在进入第 2 步 / 第 4 步时才刷新，于是第 1 步选完目标后
                    //   顶上那行仍然写着「—」、被拦的原因也不出现 —— 又一次"操作了没反应"。
                    RefreshRunReadiness();
                }
            }
        }

        public string GoalHintText
        {
            get
            {
                // ★ 修前这句写的是「选一个目标 —— 你不需要知道 H / e / t / O 这些符号」。
                //   它本意是安慰，实际是把四个符号**印在了界面上** —— 恰好违反自己那条
                //   "界面不出现实现符号"的规则（这条规则是有断言体的，只是当时只扫了目标卡片）。
                //   改成讲清"你只需要说什么"，不再列举任何符号。
                return _selectedGoal == null
                    ? "选一个目标 —— 你只需要说清要标什么，不必懂背后的数学。"
                    : _selectedGoal.PlainTitle + "：" + _selectedGoal.PlainCaption;
            }
        }

        public string SceneSummary
        {
            get { return _sceneSummary; }
            private set { SetProperty(ref _sceneSummary, value); }
        }

        public bool SceneConfirmed
        {
            get { return _sceneConfirmed; }
            private set
            {
                if (SetProperty(ref _sceneConfirmed, value))
                {
                    // 第 4 步那句「现在该做什么」取决于确认过没有（未确认要先回第 2 步）；
                    // 方案卡上的状态句也取决于它。
                    OnPropertyChanged("NextActionText");
                    OnPropertyChanged("SceneConfirmText");
                }
            }
        }

        /// <summary>
        /// 方案卡上那句"这次算不算数了"。
        ///
        /// ★ 把"确认"这件事**说成一个状态**，而不是只让按钮亮一下：
        ///   用户改完一项之后必须能立刻看出"刚才那次确认已经被我改废了、要再来一次"，
        ///   否则他会以为改完还是确认着的状态，然后带着改动直接开跑。
        /// </summary>
        public string SceneConfirmText
        {
            get
            {
                if (_sceneConfirmed)
                {
                    return "已确认 ✔　这张卡上的设定会被真的写回去并用来开跑。"
                         + "（一动上面任何一项，确认就作废，要重新确认。）";
                }

                return "还没确认　—— 对上面每一条都没意见的话，按下面「确认，就这样跑」；"
                     + "有一项不对，就点它另一个候选（改完这张卡会立刻跟着变）。"
                     + "在确认之前，「开始标定」是灰的。";
            }
        }

        public WizardFeatureItem SelectedFeature
        {
            get { return _selectedFeature; }
            set
            {
                if (SetProperty(ref _selectedFeature, value))
                {
                    OnPropertyChanged("FeatureHintText");

                    // ★ 选到「用模板」⇒ 极性按钮整体置灰、说明跟着换（模板不吃极性）。
                    OnPropertyChanged("CanUseBrightMarkButton");
                    OnPropertyChanged("CanUseDarkMarkButton");
                    OnPropertyChanged("PolarityText");
                }
            }
        }

        /// <summary>
        /// 特征类型下方那句提示。
        /// ★★ 修前这里写死一句「点『用当前视野预览』可以就地调参，看到候选与拟合圆再定」，
        ///   而<b>那个按钮在整个程序里根本不存在</b>（全仓搜"视野预览"只有这一句文案）。
        ///   引导用户去点一个不存在的东西，是"懵"的典型来源 —— 他会找一圈，然后怀疑自己看漏了。
        ///   ⇒ 现在只指向真实存在的入口：候选预览（通用）与极性切换（亮底暗点）。
        /// </summary>
        public string FeatureHintText
        {
            get
            {
                if (_selectedFeature == null)
                {
                    return "选一种特征。";
                }

                return _selectedFeature.PlainTitle + "：" + _selectedFeature.PlainCaption
                     + "　（点「候选预览」可在最新一帧上按当前极性试找一次；Mark 是亮底上的暗点就先在下面切「暗标记」。）";
            }
        }

        /// <summary>"这次一共会跑哪几条链"（人话）。</summary>
        public string ChainPlanText
        {
            get { return _chainPlanText; }
            private set { SetProperty(ref _chainPlanText, value); }
        }

        /// <summary>
        /// 方案卡正文（"这次拿什么参数跑哪几步"）。内容由 <see cref="RefreshPlanCard"/> 组装。
        /// </summary>
        public string PlanCardText
        {
            get { return _planCardText; }
            private set { SetProperty(ref _planCardText, value); }
        }

        /// <summary>
        /// 用户在方案卡上按「有一项要改」。
        ///
        /// ★ 这个按钮必须**真的做一件事**（把"该看哪儿、改完会怎样"说清楚），
        ///   不能只是把用户领到一个空白处 —— 那又变成本次现场反馈里那个"点了没反应"。
        ///   滚动到问题区由视图负责（视图才知道滚动条在哪），这里只负责说话。
        /// </summary>
        public void RequestSceneRevision()
        {
            Notice("点上面每一行里的另一个候选。改完这张卡会立刻跟着变，"
                 + "同时那次确认会作废（要再按一次「确认，就这样跑」）。");
        }

        /// <summary>"已经标好了什么"徽章。</summary>
        public string VaultText
        {
            get { return _vaultText; }
            private set { SetProperty(ref _vaultText, value); }
        }

        /// <summary>非空 = 现在跑不了，以及为什么（按钮据此置灰）。</summary>
        public string BlockedReasonText
        {
            get { return _blockedReason; }
            private set
            {
                if (SetProperty(ref _blockedReason, value))
                {
                    OnPropertyChanged("HasBlockedReason");
                }
            }
        }

        public bool CanRun
        {
            get { return _canRun; }
            private set { SetProperty(ref _canRun, value); }
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    // 跑起来/跑完 ⇔ 「取消」与「开始标定」互换可用；顺带让上方那句引导改口。
                    RefreshCommands();

                    // ★ 同样要**重算**（CanRun 是字段属性，广播只会把旧值再报一遍）：
                    //   跑的过程中要灭掉，跑完要重新亮起来。
                    RefreshRunReadiness();
                }
            }
        }

        /// <summary>0~100（进度条直接绑定）。</summary>
        public double ProgressPercent
        {
            get { return _progressPercent; }
            private set { SetProperty(ref _progressPercent, value); }
        }

        public string ProgressText
        {
            get { return _progressText; }
            private set { SetProperty(ref _progressText, value); }
        }

        public string ProgressStep
        {
            get { return _progressStep; }
            private set { SetProperty(ref _progressStep, value); }
        }

        public string LastPointText
        {
            get { return _lastPointText; }
            private set { SetProperty(ref _lastPointText, value); }
        }

        public string OutcomeText
        {
            get { return _outcomeText; }
            private set { SetProperty(ref _outcomeText, value); }
        }

        public string AcceptanceText
        {
            get { return _acceptanceText; }
            private set { SetProperty(ref _acceptanceText, value); }
        }

        public string IssueText
        {
            get { return _issueText; }
            private set { SetProperty(ref _issueText, value); }
        }

        public bool HasOutcome
        {
            get { return _outcome != null; }
        }

        #endregion

        #region 第 1 / 2 / 2.5 步

        private void BuildGoals()
        {
            Goals.Clear();
            List<WizardGoalOption> all = WizardGoalCatalog.Build();
            for (int i = 0; i < all.Count; i++)
            {
                WizardGoalOption o = all[i];
                string reason = _coordinator.BlockedReason(o.Goal);
                Goals.Add(new WizardGoalItem
                {
                    Goal = o.Goal,
                    PlainTitle = o.PlainTitle,
                    PlainCaption = o.PlainCaption,

                    // ★ "能不能跑"只有一处判断（WizardCoordinator），界面只负责显示。
                    //   否则会出现"界面说能跑、一点就报错"。
                    IsEnabled = string.IsNullOrEmpty(reason),
                    DisabledReason = reason
                });
            }

            // ★ 默认选**第一个能跑的**，而不是永远选第一个。
            //   为什么：界面（VctChoiceItemStyle）现在把 IsEnabled 接到了容器上，
            //   被禁用的项是灰的、选不中。若默认选中的恰好是禁用项，就会出现
            //   "界面上什么都没选中，而「开始标定」却亮着" —— 两边说的不是同一件事，
            //   用户点下去只会撞到下一层的拒绝。
            //   ★ 一个能跑的都没有时仍然选第一项（让"为什么不能跑"那句话有地方显示），
            //     而不是留空 —— 空着就又是一片"什么都没显示出来"。
            _selectedGoal = null;
            for (int i = 0; i < Goals.Count; i++)
            {
                if (Goals[i].IsEnabled)
                {
                    _selectedGoal = Goals[i];
                    break;
                }
            }

            if (_selectedGoal == null && Goals.Count > 0)
            {
                _selectedGoal = Goals[0];
            }

            OnPropertyChanged("SelectedGoal");
            OnPropertyChanged("GoalHintText");
        }

        private void BuildFeatures()
        {
            Features.Clear();
            Features.Add(new WizardFeatureItem
            {
                Kind = FeatureKind.CircleMark,
                PlainTitle = "圆点",
                PlainCaption = "最常用：阈值分割 + 圆度筛选 + 亚像素圆拟合"
            });
            Features.Add(new WizardFeatureItem
            {
                Kind = FeatureKind.CrossMark,
                PlainTitle = "十字",
                PlainCaption = "骨架 + 直线对求交；适合细线交叉的特征"
            });
            // ★ 「用模板」能不能选，取决于示教做没做完（trainer 里有没有那个键）。
            //   训练完成后 RefreshTemplateAvailability() 会把这一项整体替换掉（让 ListBox 重建容器）。
            _templateFeatureItem = new WizardFeatureItem
            {
                Kind = FeatureKind.TemplateMatch,
                PlainTitle = "用模板",
                PlainCaption = "没有 Mark 时用：先框选 + 训练，之后按相似度找"
            };
            FillTemplateFeatureItem(_templateFeatureItem);
            Features.Add(_templateFeatureItem);

            _selectedFeature = Features[0];
            OnPropertyChanged("SelectedFeature");
        }

        /// <summary>按当前示教状态填充「用模板」选项的可选性（BuildFeatures 与训练后刷新共用同一份口径）。</summary>
        private void FillTemplateFeatureItem(WizardFeatureItem item)
        {
            if (item == null)
            {
                return;
            }

            if (HasTemplate)
            {
                item.IsEnabled = true;
                item.DisabledReason = null;
                item.PlainCaption = "模板「" + WizardTemplateKey + "」已就绪，跑链时按相似度找它";
            }
            else
            {
                item.IsEnabled = _trainer != null;
                item.DisabledReason = _trainer == null
                    ? "这套装配里没有模板训练器，用不了模板匹配。"
                    : "还没示教。用下面的「框选 → 训练」教一个，教完这项自动可用。";
            }
        }

        private void ReflectScene()
        {
            // ★★ 用**工作副本**推导，不是环境里那一个：用户的改动先落在这里，
            //   他点"下一步"（= 确认）时才整体写回。这样"改了但没确认 / 中途放弃"
            //   不会污染环境 —— 而 ReadTopology() 返回的正是环境内部那个实例。
            if (_draft == null)
            {
                CalibTopology live = _env.ReadTopology();
                _draft = live == null ? new CalibTopology() : live.Clone();
            }

            var req = new SceneInferRequest
            {
                Topology = _draft,
                Goal = _selectedGoal == null ? WizardGoal.CameraAccuracy : _selectedGoal.Goal,
                FeatureKind = _selectedFeature == null ? FeatureKind.CircleMark : _selectedFeature.Kind,

                // ★ 用真实示教状态（修前写死 false：练完模板第 2 步仍然显示"尚未示教"）。
                HasTemplate = HasTemplate,
                MmPerPixel = _coordinator.HandEyeKnown ? ResolveMmPerPixel() : 0.0,
                ImageWidthPx = _env.Camera == null ? 0 : _env.Camera.Width,
                ImageHeightPx = _env.Camera == null ? 0 : _env.Camera.Height,
                ReachCheckSupported = true,
                RotationCenterKnown = _coordinator.RotationCenterKnown,

                // ★ 向导会<b>自动补齐</b>前置链（先跑「吸嘴转到哪」再跑「吸嘴偏了多少」），
                //   所以"还没有旋转中心"在这里不是障碍，只是"这次会多做一步"。
                CanAutoFillPrerequisites = true,
                RotateAxisTravelDeg = 0.0
            };

            _scene = SceneInference.Infer(req);

            // ── 分流：有候选答案的 ⇒ 问题（可点）；其余 ⇒ 只读信息 ──
            //   修前只有一个集合、一片纯文本，"该问的"和"只该看的"混在一起，
            //   用户无从分辨哪些能改、哪些改了也没用。
            Questions.Clear();
            SceneItems.Clear();
            for (int i = 0; i < _scene.Lines.Count; i++)
            {
                WizardSceneLine l = _scene.Lines[i];

                if (l.Options != null && l.Options.Count > 0)
                {
                    Questions.Add(BuildQuestion(l));
                    continue;
                }

                // ★ 第 2 步不重复展示"我盯的特征"：第 3 步整页就在问它，
                //   而这里显示的是**还没选之前**的默认值 —— 把默认值当成"已决定的场景"
                //   摆出来，正是第 1 步那个"系统替我拍板了"的观感。
                //   标签取契约常量，不写字面量（见 WizardSceneLabels 的注释）。
                if (string.Equals(l.Label, WizardSceneLabels.Feature, StringComparison.Ordinal))
                {
                    continue;
                }

                SceneItems.Add(new WizardSceneItem
                {
                    Label = l.Label,
                    Value = l.Value,
                    Source = l.Source,
                    Known = l.Known,
                    Caution = l.Caution,
                    Editable = l.Editable,

                    // ★ 让"这一项该由谁定"可见（见 WizardSceneItem.Hint 的注释）。
                    Hint = l.Editable
                        ? "这一项本该你拍板 —— 手工改拓扑的入口还没做，现在只能按上面这个值跑。"
                        : "这一项由工位 / 设备决定，不用你选。"
                });
            }

            string text = _scene.ToText();
            if (_scene.HasBlocker)
            {
                text += Environment.NewLine + "⚠ 上面有硬性障碍项 —— 不解决就跑不通。";
            }

            SceneSummary = text;
            RefreshPlanCard();

            // ★ 这里**故意不动** SceneConfirmed。
            //   从第 3 步退回来"看一眼"不该把确认清掉 —— 否则用户会发现
            //   "我只是翻回去看一眼，回来就不能开跑了"，然后从此不再做检查。
            //   清确认只发生在真的改了东西时（见 ApplyAnswer）。
        }

        /// <summary>
        /// 方案卡：**"这次到底会拿什么参数、跑哪几步"** 的最后一次回执。
        ///
        /// ★ 它存在的理由：改完全都落在一堆按钮的高亮上，"我改的到底生效了没有"
        ///   仍然要靠用户自己拼。把当前生效的全套设定压成一句话摆在确认按钮旁边，
        ///   用户确认的对象就不再是"我猜的那些"，而是"屏幕上这一行字"。
        /// ★ 内容全部来自 <see cref="Questions"/> 的 CurrentText（= 推导器读工作副本算出来的），
        ///   **不另算一遍**：另算一遍就是两份状态，迟早分叉。
        /// </summary>
        private void RefreshPlanCard()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(ChainPlanText);

            if (Questions.Count > 0)
            {
                sb.Append(Environment.NewLine).Append("按这些设定跑：");
                for (int i = 0; i < Questions.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append("　·　");
                    }

                    sb.Append(Questions[i].LineLabel).Append(" = ").Append(Questions[i].CurrentText);
                }
            }

            if (_scene != null && _scene.HasUnknown)
            {
                sb.Append(Environment.NewLine)
                  .Append("⚠ 上面标着「未知」的项，程序会按最保守的方式处理（不是猜一个值）。");
            }

            PlanCardText = sb.ToString();
        }

        /// <summary>把一条"有候选答案"的行包成界面能渲染、能点的对象。</summary>
        private WizardQuestionItem BuildQuestion(WizardSceneLine line)
        {
            var q = new WizardQuestionItem
            {
                LineLabel = line.Label,
                Prompt = QuestionPromptOf(line.Label),
                Source = line.Source,
                CurrentText = line.Value,
                Caution = line.Caution,

                // ★ Editable = false 的项根本不该问；万一推导器给了"带候选却标着不可改"的行，
                //   界面至少要真的置灰，而不是给出按钮让用户点了没反应。
                CanEdit = line.Editable
            };

            for (int i = 0; i < line.Options.Count; i++)
            {
                WizardChoiceOption o = line.Options[i];

                // ★ 只取"当前生效那一项"的说明，不把所有候选的说明都铺出来：
                //   五句长说明并排会变成一堵墙，而用户当下只需要知道
                //   "我选中的这个意味着什么"。
                if (o.Recommended)
                {
                    q.CurrentNote = o.Note;
                }

                // ★ 闭包捕获局部变量（不是循环变量）：这样每个按钮各自记着自己的
                //   行与项，不会被后一轮循环改掉 —— 按错项是"点 A 生效 B"的经典成因。
                string lineLabel = line.Label;
                string key = o.Key;

                q.Options.Add(new WizardAnswerOption
                {
                    Key = o.Key,
                    Label = o.Label,
                    Note = o.Note,

                    // ★ "当前生效"直接取推导器给的 Recommended —— 它读的就是工作副本，
                    //   于是用户一改、重新推导之后高亮自然跟着走。
                    //   不另存一份"选中项"状态：两份状态一定会分叉，
                    //   然后界面上高亮的和实际用的是两回事。
                    IsCurrent = o.Recommended,
                    ChooseCommand = new RelayCommand(() => ApplyAnswer(lineLabel, key))
                });
            }

            return q;
        }

        /// <summary>
        /// 把行标签翻译成人话问句。
        /// ★ 实现在 <see cref="SceneAnswers.PromptOf"/>（纯代数层）——
        ///   理由同 <see cref="ApplyAnswer"/>："不认识的标签原样返回"那条分支
        ///   留在这一层是走不到的，搬到那儿才能被断言钉住。
        /// </summary>
        private static string QuestionPromptOf(string lineLabel)
        {
            return SceneAnswers.PromptOf(lineLabel);
        }

        /// <summary>
        /// ★★ 用户点了一个答案：改工作副本 → 立刻重新推导。
        ///
        /// 为什么"立刻重推"而不是"等他点确认再重推"：
        ///   他需要当场看见自己这一改的后果（比如把相机改成"固定在旁边"，
        ///   下一行"相机随 Z 升降"就该消失）。等到确认后才变，等于让他盲改。
        ///
        /// ★ 「标签 → 字段」的分派**不在这里**，在 <see cref="SceneAnswers.Apply"/>。
        ///   理由是那条 <c>default</c>（"这个标签我没接上"）留在这一层永远走不到 ——
        ///   候选只可能来自推导器已认识的标签 —— 于是它就成了"因为我没走到，所以它没问题"。
        ///   搬到纯代数层之后，自检可以直接拿一个不认识的标签调它、断言它不静默。
        /// </summary>
        private void ApplyAnswer(string lineLabel, string optionKey)
        {
            if (_draft == null)
            {
                return;
            }

            SceneAnswerOutcome outcome = SceneAnswers.Apply(_draft, lineLabel, optionKey);

            if (outcome == SceneAnswerOutcome.NeedCurrentPosition)
            {
                // 读当前位置是环境的事，纯代数层不许碰环境（ACL）⇒ 由这里补上。
                if (!SetBasePosFromCurrent())
                {
                    return;   // 读当前位置失败，原因已经写给用户了
                }
            }
            else if (outcome == SceneAnswerOutcome.NotSupported)
            {
                // ★★ 不认识的项必须**说出来**。静默忽略 = 用户点了没反应，
                //   正是这次现场反馈"很懵"的同一形状（承诺了交互、实际没有）。
                Notice("这一项（" + lineLabel + "）还没有接上，选了不会生效 —— 现在跑的还是系统里的值。");
                return;
            }
            else if (outcome == SceneAnswerOutcome.BadOption)
            {
                // ★ 键不认识同样是"说出来的"缺陷：多半是候选列表被改过而回写没跟上。
                //   静默按老值继续 = 界面高亮跳到新项、实际参数没动 —— 最难查的一类。
                Notice("这个选项（" + optionKey + "）没被认出来，这次改动没有生效 —— "
                     + "请把这一条报出来（多半是候选列表与回写逻辑不同步）。");
                return;
            }

            // 改过了 ⇒ 之前那次确认作废，必须重新确认（否则"确认"就成了橡皮图章）。
            SceneConfirmed = false;
            NoticeText = string.Empty;

            // 立刻重推：让用户当场看到自己这一改的后果。
            ReflectScene();

            // ★★ 这里必须**重算**，不能只 OnPropertyChanged("CanRun")。
            //   CanRun 是字段属性（_canRun），广播只是把**旧值**再报一遍。
            //   而刚把 SceneConfirmed 置成 false ⇒ 若不重算，界面上的「开始标定」
            //   会留在他上次确认时的"亮着"状态 —— 确认就又变成了不承重的摆设。
            RefreshRunReadiness();

            // ★ 但"场景没变"也要能看见：如果改完推导结果一模一样，用户会以为没生效。
            //   这里不做额外提示 —— 因为选项的"当前"高亮已经跟着移动了，
            //   那就是"生效了"的可见证据。
        }

        /// <summary>
        /// 把"机械手现在所在的位置"设为九点网格中心（现场就是这么做的）。
        /// ★ 读失败必须让用户知道，不能悄悄保持旧值 —— 否则他会以为基准位已经是当前位置了。
        /// </summary>
        private bool SetBasePosFromCurrent()
        {
            try
            {
                MotionPose pose = _env.Motion.GetFeedbackPosition();
                _draft.BasePosXY = new Vec2(pose.X, pose.Y);
                _draft.BasePosKnown = true;
                return true;
            }
            catch (Exception ex)
            {
                Notice("读机械手当前位置失败（" + ex.GetType().Name + "：" + ex.Message
                    + "）—— 基准位没有改，还是原来的值。");
                return false;
            }
        }

        /// <summary>
        /// 把工作副本写回环境（用户点"下一步"= 确认时调用）。
        /// ★ 返回 false 表示没写成功，调用方**必须停下**，不许带着旧参数往下跑 ——
        ///   否则用户以为改生效了，实际跑的还是旧配置，而且事后无从发现。
        /// </summary>
        private bool CommitDraft()
        {
            if (_draft == null)
            {
                return true;
            }

            try
            {
                // ★★ 写过去的是**副本的副本**，不是 _draft 本身。
                //   为什么：ApplyTopology 的语义是整体替换（环境就存下你给的那个对象）。
                //   若直接交 _draft，两者从此**别名**同一个对象 —— 用户确认后再回第 2 步点一项，
                //   ApplyAnswer 改的是 _draft，而那**就是**环境里那一个 ⇒
                //   改动绕过了确认直接生效，工作副本这层防护等于不存在。
                //   这类缺陷没有任何症状：界面照常、环境照常、只是"确认"这两个字白写了。
                if (!_env.ApplyTopology(_draft.Clone()))
                {
                    Notice("环境拒绝了这次修改（工位配置可能是只读的）—— 现在跑的还是旧参数，先别往下走。");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Notice("写回环境失败（" + ex.GetType().Name + "：" + ex.Message + "），先别往下走。");
                return false;
            }

            return true;
        }

        private double ResolveMmPerPixel()
        {
            HomMat2D? h = _coordinator.HandEye;
            if (!h.HasValue)
            {
                return 0.0;
            }

            double norm = Math.Sqrt(h.Value.H11 * h.Value.H11 + h.Value.H21 * h.Value.H21);
            return double.IsNaN(norm) ? 0.0 : norm;
        }

        #endregion

        #region 步骤机

        private void EnterStep(int step)
        {
            if (step < 1)
            {
                step = 1;
            }
            else if (step > 5)
            {
                step = 5;
            }

            StepIndex = step;

            switch (step)
            {
                case 1:
                    BuildGoals();
                    break;

                case 2:
                    ReflectScene();
                    RefreshRunReadiness();
                    break;

                case 3:
                    BuildFeatures();
                    break;

                case 4:
                    RefreshRunReadiness();
                    break;

                case 5:
                    RefreshRunReadiness();
                    break;
            }

            // ★ 步骤变了导轨必须跟着重画（当前项/已完成标记/能不能点）。
            //   放在 switch 之后：StepIndex 若值未变（比如 EnterStep(1) 时已在 1），
            //   SetProperty 不触发 RefreshCommands，这里就是唯一可靠的重建点。
            RebuildStepRail();
        }

        private void GoNext()
        {
            // ★★ 这里全部从 `OutcomeText = ...` 改成 `Notice(...)`。
            //   原因见 NoticeText 的注释：OutcomeText 只绑在验收页，在别的步骤里写它
            //   等于"用户点了一点反馈都没有"。Notice 那行就在按钮正上方，每步都在。
            if (_stepIndex == 1 && _selectedGoal == null)
            {
                Notice("先选一个目标 —— 上面那几张卡片里点一张。");
                return;
            }

            if (_stepIndex == 2)
            {
                if (_scene != null && _scene.HasBlocker)
                {
                    Notice("这一页里有硬性障碍项（标着 ⚠ 的行），先解决再往下走 —— "
                         + "不要带着一个必然失败的场景开跑。");
                    return;
                }

                // ★★ 确认 = 真的把用户改过的前置信息**写回环境**。
                //   修前这里只置了一个布尔，而那个布尔全仓没人读 ⇒「我改」没有通路。
                //   ★ 写回失败就不许往下走：否则用户以为改生效了，实际跑的还是旧配置，
                //     而且事后完全无从发现（界面显示的是他选的，环境里是旧的）。
                if (!CommitDraft())
                {
                    return;
                }

                SceneConfirmed = true;

                // ★ 同上：必须重算而不是只广播（_canRun 是字段属性）。
                RefreshRunReadiness();
            }

            if (_stepIndex >= 4 && _outcome == null)
            {
                Notice("还没跑过。请先按「开始标定」跑一遍，再进验收。");
                return;
            }

            NoticeText = string.Empty;
            EnterStep(_stepIndex + 1);
        }

        private void GoPrev()
        {
            EnterStep(_stepIndex - 1);
        }

        /// <summary>刷新"现在能不能跑 + 一共要跑几步"。</summary>
        public void RefreshRunReadiness()
        {
            if (_selectedGoal == null)
            {
                BlockedReasonText = "还没选目标。";
                CanRun = false;
                ChainPlanText = "—";
                VaultText = _coordinator.VaultText();
                RefreshPlanCard();
                return;
            }

            List<CalibChainKind> plan = WizardCoordinator.PlanChains(_selectedGoal.Goal);
            var names = new List<string>(plan.Count);
            for (int i = 0; i < plan.Count; i++)
            {
                names.Add(WizardCoordinator.ChainPlainName(plan[i]));
            }

            ChainPlanText = string.Format(CultureInfo.InvariantCulture,
                "这次一共 {0} 步：{1}", plan.Count, string.Join(" → ", names.ToArray()));

            string reason = _coordinator.BlockedReason(_selectedGoal.Goal);

            // ★ 选了「用模板」但还没练出来：在这里拦（而不是等采样时提取器报
            //   "模板不存在"）—— 门槛前置，人话说清去哪补。
            if (string.IsNullOrEmpty(reason) && _selectedFeature != null
                && _selectedFeature.Kind == FeatureKind.TemplateMatch && !HasTemplate)
            {
                reason = "选了「用模板」，但模板还没训练 —— 回第 3 步完成「框选 → 训练」。";
            }

            BlockedReasonText = reason ?? string.Empty;

            // ★★ 「确认」唯一真正生效的地方就在这里。
            //   只把确认按钮画好看、把布尔置上，而没有任何人读它 —— 那叫走过场。
            //   用户的要求是"问够了再确认，确认了再动手"，所以这里必须挡住。
            CanRun = string.IsNullOrEmpty(reason) && !IsRunning && SceneConfirmed;
            VaultText = _coordinator.VaultText();

            // ★ 方案卡正文里含 ChainPlanText（"这次会跑哪几步"），链一变它就得跟着变。
            RefreshPlanCard();
        }

        #endregion

        #region 特征极性（暗底亮点 / 亮底暗点）

        /// <summary>当前生效的特征极性。</summary>
        public MarkPolarity SelectedPolarity
        {
            get { return _selectedPolarity; }
        }

        /// <summary>现在是「暗底上的亮标记」（提取器主路径，不反色）。</summary>
        public bool IsBrightMark
        {
            get { return _selectedPolarity == MarkPolarity.BrightMarkOnDarkBackground; }
        }

        /// <summary>现在是「亮底上的暗标记」（提取前整图反色再走主路径）。</summary>
        public bool IsDarkMark
        {
            get { return _selectedPolarity == MarkPolarity.DarkMarkOnBrightBackground; }
        }

        /// <summary>「亮标记」按钮此刻能不能点（已是它 = 不用点；模板匹配 = 极性不适用）。</summary>
        public bool CanUseBrightMarkButton
        {
            get { return PolarityApplicable && !IsBrightMark; }
        }

        /// <summary>「暗标记」按钮此刻能不能点。</summary>
        public bool CanUseDarkMarkButton
        {
            get { return PolarityApplicable && !IsDarkMark; }
        }

        /// <summary>
        /// 极性对当前状态有没有意义。模板匹配恒 false：
        /// 模板与搜索图必须同一个灰度世界（训练图什么样搜索图就什么样），
        /// 反色搜索 = find_shape_model 的梯度极性对不上 = 必然找不到。
        /// ★ 按钮置灰与 <see cref="SwitchPolarity"/> 的拦截用同一个判断 ——
        ///   不会出现"灰着但点了也拦不住"的两套口径。
        /// </summary>
        private bool PolarityApplicable
        {
            get { return !IsRunning && _selectedFeature != null && _selectedFeature.Kind != FeatureKind.TemplateMatch; }
        }

        /// <summary>极性区那行说明（现在是什么 + 什么时候该动它）。</summary>
        public string PolarityText
        {
            get
            {
                if (_selectedFeature == null)
                {
                    return "先选特征。";
                }

                if (_selectedFeature.Kind == FeatureKind.TemplateMatch)
                {
                    return "模板匹配不吃极性：训练图什么样，搜索图就什么样。要换灰度世界 → 重新「框选 → 训练」。";
                }

                return IsBrightMark
                    ? "当前：亮标记（暗底上的亮点）—— 提取器主路径，直接找。Mark 看起来比底亮就用这个。"
                    : "当前：暗标记（亮底上的暗点）—— 提取前整图反色再走主路径。几何不变（反色不动像素位置），只是把「亮底暗点」翻译成提取器熟的世界。";
            }
        }

        private void SwitchPolarity(MarkPolarity polarity)
        {
            if (polarity == _selectedPolarity)
            {
                return;
            }

            // ★ 模板匹配不吃极性：这里必须拦，别让"选了模板又点了暗标记"产生一个静默无效的选择。
            if (!PolarityApplicable)
            {
                Notice("模板匹配不吃极性（训练与搜索必须同一个灰度世界）。要换极性，先换回圆点/十字，或重新教模板。");
                return;
            }

            _selectedPolarity = polarity;
            OnPropertyChanged("SelectedPolarity");
            OnPropertyChanged("IsBrightMark");
            OnPropertyChanged("IsDarkMark");
            OnPropertyChanged("CanUseBrightMarkButton");
            OnPropertyChanged("CanUseDarkMarkButton");
            OnPropertyChanged("PolarityText");
            Notice(IsDarkMark
                ? "已切到「暗标记」：跑链与预览会先整图反色再找（几何不变）。"
                : "已切回「亮标记」：跑链与预览直接在原图上找。");
        }

        #endregion

        #region 候选预览（圆点 / 十字 / 模板共用）

        /// <summary>预览卡那行状态（在找什么、找到没有、画在哪）。</summary>
        public string PreviewStatusText
        {
            get { return _previewStatusText; }
            private set { SetProperty(ref _previewStatusText, value); }
        }

        private bool CanFeaturePreview
        {
            get
            {
                // 圆点/十字随时可以试找；模板必须有练出来的模板（否则必然失败，灰着并说明）。
                return !IsRunning && _extractor != null && _env.Camera != null
                       && (_selectedFeature == null || _selectedFeature.Kind != FeatureKind.TemplateMatch || HasTemplate);
            }
        }

        /// <summary>
        /// 候选预览：抓最新一帧，按<b>当前选中的特征 + 当前极性</b>真跑一次提取，把结果交出去画
        /// （sampleIndex 0 = 纯预览，不进任何采样集）。
        /// ★★ 预览口径与 Run 完全一致（同一个 BuildMarkSpec 构造点 + 同一个提取器实例）：
        ///   预览找得到、正式跑找不到 —— 那说明两边口径分叉了，这种分叉要在结构上不可能。
        /// ★ 期望位置给 (0,0) = 全图搜：预览没有"上次找到在哪"的先验，如实全图找；
        ///   全图搜在提取器口径里算降级（UsedFallback），payload 会如实带上，不冒充干净命中。
        /// </summary>
        private void FeaturePreview()
        {
            if (!CanFeaturePreview)
            {
                return;
            }

            CalibError error;
            byte[] raw = _env.Camera.GrabFrame(2000, out error);
            if (raw == null)
            {
                Notice("取图失败，预览没法开始。");
                return;
            }

            int w = _env.Camera.Width;
            int h = _env.Camera.Height;

            Action<byte[], int, int> frameHandler = TemplateFrameCaptured;
            if (frameHandler != null)
            {
                frameHandler(raw, w, h);
            }

            // ★ 与 Run 同一个构造点：选中特征 + 当前极性一起进 MarkSpec。
            var mark = BuildMarkSpec(_selectedFeature, _selectedPolarity);
            CalibObservation obs = _extractor.Extract(raw, w, h, mark, 0.0, 0.0, 0, null);

            string title = _selectedFeature == null ? "特征" : _selectedFeature.PlainTitle;
            var payload = new FeaturePreviewPayload
            {
                FeatureTitle = title,

                // ★ 参考半径只对阈值类路径有意义（模板的"大小"由训练框表达）。
                ReferenceRadiusPx = mark.Kind == FeatureKind.TemplateMatch ? 0.0 : _extractor.ReferenceRadiusPx
            };

            if (mark.Kind == FeatureKind.TemplateMatch && _trainer != null)
            {
                TemplateSpec ts = _trainer.GetTemplate(WizardTemplateKey);
                if (ts != null)
                {
                    payload.HasRoi = true;
                    payload.RoiR1 = Math.Min(ts.Row1, ts.Row2);
                    payload.RoiC1 = Math.Min(ts.Col1, ts.Col2);
                    payload.RoiR2 = Math.Max(ts.Row1, ts.Row2);
                    payload.RoiC2 = Math.Max(ts.Col1, ts.Col2);
                }
            }

            if (obs == null || !obs.HasPixel)
            {
                payload.Matched = false;
                payload.Message = obs == null
                    ? "提取器没有返回结果。"
                    : "未命中：" + (string.IsNullOrEmpty(obs.RejectReason) ? "找不到足够相似的区域" : obs.RejectReason);
                PreviewStatusText = title + "预览未命中 —— " + payload.Message;
            }
            else
            {
                payload.Matched = true;
                payload.PixelX = obs.Pixel.X;
                payload.PixelY = obs.Pixel.Y;
                payload.ScorePercent = obs.Report == null ? 0.0 : obs.Report.Score;
                payload.FellBack = obs.UsedFallback;
                string fallbackNote = obs.UsedFallback
                    ? "（降级路径：预览没有期望位置先验，全图搜的，结果仅供参考）"
                    : string.Empty;
                payload.Message = (obs.Report == null || string.IsNullOrEmpty(obs.Report.Detail)
                    ? (mark.Kind == FeatureKind.TemplateMatch ? "模板命中" : "候选命中")
                    : obs.Report.Detail) + fallbackNote;
                PreviewStatusText = string.Format(CultureInfo.InvariantCulture,
                    "{3}预览：score {0:F1}%，位置 (col {1:F1}, row {2:F1})。已画在左边图上（绿十字=命中，青圈=参考半径）。",
                    payload.ScorePercent, payload.PixelX, payload.PixelY, title);
            }

            Action<FeaturePreviewPayload> previewHandler = FeaturePreviewReady;
            if (previewHandler != null)
            {
                previewHandler(payload);
            }
        }

        #endregion

        #region 模板示教（框选 → 训练）

        /// <summary>示教做完了吗（训练器里有向导键）。决定「用模板」选项能不能选、开跑要不要拦。</summary>
        public bool HasTemplate
        {
            get { return _trainer != null && _trainer.HasTemplate(WizardTemplateKey); }
        }

        /// <summary>示教区那行状态（现在走到哪一步、下一步干什么）。</summary>
        public string TemplateStatusText
        {
            get { return _templateStatusText; }
            private set { SetProperty(ref _templateStatusText, value); }
        }

        private bool CanTemplatePick
        {
            get { return _trainer != null && !IsRunning && _env.Camera != null; }
        }

        private bool CanTemplateTrain
        {
            // ★ 帧与框必须配对存在：框是在哪一帧上画的，训练就得用哪一帧。
            get { return _trainer != null && !IsRunning && _templateFrameRaw != null && _templateRoiPending; }
        }

        /// <summary>① 框选：抓一帧 → 显示 → 进入框选模式。训练用的就是这一帧的原始数据。</summary>
        private void TemplatePick()
        {
            if (!CanTemplatePick)
            {
                return;
            }

            CalibError error;
            byte[] raw = _env.Camera.GrabFrame(2000, out error);
            if (raw == null)
            {
                string why = error == null ? "未知原因" : error.ToString();
                Notice("取图失败（" + why + "），框选没法开始。");
                TemplateStatusText = "取图失败：" + why + "。点「框选」重试。";
                return;
            }

            _templateFrameRaw = raw;
            _templateFrameWidth = _env.Camera.Width;
            _templateFrameHeight = _env.Camera.Height;
            _templateRoiPending = false;

            Action<byte[], int, int> frameHandler = TemplateFrameCaptured;
            if (frameHandler != null)
            {
                frameHandler(_templateFrameRaw, _templateFrameWidth, _templateFrameHeight);
            }

            RequestRoiPickMode(true);
            TemplateStatusText = string.Format(CultureInfo.InvariantCulture,
                "已抓一帧（{0}×{1}）。在左边图上按住左键拖一个框，松开即完成框选；滚轮可缩放。",
                _templateFrameWidth, _templateFrameHeight);
        }

        /// <summary>
        /// 框选完成（显示面拖完一个框，由宿主转交）。图像坐标 row/col。
        /// ★ 只暂存不训练：用户要先看到框、确认框对了，再按「训练」。
        /// </summary>
        public void OnTemplateRoiCommitted(double r1, double c1, double r2, double c2)
        {
            if (_trainer == null)
            {
                return;
            }

            _templateR1 = Math.Min(r1, r2);
            _templateC1 = Math.Min(c1, c2);
            _templateR2 = Math.Max(r1, r2);
            _templateC2 = Math.Max(c1, c2);
            _templateRoiPending = true;

            TemplateStatusText = string.Format(CultureInfo.InvariantCulture,
                "框选完成（{0:F0}×{1:F0} px @ col {2:F0}, row {3:F0}）。按「训练」教模板；框不满意就重新拖一个。",
                _templateC2 - _templateC1, _templateR2 - _templateR1, _templateC1, _templateR1);
        }

        /// <summary>② 训练：用画框那一帧训练形状模板（键固定为 <see cref="WizardTemplateKey"/>，重训 = 覆盖）。</summary>
        private void TemplateTrain()
        {
            if (!CanTemplateTrain)
            {
                Notice("还没有框。先按「框选」，在图上拖一个框出来。");
                return;
            }

            var spec = new TemplateSpec
            {
                Key = WizardTemplateKey,
                ModelKind = TemplateModelKind.Shape,
                Row1 = _templateR1,
                Col1 = _templateC1,
                Row2 = _templateR2,
                Col2 = _templateC2,
                AngleStartDeg = -180.0,
                AngleExtentDeg = 360.0,
                MinScore = 0.6,

                // ★ 来源帧信息落进 spec：以后排查"这模板是拿哪张图练的"有据可查
                SourceFramePath = null,
                Note = "向导第 2.5 步示教"
            };

            string error;
            bool ok = _trainer.Train(_templateFrameRaw, _templateFrameWidth, _templateFrameHeight, spec, out error);
            if (!ok)
            {
                Notice("模板训练失败：" + (error ?? "未知原因"));
                TemplateStatusText = "训练失败：" + (error ?? "未知原因") + "。换个纹理更明显的框再试。";
                return;
            }

            // 训练完成：退出框选模式（光标还原、清橡皮筋），刷新「用模板」可用性
            RequestRoiPickMode(false);
            RefreshTemplateAvailability();
            RefreshRunReadiness();

            TemplateSpec saved = _trainer.GetTemplate(WizardTemplateKey);
            TemplateStatusText = string.Format(CultureInfo.InvariantCulture,
                "模板已训练（{0}×{1} px，质量 {2:F0}）。现在可以选「用模板」；点「候选预览」在最新一帧上看找得准不准。",
                _templateC2 - _templateC1, _templateR2 - _templateR1,
                saved == null ? 0.0 : saved.TrainQuality);
            Notice("模板训练完成。「用模板」选项已启用。");
        }

        /// <summary>训练状态变了：广播 HasTemplate + 让「用模板」那一项整体重建（该项是普通类，只能靠替换触发重绘）。</summary>
        private void RefreshTemplateAvailability()
        {
            OnPropertyChanged("HasTemplate");

            if (_templateFeatureItem != null)
            {
                FillTemplateFeatureItem(_templateFeatureItem);
                int i = Features.IndexOf(_templateFeatureItem);
                if (i >= 0)
                {
                    Features[i] = _templateFeatureItem;
                }
            }

            OnPropertyChanged("FeatureHintText");

            // ★ 选着「用模板」且刚训练完成的话，开跑门槛立刻重算 —— 不能等下一次交互才亮。
            RefreshRunReadiness();
        }

        private void RequestRoiPickMode(bool active)
        {
            Action<bool> handler = TemplateRoiPickRequested;
            if (handler != null)
            {
                handler(active);
            }
        }

        #endregion

        #region 第 3 步：跑

        private void RequestCancel()
        {
            _cancelRequested = true;
            ProgressText = "已请求取消，正在收尾（不会留下悬空运动）…";
        }

        private void Run()
        {
            if (IsRunning)
            {
                return;
            }

            RefreshRunReadiness();
            if (!CanRun)
            {
                string why = string.IsNullOrEmpty(BlockedReasonText) ? "现在不能开跑。" : BlockedReasonText;
                Notice(why + "　（真机上要先连相机与机械手；没有真机就先按顶栏「初始化仿真环境」在仿真里走一遍。）");
                return;
            }

            NoticeText = string.Empty;

            _cancelRequested = false;
            _outcome = null;
            OnPropertyChanged("HasOutcome");
            AcceptanceText = "（跑到这里就有结论了）";
            IssueText = string.Empty;
            ProgressPercent = 0.0;
            ProgressText = "正在开跑…";
            ProgressStep = "—";
            LastPointText = "—";
            OutcomeText = "正在跑…";
            IsRunning = true;
            CanRun = false;

            var opt = new WizardRunOptions
            {
                SessionPrefix = "WZ",
                MmPerPixel = ResolveMmPerPixel(),
                ArchiveFrames = true,
                CaptureTrace = true,
                ExportFiles = true,
                PublishToHost = true,
                // ★ 逐帧可视化：内参链在"摆板取图"这一步就要让操作员看见结果
                //   （H/e/t 三链是跑完之后由 OnRunCompleted 里的 RaiseVisualization 一次性画）。
                //   这里必须 Post 回 UI 线程：回调是从后台运行线程发出的。
                Visualize = req => Post(() =>
                {
                    Action<CalibVisualizationRequest> h = VisualizationRequested;
                    if (h != null)
                    {
                        h(req);
                    }
                })
            };

            // ★ 极性跟用户当前选择走（预览与 Run 同一个构造点，口径不可能分叉）。
            var mark = BuildMarkSpec(_selectedFeature, _selectedPolarity);

            WizardGoal goal = _selectedGoal.Goal;

            Action<RunProgress> onProgress = p => Post(() => ApplyProgress(p));
            Func<bool> isCancelled = () => _cancelRequested;

            // ★ 后台线程跑：真机上一条链要几分钟，占着 UI 线程会让界面"假死"，
            //   而"卡哪说哪"恰恰要求界面全程可读。
            var worker = new System.Threading.Tasks.Task(() =>
            {
                WizardRunOutcome outcome = null;
                try
                {
                    outcome = _coordinator.Run(goal, mark, opt, onProgress, isCancelled);
                }
                catch (Exception ex)
                {
                    _env.Log.Error("向导运行抛出异常：" + ex.GetType().Name + "：" + ex.Message, ex);
                }

                Post(() => OnRunCompleted(outcome));
            });

            worker.Start();
        }

        /// <summary>
        /// 按当前选中的特征类型构造跑链用的 MarkSpec（含极性）。
        /// ★★ 提出来成公共静态：跑链（<see cref="Run"/>）、候选预览与离线断言（WizardVmChecks ⑭⑮）
        ///   用<b>同一个构造点</b> —— 判据写两遍就是靠巧合正确，分叉只会发生在边界上。
        /// ★ 模板键必须跟着选择走：提取器按 <c>spec.TemplateKey</c> 找模板，
        ///   选了模板匹配却不给键 = 跑起来只会得到"模板匹配模式但没有指定模板键"。
        /// ★ 极性原样带进 Options；「模板匹配不吃反色」这件事<b>只在提取器一处特判</b> ——
        ///   这里不重复判（判据写两遍 = 靠巧合正确）。
        /// </summary>
        public static MarkSpec BuildMarkSpec(WizardFeatureItem selectedFeature, MarkPolarity polarity)
        {
            return new MarkSpec
            {
                Kind = selectedFeature == null ? FeatureKind.CircleMark : selectedFeature.Kind,
                TemplateKey = selectedFeature != null && selectedFeature.Kind == FeatureKind.TemplateMatch
                    ? WizardTemplateKey
                    : null,
                Options = new FeatureExtractOptions { MarkPolarity = polarity }
            };
        }

        private void ApplyProgress(RunProgress p)
        {
            if (p == null)
            {
                return;
            }

            // ★★ 这段早返回【不是】冗余代码，别看它像死的就删掉。
            //
            //   修前它确实是死的：`RunProgress.Error` 的唯一生产者
            //   `ChainRunContext.ProgressError` 一次都没被调用过（见那边的咽喉点注释），
            //   所以 `p.Error != null` 恒为假。2026-09-14 在 `ChainRunContext.Fail` 上接了
            //   一次之后它才是活的 —— 用户从此能看见「卡住：<原因>」而不是干等。
            //
            //   为什么必须 return、不能落到下面那行 `p.Describe()`：
            //   `ProgressError` 报的 `Fraction` 是<b>步骤起点</b>（它手上没有 within），
            //   一旦落到下面就会 `ProgressPercent = Fraction*100` ⇒ 进度条从
            //   「第 4 步 60%」**倒退**到「第 4 步 0%」，看起来像"又重跑了"。
            //   （`Describe()` 自己也会给「卡住：…」，所以文案本来是对的 —— 会错的是百分比。）
            if (p.Error != null)
            {
                ProgressStep = p.StepTitle;
                ProgressText = "卡住：" + p.Error;
                return;
            }

            ProgressPercent = Math.Max(0.0, Math.Min(100.0, p.Fraction * 100.0));
            ProgressStep = p.StepTitle;
            ProgressText = p.Describe();

            if (p.LastObservation != null && p.SampleIndex > 0)
            {
                CalibObservation o = p.LastObservation;
                LastPointText = string.Format(CultureInfo.InvariantCulture,
                    "#{0} 像素 ({1:F1}, {2:F1})  世界 ({3:F2}, {4:F2})  质量 {5:F0}  {6}",
                    o.Index, o.Pixel.X, o.Pixel.Y, o.World.X, o.World.Y, o.MatchScore, o.Verdict);

                Action<int, CalibObservation> fp = FramePlanted;
                if (fp != null)
                {
                    fp(p.SampleIndex, o);
                }
            }
        }

        private void OnRunCompleted(WizardRunOutcome outcome)
        {
            IsRunning = false;
            _outcome = outcome;
            OnPropertyChanged("HasOutcome");
            RefreshRunReadiness();

            // ★ 必须在 `_outcome` 赋值【之后】再广播一次：`IsRunning = false` 那次刷新发生在
            //   赋值之前（那时 CanVisualize / CanAdopt 看到的还是上一次的结果）。
            RefreshCommands();

            if (outcome == null)
            {
                OutcomeText = "运行内部异常（详见日志）。";
                return;
            }

            ProgressPercent = outcome.Success ? 100.0 : ProgressPercent;

            if (outcome.Cancelled)
            {
                ProgressText = "已取消。";
            }
            else if (outcome.Success)
            {
                ProgressText = "完成。";
            }
            else
            {
                ProgressText = "未完成：" + outcome.Message;
            }

            OutcomeText = outcome.Summary() + Environment.NewLine + BuildOutcomeDetail(outcome);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < outcome.Issues.Count; i++)
            {
                sb.AppendLine(outcome.Issues[i].ToString());
            }

            IssueText = sb.Length == 0 ? "（无告警）" : sb.ToString().TrimEnd();
            AcceptanceText = BuildAcceptance(outcome);

            RaiseVisualization();
            RaiseDistortionImpact(outcome);
        }

        /// <summary>
        /// 内参链跑完后，把「去畸变后会好多少」的量化结果抛给界面。
        ///
        /// ★ 为什么用事件而不是属性：这是一次性的快照（一次标定对应一个结果），
        ///   做成绑定属性就得在界面侧处理一堆"还没跑 / 跑了但没产出"的空值分支，
        ///   而事件天然表达"量出来了"这一刻。
        /// </summary>
        public event Action<DistortionImpactAssessment> DistortionImpactMeasured;

        private void RaiseDistortionImpact(WizardRunOutcome outcome)
        {
            Action<DistortionImpactAssessment> handler = DistortionImpactMeasured;
            if (handler == null || outcome == null || outcome.Chains == null)
            {
                return;
            }

            for (int i = 0; i < outcome.Chains.Count; i++)
            {
                ChainRunResult r = outcome.Chains[i];
                if (r == null || r.IntrinsicsOutcome == null || r.IntrinsicsOutcome.Export == null)
                {
                    continue;
                }

                // ★ 去畸变影响量挂在产物的【诊断】里（不是 CalibExport 的直接字段）
                CalibExport ex = r.IntrinsicsOutcome.Export;
                DistortionImpactAssessment a = ex.Diagnostics == null ? null : ex.Diagnostics.DistortionImpact;
                if (a != null)
                {
                    handler(a);
                    return;
                }
            }
        }

        private static string BuildOutcomeDetail(WizardRunOutcome outcome)
        {
            var sb = new System.Text.StringBuilder();

            for (int i = 0; i < outcome.Chains.Count; i++)
            {
                ChainRunResult r = outcome.Chains[i];
                string label = i < outcome.ChainLabels.Count ? outcome.ChainLabels[i] : r.Chain.ToString();
                sb.Append("· ").Append(label).Append("：").Append(r.Summary()).AppendLine();
            }

            for (int i = 0; i < outcome.SkippedLabels.Count; i++)
            {
                sb.Append("· 跳过 ").Append(outcome.SkippedLabels[i]).AppendLine();
            }

            if (!string.IsNullOrEmpty(outcome.ArtifactRoot))
            {
                sb.Append("· 产物：").Append(outcome.ArtifactRoot);
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>第 4 步的验收结论 —— 用人话把"数字对不对"讲清。</summary>
        private static string BuildAcceptance(WizardRunOutcome outcome)
        {
            if (outcome == null)
            {
                return "（没有结果）";
            }

            if (!outcome.Success)
            {
                return "未通过。原因：" + (string.IsNullOrEmpty(outcome.Message) ? "见日志" : outcome.Message);
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("结论：通过。");

            ChainRunResult primary = outcome.Primary;
            if (primary != null)
            {
                if (primary.Reprojection != null)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "反投影（AR）：像素残差 RMS {0:F4} px，最大 {1:F4} px —— {2}",
                        primary.Reprojection.RmsPixelResidualPx,
                        primary.Reprojection.MaxPixelResidualPx,
                        primary.Reprojection.Verdict(1.0)).AppendLine();
                }

                CalibDiagnostics d = primary.Diagnostics;
                if (d != null)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "形状：σ1/σ2 {0:F6}（越接近 1 越好；判据线 0.03），剪切 {1:F6}",
                        d.SigmaRatio, d.ShearRatio).AppendLine();
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "残差：LOO {0:F5} mm（留一法，比自拟合诚实）", d.LooRmsMm).AppendLine();
                }
            }

            if (outcome.WarnCount > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "有 {0} 条告警，请看下面的清单。", outcome.WarnCount);
            }

            return sb.ToString().TrimEnd();
        }

        private void Adopt()
        {
            if (_outcome == null || !_outcome.Success)
            {
                Notice("还没有可采用的结论 —— 先跑通一次（采用只对通过的结果开放）。");
                return;
            }

            NoticeText = string.Empty;
            OutcomeText = "已采用本会话结果（后续目标会直接复用它，不再重标）。"
                + Environment.NewLine + _coordinator.VaultText();
            RefreshRunReadiness();
        }

        private void ResetAll()
        {
            if (IsRunning)
            {
                Notice("正在跑，先按「取消」再重置。");
                return;
            }

            _coordinator.ResetVault();
            _outcome = null;
            OnPropertyChanged("HasOutcome");
            NoticeText = string.Empty;

            // ★ 工作副本也一起丢掉：下次进第 2 步重新从环境读。
            //   否则"用户改过的值"会在一次「清空重来」之后依然赖着不走 ——
            //   那正是"清空"这两个字最不该出现的行为。
            _draft = null;

            // ★ 「清空重来」必须把"场景已确认"一起作废。
            //   都清空了还留着确认，就等于确认是橡皮图章 —— 用户根本没看过重新读出来的场景，
            //   「开始标定」却已经亮了（而这正是"确认后再动手"要挡的那件事）。
            SceneConfirmed = false;

            OutcomeText = "已清空本会话已解出的量。";
            AcceptanceText = "（跑完第 4 步后在这里给结论）";
            IssueText = string.Empty;
            ProgressPercent = 0.0;
            ProgressText = "尚未开始";
            ProgressStep = "—";
            LastPointText = "—";
            EnterStep(1);

            // ★ 显式再广播一次：下面 EnterStep(1) 里若 StepIndex 本来就是 1，
            //   SetProperty 会认为"没变"而跳过通知 ⇒ CanVisualize / CanAdopt 会留在旧值。
            RefreshCommands();
        }

        /// <summary>
        /// 把"要画什么"交出去。
        /// ★ 画的是<b>实测像素</b>与<b>用当前 H 反投回去的像素</b>两条：两者之间的连线就是残差矢量。
        ///   AR 之所以是最高判据，就是因为这条线没法通过调参变好看。
        /// </summary>
        private void RaiseVisualization()
        {
            Action<CalibVisualizationRequest> h = VisualizationRequested;
            if (h == null)
            {
                return;
            }

            ChainRunResult primary = _outcome == null ? null : _outcome.Primary;
            if (primary == null)
            {
                return;
            }

            var req = new CalibVisualizationRequest
            {
                H = _coordinator.HandEye,
                HasRotCenter = _coordinator.RotationCenterKnown,
                RotCenterWorld = _coordinator.RotationCenterWorld ?? Vec2.Zero,
                Title = (_outcome == null ? "向导" : _outcome.GoalPlainName) + " · 反投影叠加",
                Observations = primary.Sampling == null ? null : primary.Sampling.Observations,
                ReprojectionRmsPx = primary.Reprojection == null
                    ? double.NaN : primary.Reprojection.RmsPixelResidualPx
            };

            RotationCenterResult rc = primary.RotationCenter;
            if (rc != null && rc.Success)
            {
                // ★ 画的是"映射域"上的点（就是定圆用的那批），不是原始像素 ——
                //   旋转链的几何意义全部在映射域里，画像素域会让人对不上号。
                var mapped = new List<Vec2>(rc.SamplePoints.Count);
                for (int i = 0; i < rc.SamplePoints.Count; i++)
                {
                    RotationSamplePoint sp = rc.SamplePoints[i];
                    if (sp != null)
                    {
                        mapped.Add(sp.Mapped);
                    }
                }

                req.MappedPoints = mapped;
                req.FittedRadiusMm = rc.FittedRadiusMm;
                req.RotCenterWorld = rc.Center;
                req.HasRotCenter = true;
            }

            h(req);
        }

        #endregion

        #region 线程

        private void Post(Action a)
        {
            if (a == null)
            {
                return;
            }

            if (_dispatcher == null || _dispatcher.CheckAccess())
            {
                a();
            }
            else
            {
                _dispatcher.BeginInvoke(a);
            }
        }

        #endregion
    }
}
