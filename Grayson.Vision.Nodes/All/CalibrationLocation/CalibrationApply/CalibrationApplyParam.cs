using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CalibrationApply
{
    public class CalibrationApplyParam : ParamBase
    {
        private string _calibrationProfileName = "Default_HandEye";
        /// <summary>
        /// 绑定的标定方案名称/标识
        /// </summary>
        public string CalibrationProfileName
        {
            get => _calibrationProfileName;
            set => Set(ref _calibrationProfileName, value);
        }

        private string _homMatFilePath = "";
        /// <summary>
        /// 标定矩阵文件路径 (.tup / .mat)
        /// </summary>
        public string HomMatFilePath
        {
            get => _homMatFilePath;
            set => Set(ref _homMatFilePath, value);
        }

        private bool _isPixelToWorld = true;
        /// <summary>
        /// 转换方向：true 为 像素 -> 物理 (mm)；false 为 物理 -> 像素
        /// </summary>
        public bool IsPixelToWorld
        {
            get => _isPixelToWorld;
            set => Set(ref _isPixelToWorld, value);
        }
    }
}