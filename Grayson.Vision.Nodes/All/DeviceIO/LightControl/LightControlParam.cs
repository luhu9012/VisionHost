using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DeviceIO.LightControl
{
    public class LightControlParam : ParamBase
    {
        private string _lightAlias = "TopLight";

        /// <summary>
        /// 关联逻辑光源设备
        /// </summary>
        [LogicalDeviceBinding(deviceType: DeviceCategory.LightController, deviceName: "Top Light Controller", requiredSpec: "串口/网口光源控制器")]
        public string LightAlias
        {
            get => _lightAlias;
            set => Set(ref _lightAlias, value);
        }

        private int _channel = 1;
        /// <summary>
        /// 光源通道号 (如 1, 2, 3, 4)
        /// </summary>
        public int Channel
        {
            get => _channel;
            set => Set(ref _channel, value);
        }

        private int _intensity = 128;
        /// <summary>
        /// 亮度值 (0 ~ 255)
        /// </summary>
        public int Intensity
        {
            get => _intensity;
            set => Set(ref _intensity, value);
        }

        private bool _turnOn = true;
        /// <summary>
        /// 开关状态 (True=开启, False=关闭)
        /// </summary>
        public bool TurnOn
        {
            get => _turnOn;
            set => Set(ref _turnOn, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(LightAlias) && string.IsNullOrWhiteSpace(LightAlias))
                    return "光源设备别名不能为空";
                if (columnName == nameof(Channel) && Channel < 1)
                    return "通道号必须大于或等于 1";
                if (columnName == nameof(Intensity) && (Intensity < 0 || Intensity > 255))
                    return "亮度值必须在 0 - 255 之间";
                return null;
            }
        }
        #endregion
    }
}