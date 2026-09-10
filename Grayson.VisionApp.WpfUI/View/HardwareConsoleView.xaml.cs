using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.WpfUI.View.HardwareConsole;
using Grayson.Vision.WpfUI.ViewModel;
using Grayson.Vision.WpfUI.ViewModel.HardwareConsole;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// HardwareConsoleView.xaml 的交互逻辑
    /// </summary>
    public partial class HardwareConsoleView : UserControl
    {
        // ============================================================
        // 独立窗口生命周期（2026-09-02 升级）：
        // 需求：从左侧菜单切到其它页面时，独立窗口应【继续存在】，只有手动关闭
        // 或关程序才消失。由于 HardwareConsoleView 每次导航都是新实例，若把窗口表
        // /VM 存在实例字段里，页面一销毁窗口与活动就全没了。
        // 方案：把「独立窗口表」与「正在被独立窗口使用的 ViewModel」提升为 static——
        //   · 页面 Unloaded 时若有独立窗口开着 → VM 转存 _survivingVm（不 Cleanup、不关窗），
        //     子面板轮询/采集继续（IsActive 保持 detached=true 的最后状态）；
        //   · 重新进入硬件控制台页面 → 复用 _survivingVm（同一 VM/设备句柄，无双 VM 冲突）；
        //   · 最后一个独立窗口关闭时：页面在场则回到 Tab 激活逻辑；页面已离开则 Cleanup+释放。
        // 关闭主程序时所有窗口自然随进程退出。
        // ============================================================
        private static readonly Dictionary<string, Window> _detachedWindows = new Dictionary<string, Window>();
        private static HardwareConsoleViewModel _survivingVm;

        public HardwareConsoleView()
        {
            InitializeComponent();

            // 优先复用独立窗口仍在使用的 VM（跨导航保活）；否则新建
            DataContext = _survivingVm ?? new HardwareConsoleViewModel();
        }

        private HardwareConsoleViewModel ViewModel => DataContext as HardwareConsoleViewModel;

        private static bool IsDetached(string key) => _detachedWindows.ContainsKey(key);

        /// <summary>
        /// Tab 切换 / 独立窗口开合后统一刷新各子面板激活态：
        /// 面板激活 = 主 Tab 当前选中 或 该面板已弹独立窗口 —— 两者任一满足即保持轮询/采集，
        /// 从而实现"独立窗口不阻塞主界面、并行操作观看"。
        /// </summary>
        private void RefreshActiveStates()
        {
            if (ViewModel == null) return;

            var sel = HardwareTabControl.SelectedItem;
            ViewModel.CameraDebugVM.IsActive = sel == TabCamera || IsDetached("Camera");
            ViewModel.AxisControlVM.IsActive = sel == TabAxis || IsDetached("Axis");
            ViewModel.RobotDebugVM.IsActive = sel == TabRobot || IsDetached("Robot");
            ViewModel.IoMonitorVM.IsActive = sel == TabIo || IsDetached("IO");
            ViewModel.CommDebugVM.IsActive = sel == TabComm || IsDetached("Comm");
        }

        /// <summary>
        /// Tab 切换时刷新激活态（机械臂等面板仅在激活时保持轮询，节省 RC+/总线会话）。
        /// </summary>
        private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshActiveStates();
        }

        /// <summary>
        /// Tab Header 上 ⧉ 按钮：把当前面板在独立非模态窗口打开。
        /// 内容为该面板 View 的新实例 + 同一 ViewModel（共享数据与设备句柄），
        /// 因此主界面与独立窗口看到同一状态、任一侧操作等效。
        /// 窗口生命周期独立于本页：切走页面不关窗（见 OnUnloaded/Closed 逻辑）。
        /// </summary>
        private void OnDetachTab_Click(object sender, RoutedEventArgs e)
        {
            var key = (sender as Button)?.Tag as string;
            if (string.IsNullOrEmpty(key)) return;

            // 已打开 → 激活置前
            Window existing;
            if (_detachedWindows.TryGetValue(key, out existing) && existing != null)
            {
                existing.Activate();
                return;
            }

            var (title, view) = CreateDetachedContent(key);
            if (view == null) return;

            var win = new Window
            {
                Title = title,
                Content = view,
                Width = 1240,
                Height = 800,
                MinWidth = 900,
                MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = Application.Current?.TryFindResource("BackgroundBrush") as System.Windows.Media.Brush
            };

            _detachedWindows[key] = win;
            MarkDetached(key, true);
            RefreshActiveStates();

            win.Closed += (s2, e2) =>
            {
                _detachedWindows.Remove(key);
                MarkDetached(key, false);

                if (_detachedWindows.Count == 0)
                {
                    // 最后一个独立窗口关闭
                    if (IsVisible)
                    {
                        // 硬件控制台页面还在 → 回到"仅 Tab 激活"逻辑
                        RefreshActiveStates();
                    }
                    else
                    {
                        // 页面已导航离开：彻底释放跨页保活的 VM
                        var vm = ViewModel;
                        _survivingVm = null;
                        vm?.Cleanup();
                    }
                }
                else
                {
                    // 仍有其它独立窗口开着：保持并行激活
                    RefreshActiveStates();
                }
            };

            win.Show();
        }

        /// <summary>按 Tab 标识创建独立窗口内容：View 新实例 + 共享子 ViewModel</summary>
        private (string title, UserControl view) CreateDetachedContent(string key)
        {
            switch (key)
            {
                case "Camera":
                    return ("📷 相机调试（独立）", new CameraDebugView { DataContext = ViewModel.CameraDebugVM });
                case "Axis":
                    return ("⚙️ 轴点动调试（独立）", new AxisControlView { DataContext = ViewModel.AxisControlVM });
                case "Robot":
                    return ("🤖 机械臂调试（独立）", new RobotDebugView { DataContext = ViewModel.RobotDebugVM });
                case "IO":
                    return ("🔌 数字量 IO 监视（独立）", new IoMonitorView { DataContext = ViewModel.IoMonitorVM });
                case "Comm":
                    return ("📡 通信调测（独立）", new CommDebugView { DataContext = ViewModel.CommDebugVM });
                default:
                    return (null, null);
            }
        }

        /// <summary>
        /// 维护 VM 的独立窗口标记：IO/Comm 面板的 View 用 Loaded/Unloaded 自管 IsActive，
        /// 标记为 detached 后其主 Tab 切走（Unloaded）不再停轮询/订阅。
        /// </summary>
        private void MarkDetached(string key, bool detached)
        {
            if (ViewModel == null) return;
            switch (key)
            {
                case "IO": ViewModel.IoMonitorVM.IsDetached = detached; break;
                case "Comm": ViewModel.CommDebugVM.IsDetached = detached; break;
            }
        }

        /// <summary>
        /// 页面卸载（导航离开）：
        /// · 有独立窗口开着 → 保留窗口与 VM（转存 _survivingVm，不 Cleanup、不关窗，
        ///   独立窗口继续轮询/采集/可操作）；
        /// · 无独立窗口 → 正常 Cleanup（VM 随页面回收）。
        /// </summary>
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_detachedWindows.Count > 0)
            {
                _survivingVm = ViewModel; // 跨导航保活：子面板 IsActive 保持 detached 状态，活动继续
                return;
            }

            _survivingVm = null;
            ViewModel?.Cleanup();
        }
    }
}
