using System.Windows;
using System.Windows.Controls;

namespace Grayson.Vision.Nodes.All.FlowControl.SwitchCase
{
    public partial class SwitchCaseTemplateView : UserControl
    {
        public SwitchCaseTemplateView()
        {
            InitializeComponent();
        }

        private void BtnAddCase_Click(object sender, RoutedEventArgs e)
        {
            // 通过 Button 的 DataContext 动态查找对应的 SwitchCaseParam
            if (sender is FrameworkElement element && element.DataContext is SwitchCaseParam param)
            {
                int nextIndex = param.CaseItems.Count + 1;
                param.CaseItems.Add(new SwitchCaseItem
                {
                    CaseValue = nextIndex.ToString(),
                    BranchName = $"Branch{nextIndex}"
                });
            }
        }

        private void BtnRemoveCase_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is SwitchCaseItem item)
            {
                // 沿着 VisualTree/LogicalTree 获取父级 Context 或者通过 items 关系操作
                if (btn.Tag is SwitchCaseParam param)
                {
                    param.CaseItems.Remove(item);
                }
                else
                {
                    // 备用方式：直接在控件所在的 DataContext 层级查找或传递
                    var parentControl = FindParent<UserControl>(btn);
                    if (parentControl?.DataContext is SwitchCaseParam parentParam)
                    {
                        parentParam.CaseItems.Remove(item);
                    }
                }
            }
        }

        private static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;

            if (parentObject is T parent)
                return parent;

            return FindParent<T>(parentObject);
        }
    }
}