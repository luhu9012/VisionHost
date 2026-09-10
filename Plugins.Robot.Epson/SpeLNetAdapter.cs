#if EPSON_SPEL
// ============================================================================
// Epson RC+ SPEL .NET SDK 真实适配层（RCAPINet.Spel）
//
// 启用条件：解决方案根目录 DLLLib\spelnet64.dll 存在
//（编译宏 EPSON_SPEL 由 csproj 按该文件是否存在自动定义）。
//
// 安装步骤（接真实机械手时）：
//   1. 安装 Epson RC+ 8.0（含 RC+ API 8.0 选件）；
//   2. 将安装目录（默认 C:\EpsonRC80\bin，随安装路径变化）下的
//      spelnet64.dll 复制到本解决方案的 DLLLib\ 文件夹；
//   3. 重新编译本项目，本文件即被编入，EpsonSdkFactory 自动切换真实 SDK。
//
// ⚠ 注意：不同 RC+ 版本（7.0 / 8.0）的 Spel 类成员略有差异。
//   本文件按 RC+ 8.0 API 手册的常用写法实现：
//     - 连接：new Spel() + Connections[0].ConnectionString + Connect()
//       （RC+ 7 老写法为 Initialize() + Project + Connect(编号)，见各处注释）
//     - 运动：Go / Move（四轴坐标重载），完成后 WaitRobot() 确认到位
//     - IO  ：On / Off / In（SPEL+ 位编号从 1 开始，适配层已做 +1 换算）
//   接硬件时请对照本机安装的《RC+ API 8.0 手册》核对方法签名。
// ============================================================================

using System;
using System.Collections.Generic;

namespace Plugins.Robot.Epson
{
    /// <summary>RCAPINet.Spel 工厂适配器</summary>
    internal sealed class SpeLNetSdk : IEpsonSdk
    {
        public bool IsSimulated => false;

        public IEpsonSdkController CreateController()
        {
            return new SpeLNetController();
        }
    }

    /// <summary>RCAPINet.Spel 单控制器适配器（默认 Robot 1）</summary>
    internal sealed class SpeLNetController : IEpsonSdkController
    {
        private RCAPINet.Spel _spel;

        /// <summary>指令位置跟踪（X Y Z U）——真实反馈见 GetPosition 注释</summary>
        private readonly float[] _pos = new float[4];

        /// <summary>输出口状态跟踪（供 GetOutput 回读，SPEL+ 无直接输出回读）</summary>
        private readonly Dictionary<int, bool> _outputs = new Dictionary<int, bool>();

        public bool IsSimulated => false;

        public bool IsOpen
        {
            get { return _spel != null; }
        }

        /// <summary>
        /// 连接控制器。
        /// address：控制器 IP（如 "192.168.1.10"）或 "localhost"（RC+ 装在本机）。
        /// </summary>
        public string Open(string address)
        {
            try
            {
                _spel = new RCAPINet.Spel();

                // RC+ 8 写法：通过连接集合指定地址后连接
                _spel.Connections[0].ConnectionString = address;
                _spel.Connections[0].Connect();

                // RC+ 7 老写法（如版本不兼容时改用）：
                //   _spel.Initialize();
                //   _spel.Project = @"C:\EpsonRC80\Projects\Mahjong\Mahjong.sprj";
                //   _spel.Connect(1);

                _spel.Robot = 1;  // 本工位使用第 1 台机械手

                return null;
            }
            catch (Exception ex)
            {
                _spel = null;
                return $"连接 Epson 控制器 [{address}] 失败: {ex.Message}";
            }
        }

        public string Close()
        {
            try
            {
                if (_spel != null)
                {
                    _spel.Disconnect();
                    _spel = null;
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"断开 Epson 控制器异常: {ex.Message}";
            }
        }

        /// <summary>伺服上/下电</summary>
        public string SetMotorsOn(bool on)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.MotorsOn = on;
                return null;
            }
            catch (Exception ex)
            {
                return $"伺服 {(on ? "上电" : "下电")}失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 四轴全坐标定位：PTP 用 Go，CP 直线插补用 Move；完成后 WaitRobot 确认。
        /// speed 单位 mm/s（SPEL+ Speed 指令口径），&lt;=0 时不修改当前速度。
        /// </summary>
        public string MoveTo(float x, float y, float z, float u, float speed, bool linear)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                if (speed > 0)
                {
                    _spel.Speed = speed;
                }

                if (linear)
                {
                    // CP 直线插补；如当前版本无四参重载，可改用
                    // _spel.Move(SpelPoint) 形式（先构造 SpelPoint 再传入）
                    _spel.Move(x, y, z, u);
                }
                else
                {
                    // PTP 关节插补（最快路径）
                    _spel.Go(x, y, z, u);
                }

                // 等待运动完成（默认 Go/Move 为异步指令，需显式等待）
                _spel.WaitRobot();

                // 指令位置跟踪（供 GetPosition 读取）
                _pos[0] = x; _pos[1] = y; _pos[2] = z; _pos[3] = u;
                return null;
            }
            catch (Exception ex)
            {
                return $"机械手运动失败: {ex.Message}";
            }
        }

        /// <summary>回机械原点</summary>
        public string Home()
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.Home();
                _spel.WaitRobot();
                for (int i = 0; i < 4; i++) _pos[i] = 0f;
                return null;
            }
            catch (Exception ex)
            {
                return $"回原点失败: {ex.Message}";
            }
        }

        /// <summary>减速停止当前运动</summary>
        public string Halt()
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                _spel.Halt();
                return null;
            }
            catch (Exception ex)
            {
                return $"停止运动失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取轴位置。当前返回软件跟踪的指令位置（每次 MoveTo 更新）。
        /// 如需编码器真实反馈，可在此替换为 RC+ API 的位置读取成员
        /// （不同版本接口不同，接硬件时对照手册补充）。
        /// </summary>
        public float GetPosition(int axis)
        {
            return (axis >= 0 && axis < 4) ? _pos[axis] : 0f;
        }

        /// <summary>运动是否完成（MoveTo 内已 WaitRobot 同步等待，恒为 true）</summary>
        public bool IsMotionDone()
        {
            return true;
        }

        /// <summary>
        /// 数字输出。业务 0 基号 → SPEL+ 物理口（EpsonIoMap：吸嘴1=15/吸嘴2=14，现场实测 2026-09-02）。
        /// 例：业务层写 0 号口（吸嘴1真空）→ 控制器 On(15)。
        /// </summary>
        public string SetOutput(int ioNumber, bool state)
        {
            if (_spel == null) return "控制器未连接";
            try
            {
                int port = EpsonIoMap.SpelPort(ioNumber);
                if (state) _spel.On(port);
                else _spel.Off(port);

                _outputs[ioNumber] = state;
                return null;
            }
            catch (Exception ex)
            {
                return $"输出 On/Off({EpsonIoMap.SpelPort(ioNumber)}) 失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 数字输入读取。外部 0 基编号 → SPEL+ 1 基编号（In 指令）。
        /// </summary>
        public bool? GetInput(int ioNumber)
        {
            if (_spel == null) return null;
            try
            {
                return Convert.ToInt32(_spel.In(ioNumber + 1)) != 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Epson] 读输入 In({ioNumber + 1}) 失败: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
#endif
