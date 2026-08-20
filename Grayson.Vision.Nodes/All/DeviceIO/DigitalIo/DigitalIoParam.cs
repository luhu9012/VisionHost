using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DeviceIO.DigitalIo
{
    public class DigitalIoParam : ParamBase
    {
        private string _cardAlias = "MotionCard1";
        [LogicalDeviceBinding(deviceType: DeviceCategory.MotionCard, deviceName: "板卡设备", requiredSpec: "IMotionCard")]
        public string CardAlias
        {
            get => _cardAlias;
            set => Set(ref _cardAlias, value);
        }

        private bool _isOutput = true; // false: 读取Input, true: 设置Output
        public bool IsOutput
        {
            get => _isOutput;
            set => Set(ref _isOutput, value);
        }

        private int _ioIndex = 0;
        public int IoIndex
        {
            get => _ioIndex;
            set => Set(ref _ioIndex, value);
        }

        private bool _outState = false; // 写入模式下的默认输出状态
        public bool OutState
        {
            get => _outState;
            set => Set(ref _outState, value);
        }

        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(CardAlias) && string.IsNullOrWhiteSpace(CardAlias))
                    return "运动控制卡别名不能为空";
                if (columnName == nameof(IoIndex) && IoIndex < 0)
                    return "IO 引脚索引号不能小于 0";
                return null;
            }
        }
    }
}