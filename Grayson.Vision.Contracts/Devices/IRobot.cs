using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>工业六轴机器人通用接口</summary>
    public interface IRobot : IDevice
    {
        /// <summary>移动到目标世界位姿</summary>
        Result MoveToPose(Pose3D pose, double speedPercent);

        /// <summary>读取机器人当前末端位姿</summary>
        Result<Pose3D> GetCurrentPose();

        /// <summary>设置工具坐标系偏移补偿</summary>
        Result SetToolOffset(Pose3D offset);
    }
}