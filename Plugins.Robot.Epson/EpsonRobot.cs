using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;

namespace Plugins.Robot.Epson
{
    /// <summary>
    /// Epson SCARA 4 轴机械手设备实现（本工位：双吸嘴麻将分拣）。
    ///
    /// 契约映射：
    /// - 实现 IMotionCard：把 Epson 的 X/Y/Z/U 四轴映射为统一的轴号
    ///   0=X（大臂）、1=Y（小臂）、2=Z（上下）、3=U（旋转），
    ///   单轴 MoveAbsolute 内部自动补全其余三轴当前值后整点位运动（Go/Move）；
    /// - 实现 IIoDevice：双吸嘴的真空阀即两路数字输出（0=吸嘴1真空、1=吸嘴2真空），
    ///   业务层用 0 基编号，适配层内部换算为 SPEL+ 的 1 基编号。
    ///
    /// 硬件访问全部经 EpsonSdkFactory 选择的适配层完成：
    /// - 装有 RC+（DLLLib\spelnet64.dll）→ 真实控制器；
    /// - 未装 → 离线仿真机械手（运动瞬时完成、输入回环输出）。
    /// </summary>
    public class EpsonRobot : IMotionCard, IIoDevice
    {
        #region 轴号常量（业务层与节点参数统一使用）

        /// <summary>X 轴（SCARA 大臂，绝对坐标 mm）</summary>
        public const int AxisX = 0;
        /// <summary>Y 轴（SCARA 小臂，绝对坐标 mm）</summary>
        public const int AxisY = 1;
        /// <summary>Z 轴（吸嘴上下，绝对坐标 mm，向下为负）</summary>
        public const int AxisZ = 2;
        /// <summary>U 轴（末端旋转，绝对角度 deg）</summary>
        public const int AxisU = 3;

        /// <summary>本机械手支持的轴数（SCARA 4 轴）</summary>
        public const int AxisCount = 4;

        #endregion

        #region IDevice 基础属性

        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceKey { get; set; }
        public string BrandName { get; set; }
        public event EventHandler<DeviceState> StateChanged;
        private DeviceState _state = DeviceState.Disconnected;

        public DeviceState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    StateChanged?.Invoke(this, _state);
                }
            }
        }

        public DeviceCategory Category { get; set; } = DeviceCategory.MotionCard;

        /// <summary>最后一次心跳/在线检测时间（UTC）</summary>
        public DateTime LastHeartbeatAt { get; set; }

        public Result Heartbeat() => CheckStatus();

        /// <summary>本地 UI/配置层参数字典</summary>
        public Dictionary<string, object> ConfigParams = new Dictionary<string, object>();

        #endregion

        #region 内部状态

        /// <summary>SDK 控制器适配器（真实 RC+ 或离线仿真）</summary>
        private IEpsonSdkController _sdk;

        /// <summary>各轴指令位置缓存（MoveAbsolute 时逐轴更新）</summary>
        private readonly float[] _pos = new float[AxisCount];

        /// <summary>输出口状态跟踪（供 GetOutput / IIoDevice.ReadDo 回读）</summary>
        private readonly Dictionary<int, bool> _outputStates = new Dictionary<int, bool>();

        private bool IsSdkOpen => _sdk != null && _sdk.IsOpen;

        #endregion

        #region 连接 / 断开 / 状态

        public Result Connect()
        {
            try
            {
                if (IsSdkOpen) return Result.Ok();

                if (_sdk == null)
                {
                    _sdk = EpsonSdkFactory.Instance.CreateController();
                }

                // DeviceId 即控制器地址：IP（如 192.168.1.10）或 localhost
                string err = _sdk.Open(DeviceId);
                if (err != null)
                {
                    State = DeviceState.Error;
                    return Result.Fail($"Epson 控制器 [{DeviceId}] 连接失败: {err}");
                }

                State = DeviceState.Connected;

                // 连接后默认伺服上电（生产环境如需人工确认上电，可注释此行）
                _sdk.SetMotorsOn(true);

                return Result.Ok();
            }
            catch (Exception ex)
            {
                State = DeviceState.Error;
                return Result.Fail($"Epson 控制器连接异常: {ex.Message}", ex: ex);
            }
        }

        public Result Disconnect()
        {
            try
            {
                if (_sdk != null)
                {
                    _sdk.Dispose();
                    _sdk = null;
                }
                State = DeviceState.Disconnected;
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail($"断开 Epson 控制器异常: {ex.Message}", ex: ex);
            }
        }

        public Result CheckStatus()
        {
            LastHeartbeatAt = DateTime.UtcNow;

            if (!IsSdkOpen)
            {
                State = DeviceState.Disconnected;
                return Result.Fail("Epson 控制器未连接");
            }

            State = DeviceState.Connected;
            return Result.Ok();
        }

        #endregion

        #region IDevice 通用参数

        public Result SetParam(string key, object value)
        {
            if (value == null) return Result.Fail("参数值不能为 null");
            ConfigParams[key] = value;
            return Result.Ok();
        }

        public Result<object> GetParam(string key)
        {
            object v;
            return ConfigParams.TryGetValue(key, out v)
                ? Result<object>.Ok(v)
                : Result<object>.Fail($"{key}键不存在");
        }

        #endregion

        #region IMotionCard —— 轴配置（Epson 由示教/项目文件管理，此处为兼容空实现）

        public Result SetAxisType(int axis, int atype)
        {
            // SCARA 轴类型由 RC+ 工程决定，上位机不修改
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result SetUnits(int axis, float units)
        {
            // Epson 坐标单位固定为 mm/deg，无需脉冲当量
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result SetMotionParam(int axis, MotionParam param)
        {
            // 速度/加速度由 RC+ 工程或 Speed 属性管理，此处仅记录
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result SetSoftLimits(int axis, float positiveLimit, float negativeLimit)
        {
            // 软限位由 RC+ 安全配置管理，上位机不修改
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result SetCommandPosition(int axis, float position)
        {
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            _pos[axis] = position;
            return Result.Ok();
        }

        public Result<float> GetCommandPosition(int axis)
        {
            if (!IsSdkOpen) return Result<float>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<float>.Fail($"非法轴号 {axis}");
            return Result<float>.Ok(_sdk.GetPosition(axis));
        }

        public Result<float> GetFeedbackPosition(int axis)
        {
            // 与指令位置一致（适配层为指令位置跟踪，见 SpeLNetController.GetPosition 注释）
            return GetCommandPosition(axis);
        }

        public Result<float> GetCurrentSpeed(int axis)
        {
            // Epson 运动为同步完成（WaitRobot 后静止），当前速度恒为 0
            return IsSdkOpen ? Result<float>.Ok(0f) : Result<float>.Fail("控制器未连接");
        }

        public Result<AxisStatusFlags> GetAxisStatus(int axis)
        {
            if (!IsSdkOpen) return Result<AxisStatusFlags>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<AxisStatusFlags>.Fail($"非法轴号 {axis}");

            // 报警/限位等详细状态由 RC+ 系统事件管理，此处返回正常。
            // 需要精细状态时可在适配层订阅 Spel 系统事件（EStop/Error 等）。
            return Result<AxisStatusFlags>.Ok(AxisStatusFlags.Normal);
        }

        public Result<bool> IsAxisIdle(int axis)
        {
            if (!IsSdkOpen) return Result<bool>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<bool>.Fail($"非法轴号 {axis}");
            return Result<bool>.Ok(_sdk.IsMotionDone());
        }

        #endregion

        #region IMotionCard —— 使能与停止

        public Result SetAxisEnable(int axis, bool enable)
        {
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            if (!IsSdkOpen) return Result.Fail("控制器未连接");

            // Epson 伺服为整机上电（MotorsOn），不分轴使能
            string err = _sdk.SetMotorsOn(enable);
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result StopAxis(int axis)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            string err = _sdk.Halt();
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result RapidStop()
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            string err = _sdk.Halt();
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        #endregion

        #region IMotionCard —— 运动 / JOG / 回零

        public Result JogMove(int axis, int direction)
        {
            // SCARA 点动通常在示教器/RC+ 界面完成；
            // 这里提供 1mm 微动以便上位机微调（走 Go 全坐标）
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");

            float delta = (direction >= 0 ? 1f : -1f);
            return MoveAbsolute(axis, _sdk.GetPosition(axis) + delta, 0);
        }

        public Result MoveRelative(int axis, float distance, float speed)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            return MoveAbsolute(axis, _sdk.GetPosition(axis) + distance, speed);
        }

        /// <summary>
        /// 单轴绝对运动：补全其余三轴当前位置后，整点位四轴 Go 运动。
        /// speed 单位 mm/s，&lt;= 0 时沿用控制器当前速度。
        /// </summary>
        public Result MoveAbsolute(int axis, float position, float speed)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}（Epson SCARA 仅支持 0=X/1=Y/2=Z/3=U）");

            // 以控制器当前位置为基准，仅替换目标轴 → 组成四轴目标点
            float x = _sdk.GetPosition(AxisX);
            float y = _sdk.GetPosition(AxisY);
            float z = _sdk.GetPosition(AxisZ);
            float u = _sdk.GetPosition(AxisU);

            switch (axis)
            {
                case AxisX: x = position; break;
                case AxisY: y = position; break;
                case AxisZ: z = position; break;
                case AxisU: u = position; break;
            }

            string err = _sdk.MoveTo(x, y, z, u, speed, linear: false);
            if (err != null) return Result.Fail($"轴{axis} 运动失败: {err}");

            _pos[axis] = position;
            return Result.Ok();
        }

        /// <summary>整轴回零（Epson Home 全轴回原点）</summary>
        public Result Home(int axis, int homeMode)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");

            string err = _sdk.Home();
            if (err != null) return Result.Fail(err);

            for (int i = 0; i < AxisCount; i++) _pos[i] = 0f;
            return Result.Ok();
        }

        #endregion

        #region IMotionCard —— IO 读写

        /// <summary>
        /// 数字输出（0 基编号）。本工位约定：
        /// 输出 0 = 吸嘴1 真空阀、输出 1 = 吸嘴2 真空阀。
        /// </summary>
        public Result SetOutput(int ioNum, bool state)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            string err = _sdk.SetOutput(ioNum, state);
            if (err != null) return Result.Fail(err);

            _outputStates[ioNum] = state;
            return Result.Ok();
        }

        /// <summary>数字输入读取（0 基编号；仿真层为输出回环）</summary>
        public Result<bool> GetInput(int ioNum)
        {
            if (!IsSdkOpen) return Result<bool>.Fail("控制器未连接");
            bool? v = _sdk.GetInput(ioNum);
            return v.HasValue ? Result<bool>.Ok(v.Value) : Result<bool>.Fail($"读取输入 {ioNum} 失败");
        }

        /// <summary>输出口状态回读（软件跟踪值）</summary>
        public Result<bool> GetOutput(int ioNum)
        {
            bool v;
            return _outputStates.TryGetValue(ioNum, out v)
                ? Result<bool>.Ok(v)
                : Result<bool>.Ok(false);
        }

        public Result ConfigAxisSignals(int axis, int homeIo, int fwdIo, int revIo, int alarmIo)
        {
            // 信号映射由 RC+ 工程配置，上位机不修改
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result SetInputInvert(int ioNum, bool isInverted)
        {
            // 输入极性由 RC+ 工程配置，上位机不修改
            return Result.Ok();
        }

        public Result<float> GetAnalogInput(int ioNum)
        {
            return Result<float>.Fail("Epson 插件暂不支持模拟量输入");
        }

        public Result SetAnalogOutput(int ioNum, float value)
        {
            return Result.Fail("Epson 插件暂不支持模拟量输出");
        }

        #endregion

        #region IMotionCard —— 多轴插补

        /// <summary>
        /// 直线插补：把参与轴的目标位置合并成四轴整点，走 CP 直线运动（Move）。
        /// </summary>
        public Result LineInterpolation(int[] axisList, float[] targetPositions, float speed, bool isAbsolute = true)
        {
            if (!IsSdkOpen) return Result.Fail("控制器未连接");
            if (axisList == null || targetPositions == null || axisList.Length != targetPositions.Length)
            {
                return Result.Fail("插补参数非法：轴列表与目标位置数量不一致");
            }

            float x = _sdk.GetPosition(AxisX);
            float y = _sdk.GetPosition(AxisY);
            float z = _sdk.GetPosition(AxisZ);
            float u = _sdk.GetPosition(AxisU);

            for (int i = 0; i < axisList.Length; i++)
            {
                int axis = axisList[i];
                if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
                float target = isAbsolute ? targetPositions[i]
                                          : _sdk.GetPosition(axis) + targetPositions[i];
                switch (axis)
                {
                    case AxisX: x = target; break;
                    case AxisY: y = target; break;
                    case AxisZ: z = target; break;
                    case AxisU: u = target; break;
                }
            }

            string err = _sdk.MoveTo(x, y, z, u, speed, linear: true);
            if (err != null) return Result.Fail($"直线插补失败: {err}");

            for (int i = 0; i < axisList.Length; i++) _pos[axisList[i]] = targetPositions[i];
            return Result.Ok();
        }

        /// <summary>圆弧插补：SCARA 4 轴场景极少使用，暂不实现</summary>
        public Result CircularInterpolation(int[] axisList, float[] midPoint, float[] endPoint, float speed, bool isAbsolute = true)
        {
            return Result.Fail("Epson 插件暂不支持圆弧插补（如需要请在 SPEL+ 工程中实现 Arc 后扩展）");
        }

        /// <summary>连续路径插补：暂不实现</summary>
        public Result ContinuousInterpolation(int[] axisList, List<InterpolationPoint> points, float speed)
        {
            return Result.Fail("Epson 插件暂不支持连续路径插补");
        }

        #endregion

        #region IIoDevice

        public Result<bool> ReadDi(int channelIndex) => GetInput(channelIndex);
        public Result<bool> ReadDo(int channelIndex) => GetOutput(channelIndex);

        public Result WriteDo(int channelIndex, bool state) => SetOutput(channelIndex, state);

        #endregion

        #region IDisposable

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                try
                {
                    Disconnect();
                }
                catch { /* 释放过程不抛异常 */ }
            }
            _disposed = true;
        }

        ~EpsonRobot()
        {
            Dispose(false);
        }

        #endregion

        private static bool IsValidAxis(int axis) => axis >= 0 && axis < AxisCount;

        /// <summary>默认构造（DeviceId = 控制器地址）</summary>
        public EpsonRobot(string deviceId)
        {
            DeviceId = deviceId;
        }
    }
}
