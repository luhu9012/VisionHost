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
        public DataTemplate HandEyeWithRotationTemplate { get; set; } // 旋转段专属模板（e）
        public DataTemplate CheckerboardTemplate { get; set; }
        public DataTemplate PixelScaleTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is CalibrationProfile profile)
            {
                switch (profile.Quantity)
                {
                    case CalibrationQuantity.HandEye:
                        return NinePointTemplate;

                    case CalibrationQuantity.ToolRotation:
                        // ★ 2026-09-12：旋转段拆为独立 e 会话，走旋转专属模板（12/15 点旋转采样）。
                        //   旧 PickPlaceHandEye/HandEyeWithRotation 混合档案已不再存在，旋转语义由 Quantity=ToolRotation 唯一承载。
                        return HandEyeWithRotationTemplate;

                    case CalibrationQuantity.LensDistortion:
                        return CheckerboardTemplate;

                    case CalibrationQuantity.PixelScale:
                        return PixelScaleTemplate;

                    default:
                        // ToolOffset 等：对针走独立视图（ShowToolOffsetAlignView），此处回退九点模板兜底
                        return NinePointTemplate;
                }
            }
            return base.SelectTemplate(item, container);
        }
    }
}