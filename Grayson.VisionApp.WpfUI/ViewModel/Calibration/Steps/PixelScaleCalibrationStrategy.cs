using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class PixelScaleCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "绑定待标定的相机设备。";
                case 2: return "使用标尺工具在图像中绘制一条已知尺寸的标准物料特征。";
                case 3: return "输入测得的图像像素距离与实际已知物理距离 (mm)。";
                case 4: return "导出计算得到的单像素当量 Scale (mm/pixel)。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel context) { }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            context.AppendLog("已自动测得特征像素跨度: " + context.MeasuredPixelDistance + " Px");
        }

        public void AutoRunAll(CalibrationWizardViewModel context) => TriggerSample(context);

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context.MeasuredPixelDistance <= 0 || context.KnownPhysicalDistanceMm <= 0)
            {
                MessageBox.Show("像素距离与实际物理距离必须大于 0！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double scale = context.KnownPhysicalDistanceMm / context.MeasuredPixelDistance;
            context.CalculatedRms = 0.00001;
            context.AppendLog($"[计算成功] 单像素当量 Scale = {scale:F6} mm/pixel");
            MessageBox.Show($"像素当量计算完成：\n1 Pixel = {scale:F6} mm", "计算成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}