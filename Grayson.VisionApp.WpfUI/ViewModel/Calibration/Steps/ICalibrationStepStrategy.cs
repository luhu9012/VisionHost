namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public interface ICalibrationStepStrategy
    {
        /// <summary>当前步骤引导提示</summary>
        string GetStepGuideTip(int step);

        /// <summary>初始化默认数据点</summary>
        void InitializePoints(CalibrationWizardViewModel context);

        /// <summary>执行单点或特定阶段采集</summary>
        void TriggerSample(CalibrationWizardViewModel context);

        /// <summary>一键全自动采集</summary>
        void AutoRunAll(CalibrationWizardViewModel context);

        /// <summary>核心标定计算</summary>
        void ExecuteCalibration(CalibrationWizardViewModel context);
    }
}