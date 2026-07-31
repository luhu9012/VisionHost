using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>多轴运动控制卡通用接口</summary>
    public interface IMotionController : IDevice
    {
        /// <summary>轴绝对定位移动</summary>
        Result MoveAbsolute(int axis, double pos, double vel);

        /// <summary>相对位移移动</summary>
        Result MoveRelative(int axis, double delta, double vel);

        /// <summary>轴急停</summary>
        Result EmergencyStop(int axis);

        /// <summary>读取当前轴实际位置</summary>
        Result<double> GetAxisPos(int axis);
    }
}