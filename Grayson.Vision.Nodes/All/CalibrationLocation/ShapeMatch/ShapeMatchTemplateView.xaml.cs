using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch
{
    /// <summary>
    /// ShapeMatchTemplateView.xaml 的交互逻辑
    /// </summary>
    public partial class ShapeMatchTemplateView : UserControl
    {
        public ShapeMatchTemplateView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 属性面板每次打开（DataContext 变为实时 Param）时刷新模板下拉清单，
        /// 保证能引用到刚在【模板管理】界面新建的模板。
        /// ⚠ 不能用 this.DataContext（模板提取机制的"死实例"坑），必须从 sender 取实时 DataContext。
        /// </summary>
        private void OnRootDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShapeMatchParam param)
            {
                param.RefreshTemplates();
            }
        }

        /// <summary>
        /// 双保险：每次点开下拉框都强制重扫磁盘模板目录。
        /// 覆盖"属性窗口已打开期间新建模板/DataContextChanged 时序异常"等场景，保证点开必有最新清单。
        /// sender 模式取实时 DataContext（死实例坑规避）。
        /// </summary>
        private void TemplateCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is ComboBox cb && cb.DataContext is ShapeMatchParam param)
            {
                param.RefreshTemplates();
            }
        }
    }
}
