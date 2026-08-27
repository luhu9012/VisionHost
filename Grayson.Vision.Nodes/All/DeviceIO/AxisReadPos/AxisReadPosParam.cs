using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DeviceIO.AxisReadPos
{
    /// <summary>
    /// 轴位置读取参数模型
    /// 用于查询运动控制卡单轴的当前指令位置、反馈位置、运动状态和报警标志
    /// </summary>
    public class AxisReadPosParam : ParamBase
    {
        private string _cardAlias = "MotionCard1";
        [LogicalDeviceBinding(deviceType: DeviceCategory.MotionCard, deviceName: "板卡设备", requiredSpec: "IMotionCard")]
        public string CardAlias
        {
            get => _cardAlias;
            set => Set(ref _cardAlias, value);
        }

        private int _axisIndex = 0;
        public int AxisIndex
        {
            get => _axisIndex;
            set => Set(ref _axisIndex, value);
        }

        private bool _readFeedback = false;
        /// <summary>
        /// true: 读取编码器反馈位置(MPOS)；false: 读取指令位置(DPOS)
        /// </summary>
        public bool ReadFeedback
        {
            get => _readFeedback;
            set => Set(ref _readFeedback, value);
        }

        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(CardAlias) && string.IsNullOrWhiteSpace(CardAlias))
                    return "控制卡别名不能为空";
                if (columnName == nameof(AxisIndex) && AxisIndex < 0)
                    return "轴号不能小于 0";
                return null;
            }
        }
    }
}
