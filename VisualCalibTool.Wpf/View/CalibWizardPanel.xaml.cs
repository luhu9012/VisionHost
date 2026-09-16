using System.Windows;
using System.Windows.Controls;
using VisualCalibTool.ViewModels;

namespace VisualCalibTool.Views
{
    /// <summary>
    /// 强引导向导面板。★ 它<b>不自己取 DataContext</b>：宿主在
    /// <c>CalibToolHostControl.xaml</c> 里用 <c>DataContext="{Binding Wizard}"</c> 注入
    /// <c>CalibWizardViewModel</c>，这样"向导"这个面板也能被别的宿主单独拿去用。
    /// </summary>
    public partial class CalibWizardPanel : UserControl
    {
        public CalibWizardPanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 方案卡上的「有一项要改」。
        ///
        /// ★ 为什么这个按钮用代码后置而不是绑命令：它要做的事**一半是视图的事**
        ///   （把滚动条带回上面的问题区）—— 视图模型不知道滚动条在哪，也不该知道。
        ///   一半是"要说话"（提醒改完确认会作废），那半交给视图模型。
        ///
        /// ★ 为什么不干脆不做这个按钮、只在文案里写"不对就去改上面"：
        ///   滚到下面看方案卡的时候，问题区已经在视野之外了，"上面"具体指哪要用户自己找。
        ///   给一个能把他送回去的按钮，比多写一句话有用。而**一个点了不动的按钮**
        ///   比没有按钮更坏（那就是本次现场反馈里"点了没反应"的形状）——
        ///   所以它必须真的滚动 + 真的给出一句话。
        /// </summary>
        private void ReviseSceneButton_Click(object sender, RoutedEventArgs e)
        {
            WizardScroll.ScrollToTop();

            var vm = DataContext as CalibWizardViewModel;
            if (vm != null)
            {
                vm.RequestSceneRevision();
            }
        }
    }
}
