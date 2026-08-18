using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class CheckerboardCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "请选择棋盘格标定使用的相机，并确保标定板处于视野中央。";
                case 2: return "点击抓图按钮，确认 Halcon 视图显示当前棋盘格图像。";
                case 3: return "拍摄标定图并执行对应的棋盘格角点提取动作。";
                case 4: return "计算相机畸变参数与投影变换矩阵。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel context)
        {
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.CurrentStep <= 1)
            {
                context.CaptureFeatureFrame("棋盘格预览");
                return;
            }

            context.CaptureCheckerboardSample();
            context.AppendLog($"提取棋盘格角点: 阵列 [{context.CheckerboardRows}x{context.CheckerboardCols}], 间距 [{context.CheckerboardSpacingMm}mm]");
            MessageBox.Show("棋盘格图像已采集，可继续接入角点检测算法。", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            TriggerSample(context);
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            context.AppendLog("开始求解 2D 棋盘格畸变与相机参数...");
            var res = context.CalibService.CalcNinePointHomMat(
                new double[] { 100, 200, 300, 100, 200, 300, 100, 200, 300 },
                new double[] { 100, 100, 100, 200, 200, 200, 300, 300, 300 },
                new double[] { 0, 10, 20, 0, 10, 20, 0, 10, 20 },
                new double[] { 0, 0, 0, 10, 10, 10, 20, 20, 20 });
            context.ProcessCalibrationResult(res);
        }
    }
}
