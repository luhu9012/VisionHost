using Grayson.Vision.Contracts.Infrastructure.Services;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static Grayson.Vison.FlowEdit.ViewModels.FlowVm;

namespace Grayson.Vison.FlowEdit.Services
{
    // 默认 WPF 实现，同时实现消息对话框与文件对话框契约
    public class WpfDialogService : IDialogService, IFileDialogService
    {
        public void ShowInfo(string message, string title = "提示")
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowWarning(string message, string title = "警告")
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        public void ShowError(string message, string title = "错误")
            => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        public bool ShowConfirm(string message, string title = "确认")
            => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

        public string ShowInputDialog(string message, string title = "输入", string defaultValue = "")
            => PromptDialog.Show(title, message, defaultValue);

        public Task ShowWaitingDialog(string message, Task operation)
        {
            // 当前为简易实现：直接等待任务完成，后续可扩展为真实等待对话框
            return operation;
        }

        public string ShowSaveFileDialog(string filter, string defaultName = "")
        {
            var sfd = new SaveFileDialog { Filter = filter, FileName = defaultName };
            return sfd.ShowDialog() == true ? sfd.FileName : null;
        }

        public string ShowOpenFileDialog(string filter)
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