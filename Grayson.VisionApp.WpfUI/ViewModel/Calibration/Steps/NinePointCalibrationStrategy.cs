using Grayson.Vision.Contracts.Calibration.Models;
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
                case 1: return "请选择标定用到的相机与运动控制卡，并设置平移走位步长。";
                case 2: return "点击抓图按钮，确认 Halcon 视图中显示当前相机实时画面与标定特征。";
                case 3: return "执行九点采样后，向导会驱动 X/Y 走位并记录像素与物理坐标。";
                case 4: return "点击拟合计算生成 2D 仿射变换矩阵 HomMat2D。";
                default: return "请按照提示完成当前步骤设置。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            vm.CalibrationPoints.Clear();
            for (int i = 1; i <= 9; i++)
            {
                vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
            }
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.CurrentStep <= 1)
            {
                context.CaptureFeatureFrame("九点标定特征预览");
                return;
            }

            context.CaptureNextCalibrationPoint();
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            context.AppendLog("开始九点全自动走位标定流程...");
            InitializePoints(context);
            context.AutoCollectNinePointSamples();
            context.AppendLog("九点数据采集完成，自动进入拟合计算...");
            context.CurrentStep = 3;
            ExecuteCalibration(context);
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context.CalibrationPoints.Count < 9 || context.CalibrationPoints.Any(p => p.PixelX == 0 && p.PixelY == 0))
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
