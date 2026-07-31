//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: IDialogService.cs
// 创 建: 2026-07-18
// 说 明: 对话框服务接口,提供统一的弹窗管理
//===================================================================================

using System.Threading.Tasks;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 对话框服务接口
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        /// 显示信息消息
        /// </summary>
        void ShowInfo(string message, string title = "提示");

        /// <summary>
        /// 显示警告消息
        /// </summary>
        void ShowWarning(string message, string title = "警告");

        /// <summary>
        /// 显示错误消息
        /// </summary>
        void ShowError(string message, string title = "错误");

        /// <summary>
        /// 显示确认对话框
        /// </summary>
        /// <returns>用户是否确认</returns>
        bool ShowConfirm(string message, string title = "确认");

        /// <summary>
        /// 显示输入对话框
        /// </summary>
        /// <returns>用户输入的文本,取消返回 null</returns>
        string ShowInputDialog(string message, string title = "输入", string defaultValue = "");

        /// <summary>
        /// 显示等待对话框 (用于长时间操作)
        /// </summary>
        Task ShowWaitingDialog(string message, Task operation);
    }
}
