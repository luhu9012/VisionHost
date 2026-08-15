using System.Windows;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    public partial class HardwareSelectWindow : Window
    {
        public HardwareSelectWindow(HardwareSelectViewModel viewModel)
        {
            InitializeComponent();
            this.DataContext = viewModel;

            viewModel.RequestClose += () =>
            {
                this.DialogResult = viewModel.DialogResult;
                this.Close();
            };
        }
    }
}