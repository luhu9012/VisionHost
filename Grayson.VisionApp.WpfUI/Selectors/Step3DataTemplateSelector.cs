using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.VisionApp.WpfUI.Selectors
{
    public class Step3DataTemplateSelector : DataTemplateSelector
    {
        public DataTemplate NinePointTemplate { get; set; }
        public DataTemplate HandEyeWithRotationTemplate { get; set; } // 新增：12点/15点专属模板
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
                    case CalibrationType.PickPlaceHandEye:
                        // ★ 2026-09-06：PickPlaceHandEye 由旧 default(NinePoint 纯九点模板) 改走
                        //   HandEyeWithRotation 模板——吸放式需要"单步吸放采样/一键全自动"等 PickPlace
                        //   特化按钮与旋转 Tab（旧漏网：吸放档案此前看不到吸放/旋转 UI）。
                        //   Tab1/Tab2 显隐由会话属性（SampleTranslateTabVisible / HasRotationStage）驱动，
                        //   v2 H 段只显平移 Tab、e 会话只显旋转 Tab、旧壳混合档案双 Tab 全显。
                        return HandEyeWithRotationTemplate; // 匹配 12/15 点旋转标定

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
}