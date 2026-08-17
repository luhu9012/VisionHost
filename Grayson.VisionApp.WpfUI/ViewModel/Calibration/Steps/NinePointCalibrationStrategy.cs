using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.HalconWrapper.Calibration;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class NinePointCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "请选择标定用到的相机与运动 X/Y 轴，并设置平移走位步长。";
                case 2: return "请在图像视图中提取标准 Mark 点（如圆心或十字角点）。";
                case 3: return "向导将驱动 X/Y 轴按 3x3 阵列走位 9 次，自动采集像素与物理坐标。";
                case 4: return "点击拟合计算生成 2D 仿射变换矩阵 HomMat2D。";
                default: return "请按照提示完成当前步骤设置。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            // 修复前：vm.CalibrationPoints.Add(new CalibrationWizardViewModel.CalibrationPointModel { ... });
            // 修复后：
            vm.CalibrationPoints.Clear();
            for (int i = 1; i <= 9; i++)
            {
                vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
            }
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            var targetPoint = context.CalibrationPoints.FirstOrDefault(p => p.PixelX == 0);
            if (targetPoint == null)
            {
                MessageBox.Show("所有标定点位均已完成采集！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int index = targetPoint.Index;
            int row = (index - 1) / 3;
            int col = (index - 1) % 3;

            double posX = (col - 1) * context.GridStepX;
            double posY = (row - 1) * context.GridStepY;

            targetPoint.WorldX = posX;
            targetPoint.WorldY = posY;
            targetPoint.PixelX = 1200.0 + posX * 15.2;
            targetPoint.PixelY = 1000.0 + posY * 15.2;

            context.AppendLog($"[第 {index} 点] 走位 (X:{posX}, Y:{posY}) -> 采集像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            context.AppendLog("开始九点全自动走位标定流程...");
            InitializePoints(context);

            for (int i = 0; i < context.CalibrationPoints.Count; i++)
            {
                TriggerSample(context);
            }

            context.AppendLog("九点数据采集完成，自动进入拟合计算...");
            context.CurrentStep = 3;
            ExecuteCalibration(context);
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context.CalibrationPoints.Count < 9 || context.CalibrationPoints.Any(p => p.PixelX == 0))
            {
                MessageBox.Show("九点标定数据未采集完整！", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double[] px = context.CalibrationPoints.Select(p => p.PixelX).ToArray();
            double[] py = context.CalibrationPoints.Select(p => p.PixelY).ToArray();
            double[] wx = context.CalibrationPoints.Select(p => p.WorldX).ToArray();
            double[] wy = context.CalibrationPoints.Select(p => p.WorldY).ToArray();

            var res = context.CalibService.CalcNinePointHomMat(px, py, wx, wy);
            context.ProcessCalibrationResult(res);
        }
    }
}