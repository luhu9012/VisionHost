using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using static Grayson.Vison.FlowEdit.ViewModels.FlowVm;

namespace Grayson.Vison.FlowEdit.Services
{
    public interface IDialogService
    {
        bool ShowConfirm(string title, string message);
        void ShowError(string title, string message);
        string ShowPrompt(string title, string prompt, string defaultValue = "");
        string SaveFileDialog(string filter, string defaultName);
        string OpenFileDialog(string filter);
    }

    // 默认 WPF 实现
    public class WpfDialogService : IDialogService
    {
        public bool ShowConfirm(string title, string message)
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        public void ShowError(string title, string message)
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        public string ShowPrompt(string title, string prompt, string defaultValue = "")
            => PromptDialog.Show(title, prompt, defaultValue); // 移入 UI 层文件夹

        public string SaveFileDialog(string filter, string defaultName)
        {
            var sfd = new SaveFileDialog { Filter = filter, FileName = defaultName };
            return sfd.ShowDialog() == true ? sfd.FileName : null;
        }

        public string OpenFileDialog(string filter)
        {
            var ofd = new OpenFileDialog { Filter = filter };
            return ofd.ShowDialog() == true ? ofd.FileName : null;
        }
    }
    /// <summary>
    /// 简易 WPF 弹窗服务类
    /// </summary>
    public static class PromptDialog
    {
        public static string Show(string title, string prompt, string defaultValue = "")
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
                ResizeMode = ResizeMode.NoResize
            };

            var stack = new StackPanel { Margin = new Thickness(15) };
            stack.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });

            var txtInput = new TextBox { Text = defaultValue, Height = 25, VerticalContentAlignment = VerticalAlignment.Center };
            txtInput.SelectAll();
            stack.Children.Add(txtInput);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 15, 0, 0) };
            var btnOk = new Button { Content = "确定", Width = 70, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var btnCancel = new Button { Content = "取消", Width = 70, Height = 26, IsCancel = true };

            btnOk.Click += (s, e) => { win.DialogResult = true; };
            btnPanel.Children.Add(btnOk);
            btnPanel.Children.Add(btnCancel);
            stack.Children.Add(btnPanel);

            win.Content = stack;
            return win.ShowDialog() == true ? txtInput.Text : null;
        }
    }

}