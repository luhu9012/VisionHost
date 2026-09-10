// Grayson.Vision.WpfUI/View/StationMonitorExtensions/VisionPickPlaceTeachPanel.xaml.cs
using Grayson.Vision.WpfUI.ViewModel.StationMonitorExtensions;
using System.Windows.Controls;

namespace Grayson.Vision.WpfUI.View.StationMonitorExtensions
{
    /// <summary>
    /// VisionPickPlace（通用取放引擎）参数/示教面板（角度策略 + 位点/偏心/真空 IO + TeachMode）。
    /// 2026-09-06 职责收敛：由「工位监视页扩展位」迁至「工位工程工作台 → ④ 执行方案」Tab 就地挂载
    /// （StationManageViewModel 经 StationMonitorExtensionRegistry 按 ProcessKey 创建），
    /// S2（双吸嘴）/S3（同心吸嘴+多相机）共用，公共监视页零引用（已回归纯看板）。
    /// </summary>
    public partial class VisionPickPlaceTeachPanel : UserControl
    {
        public VisionPickPlaceTeachPanel()
        {
            InitializeComponent();
            DataContext = new VisionPickPlaceTeachViewModel();
        }
    }
}
