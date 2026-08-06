using Grayson.Vision.Nodes.Common;
using Grayson.Vision.Contracts.Devices.Enums;

namespace Grayson.Vision.Nodes.All.ImageInput.AcquireImage
{
    public class AcquireImageParam : ParamBase
    {
        private string _cameraAlias = "TopCam";

        // 🌟 通过特性标注：该属性关联的是相机逻辑设备
        [LogicalDeviceBinding(deviceType: DeviceCategory.Camera, deviceName: "Top Camera", requiredSpec: "面阵工业相机")]
        public string CameraAlias
        {
            get => _cameraAlias;
            set => Set(ref _cameraAlias, value);
        }

        private string _triggerMode = "Software";
        public string TriggerMode
        {
            get => _triggerMode;
            set => Set(ref _triggerMode, value);
        }

        private double _exposureTime = 5000;
        public double ExposureTime
        {
            get => _exposureTime;
            set => Set(ref _exposureTime, value);
        }

        private double _gain = 1.0;
        public double Gain
        {
            get => _gain;
            set => Set(ref _gain, value);
        }

        private int _timeoutMs = 3000;
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(CameraAlias) && string.IsNullOrWhiteSpace(CameraAlias))
                    return "相机别名不能为空";
                if (columnName == nameof(ExposureTime) && ExposureTime <= 0)
                    return "曝光时间必须大于0";
                if (columnName == nameof(TimeoutMs) && TimeoutMs < 100)
                    return "超时时间不能小于100ms";
                return null;
            }
        }
        #endregion
    }
}