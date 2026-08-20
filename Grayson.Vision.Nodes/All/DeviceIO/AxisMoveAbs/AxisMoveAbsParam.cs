using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DeviceIO.AxisMoveAbs
{
    public class AxisMoveAbsParam : ParamBase
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

        private float _targetPosition = 0.0f;
        public float TargetPosition
        {
            get => _targetPosition;
            set => Set(ref _targetPosition, value);
        }

        private float _speed = 50.0f;
        public float Speed
        {
            get => _speed;
            set => Set(ref _speed, value);
        }

        private bool _waitUntilDone = true; // 是否阻塞等待运动停止
        public bool WaitUntilDone
        {
            get => _waitUntilDone;
            set => Set(ref _waitUntilDone, value);
        }

        private int _timeoutMs = 10000; // 超时时间(ms)
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(CardAlias) && string.IsNullOrWhiteSpace(CardAlias))
                    return "控制卡别名不能为空";
                if (columnName == nameof(AxisIndex) && AxisIndex < 0)
                    return "轴号不能小于 0";
                if (columnName == nameof(Speed) && Speed <= 0)
                    return "运动速度必须大于 0";
                return null;
            }
        }
    }
}