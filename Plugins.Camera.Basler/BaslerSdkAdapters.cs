using Grayson.Vision.Contracts.Devices;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Plugins.Camera.Basler
{
    // ======================================================================
    // 巴斯勒相机 SDK 适配层
    //
    // 设计目的：
    //   BaslerCamera（对外实现 ICamera 契约）不直接引用 pylon SDK 类型，
    //   而是通过本适配层访问硬件。这样做有两个好处：
    //   1. 本机未安装 pylon SDK（无 Basler.Pylon.dll）时，工程依然可以编译，
    //      并自动切换到“离线仿真相机”，业务流/节点可以先联调；
    //   2. 更换 pylon 大版本（如 pylon 6 → pylon 7）时，只需修改
    //      PylonNetAdapter.cs 一个文件，业务代码（BaslerCamera）零改动。
    //
    // 选择逻辑见 BaslerSdkFactory.Create：
    //   DLLLib\Basler.Pylon.dll 存在 → 真实 pylon 适配层（编译期由 BASLER_PYLON 宏控制）
    //   不存在                     → SimulatedBaslerSdk（合成工件测试图）
    // ======================================================================

    /// <summary>SDK 层设备枚举信息（SN / 型号 / 显示名）</summary>
    internal sealed class BaslerSdkDeviceInfo
    {
        public string SerialNumber { get; set; }
        public string ModelName { get; set; }
        public string FriendlyName { get; set; }
    }

    /// <summary>
    /// 巴斯勒相机 SDK 适配器（单台相机实例）。
    /// 约定：所有方法返回 null 表示成功，返回非 null 字符串为错误消息，
    /// 与契约层 Result.Fail(msg) 直接对接。
    /// </summary>
    internal interface IBaslerSdkCamera : IDisposable
    {
        /// <summary>是否仿真适配层（用于日志提示）</summary>
        bool IsSimulated { get; }

        /// <summary>当前是否已打开（连接）</summary>
        bool IsOpen { get; }

        /// <summary>打开相机（按序列号定位）</summary>
        string Open(string serialNumber);

        /// <summary>关闭相机并释放 SDK 资源</summary>
        string Close();

        /// <summary>
        /// 写 GenICam 参数节点。
        /// name 为 pylon 标准节点名，如 "ExposureTimeRaw"、"GainRaw"、
        /// "TriggerMode"、"TriggerSource"、"AcquisitionFrameRate" 等。
        /// </summary>
        string SetNode(string name, object value);

        /// <summary>读 GenICam 参数节点；读取失败返回 null</summary>
        object GetNode(string name);

        /// <summary>
        /// 开启采集流。每收到一帧通过 frameSink 回调上抛
        /// （帧数据已转换为 MONO8 / BGR24 标准格式）。
        /// </summary>
        string StartGrabbing(Action<FrameEventArgs> frameSink);

        /// <summary>停止采集流</summary>
        string StopGrabbing();

        /// <summary>执行一次软触发（需先设置 TriggerMode=On + TriggerSource=Software）</summary>
        string TriggerSoftware();

        /// <summary>
        /// 相机侧完整配置为软触发取图（TriggerSelector/TriggerMode/TriggerSource/
        /// 关帧率限制），并在取流后等待「武装就绪」再允许触发。
        /// 返回 null 表示成功；非 null 为错误消息（可忽略的次要节点失败也一并拼接）。
        /// </summary>
        string ConfigureSoftwareTrigger();
    }

    /// <summary>巴斯勒 SDK 工厂（设备枚举 + 相机实例创建）</summary>
    internal interface IBaslerSdk
    {
        /// <summary>是否仿真适配层</summary>
        bool IsSimulated { get; }

        /// <summary>枚举当前在线的所有巴斯勒相机（GigE / USB）</summary>
        List<BaslerSdkDeviceInfo> EnumerateDevices();

        /// <summary>按序列号创建相机适配实例（未打开状态，随后调用 Open）</summary>
        IBaslerSdkCamera CreateCamera(string serialNumber);
    }

    /// <summary>
    /// SDK 工厂入口：根据 DLLLib\Basler.Pylon.dll 是否存在自动选择真实/仿真适配层。
    /// </summary>
    internal static class BaslerSdkFactory
    {
        private static IBaslerSdk _instance;
        private static readonly object _lock = new object();

        /// <summary>全局唯一 SDK 适配器实例</summary>
        public static IBaslerSdk Instance
        {
            get
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
#if BASLER_PYLON
                        _instance = new PylonNetSdk();
#else
                        // 未检测到 DLLLib\Basler.Pylon.dll，使用离线仿真：
                        // 相机可正常“连接/采图”，图像为程序合成的工件测试图，
                        // 便于在没有真实巴斯勒相机时先行验证业务流。
                        _instance = new SimulatedBaslerSdk();
#endif
                    }
                    return _instance;
                }
            }
        }
    }

    // ======================================================================
    // 离线仿真适配层
    // ======================================================================

    /// <summary>
    /// 巴斯勒 SDK 离线仿真实现。
    ///
    /// 用途：本机未安装 pylon SDK / 未接真实相机时，提供一个行为完整的
    /// “虚拟巴斯勒相机”，让「采图节点 → 匹配 → 标定 → 机器人吸取」的
    /// 整条业务流可以在办公室环境先行跑通。
    ///
    /// 仿真图像内容：暗背景 + 两块高亮“工件牌”矩形（MONO8 灰度），
    /// 与双吸嘴工位一次吸取两颗工件的场景一致。
    /// </summary>
    internal sealed class SimulatedBaslerSdk : IBaslerSdk
    {
        public bool IsSimulated => true;

        public List<BaslerSdkDeviceInfo> EnumerateDevices()
        {
            // 仿真层固定枚举出一台虚拟相机，SN 固定便于数据库持久化/恢复
            return new List<BaslerSdkDeviceInfo>
            {
                new BaslerSdkDeviceInfo
                {
                    SerialNumber = "SIM-BASLER-0001",
                    ModelName = "acA1300-30gm (Simulated)",
                    FriendlyName = "巴斯勒虚拟相机(工件工位)"
                }
            };
        }

        public IBaslerSdkCamera CreateCamera(string serialNumber)
        {
            return new SimulatedBaslerCamera(serialNumber);
        }
    }

    /// <summary>仿真相机实例：生成合成工件测试图</summary>
    internal sealed class SimulatedBaslerCamera : IBaslerSdkCamera
    {
        private readonly string _serial;
        private readonly Dictionary<string, object> _nodes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private System.Threading.Timer _continuousTimer;
        private Action<FrameEventArgs> _frameSink;
        private long _frameCounter;
        private bool _open;
        private bool _grabbing;
        private int _softwareTriggerMode; // 0=连续 1=软触发 2=硬触发

        // 仿真图像参数（模拟 1294x969 的 acA1300 相机）
        private const int Width = 1294;
        private const int Height = 969;

        public bool IsSimulated => true;
        public bool IsOpen => _open;

        public SimulatedBaslerCamera(string serialNumber)
        {
            _serial = serialNumber;

            // 预置常见 GenICam 节点默认值（与真实 ace 相机一致）
            _nodes["ExposureTimeRaw"] = 5000L;    // 曝光时间 μs
            _nodes["GainRaw"] = 0L;               // 增益
            _nodes["Width"] = (long)Width;
            _nodes["Height"] = (long)Height;
            _nodes["TriggerMode"] = "Off";        // 默认连续采集
            _nodes["TriggerSource"] = "Software";
        }

        public string Open(string serialNumber)
        {
            _open = true;
            Debug.WriteLine($"[SimBasler] 虚拟相机 {serialNumber ?? _serial} 已连接（离线仿真模式）");
            return null;
        }

        public string Close()
        {
            StopGrabbing();
            _open = false;
            return null;
        }

        public string SetNode(string name, object value)
        {
            _nodes[name] = value;
            // 记录触发模式，仿真层据此决定出图时机（连续定时出图 / 触发出图）
            if (string.Equals(name, "TriggerMode", StringComparison.OrdinalIgnoreCase))
            {
                bool on = Convert.ToString(value).Equals("On", StringComparison.OrdinalIgnoreCase) ||
                          Convert.ToString(value).Equals("1");
                _softwareTriggerMode = on ? 1 : 0;
            }
            return null;
        }

        public object GetNode(string name)
        {
            object v;
            return _nodes.TryGetValue(name, out v) ? v : null;
        }

        public string StartGrabbing(Action<FrameEventArgs> frameSink)
        {
            if (!_open) return "相机未打开";
            _frameSink = frameSink;
            _grabbing = true;

            // 连续模式下以 10fps 定时出图；触发模式下只在 TriggerSoftware() 时出图
            if (_softwareTriggerMode == 0)
            {
                _continuousTimer = new System.Threading.Timer(
                    _ => PushFrame(), null, 0, 100);
            }
            return null;
        }

        public string StopGrabbing()
        {
            _grabbing = false;
            if (_continuousTimer != null)
            {
                _continuousTimer.Dispose();
                _continuousTimer = null;
            }
            return null;
        }

        public string TriggerSoftware()
        {
            if (!_grabbing) return "采集流未开启，软触发无效";
            PushFrame();
            return null;
        }

        /// <summary>仿真层：配置软触发（仅切换出图时机，无真实节点语义）</summary>
        public string ConfigureSoftwareTrigger()
        {
            _softwareTriggerMode = 1;
            _nodes["TriggerMode"] = "On";
            _nodes["TriggerSource"] = "Software";
            return null;
        }

        /// <summary>合成一帧“工件工位”测试图并上抛</summary>
        private void PushFrame()
        {
            try
            {
                var sink = _frameSink;
                if (sink == null || !_grabbing) return;

                // 生成 MONO8 图像：深灰底 + 两块亮色工件牌
                // （两颗牌间距固定，模拟双吸嘴一次吸取两颗的上料场景）
                byte[] buf = new byte[Width * Height];
                for (int i = 0; i < buf.Length; i++) buf[i] = 40;

                // 工件牌尺寸约 30x42mm 视场内 ~120x170 像素
                DrawTile(buf, 420, 350, 120, 170, 215);
                DrawTile(buf, 620, 350, 120, 170, 215);

                long no = Interlocked.Increment(ref _frameCounter);
                sink(new FrameEventArgs
                {
                    Width = Width,
                    Height = Height,
                    Buffer = buf,
                    PixelFormat = "MONO8",
                    FrameNum = no,
                    Timestamp = DateTime.UtcNow.Ticks
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SimBasler] 仿真出图异常: {ex.Message}");
            }
        }

        /// <summary>画一块带圆角感的“工件牌”亮矩形</summary>
        private static void DrawTile(byte[] buf, int x0, int y0, int w, int h, byte gray)
        {
            for (int y = y0; y < y0 + h && y < Height; y++)
            {
                for (int x = x0; x < x0 + w && x < Width; x++)
                {
                    // 边缘渐暗，形成倒角效果，更接近真实牌的形状特征
                    int dx = Math.Min(x - x0, x0 + w - 1 - x);
                    int dy = Math.Min(y - y0, y0 + h - 1 - y);
                    int edge = Math.Min(dx, dy);
                    byte v = edge >= 6 ? gray : (byte)(gray * (40 + edge * 12) / 100);
                    buf[y * Width + x] = v;
                }
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
