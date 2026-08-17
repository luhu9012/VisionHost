using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.HalconWrapper.Calibration;
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