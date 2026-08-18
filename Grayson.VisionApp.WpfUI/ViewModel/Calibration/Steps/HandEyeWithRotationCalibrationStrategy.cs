using Grayson.Vision.Contracts.Calibration.Models;
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
                case 1: return "请选择多点手眼标定所需的相机与运动控制卡。";
                case 2: return "先抓图确认吸嘴或 Mark 点特征，再进入采样步骤。";
                case 3: return "先完成平移 9 点，再执行 3~5 个旋转角度采样拟合旋转中心。";
                case 4: return "系统将同时求解平移 HomMat2D 矩阵与旋转中心结果。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            vm.CalibrationPoints.Clear();
            for (int i = 1; i <= 9; i++)
            {
                vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
            }

            vm.RotationPoints.Clear();
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 0, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 15, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = -15, PixelX = 0, PixelY = 0 });
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.CurrentStep <= 1)
            {
                context.CaptureFeatureFrame("旋转手眼特征预览");
                return;
            }

            if (context.CalibrationPoints.Any(p => p.PixelX == 0 && p.PixelY == 0))
            {
                context.CaptureNextCalibrationPoint();
                return;
            }

            context.CaptureNextRotationPoint();
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            context.AppendLog("开始多点手眼 + 旋转中心自动采样流程...");
            InitializePoints(context);
            context.AutoCollectNinePointSamples();
            context.AutoCollectRotationSamples();
            context.CurrentStep = 3;
            ExecuteCalibration(context);
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context.CalibrationPoints.Count < 9 || context.CalibrationPoints.Any(p => p.PixelX == 0 && p.PixelY == 0) || context.RotationPoints.Count < 3 || context.RotationPoints.Any(p => p.PixelX == 0 && p.PixelY == 0))
            {
                MessageBox.Show("平移 9 点或旋转点位采集未完成！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double[] px = context.CalibrationPoints.Select(p => p.PixelX).ToArray();
            double[] py = context.CalibrationPoints.Select(p => p.PixelY).ToArray();
            double[] wx = context.CalibrationPoints.Select(p => p.WorldX).ToArray();
            double[] wy = context.CalibrationPoints.Select(p => p.WorldY).ToArray();
            var res = context.CalibService.CalcNinePointHomMat(px, py, wx, wy);
            context.UpdateRotationCenterResults();
            context.ProcessCalibrationResult(res);
        }
    }
}
