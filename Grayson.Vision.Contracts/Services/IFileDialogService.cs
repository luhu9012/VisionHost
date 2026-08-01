//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 通用文件对话框服务契约，将 Win32 文件对话框操作抽象化。
//===================================================================================

namespace Grayson.Vision.Contracts.Services
{
    /// <summary>
    /// 文件对话框服务契约
    /// </summary>
    public interface IFileDialogService
    {
        /// <summary>
        /// 弹出保存文件对话框
        /// </summary>
        /// <param name="filter">文件类型过滤字符串</param>
        /// <param name="defaultName">默认文件名</param>
        /// <returns>用户选择的完整路径，取消返回 null</returns>
        string ShowSaveFileDialog(string filter, string defaultName = "");

        /// <summary>
        /// 弹出打开文件对话框
        /// </summary>
        /// <param name="filter">文件类型过滤字符串</param>
        /// <returns>用户选择的完整路径，取消返回 null</returns>
        string ShowOpenFileDialog(string filter);
    }
}
