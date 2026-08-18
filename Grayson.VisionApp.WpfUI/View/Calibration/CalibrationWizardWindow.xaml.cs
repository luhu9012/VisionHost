//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardWindow.xaml.cs
//===================================================================================
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.HalconWrapper.Calibration;
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
    }
}