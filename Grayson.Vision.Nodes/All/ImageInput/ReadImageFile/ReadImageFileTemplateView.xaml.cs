using System.Windows.Controls;

namespace Grayson.Vision.Nodes.All.ImageInput.ReadImageFile
{
    public partial class ReadImageFileTemplateView : UserControl
    {
        public ReadImageFileTemplateView()
        {
            InitializeComponent();
        }

        private void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ⚠ 模板会被 NodePluginLoader 提取到全局资源，代码后台 this 实例的 DataContext
            // 恒为 null，必须用 sender（实时 ListBox）取 DataContext
            if (sender is ListBox listBox && listBox.DataContext is ReadImageFileParam param)
            {
                if (listBox.SelectedIndex >= 0)
                {
                    param.CurrentImageIndex = listBox.SelectedIndex;
                }
            }
        }
    }
}