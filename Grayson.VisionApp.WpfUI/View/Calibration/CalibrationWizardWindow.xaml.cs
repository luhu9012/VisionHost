//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardWindow.xaml.cs
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.View
{
    /// <summary>
    /// 第三步界面数据模板选择器（兼容 C# 7.3）
    /// </summary>
    public class Step3DataTemplateSelector : DataTemplateSelector
    {
        public DataTemplate NinePointTemplate { get; set; }
        public DataTemplate HandEyeWithRotationTemplate { get; set; }
        public DataTemplate CheckerboardTemplate { get; set; }
        public DataTemplate PixelScaleTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is CalibrationProfile profile)
            {
                switch (profile.Type)
                {
                    case CalibrationType.NinePointHandEye:
                        return NinePointTemplate;
                    case CalibrationType.HandEyeWithRotation:
                       return HandEyeWithRotationTemplate;

                    case CalibrationType.Checkerboard2D:
                        return CheckerboardTemplate;

                    case CalibrationType.PixelScale:
                        return PixelScaleTemplate;

                    default:
                        return NinePointTemplate;
                }
            }
            return base.SelectTemplate(item, container);
        }
    }

    public partial class CalibrationWizardWindow : Window
    {
        public CalibrationWizardWindow() : this(null)
        {
        }

        public CalibrationWizardWindow(CalibrationProfile profile)
        {
            InitializeComponent();
            this.DataContext = new CalibrationWizardViewModel(this, profile);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        /// <summary>
        /// 特征参数滑块变化：交回 ViewModel 防抖重试（300ms），实现所见即所得。
        /// 无帧时 VM 内部会直接忽略，避免空跑。
        /// </summary>
        private void FeatureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DataContext is CalibrationWizardViewModel vm) vm.ReapplyFeatureExtraction();
        }

        /// <summary>
        /// 参数面板"立即重试"按钮：用当前参数立即重新提取一次并刷新叠加显示。
        /// </summary>
        private void RetryExtract_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is CalibrationWizardViewModel vm) vm.ReapplyFeatureExtraction();
        }

        /// <summary>
        /// 当前已注入的显示上下文适配器（跟随向导当前选中页可见的显示控件切换）。
        /// </summary>
        private HalconDisplayContextAdapter _displayContextAdapter;

        /// <summary>
        /// 标定图像显示控件加载完成后，把显示上下文适配器指向当前可见的显示控件。
        /// 创建 ICalibrationDisplayContext 适配器（持有控件内部的 HWindow 句柄）
        /// 注入给 ViewModel → CalibrationService，
        /// 实现特征识别过程在视图窗口上的精细叠加显示。
        /// 注意：控件本身不实现该接口（公共 API 暴露 halcondotnet 类型会导致
        /// XAML 编译器加载 halcondotnet 而报 MC1000），由适配器在代码后台桥接。
        /// </summary>
        private void HalconDisplayStep1_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshDisplayContextAdapter();
        }

        /// <summary>
        /// 向导切页后刷新显示上下文适配器。
        /// 第三步各标定类型模板（NinePoint / HandEye / Checkerboard / PixelScale）的
        /// 显示控件是 DataTemplate 动态实例化的另一批 HalconImageDisplayHost：
        /// SelectionChanged 时立刻扫描拿到的还是旧页内容（模板尚未实例化），
        /// 排到 Loaded / Background 两个优先级各刷一次，保证新页控件加载完成后必然重指。
        /// </summary>
        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshDisplayContextAdapter),
                System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(new Action(RefreshDisplayContextAdapter),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 把标定显示上下文适配器（重）指向"当前选中页中的显示控件"。
        /// 向导存在多个 HalconImageDisplayHost：步骤2特征配置页一个（HalconDisplayStep1）、
        /// 第三步每个标定类型模板各一个（DataTemplate 实例化）。TabControl 只把选中页的
        /// 内容连接进可视化树 —— 扫描 TabControl 可视化树取到的即当前可见控件。
        /// 算子叠加绘制（场景式 API）只画到适配器指向的那个 HWindow：若切页后不重指，
        /// 绘制会落到隐藏页的窗口上，当前页窗口看不到任何算子过程结果（本 bug 根因）。
        /// </summary>
        private void RefreshDisplayContextAdapter()
        {
            if (!(DataContext is CalibrationWizardViewModel vm)) return;

            // 只有当前选中 Tab 的内容才在可视化树中，扫描即得当前可见的显示控件
            HalconImageDisplayHost host = FindVisualDescendants<HalconImageDisplayHost>(MainTabs).FirstOrDefault();

            // 当前页无显示控件（步骤1/步骤4）：保留上次适配器不置空——
            // 画到隐藏/断连窗口无副作用，且避免切换瞬间置空引发绘制中断
            if (host == null) return;
            if (_displayContextAdapter != null && _displayContextAdapter.Host == host) return;

            _displayContextAdapter = new HalconDisplayContextAdapter(host);
            vm.CalibrationDisplayContext = _displayContextAdapter;
        }

        /// <summary>深度遍历可视化子树，找出指定类型的全部控件</summary>
        private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) yield break;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T typed) yield return typed;
                foreach (var desc in FindVisualDescendants<T>(child))
                {
                    yield return desc;
                }
            }
        }
    }
}