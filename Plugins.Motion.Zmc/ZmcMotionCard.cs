using cszmcaux;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Plugins.Motion.Zmc
{
    /// <summary>
    /// 正运动 (ZMC) 运动控制卡设备驱动实现
    /// </summary>
    public class ZmcMotionCard : IMotionCard, IIoDevice
    {
        #region IDevice 基础属性与字段

        /// <summary>设备唯一标识 (例如: IP 地址、COM 口或板卡索引)</summary>
        public string DeviceId { get; set; }

        /// <summary>设备显示名称</summary>
        public string DeviceName { get; set; }

        /// <summary>设备逻辑 Key</summary>
        public string DeviceKey { get; set; }

        /// <summary>品牌名称</summary>
        public string BrandName { get; set; } = "ZMotion";

        /// <summary>设备大类</summary>
        public DeviceCategory Category { get; set; } = DeviceCategory.MotionCard;

        public DateTime LastHeartbeatAt { get; set; }

        public Result Heartbeat()
        {
            return CheckStatus();
        }

        private DeviceState _state = DeviceState.Disconnected;

        /// <summary>设备状态变更事件</summary>
        public event EventHandler<DeviceState> StateChanged;

        /// <summary>设备当前运行/连接状态</summary>
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
        #region IIoDevice 统一 IO 抽象接口实现

        /// <summary>读取通用数字输入口 (DI)</summary>
        public Result<bool> ReadDi(int channelIndex)
        {
            return GetInput(channelIndex); // 复用底层的 GetInput 方法[cite: 29]
        }

        /// <summary>读取通用数字输出口 (DO)</summary>
        public Result<bool> ReadDo(int channelIndex)
        {
            return GetOutput(channelIndex); // 复用底层的 GetOutput 方法[cite: 29]
        }

        /// <summary>控制通用数字输出口 (DO)</summary>
        public Result WriteDo(int channelIndex, bool state)
        {
            return SetOutput(channelIndex, state); // 复用底层的 SetOutput 方法[cite: 29]
        }

        #endregion

        /// <summary>扩展配置参数字典</summary>
        public Dictionary<string, object> ConfigParams { get; } = new Dictionary<string, object>();

        /// <summary>正运动控制卡强类型连接选项配置</summary>
        public ZmcConnectionOptions ConnectionOptions { get; private set; }

        /// <summary>底层正运动 C/C++ API 通信句柄</summary>
        public IntPtr CardHandle { get; private set; } = IntPtr.Zero;

        #endregion

        #region 构造函数

        /// <summary>
        /// 2 参数构造函数 (供硬件插件工厂 CreateDevice 调用)
        /// </summary>
        public ZmcMotionCard(string deviceId, ZmcConnectionOptions options)
            : this(deviceId, deviceId, deviceId, options)
        {
        }

        /// <summary>
        /// 基础构造函数 (默认按以太网方式连接，DeviceId 作为 IP 地址)
        /// </summary>
        public ZmcMotionCard(string deviceId, string deviceName, string deviceKey)
            : this(deviceId, deviceName, deviceKey, new ZmcConnectionOptions { ConnectionType = ZmcConnectionType.Ethernet, IpAddress = deviceId })
        {
        }

        /// <summary>
        /// 完整构造函数，接收工厂传入的连接配置参数 ZmcConnectionOptions
        /// </summary>
        public ZmcMotionCard(string deviceId, string deviceName, string deviceKey, ZmcConnectionOptions options)
        {
            DeviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            DeviceName = string.IsNullOrEmpty(deviceName) ? deviceId : deviceName;
            DeviceKey = string.IsNullOrEmpty(deviceKey) ? deviceId : deviceKey;
            ConnectionOptions = options ?? new ZmcConnectionOptions { ConnectionType = ZmcConnectionType.Ethernet, IpAddress = deviceId };
        }

        #endregion

        #region IDevice 生命周期管理

        /// <summary>
        /// 根据 ZmcConnectionOptions 中指定的通信方式建立控制卡连接
        /// </summary>
        public Result Connect()
        {
            if (State == DeviceState.Connected && CardHandle != IntPtr.Zero)
                return Result.Ok();

            int ret = -1;
            IntPtr handle;

            switch (ConnectionOptions.ConnectionType)
            {
                case ZmcConnectionType.Ethernet:
                    string targetIp = !string.IsNullOrWhiteSpace(ConnectionOptions.IpAddress)
                        ? ConnectionOptions.IpAddress
                        : (ConfigParams.TryGetValue("IP", out var ipObj) ? ipObj?.ToString() : DeviceId);

                    if (string.IsNullOrWhiteSpace(targetIp))
                        return Result.Fail("连接失败：以太网 IP 地址为空。");

                    ret = zmcaux.ZAux_OpenEth(targetIp, out handle);
                    break;

                case ZmcConnectionType.Serial:
                    // CS1501 修复：使用标准的 string.Replace("COM", "") 或 string.TrimStart
                    string comStr = ConnectionOptions.ComPort ?? "COM1";
                    string portNumberStr = comStr.ToUpper().Replace("COM", "");

                    if (!uint.TryParse(portNumberStr, out uint comPortNum))
                    {
                        comPortNum = 1; // 默认 COM1
                    }

                    ret = zmcaux.ZAux_OpenCom(comPortNum, out handle);
                    break;

                case ZmcConnectionType.Pci:
                    // CS1503 修复：ZAux_OpenPci 接收 int 类型的卡号索引，移除 uint 强转
                    ret = zmcaux.ZAux_OpenPci(ConnectionOptions.PciCardIndex, out handle);
                    break;

                default:
                    return Result.Fail($"不支持的连接类型: {ConnectionOptions.ConnectionType}");
            }

            if (ret == 0 && handle != IntPtr.Zero)
            {
                CardHandle = handle;
                State = DeviceState.Connected;
                return Result.Ok();
            }

            State = DeviceState.Disconnected;
            return Result.Fail($"连接正运动控制卡失败 [{ConnectionOptions.ConnectionType}]，标识: {DeviceId}，错误码: {ret}");
        }
        /// <summary>
        /// 断开板卡通信连接并释放 SDK 句柄
        /// </summary>
        public Result Disconnect()
        {
            if (CardHandle != IntPtr.Zero)
            {
                zmcaux.ZAux_Close(CardHandle);
                CardHandle = IntPtr.Zero;
            }
            State = DeviceState.Disconnected;
            return Result.Ok();
        }

        /// <summary>
        /// 校验系统/控制器通信链路健康状态
        /// </summary>
        public Result CheckStatus()
        {
            if (State != DeviceState.Connected || CardHandle == IntPtr.Zero)
                return Result.Fail("设备未连接。");

            int status = 0;
            // 读取控制器全局 MAIN_STATUS 变量检测是否通信正常
            int ret = zmcaux.ZAux_Direct_GetVariableInt(CardHandle, "MAIN_STATUS", ref status);
            return ret == 0 ? Result.Ok() : Result.Fail($"获取设备状态失败，错误码: {ret}");
        }

        /// <summary>
        /// 设置扩展自定义参数
        /// </summary>
        public Result SetParam(string key, object value)
        {
            if (string.IsNullOrWhiteSpace(key)) return Result.Fail("键值不能为空。");
            ConfigParams[key] = value;
            return Result.Ok();
        }

        /// <summary>
        /// 读取扩展自定义参数
        /// </summary>
        public Result<object> GetParam(string key)
        {
            if (ConfigParams.TryGetValue(key, out var val))
                return Result<object>.Ok(val);

            return Result<object>.Fail($"参数键 [{key}] 不存在。");
        }

        #endregion

        #region IMotionCard - 轴参数与状态

        /// <summary>设置轴类型 (例如: 1-脉冲轴, 7-脉冲+方向编码器, 65-Bus 虚拟轴等)</summary>
        public Result SetAxisType(int axis, int atype)
        {
            int ret = zmcaux.ZAux_Direct_SetAtype(CardHandle, axis, atype);
            return ret == 0 ? Result.Ok() : Result.Fail($"设置轴 [{axis}] 类型失败: {ret}");
        }

        /// <summary>设置轴的脉冲当量 (Units: 每毫米/角度对应的脉冲数)</summary>
        public Result SetUnits(int axis, float units)
        {
            int ret = zmcaux.ZAux_Direct_SetUnits(CardHandle, axis, units);
            return ret == 0 ? Result.Ok() : Result.Fail($"设置轴 [{axis}] 脉冲当量失败: {ret}");
        }

        /// <summary>设置轴运动参数 (运行速度、加速度、减速时间、初速)</summary>
        public Result SetMotionParam(int axis, MotionParam param)
        {
            int ret1 = zmcaux.ZAux_Direct_SetSpeed(CardHandle, axis, param.Speed);
            int ret2 = zmcaux.ZAux_Direct_SetAccel(CardHandle, axis, param.Accel);
            int ret3 = zmcaux.ZAux_Direct_SetDecel(CardHandle, axis, param.Decel);
            int ret4 = zmcaux.ZAux_Direct_SetCreep(CardHandle, axis, param.CreepSpeed);

            //zmcaux.ZAux_Direct_SetAtype(CardHandle, axis, );//轴类型
            zmcaux.ZAux_Direct_SetUnits(CardHandle, axis, param.Unit);//脉冲当量
            zmcaux.ZAux_Direct_SetLspeed(CardHandle, axis, param.Lspeed);//起始速度
            zmcaux.ZAux_Direct_SetSramp(CardHandle, axis, param.Sramp);//S曲线加减速

            if (ret1 == 0 && ret2 == 0 && ret3 == 0 && ret4 == 0)
                return Result.Ok();

            return Result.Fail($"下发轴 [{axis}] 运动参数失败。");
        }

        /// <summary>回读轴运动参数（速度/加减速/当量等），供 UI 显示控制器当前真实值</summary>
        public Result<MotionParam> GetMotionParam(int axis)
        {
            if (CardHandle == IntPtr.Zero) return Result<MotionParam>.Fail("运动卡未连接");

            float speed = 0, accel = 0, decel = 0, creep = 0, unit = 0, lspeed = 0, sramp = 0;
            int r1 = zmcaux.ZAux_Direct_GetSpeed(CardHandle, axis, ref speed);
            int r2 = zmcaux.ZAux_Direct_GetAccel(CardHandle, axis, ref accel);
            int r3 = zmcaux.ZAux_Direct_GetDecel(CardHandle, axis, ref decel);
            int r4 = zmcaux.ZAux_Direct_GetCreep(CardHandle, axis, ref creep);
            int r5 = zmcaux.ZAux_Direct_GetUnits(CardHandle, axis, ref unit);
            int r6 = zmcaux.ZAux_Direct_GetLspeed(CardHandle, axis, ref lspeed);
            int r7 = zmcaux.ZAux_Direct_GetSramp(CardHandle, axis, ref sramp);

            if (r1 != 0 || r2 != 0 || r3 != 0 || r4 != 0 || r5 != 0 || r6 != 0 || r7 != 0)
                return Result<MotionParam>.Fail($"回读轴 [{axis}] 运动参数失败。");

            return Result<MotionParam>.Ok(new MotionParam
            {
                Speed = speed,
                Accel = accel,
                Decel = decel,
                CreepSpeed = creep,
                Unit = unit,
                Lspeed = lspeed,
                Sramp = sramp
            });
        }

        /// <summary>下发轴的正/反向软限位坐标</summary>
        public Result SetSoftLimits(int axis, float positiveLimit, float negativeLimit)
        {
            int ret1 = zmcaux.ZAux_Direct_SetFsLimit(CardHandle, axis, positiveLimit);
            int ret2 = zmcaux.ZAux_Direct_SetRsLimit(CardHandle, axis, negativeLimit);

            return (ret1 == 0 && ret2 == 0) ? Result.Ok() : Result.Fail($"设置轴 [{axis}] 软限位失败。");
        }

        /// <summary>回读轴软限位：[0]=正软限位(FsLimit)，[1]=负软限位(RsLimit)</summary>
        public Result<float[]> GetSoftLimits(int axis)
        {
            if (CardHandle == IntPtr.Zero) return Result<float[]>.Fail("运动卡未连接");

            float fs = 0, rs = 0;
            int r1 = zmcaux.ZAux_Direct_GetFsLimit(CardHandle, axis, ref fs);
            int r2 = zmcaux.ZAux_Direct_GetRsLimit(CardHandle, axis, ref rs);
            if (r1 != 0 || r2 != 0)
                return Result<float[]>.Fail($"回读轴 [{axis}] 软限位失败。");

            return Result<float[]>.Ok(new[] { fs, rs });
        }

        /// <summary>修改规划位置指令 DPOS</summary>
        public Result SetCommandPosition(int axis, float position)
        {
            int ret = zmcaux.ZAux_Direct_SetDpos(CardHandle, axis, position);
            return ret == 0 ? Result.Ok() : Result.Fail($"写入轴 [{axis}] DPOS 失败: {ret}");
        }

        /// <summary>获取规划位置指令 DPOS</summary>
        public Result<float> GetCommandPosition(int axis)
        {
            float pos = 0;
            int ret = zmcaux.ZAux_Direct_GetDpos(CardHandle, axis, ref pos);
            return ret == 0 ? Result<float>.Ok(pos) : Result<float>.Fail($"获取轴 [{axis}] DPOS 失败: {ret}");
        }

        /// <summary>获取编码器反馈实际位置 MPOS</summary>
        public Result<float> GetFeedbackPosition(int axis)
        {
            float pos = 0;
            int ret = zmcaux.ZAux_Direct_GetMpos(CardHandle, axis, ref pos);
            return ret == 0 ? Result<float>.Ok(pos) : Result<float>.Fail($"获取轴 [{axis}] MPOS 失败: {ret}");
        }

        /// <summary>获取轴当前实时运动速度</summary>
        public Result<float> GetCurrentSpeed(int axis)
        {
            float speed = 0;
            int ret = zmcaux.ZAux_Direct_GetVpSpeed(CardHandle, axis, ref speed);
            return ret == 0 ? Result<float>.Ok(speed) : Result<float>.Fail($"获取轴 [{axis}] 当前速度失败: {ret}");
        }

        /// <summary>读取并解析轴状态标志 (正限位、反限位、报警等)</summary>
        public Result<AxisStatusFlags> GetAxisStatus(int axis)
        {
            int status = 0;
            int ret = zmcaux.ZAux_Direct_GetAxisStatus(CardHandle, axis, ref status);
            if (ret != 0) return Result<AxisStatusFlags>.Fail($"读取轴 [{axis}] 状态失败: {ret}");

            // ZMC AXIS_STATUS 字位定义（与 Contracts.AxisStatusFlags 枚举对齐）：
            // bit0=正限位, bit1=负限位, bit2=伺服报警, bit3=原点开关。
            // 注意：旧实现整体错位一位（bit1→FwdLimit/bit2→RevLimit/bit3→Alarm），
            // 会导致限位/报警误判（如原点信号被当成报警导致业务过程中止）。
            AxisStatusFlags flags = AxisStatusFlags.Normal;
            if ((status & (1 << 0)) != 0) flags |= AxisStatusFlags.FwdLimit;
            if ((status & (1 << 1)) != 0) flags |= AxisStatusFlags.RevLimit;
            if ((status & (1 << 2)) != 0) flags |= AxisStatusFlags.Alarm;
            if ((status & (1 << 3)) != 0) flags |= AxisStatusFlags.HomeSwitch;

            return Result<AxisStatusFlags>.Ok(flags);
        }

        /// <summary>查询轴当前是否处于空闲停止状态</summary>
        public Result<bool> IsAxisIdle(int axis)
        {
            int idle = 0;
            int ret = zmcaux.ZAux_Direct_GetIfIdle(CardHandle, axis, ref idle);
            return ret == 0 ? Result<bool>.Ok(idle == -1 || idle == 1) : Result<bool>.Fail($"查询轴 [{axis}] 空闲状态失败: {ret}");
        }

        #endregion

        #region IMotionCard - 使能与停止

        /// <summary>控制轴伺服使能开/关</summary>
        public Result SetAxisEnable(int axis, bool enable)
        {
            int ret = zmcaux.ZAux_Direct_SetAxisEnable(CardHandle, axis, enable ? 1 : 0);
            return ret == 0 ? Result.Ok() : Result.Fail($"设置轴 [{axis}] 使能状态失败: {ret}");
        }

        /// <summary>减速停止指定轴</summary>
        public Result StopAxis(int axis)
        {
            int ret = zmcaux.ZAux_Direct_Single_Cancel(CardHandle, axis, 2); // 2 代表减速停止
            return ret == 0 ? Result.Ok() : Result.Fail($"停止轴 [{axis}] 失败: {ret}");
        }

        /// <summary>所有轴紧急停止</summary>
        public Result RapidStop()
        {
            int ret = zmcaux.ZAux_Direct_Rapidstop(CardHandle, 2);
            return ret == 0 ? Result.Ok() : Result.Fail($"全轴急停失败: {ret}");
        }

        #endregion

        #region IMotionCard - 单轴运动 & JOG & 回零

        /// <summary>启动指定轴的点动 (JOG) 运动，direction >= 0 为正向，< 0 为反向</summary>
        public Result JogMove(int axis, int direction)
        {
            int dir = direction >= 0 ? 1 : -1;
            int ret = zmcaux.ZAux_Direct_Single_Vmove(CardHandle, axis, dir);
            return ret == 0 ? Result.Ok() : Result.Fail($"轴 [{axis}] JOG 启动失败: {ret}");
        }

        /// <summary>控制单轴执行相对位置运动</summary>
        public Result MoveRelative(int axis, float distance, float speed)
        {
            float safeSpeed = ResolveSafeSpeed(axis, speed);
            zmcaux.ZAux_Direct_SetSpeed(CardHandle, axis, safeSpeed);
            int ret = zmcaux.ZAux_Direct_Single_Move(CardHandle, axis, distance);
            return ret == 0 ? Result.Ok() : Result.Fail($"轴 [{axis}] 相对运动失败: {ret}");
        }

        /// <summary>控制单轴执行绝对位置运动</summary>
        public Result MoveAbsolute(int axis, float position, float speed)
        {
            float safeSpeed = ResolveSafeSpeed(axis, speed);
            zmcaux.ZAux_Direct_SetSpeed(CardHandle, axis, safeSpeed);
            int ret = zmcaux.ZAux_Direct_Single_MoveAbs(CardHandle, axis, position);
            return ret == 0 ? Result.Ok() : Result.Fail($"轴 [{axis}] 绝对运动失败: {ret}");
        }

        /// <summary>
        /// 【门型运动 Jump】ZMC 脉冲卡没有"门型/点位运动族"这一概念，无法表达
        /// "先抬到 limZ → 水平走 → 降目标"。
        ///
        /// ★这里**显式返回 Fail**，绝不静默按"XY 平移 + 降 Z"顶替：
        ///   静默顶替会把「安全通过高度被忽略」这件事盖住 —— 正是本次要修的病灶形态
        ///   （吸持工件在低位横穿 / 走 L 形拐角）。调用方必须拿到 Fail 后**显式降级并留痕**。
        /// </summary>
        public Result MoveJump(float x, float y, float z, float u, float limZ, float speed)
        {
            return Result.Fail("ZMC 运动卡不支持门型运动(JUMP)，无法表达'先抬到安全高度再水平走'"
                + "（本卡仅单轴脉冲运动）；调用方应降级为【先抬 Z 到安全高度 → XY 走位 → 再降 Z】的分段走位");
        }

        /// <summary>
        /// 解析安全的运动速度：speed &lt;= 0 时读取轴当前设定速度（ZAux_Direct_GetSpeed）沿用，
        /// 仍无效则兜底 50。
        /// 严禁 SetSpeed(0) 后下发 MoveAbs：ZMC 控制器速度 0 时指令被缓冲但轴不运动，
        /// IDLE 恒为 0，上位机"等位"逻辑将一直轮询到超时（现象：点击启动无响应）。
        /// 速度单位 = 轴 UNITS（通常 mm/s 或 deg/s）。
        /// </summary>
        private float ResolveSafeSpeed(int axis, float speed)
        {
            if (speed > 0) return speed;

            float cur = 0;
            if (zmcaux.ZAux_Direct_GetSpeed(CardHandle, axis, ref cur) == 0 && cur > 0)
                return cur;

            return 50f; // 兜底：控制器从未设置过该轴速度时给一个合理默认值
        }

        /// <summary>触发指定轴的原点回归 (Datum)</summary>
        public Result Home(int axis, int homeMode)
        {
            zmcaux.ZAux_Direct_SetDatumIn(CardHandle, axis, 2); // 2 对应IO输入端口2；配置原点信号。ZMC系列默认OFF时信号有效，常开传感器需要反转输入口为ON
            //zmcaux.ZAux_Direct_SetInvertIn(CardHandle, axis, 1);//常开常闭反转
            int ret = zmcaux.ZAux_Direct_Single_Datum(CardHandle, axis, homeMode);
            return ret == 0 ? Result.Ok() : Result.Fail($"轴 [{axis}] 回零触发失败: {ret}");
        }

        #endregion

        #region IMotionCard - IO 控制与硬件信号映射

        /// <summary>读取通用数字输入口 (IN) 状态</summary>
        public Result<bool> GetInput(int ioNum)
        {
            uint state = 0;
            int ret = zmcaux.ZAux_Direct_GetIn(CardHandle, ioNum, ref state);
            //Console.WriteLine($"读取输入口 IN({ioNum}) 状态: {state}");
            return ret == 0 ? Result<bool>.Ok(state == 1) : Result<bool>.Fail($"读取输入口 IN({ioNum}) 失败: {ret}");
        }

        /// <summary>写入通用数字输出口 (OUT) 状态</summary>
        public Result SetOutput(int ioNum, bool state)
        {
            int ret = zmcaux.ZAux_Direct_SetOp(CardHandle, ioNum, (uint)(state ? 1 : 0));
            return ret == 0 ? Result.Ok() : Result.Fail($"写入输出口 OUT({ioNum}) 失败: {ret}");
        }

        /// <summary>读取通用数字输出口 (OUT) 当前状态</summary>
        public Result<bool> GetOutput(int ioNum)
        {
            uint state = 0;
            int ret = zmcaux.ZAux_Direct_GetOp(CardHandle, ioNum, ref state);
            return ret == 0 ? Result<bool>.Ok(state == 1) : Result<bool>.Fail($"读取输出口 OUT({ioNum}) 失败: {ret}");
        }

        /// <summary>配置轴对应的原点、正限位、反限位以及驱动器报警 IO 通道号</summary>
        public Result ConfigAxisSignals(int axis, int homeIo, int fwdIo, int revIo, int alarmIo)
        {
            int r1 = zmcaux.ZAux_Direct_SetDatumIn(CardHandle, axis, homeIo);
            int r2 = zmcaux.ZAux_Direct_SetFwdIn(CardHandle, axis, fwdIo);
            int r3 = zmcaux.ZAux_Direct_SetRevIn(CardHandle, axis, revIo);
            int r4 = zmcaux.ZAux_Direct_SetAlmIn(CardHandle, axis, alarmIo);

            return (r1 == 0 && r2 == 0 && r3 == 0 && r4 == 0)
                ? Result.Ok()
                : Result.Fail($"配置轴 [{axis}] 硬件信号映射失败。");
        }

        /// <summary>设置数字量输入口极性 (如高/低电平翻转)</summary>
        public Result SetInputInvert(int ioNum, bool isInverted)
        {
            int ret = zmcaux.ZAux_Direct_SetInvertIn(CardHandle, ioNum, isInverted ? 1 : 0);
            return ret == 0 ? Result.Ok() : Result.Fail($"设置输入口 IN({ioNum}) 极性失败: {ret}");
        }

        /// <summary>读取模拟量输入 (AD) 值</summary>
        public Result<float> GetAnalogInput(int ioNum)
        {
            float val = 0;
            int ret = zmcaux.ZAux_Direct_GetAD(CardHandle, ioNum, ref val);
            return ret == 0 ? Result<float>.Ok(val) : Result<float>.Fail($"读取模拟量 AD({ioNum}) 失败: {ret}");
        }

        /// <summary>设置模拟量输出 (DA) 值</summary>
        public Result SetAnalogOutput(int ioNum, float value)
        {
            int ret = zmcaux.ZAux_Direct_SetDA(CardHandle, ioNum, value);
            return ret == 0 ? Result.Ok() : Result.Fail($"设置模拟量 DA({ioNum}) 失败: {ret}");
        }

        #endregion

        #region IMotionCard - 多轴插补运动

        /// <summary>多轴直线插补</summary>
        public Result LineInterpolation(int[] axisList, float[] targetPositions, float speed, bool isAbsolute = true)
        {
            if (axisList == null || targetPositions == null || axisList.Length != targetPositions.Length)
                return Result.Fail("插补参数不匹配：轴列表与坐标数组长度必须一致。");

            // 绑主轴基极组
            zmcaux.ZAux_Direct_Base(CardHandle, axisList.Length, axisList);
            zmcaux.ZAux_Direct_SetSpeed(CardHandle, axisList[0], speed);

            int ret = isAbsolute
                ? zmcaux.ZAux_Direct_MoveAbs(CardHandle, axisList.Length, axisList, targetPositions)
                : zmcaux.ZAux_Direct_Move(CardHandle, axisList.Length, axisList, targetPositions);

            return ret == 0 ? Result.Ok() : Result.Fail($"直线插补执行失败: {ret}");
        }

        /// <summary>两轴圆弧插补 (按中间点与终点描绘)</summary>
        public Result CircularInterpolation(int[] axisList, float[] midPoint, float[] endPoint, float speed, bool isAbsolute = true)
        {
            if (axisList == null || axisList.Length < 2)
                return Result.Fail("圆弧插补至少需要 2 个轴。");

            if (midPoint == null || midPoint.Length < 2 || endPoint == null || endPoint.Length < 2)
                return Result.Fail("中间点和终点坐标必须至少包含 2 个维度 (X, Y)。");

            zmcaux.ZAux_Direct_Base(CardHandle, axisList.Length, axisList);
            zmcaux.ZAux_Direct_SetSpeed(CardHandle, axisList[0], speed);

            int ret = isAbsolute
                ? zmcaux.ZAux_Direct_MoveCirc2Abs(CardHandle, axisList.Length, axisList, midPoint[0], midPoint[1], endPoint[0], endPoint[1])
                : zmcaux.ZAux_Direct_MoveCirc2(CardHandle, axisList.Length, axisList, midPoint[0], midPoint[1], endPoint[0], endPoint[1]);

            return ret == 0 ? Result.Ok() : Result.Fail($"圆弧插补执行失败: {ret}");
        }

        /// <summary>连续轨迹插补缓冲区压栈</summary>
        public Result ContinuousInterpolation(int[] axisList, List<InterpolationPoint> points, float speed)
        {
            if (axisList == null || points == null || points.Count == 0)
                return Result.Fail("连续插补点路径为空。");

            zmcaux.ZAux_Direct_Base(CardHandle, axisList.Length, axisList);
            zmcaux.ZAux_Direct_SetSpeed(CardHandle, axisList[0], speed);

            foreach (var pt in points)
            {
                int ret = zmcaux.ZAux_Direct_MoveAbs(CardHandle, axisList.Length, axisList, pt.Position);
                if (ret != 0) return Result.Fail($"压入连续插补缓冲区失败，错误码: {ret}");
            }

            return Result.Ok();
        }

        #endregion

        #region IDisposable 资源释放

        private bool _disposed = false;

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
                // 释放托管与非托管连接句柄
                Disconnect();
            }
            _disposed = true;
        }

        ~ZmcMotionCard()
        {
            Dispose(false);
        }

        #endregion
    }
}