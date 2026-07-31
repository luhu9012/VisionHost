//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: NavigationService.cs
// 创 建: 2026-07-18
// 说 明: 导航服务实现,使用简单的字典映射页面类型到 UserControl 实例
//===================================================================================

using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.WpfUI.Common;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 导航服务实现
    /// </summary>
    public class NavigationService : INavigationService
    {
        /// <summary>
        /// 当前应用的导航服务实例（主窗口初始化时赋值）
        /// 供页面 ViewModel 发起跨页跳转（如：产线工位管理页跳转到硬件配置页）
        /// </summary>
        public static INavigationService Current { get; set; }

        private readonly ContentControl _contentPresenter;
        private readonly Dictionary<PageType, Func<UserControl>> _pageFactory;
        private readonly Stack<PageType> _navigationStack;

        public NavigationService(ContentControl contentPresenter)
        {
            _contentPresenter = contentPresenter ?? throw new ArgumentNullException(nameof(contentPresenter));
            _pageFactory = new Dictionary<PageType, Func<UserControl>>();
            _navigationStack = new Stack<PageType>();
        }

        /// <summary>
        /// 注册页面工厂方法
        /// </summary>
        public void RegisterPage(PageType pageType, Func<UserControl> factory)
        {
            _pageFactory[pageType] = factory;
        }

        /// <summary>
        /// 导航到指定页面
        /// </summary>
        public void NavigateTo(PageType pageType, object parameter = null)
        {
            if (!_pageFactory.ContainsKey(pageType))
            {
                throw new InvalidOperationException($"页面类型 {pageType} 未注册");
            }

            // 创建页面实例
            var page = _pageFactory[pageType]();

            // 如果 ViewModel 支持接收参数,可以在这里传递
            if (page.DataContext != null && parameter != null)
            {
                // TODO: 实现参数传递逻辑
            }

            _contentPresenter.Content = page;
            _navigationStack.Push(pageType);

            PageChanged?.Invoke(this, pageType);
        }

        /// <summary>
        /// 返回上一页
        /// </summary>
        public void GoBack()
        {
            if (_navigationStack.Count > 1)
            {
                _navigationStack.Pop();
                var previousPage = _navigationStack.Peek();
                NavigateTo(previousPage);
            }
        }

        /// <summary>
        /// 是否可以返回
        /// </summary>
        public bool CanGoBack => _navigationStack.Count > 1;

        /// <summary>
        /// 页面切换事件
        /// </summary>
        public event EventHandler<PageType> PageChanged;
    }
}
