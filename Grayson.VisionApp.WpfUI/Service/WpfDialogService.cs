//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: WpfDialogService.cs
// 说 明: IDialogService 与 IFileDialogService 的 WPF 具体实现
//===================================================================================
using System;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Grayson.Vision.Contracts.Infrastructure.Services;


namespace Grayson.Vision.WpfUI.Service
{
    public class WpfDialogService : IDialogService, IFileDialogService
    {
        #region IDialogService 实现
        public void ShowInfo(string message, string title = "提示")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void ShowWarning(string message, string title = "警告")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        public void ShowError(string message, string title = "错误")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        public bool ShowConfirm(string message, string title = "确认")
        {
            return MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        public string ShowInputDialog(string message, string title = "输入", string defaultValue = "")
        {
            // 简单输入框处理，没有复杂 UI 时可直接返回默认值或弹自定义窗体
            return defaultValue;
        }

        public async Task ShowWaitingDialog(string message, Task operation)
        {
            if (operation != null)
            {
                await operation;
            }
        }
        #endregion

        #region IFileDialogService 实现
        public string ShowOpenFileDialog(string filter)
        {
            var dialog = new OpenFileDialog { Filter = filter };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string ShowSaveFileDialog(string filter, string defaultName = "")
        {
            var dialog = new SaveFileDialog { Filter = filter, FileName = defaultName };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        /// <summary>
        /// 系统设置特有的文件夹选择对话框（支持 System.Windows.Forms 或 Microsoft.Win32.OpenFolderDialog）
        /// </summary>
        public string ShowFolderBrowserDialog(string initialPath = "")
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择文件夹";
                if (!string.IsNullOrEmpty(initialPath) && System.IO.Directory.Exists(initialPath))
                {
                    dialog.SelectedPath = initialPath;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    return dialog.SelectedPath;
                }
            }
            return null;
        }
        #endregion
    }
}