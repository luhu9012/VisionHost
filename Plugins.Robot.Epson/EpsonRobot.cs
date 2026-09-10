using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;

namespace Plugins.Robot.Epson
{
    /// <summary>
    /// Epson SCARA 4 轴机械手设备实现（本工位：双吸嘴工件分拣）。
    ///
    /// 契约映射：
    /// - 实现 IMotionCard：把 Epson 的 X/Y/Z/U 四轴映射为统一的轴号
    ///   0=X（大臂）、1=Y（小臂）、2=Z（上下）、3=U（旋转），
    ///   单轴 MoveAbsolute 内部自动补全其余三轴当前值后整点位运动（Go/Move）；
    /// - 实现 IIoDevice：双吸嘴的真空阀即两路数字输出，业务层统一 0 基编号
    ///   （0=吸嘴1真空、1=吸嘴2真空；单吸嘴机型只有吸嘴1），适配层经 EpsonIoMap
    ///   映射为 SPEL+ 物理口：吸嘴1=OUT15、吸嘴2=OUT14（现场实测 2026-09-02）。
    ///
    /// 硬件访问全部经适配层完成，传输层按优先级/配置自动选择：
    /// - 设备参数 ConnectionString 含 Protocol=TCP（如 Protocol=TCP;IP=192.168.3.11;Port=8000）
    ///   → TCP 脚本协议适配层（EpsonTcpScriptAdapter）：与 RC+ 工程里 OpenNet 开的
    ///   TCP 服务器对话（实机可用，TCP 探测/连接详见 EpsonPlugin 与通信模式文档）；
    /// - 否则按 EpsonSdkFactory 默认链：DLLLib\spelnet64.dll（RC+8）→
    ///   DLLLib\RCAPINet.dll（RC+7.x）→ 离线仿真机械手。
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

                // ---- 传输层选择 ----
                // 设备管理手动添加时 ConnectionString 会写入 ConfigParams：
                //   "Protocol=TCP;IP=127.0.0.1;Port=502" → TCP 脚本协议（模拟器联调）；
                //   未填写 / 其他值 → 工厂默认链（RC+8 SDK → RC+7 SDK → 离线仿真）。
                string connStr = ConfigParams.TryGetValue("ConnectionString", out var connObj)
                    ? connObj?.ToString() : null;
                bool tcpMode = connStr != null &&
                               connStr.IndexOf("Protocol=TCP", StringComparison.OrdinalIgnoreCase) >= 0;

                // 传输模式变化（用户改过连接串）→ 丢弃旧控制器重建
                if (_sdk != null && (_sdk is EpsonTcpScriptController) != tcpMode)
                {
                    _sdk.Dispose();
                    _sdk = null;
                }

                if (_sdk == null)
                {
                    _sdk = tcpMode
                        ? (IEpsonSdkController)new EpsonTcpScriptController()
                        : EpsonSdkFactory.Instance.CreateController();
                }

                // TCP 模式：IP/Port 从连接串解析（默认 127.0.0.1:502，与 SPEL+ SetNet 对应）；
                // SDK 模式：DeviceId 即控制器地址（IP 或 localhost）
                string address = DeviceId;
                if (tcpMode)
                {
                    string ip = ParseConnValue(connStr, "IP") ?? "127.0.0.1";
                    string port = ParseConnValue(connStr, "Port") ?? "502";
                    address = $"{ip}:{port}";
                }

                string err = _sdk.Open(address);
                if (err != null)
                {
                    State = DeviceState.Error;
                    return Result.Fail($"Epson 控制器 [{DeviceId}] 连接失败: {err}");
                }

                State = DeviceState.Connected;

                // 连接后默认伺服上电（生产环境如需人工确认上电，可注释此行）。
                // 注意：模拟器/下电状态下 MotorsOn 可能抛 SpelException——
                // 适配层已 catch 并返回错误文本，这里仅记录不阻断
                //（连接本身已成功，上电失败只影响后续运动指令）。
                string motorErr = _sdk.SetMotorsOn(true);
                if (motorErr != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Epson] 连接成功但伺服上电失败（不影响连接状态）: {motorErr}");
                }

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

            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen)
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

        public Result<MotionParam> GetMotionParam(int axis)
        {
            // Epson 速度/加速度由 RC+ 工程管理，上位机无法逐轴回读 → 返回 Fail，调用方回退默认值
            return Result<MotionParam>.Fail("Epson 轴参数由 RC+ 工程管理，不支持回读");
        }

        public Result SetSoftLimits(int axis, float positiveLimit, float negativeLimit)
        {
            // 软限位由 RC+ 安全配置管理，上位机不修改
            return IsValidAxis(axis) ? Result.Ok() : Result.Fail($"非法轴号 {axis}");
        }

        public Result<float[]> GetSoftLimits(int axis)
        {
            // 软限位由 RC+ 安全配置管理，无法回读 → 返回 Fail，调用方回退默认值
            return Result<float[]>.Fail("Epson 软限位由 RC+ 安全配置管理，不支持回读");
        }

        public Result SetCommandPosition(int axis, float position)
        {
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            _pos[axis] = position;
            return Result.Ok();
        }

        public Result<float> GetCommandPosition(int axis)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result<float>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<float>.Fail($"非法轴号 {axis}");
            return Result<float>.Ok(sdk.GetPosition(axis));
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
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result<AxisStatusFlags>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<AxisStatusFlags>.Fail($"非法轴号 {axis}");

            // 报警/限位等详细状态由 RC+ 系统事件管理，此处返回正常。
            // 需要精细状态时可在适配层订阅 Spel 系统事件（EStop/Error 等）。
            return Result<AxisStatusFlags>.Ok(AxisStatusFlags.Normal);
        }

        public Result<bool> IsAxisIdle(int axis)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result<bool>.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result<bool>.Fail($"非法轴号 {axis}");
            return Result<bool>.Ok(sdk.IsMotionDone());
        }

        #endregion

        #region IMotionCard —— 使能与停止

        public Result SetAxisEnable(int axis, bool enable)
        {
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");

            // Epson 伺服为整机上电（MotorsOn），不分轴使能
            string err = sdk.SetMotorsOn(enable);
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result StopAxis(int axis)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            string err = sdk.Halt();
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result RapidStop()
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            string err = sdk.Halt();
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        #endregion

        #region IMotionCard —— 运动 / JOG / 回零

        public Result JogMove(int axis, int direction)
        {
            // SCARA 点动通常在示教器/RC+ 界面完成；
            // 这里提供 1mm 微动以便上位机微调（走 Go 全坐标）
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");

            float delta = (direction >= 0 ? 1f : -1f);
            return MoveAbsolute(axis, sdk.GetPosition(axis) + delta, 0);
        }

        public Result MoveRelative(int axis, float distance, float speed)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
            return MoveAbsolute(axis, sdk.GetPosition(axis) + distance, speed);
        }

        /// <summary>
        /// 单轴绝对运动：补全其余三轴当前位置后，整点位四轴 Go 运动。
        /// speed 单位 mm/s，&lt;= 0 时沿用控制器当前速度。
        /// </summary>
        public Result MoveAbsolute(int axis, float position, float speed)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}（Epson SCARA 仅支持 0=X/1=Y/2=Z/3=U）");

            // 以控制器当前位置为基准，仅替换目标轴 → 组成四轴目标点
            float x = sdk.GetPosition(AxisX);
            float y = sdk.GetPosition(AxisY);
            float z = sdk.GetPosition(AxisZ);
            float u = sdk.GetPosition(AxisU);

            switch (axis)
            {
                case AxisX: x = position; break;
                case AxisY: y = position; break;
                case AxisZ: z = position; break;
                case AxisU: u = position; break;
            }

            string err = sdk.MoveTo(x, y, z, u, speed, linear: false);
            if (err != null) return Result.Fail($"轴{axis} 运动失败: {err}");

            _pos[axis] = position;
            return Result.Ok();
        }

        /// <summary>整轴回零（Epson Home 全轴回原点）</summary>
        public Result Home(int axis, int homeMode)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");

            string err = sdk.Home();
            if (err != null) return Result.Fail(err);

            for (int i = 0; i < AxisCount; i++) _pos[i] = 0f;
            return Result.Ok();
        }

        #endregion

        #region IMotionCard —— IO 读写

        /// <summary>
        /// 数字输出（业务 0 基编号，经 EpsonIoMap 映射 SPEL+ 物理口）。本工位约定：
        /// 业务 0 = 吸嘴1 真空阀 → SPEL+ OUT15；业务 1 = 吸嘴2 真空阀 → SPEL+ OUT14。
        /// 单吸嘴机型只有吸嘴1（业务 0）。
        /// </summary>
        public Result SetOutput(int ioNum, bool state)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            string err = sdk.SetOutput(ioNum, state);
            if (err != null) return Result.Fail(err);

            _outputStates[ioNum] = state;
            return Result.Ok();
        }

        /// <summary>数字输入读取（0 基编号；仿真层为输出回环）</summary>
        public Result<bool> GetInput(int ioNum)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result<bool>.Fail("控制器未连接");
            bool? v = sdk.GetInput(ioNum);
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

        #region 机械手专属 —— 四轴整点定位（调试台/业务流共用）

        /// <summary>
        /// 四轴整点 PTP 定位（Go 关节插补）：X/Y/Z/U 同时运动到目标点。
        /// SCARA 走 PTP 比逐轴补全更符合机械手习惯（各轴按关节插补同时到达），
        /// 供机械手调试台的"整点走位"与后续工位业务的吸取/放置点位运动使用。
        /// speed 单位 mm/s，&lt;= 0 时沿用控制器当前速度。
        /// </summary>
        public Result MoveToPtp(float x, float y, float z, float u, float speed)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            string err = sdk.MoveTo(x, y, z, u, speed, linear: false);
            if (err != null) return Result.Fail($"PTP 定位失败: {err}");

            _pos[AxisX] = x;
            _pos[AxisY] = y;
            _pos[AxisZ] = z;
            _pos[AxisU] = u;
            return Result.Ok();
        }

        /// <summary>一次性读取四轴当前位置（X/Y/Z/U，指令位置口径）。</summary>
        public Result<float[]> GetPositionsAll()
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result<float[]>.Fail("控制器未连接");
            return Result<float[]>.Ok(new[]
            {
                sdk.GetPosition(AxisX),
                sdk.GetPosition(AxisY),
                sdk.GetPosition(AxisZ),
                sdk.GetPosition(AxisU)
            });
        }

        #endregion

        #region IMotionCard —— 多轴插补

        /// <summary>
        /// 直线插补：把参与轴的目标位置合并成四轴整点，走 CP 直线运动（Move）。
        /// </summary>
        public Result LineInterpolation(int[] axisList, float[] targetPositions, float speed, bool isAbsolute = true)
        {
            var sdk = _sdk; // ★ 局部快照防并发竞态(Disconnect 后台置 null)
            if (sdk == null || !sdk.IsOpen) return Result.Fail("控制器未连接");
            if (axisList == null || targetPositions == null || axisList.Length != targetPositions.Length)
            {
                return Result.Fail("插补参数非法：轴列表与目标位置数量不一致");
            }

            float x = sdk.GetPosition(AxisX);
            float y = sdk.GetPosition(AxisY);
            float z = sdk.GetPosition(AxisZ);
            float u = sdk.GetPosition(AxisU);

            for (int i = 0; i < axisList.Length; i++)
            {
                int axis = axisList[i];
                if (!IsValidAxis(axis)) return Result.Fail($"非法轴号 {axis}");
                float target = isAbsolute ? targetPositions[i]
                                          : sdk.GetPosition(axis) + targetPositions[i];
                switch (axis)
                {
                    case AxisX: x = target; break;
                    case AxisY: y = target; break;
                    case AxisZ: z = target; break;
                    case AxisU: u = target; break;
                }
            }

            string err = sdk.MoveTo(x, y, z, u, speed, linear: true);
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

        /// <summary>
        /// 从分号分隔的连接串中取键值（大小写不敏感）。
        /// 例：ParseConnValue("Protocol=TCP;IP=127.0.0.1;Port=502", "IP") → "127.0.0.1"
        /// </summary>
        private static string ParseConnValue(string connStr, string key)
        {
            if (string.IsNullOrEmpty(connStr)) return null;
            foreach (string pair in connStr.Split(';', ',', '&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 &&
                    string.Equals(pair.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Substring(eq + 1).Trim();
                }
            }
            return null;
        }

        /// <summary>默认构造（DeviceId = 控制器地址）</summary>
        public EpsonRobot(string deviceId)
        {
            DeviceId = deviceId;
        }
    }
}
