//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: INavigationService.cs
// 创 建: 2026-07-18
// 说 明: 导航服务接口,负责主框架内的页面切换
//===================================================================================

using System;
using Grayson.Vision.WpfUI.Common;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 导航服务接口
    /// </summary>
    public interface INavigationService
    {
        /// <summary>
        /// 导航到指定页面
        /// </summary>
        /// <param name="pageType">页面类型</param>
        /// <param name="parameter">导航参数</param>
        void NavigateTo(PageType pageType, object parameter = null);

        /// <summary>
        /// 返回上一页
        /// </summary>
        void GoBack();

        /// <summary>
        /// 是否可以返回
        /// </summary>
        bool CanGoBack { get; }

        /// <summary>
        /// 页面切换事件
        /// </summary>
        event EventHandler<PageType> PageChanged;
    }
}
