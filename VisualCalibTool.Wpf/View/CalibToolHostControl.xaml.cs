using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using VisualCalibTool.Domain;
using VisualCalibTool.ViewModels;

namespace VisualCalibTool.Views
{
    /// <summary>
    /// 可嵌入宿主 / 可独立运行的标定工具主界面。
    /// ★ 这是类库对外唯一的"入口控件"：宿主 <c>new CalibToolHostControl()</c> 放进任意容器即可。
    /// </summary>
    public partial class CalibToolHostControl : UserControl
    {
        private readonly CalibShellViewModel _vm;

        public CalibToolHostControl()
        {
            InitializeComponent();

            _vm = new CalibShellViewModel();

            // 唯一的显示接缝：视图模型只拿到接口，不拿到控件类型
            _vm.Surface = ImageView;
            ImageView.HalconUnavailable += OnHalconUnavailable;
            _vm.DistortionImpactMeasured += OnDistortionImpactMeasured;
            _vm.LogLines.CollectionChanged += OnLogLinesChanged;

            DataContext = _vm;

            Loaded += CalibToolHostControl_Loaded;
            Unloaded += CalibToolHostControl_Unloaded;
        }

        /// <summary>供宿主读写（例如注入设备环境、订阅日志）。</summary>
        public CalibShellViewModel ViewModel
        {
            get { return _vm; }
        }

        /// <summary>宿主是否接管初始化（嵌入主项目时通常由宿主先注入环境，再自行初始化）。</summary>
        public bool AutoInitializeOnLoad = true;

        private void CalibToolHostControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (AutoInitializeOnLoad && _vm.InitCommand.CanExecute(null))
            {
                _vm.InitCommand.Execute(null);
            }
        }

        private void CalibToolHostControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
            _vm.Dispose();
        }

        /// <summary>
        /// 缺 HALCON 原生库（halcon.dll）。这是<b>环境问题</b>不是代码问题，
        /// 必须说清怎么修，不能让它以英文 <c>DllNotFoundException</c> 的形式从 Loaded 里把程序带走。
        /// </summary>
        private void OnHalconUnavailable(string reason)
        {
            _vm.ReportEnvironmentIssue(reason);
        }

        /// <summary>
        /// 内参链量出了「去畸变后会好多少」—— 直接喂给对比视图。
        /// ★ 这里不做任何加工：界面拿到的就是算法算出来的那一份，
        ///   免得"显示的是一套、算的是另一套"（这类错位最难查）。
        /// </summary>
        private void OnDistortionImpactMeasured(DistortionImpactAssessment assessment)
        {
            DistortionView.SetAssessment(assessment);
        }

        /// <summary>滚动合并标志：同一批多条日志只排一次滚动。</summary>
        private bool _logScrollQueued;

        private void OnLogLinesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add || LogListBox.Items.Count == 0)
            {
                return;
            }

            // ★★ 日志自动滚到底 —— 但【绝不能】在这里同步滚。
            //   本处理器在构造函数里订阅（早于 ItemsSource 绑定建立时 ItemsControl 自己挂的
            //   视图处理器），Add 事件到达此刻，条目还没被生成器消费。此时调 ScrollIntoView
            //   会强制同步 UpdateLayout → 生成器 Verify() 发现"累积计数与实际计数不一致"
            //   → System.InvalidOperationException（累积 7 vs 实际 8）。
            //   之前一直没炸只是因为日志框恰好不在默认页签里（没被实例化）；改成默认页签当场炸。
            //   修法：延迟到 Background 优先级（布局完成之后）再滚，并用标志合并同一批多条日志。
            if (_logScrollQueued)
            {
                return;
            }

            _logScrollQueued = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _logScrollQueued = false;
                    if (LogListBox.Items.Count > 0)
                    {
                        LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
