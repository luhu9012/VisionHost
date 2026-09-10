// Grayson.Vision.WpfUI/View/StationMonitorExtensions/MahjongPickTeachPanel.xaml.cs
using Grayson.Vision.WpfUI.ViewModel.StationMonitorExtensions;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View.StationMonitorExtensions
{
    /// <summary>
    /// MahjongPick 参数/示教面板（示教闭环）。
    /// 2026-09-06 职责收敛：由「工位监视页扩展位」迁至「工位工程工作台 → ④ 执行方案」Tab 就地挂载
    /// （StationManageViewModel 经 StationMonitorExtensionRegistry 按 ProcessKey 创建），公共监视页零引用。
    /// </summary>
    public partial class MahjongPickTeachPanel : UserControl
    {
        public MahjongPickTeachPanel()
        {
            InitializeComponent();
            DataContext = new MahjongPickTeachViewModel();
        }
    }
}
