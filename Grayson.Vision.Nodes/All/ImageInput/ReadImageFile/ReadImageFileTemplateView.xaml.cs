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
            if (DataContext is ReadImageFileParam param && sender is ListBox listBox)
            {
                if (listBox.SelectedIndex >= 0)
                {
                    param.CurrentImageIndex = listBox.SelectedIndex;
                }
            }
        }
    }
}