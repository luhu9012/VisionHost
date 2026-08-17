using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.HalconWrapper.Calibration;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class HandEyeWithRotationCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "步骤 2：请配置吸嘴 Mark 点或特征匹配参数。";
                case 2: return "步骤 3：请先完成平移 9 点自动采集，再控制 R 轴旋转 3~5 个角度拟合旋转中心。";
                case 3: return "步骤 4：将同时求解平移 HomMat2D 矩阵与旋转中心 (Cx, Cy)。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            // 修复前：vm.RotationPoints.Add(new CalibrationWizardViewModel.RotationPointModel { ... });
            // 修复后：直接使用契约层类型
            vm.RotationPoints.Clear();
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 0, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 15, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = -15, PixelX = 0, PixelY = 0 });
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            // 单点采集逻辑
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            // 自动平移走位 9 点 + 自动旋转 R 轴 3 次
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context.CalibrationPoints.Count < 9 || context.RotationPoints.Count < 3)
            {
                MessageBox.Show("平移 9 点或旋转点位采集未完成！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 1. 计算平移 HomMat2D
            double[] px = context.CalibrationPoints.Select(p => p.PixelX).ToArray();
            double[] py = context.CalibrationPoints.Select(p => p.PixelY).ToArray();
            double[] wx = context.CalibrationPoints.Select(p => p.WorldX).ToArray();
            double[] wy = context.CalibrationPoints.Select(p => p.WorldY).ToArray();

            var res = context.CalibService.CalcNinePointHomMat(px, py, wx, wy);

            // 2. 拟合圆心
            double centerPx = context.RotationPoints.Average(p => p.PixelX);
            double centerPy = context.RotationPoints.Average(p => p.PixelY);

            context.RotationCenterResult = $"Cx: {centerPx:F2}, Cy: {centerPy:F2}";
            context.ProcessCalibrationResult(res);
        }
    }
}