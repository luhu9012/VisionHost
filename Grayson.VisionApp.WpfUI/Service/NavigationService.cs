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
        /// <summary>
        /// 导航到指定页面
        /// </summary>
        public void NavigateTo(PageType pageType, object parameter = null)
        {
            if (!_pageFactory.ContainsKey(pageType))
            {
                throw new InvalidOperationException($"页面类型 {pageType} 未注册");
            }

            // 🌟 1. 触发旧页面的 OnNavigatedFrom（离开当前页面）
            if (_contentPresenter.Content is UserControl oldPage)
            {
                if (oldPage.DataContext is INavigationAware oldVm)
                {
                    oldVm.OnNavigatedFrom();
                }
                else if (oldPage is INavigationAware oldViewAware)
                {
                    oldViewAware.OnNavigatedFrom();
                }
            }

            // 创建新页面实例
            var page = _pageFactory[pageType]();

            // 🌟 2. 触发新页面的 OnNavigatedTo（优先交由 ViewModel 处理，其次交由 View 处理）
            if (page.DataContext is INavigationAware targetVm)
            {
                targetVm.OnNavigatedTo(parameter);
            }
            else if (page is INavigationAware targetViewAware)
            {
                targetViewAware.OnNavigatedTo(parameter);
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
