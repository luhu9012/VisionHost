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
            if (context.LegacyPhase <= 2)
            {
                context.CaptureFeatureFrame("棋盘格预览");
                return;
            }

            context.CaptureCheckerboardSample();
            context.AppendLog("提取棋盘格角点: 当前版本尚未实现真实角点检测（占位类型），仅采集图像供调试。");
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            TriggerSample(context);
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            // ⚠ 2026-09-04 真实性修复：此前这里用硬编码 9 对伪点跑 HomMat，"求解畸变"得到的是与图像无关的
            //   假矩阵（RMS≈0 还能通过健康检查入库），用户以为做了标定板/畸变标定，实际拿到的是废数据。
            //   真正的 find_caltab/calibrate_cam 未实现前，一律明确拒绝，绝不产出假数据。
            context.AppendLog("[棋盘格/相机内参] ⛔ 已阻止假标定：真实标定板角点检测（find_caltab/calibrate_cam）尚未实现。");
            MessageBox.Show(
                "「棋盘格 2D / 相机内参」标定尚未实现：缺少真实标定板角点检测（HALCON find_caltab / calibrate_cam 未接入）。\n\n" +
                "为避免无效标定数据入库，本次计算已中止。请在标定管理页把方案类型切换为：\n" +
                "九点手眼 / 九点+旋转(偏心) / 吸放式 Pick&Place。",
                "类型未实现", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
