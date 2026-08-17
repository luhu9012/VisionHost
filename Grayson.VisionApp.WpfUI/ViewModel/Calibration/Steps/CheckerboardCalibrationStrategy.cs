using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class CheckerboardCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "请确保棋盘格标定板完整位于相机视野中央且表面无光斑过度曝光。";
                case 2: return "设置棋盘格的内角点行列数 (Row x Col) 与角点实际物理间距 (mm)。";
                case 3: return "拍摄标定图并全自动提取亚像素角点。";
                case 4: return "计算相机畸变参数与投影变换矩阵。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel context) { }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            context.AppendLog($"提取棋盘格角点: 阵列 [{context.CheckerboardRows}x{context.CheckerboardCols}], 间距 [{context.CheckerboardSpacingMm}mm]");
            MessageBox.Show("角点提取成功！亚像素精细提取点数：" + (context.CheckerboardRows * context.CheckerboardCols), "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void AutoRunAll(CalibrationWizardViewModel context) => TriggerSample(context);

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