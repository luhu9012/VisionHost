#if BASLER_PYLON
// ============================================================================
// 巴斯勒 pylon .NET SDK 真实适配层
//
// 启用条件：解决方案根目录 DLLLib\Basler.Pylon.dll 存在
//（编译宏 BASLER_PYLON 由 csproj 按该文件是否存在自动定义）。
//
// ⚠ 版本铁律（pylon 8.x，2026-09-01 核实）：
//   本文件按 pylon 8 的 .NET API 编写，与网上大量 pylon 5/6 教程不兼容：
//     ① 枚举设备：CameraFinder.Enumerate()        （旧版是 CameraFactory.EnumerateDevices）
//     ② 打开首台：new Camera()                     （旧版是 CameraFactory.CreateFirstDevice）
//     ③ 布尔参数：IBooleanParameter                （旧版是 IBoolParameter）
//     ④ 像素转换：converter.Convert(byte[], IImage)（旧版是 Convert<T>(IGrabResult)）
//     ⑤ PixelType 是【枚举】，需 (PixelType)grabResult.PixelTypeValue 强转
//     ⑥ IParameter 没有 SetValue，需转具体接口或用 ParseAndSetValue(string)
//     ⑦ IGrabResult 没有 Buffer 属性，原生指针用 PixelDataPointer
//     ⑧ DLLLib 必须放 net4.0/x64 版本（net8.0 版引用 System.Runtime，net472 用不了）
//
// 命名空间冲突坑（两连坑，2026-09-01 踩平）：
//   本项目命名空间是 Plugins.Camera.Basler ——
//   ① 直接写 `Camera` 会优先匹配到 Plugins.Camera 命名空间段 → CS0118；
//   ② 用 `using Pylon = Basler.Pylon;` 想规避，但 pylon SDK 自带全局命名空间 Pylon → CS0576。
// 结论：别名只能取第三方名字（Bsl），且所有 pylon 类型一律写 Bsl.Xxx，不写裸类型名。
// ============================================================================

using Grayson.Vision.Contracts.Devices;
using System;
using System.Collections.Generic;
using Bsl = Basler.Pylon;     // ★ 别名不能叫 Pylon（与 SDK 全局命名空间 Pylon 冲突）

namespace Plugins.Camera.Basler
{
    /// <summary>pylon .NET API 工厂适配器：负责设备枚举与相机实例创建</summary>
    internal sealed class PylonNetSdk : IBaslerSdk
    {
        public bool IsSimulated => false;

        /// <summary>枚举所有在线巴斯勒相机（GigE / USB / 其他传输层统一枚举）</summary>
        public List<BaslerSdkDeviceInfo> EnumerateDevices()
        {
            var list = new List<BaslerSdkDeviceInfo>();
            foreach (Bsl.ICameraInfo info in Bsl.CameraFinder.Enumerate())
            {
                list.Add(new BaslerSdkDeviceInfo
                {
                    SerialNumber = SafeGet(info, Bsl.CameraInfoKey.SerialNumber),
                    ModelName = SafeGet(info, Bsl.CameraInfoKey.ModelName),
                    FriendlyName = SafeGet(info, Bsl.CameraInfoKey.FriendlyName),
                });
            }
            return list;
        }

        private static string SafeGet(Bsl.ICameraInfo info, string key)
        {
            try { return info[key]; }
            catch { return null; }
        }

        public IBaslerSdkCamera CreateCamera(string serialNumber)
        {
            return new PylonNetCamera(serialNumber);
        }
    }

    /// <summary>pylon .NET API 单相机适配器</summary>
    internal sealed class PylonNetCamera : IBaslerSdkCamera
    {
        private readonly string _serial;
        private Bsl.Camera _camera;
        private Bsl.PixelDataConverter _converter;
        private long _frameCounter;
        private long _grabFailureCount;

        /// <summary>
        /// 取流后台线程。
        /// 弃用 ImageGrabbed 事件方式：该事件方式在 acA2500-14gc 上表现为
        /// StreamGrabber.Start() 后取流线程立即以 TaskCanceledException 退出、零帧到达。
        /// 改为手动 RetrieveBuffer 循环，既能规避事件机制的坑，又能直接拿到
        /// GrabResult.ErrorCode/ErrorDescription，定位"能连上但不出图"类问题。
        /// </summary>
        private System.Threading.Thread _grabThread;
        private volatile bool _grabbing;

        /// <summary>当前帧回调（支持采集运行中刷新，避免重复订阅同一事件）</summary>
        private Action<FrameEventArgs> _frameSink;

        public bool IsSimulated => false;

        /// <summary>当前是否已打开（pylon: Camera.IsOpen）</summary>
        public bool IsOpen
        {
            get { return _camera != null && _camera.IsOpen; }
        }

        public PylonNetCamera(string serialNumber)
        {
            _serial = serialNumber;
        }

        /// <summary>
        /// 打开相机（按序列号定位；无序列号则打开枚举到的第一台）。
        /// 注（2026-09-02）：PixelFormat/AcquisitionStatusSelector 的 not writable 锁无害
        /// （YUV422Packed 出图正常），只记日志；真正需要 DeviceReset 的病态
        /// （Start 后 IsGrabbing=False 且恢复无效）在 GrabLoop 里处理。
        /// </summary>
        public string Open(string serialNumber)
        {
            try
            {
                string sn = string.IsNullOrEmpty(serialNumber) ? _serial : serialNumber;

                // ★ 旧相机对象先干净关闭再重建（2026-09-02 修复偶发软触发 IsGrabbing=False）：
                //   之前直接 new 覆盖，旧 Camera 未显式 Close/Dispose——软触发残留态（TriggerMode=On
                //   + 固件 AcquisitionActive）可能跨对象累积，导致新会话 StreamGrabber.Start() 后
                //   IsGrabbing=False、AcquisitionStart "not writable"（相机固件认为自己在采）。
                if (_camera != null)
                {
                    try { if (_camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing) _camera.StreamGrabber.Stop(); } catch { }
                    try { if (_camera.IsOpen) _camera.Close(); } catch { }
                    try { _camera.Dispose(); } catch { }
                    _camera = null;
                }

                _camera = string.IsNullOrEmpty(sn)
                    ? new Bsl.Camera()          // 打开第一台可用设备
                    : new Bsl.Camera(sn);       // 按序列号打开
                _converter = new Bsl.PixelDataConverter();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _camera.Open();

                // ★ 采集模式必须显式设为 Continuous。
                //   pylon 不会保证默认就是连续采集（相机 UserSet 可能留着 SingleFrame），
                //   那样 StreamGrabber.Start() 后相机只等触发、永远不出图。
                string modeErr = SetNode("AcquisitionMode", "Continuous");
                if (modeErr != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] 设置连续采集模式失败（不致命）: {modeErr}");
                }

                // ★ 像素格式切换（2026-09-02）：acA2500-14gc 默认 YUV422Packed 经 PixelDataConverter
                //   转 BGR24 有斜线分屏伪影；改 BayerRG8 让 pylon 直接去马赛克（Bayer→BGR8packed）。
                //   注意：PixelFormat 是 grab 期间被锁的节点。若上一会话强关程序导致相机固件仍处于
                //   采集状态，写入会报 "Enum entry is not writable" → 先补发 AcquisitionStop 解锁再写。
                string acqStopErr = null;
                try
                {
                    var acqStop = _camera.Parameters["AcquisitionStop"] as Bsl.ICommandParameter;
                    if (acqStop != null)
                    {
                        try { acqStop.Execute(); acqStopErr = null; }
                        catch (Exception aex) { acqStopErr = aex.Message; /* 未在采集中执行会抛错 */ }
                    }
                }
                catch { /* 忽略 */ }

                string curPix = Convert.ToString(GetNode("PixelFormat"));
                string pixErr = SetNode("PixelFormat", "BayerRG8");
                if (pixErr != null)
                {
                    // ★ 2026-09-02 修正：PixelFormat 锁【不再触发 DeviceReset】！
                    //   实测 acA2500-14gc 的 PixelFormat/AcquisitionStatusSelector 节点在连接后
                    //   几乎总是 not writable（固件特性/UserSet 锁定），DeviceReset 也解不开，
                    //   但 YUV422Packed → BGR24 出图完全正常（Stride 修复后无斜线伪影）。
                    //   之前用 PixelFormat 锁触发 DeviceReset 导致每次打开干等 15~20s（相机重启）。
                    //   DeviceReset 只留给 GrabLoop 里"Start 后 IsGrabbing=False 且恢复无效"的真病态。
                    System.Diagnostics.Debug.WriteLine($"[Basler] 切换到 BayerRG8 失败(可忽略，保留 {curPix} 出图): {pixErr}" +
                        (acqStopErr != null ? $"（AcquisitionStop 也未成功: {acqStopErr}）" : ""));
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] 已切换像素格式为 BayerRG8（原 {curPix}，解决 YUV422→BGR 斜线分屏伪影）");
                }

                // ★ GigE 包大小安全化（2026-09-01 修复"能连上但不出图"）：
                //   pylon 不像海康 MVS 那样自动把 GevSCPSPacketSize 收敛到网卡 MTU。
                //   相机默认包大小常达 8192+，一旦超过网卡 MTU（未开巨帧时=1500），
                //   图像流 UDP 大包被丢弃 → 取流线程永远收不到帧 → 表现为能连、能读参数、
                //   但 Start 后零帧（伴随 TaskCanceledException 退出）。收敛到 1500 是标准
                //   以太网安全值，任何网卡都收得住；若当前已 ≤1500 则不改动。
                try
                {
                    object psObj = GetNode("GevSCPSPacketSize");
                    if (psObj != null)
                    {
                        long ps = Convert.ToInt64(psObj);
                        if (ps > 1500)
                        {
                            string psErr = SetNode("GevSCPSPacketSize", 1500L);
                            System.Diagnostics.Debug.WriteLine(
                                psErr == null
                                    ? $"[Basler] GigE 包大小 {ps} → 收敛到 1500（避免超过网卡 MTU 丢流）"
                                    : $"[Basler] GigE 包大小收敛失败(不致命): {psErr}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[Basler] GigE 包大小={ps}（≤1500，无需收敛）");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] GigE 包大小处理异常(不致命): {ex.Message}");
                }

                // ★ 连接默认强制复位为连续采集（TriggerMode=Off）—— 关键根因修复。
                //   相机硬件不会自动复位上一次软触发遗留的 TriggerMode=On；若此处不显式
                //   复位，上层（ConfigParams 缺 TriggerModeSelect 时）既不下发 Off、UI 又
                //   停在默认连续模式，导致连上后连续采集相机一直在等软触发 → 零帧、无画面。
                //   后续若 UI 选择软触发，SetTriggerMode(1) 会再把 TriggerMode 设回 On。
                string tmErr = SetNode("TriggerMode", "Off");
                if (tmErr != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] 复位触发模式为连续失败(不致命): {tmErr}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Basler] 已复位触发模式为连续采集(TriggerMode=Off)");
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Basler] Open 总耗时 {sw.ElapsedMilliseconds} ms | 相机IP={CameraIp()} | 主机IPv4=[{HostIpv4s()}]");
                DumpCameraState("已连接");
                return null;
            }
            catch (Exception ex)
            {
                return $"打开巴斯勒相机失败: {ex.Message}";
            }
        }

        /// <summary>打印相机关键状态，用于定位"能连上但不出图"类问题</summary>
        private void DumpCameraState(string phase)
        {
            try
            {
                object w = GetNode("Width"), h = GetNode("Height");
                object tm = GetNode("TriggerMode"), ts = GetNode("TriggerSource");
                object am = GetNode("AcquisitionMode"), pf = GetNode("PixelFormat");
                System.Diagnostics.Debug.WriteLine(
                    $"[Basler] {phase}: 型号={SafeCameraInfo(Bsl.CameraInfoKey.ModelName)} " +
                    $"SN={SafeCameraInfo(Bsl.CameraInfoKey.SerialNumber)} " +
                    $"尺寸={w}x{h} 像素格式={pf} | AcquisitionMode={am} TriggerMode={tm} TriggerSource={ts}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Basler] 状态打印异常: {ex.Message}");
            }
        }

        private string SafeCameraInfo(string key)
        {
            try { return _camera.CameraInfo[key]; }
            catch { return "?"; }
        }

        /// <summary>读相机 IP：优先 CameraInfo.DeviceIpAddress（GigE 的权威来源），兜底 GevCurrentIPAddress 节点</summary>
        private string CameraIp()
        {
            try
            {
                string v = _camera.CameraInfo[Bsl.CameraInfoKey.DeviceIpAddress];
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch { /* 继续兜底 */ }
            try
            {
                object g = GetNode("GevCurrentIPAddress");
                if (g != null) return Convert.ToString(g);
            }
            catch { /* 忽略 */ }
            return "?";
        }

        /// <summary>枚举本机 IPv4 地址，用于与相机 IP 比对子网（跨子网 GigE 流常收不到）</summary>
        private static string HostIpv4s()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                foreach (var addr in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        if (sb.Length > 0) sb.Append(',');
                        sb.Append(addr);
                    }
                }
                return sb.Length > 0 ? sb.ToString() : "?";
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// 相机侧 GigE 流诊断（2026-09-01）：
        /// ① AcquisitionActive —— 相机传感器是否在曝光出帧；
        /// ② GevSCDA —— 流通道 0 的目的 IP（相机把图像流发往哪，应为主机 IP）；
        /// ③ FrameTransfer —— 是否有帧在向主机传输。
        /// </summary>
        private void DumpGigEStreamDiagnostics()
        {
            try
            {
                // ① 相机是否在采集
                string setErr = SetNode("AcquisitionStatusSelector", "AcquisitionActive");
                if (setErr == null)
                {
                    object acqActive = GetNode("AcquisitionStatus");
                    System.Diagnostics.Debug.WriteLine($"[Basler] 相机采集状态 AcquisitionActive={acqActive}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] 读 AcquisitionActive 失败(不致命): {setErr}");
                }

                // ② 流通道 0 的目的 IP（GevSCDA 是 32 位整数，按大端还原成点分十进制）
                SetNode("GevStreamChannelSelector", "StreamChannel0");
                object scda = GetNode("GevSCDA");
                if (scda != null)
                {
                    long ipVal = Convert.ToInt64(scda);
                    string ip = $"{(ipVal >> 24) & 0xFF}.{(ipVal >> 16) & 0xFF}.{(ipVal >> 8) & 0xFF}.{ipVal & 0xFF}";
                    System.Diagnostics.Debug.WriteLine($"[Basler] 流目的IP GevSCDA={scda} → {ip}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Basler] 流目的IP GevSCDA 读取失败(不致命)");
                }

                // ③ 是否有帧在传输
                SetNode("AcquisitionStatusSelector", "FrameTransfer");
                object ft = GetNode("AcquisitionStatus");
                System.Diagnostics.Debug.WriteLine($"[Basler] 帧传输中 FrameTransfer={ft}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Basler] GigE 流诊断异常(不致命): {ex.Message}");
            }
        }

        /// <summary>
        /// 像素内容采样统计（2026-09-02）：min/max/均值/非零占比/不同灰度值数。
        /// 用于判定帧数据是纯黑（max=0）还是有效图像（max&gt;0 且非零占比高）。
        /// </summary>
        private static string SamplePixels(byte[] data)
        {
            try
            {
                if (data == null || data.Length == 0) return "buffer=空";
                int step = Math.Max(1, data.Length / 200000);
                long sum = 0; int minV = 255; int maxV = 0; int cnt = 0; int nonZero = 0;
                for (int i = 0; i < data.Length; i += step)
                {
                    int b = data[i];
                    if (b < minV) minV = b;
                    if (b > maxV) maxV = b;
                    sum += b; cnt++;
                    if (b != 0) nonZero++;
                }
                double mean = (double)sum / cnt;
                var seen = new bool[256];
                int uniqStep = Math.Max(1, data.Length / 100000);
                for (int i = 0; i < data.Length; i += uniqStep) seen[data[i]] = true;
                int distinct = 0;
                for (int i = 0; i < 256; i++) if (seen[i]) distinct++;
                return $"min={minV} max={maxV} mean={mean:F1} 非零占比={100.0 * nonZero / cnt:F1}% 不同值≈{distinct}";
            }
            catch (Exception ex)
            {
                return $"采样异常: {ex.Message}";
            }
        }

        /// <summary>关闭相机并释放资源</summary>
        public string Close()
        {
            try
            {
                if (_camera != null)
                {
                    // 先停取流线程，避免它在相机被 Dispose 时仍持有句柄
                    _grabbing = false;
                    if (_camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing)
                    {
                        _camera.StreamGrabber.Stop();
                    }
                    if (_grabThread != null)
                    {
                        _grabThread.Join(3000);
                        _grabThread = null;
                    }
                    if (_camera.IsOpen) _camera.Close();
                    _camera.Dispose();
                    _camera = null;
                }
                if (_converter != null)
                {
                    _converter.Dispose();
                    _converter = null;
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"关闭巴斯勒相机异常: {ex.Message}";
            }
        }

        /// <summary>
        /// 写 GenICam 节点。
        /// pylon 8 的 IParameter 没有 SetValue，需先转成具体参数接口再写；
        /// 全部无法匹配时兜底用 ParseAndSetValue(string)（pylon 按节点类型自行解析）。
        /// </summary>
        public string SetNode(string name, object value)
        {
            if (!IsOpen) return "相机未打开，无法写入节点";
            try
            {
                Bsl.IParameter param = _camera.Parameters[name];
                if (param == null) return $"节点 [{name}] 不存在";

                if (value is bool)
                {
                    var p = param as Bsl.IBooleanParameter;
                    if (p != null) { p.SetValue((bool)value); return null; }
                }
                else if (value is string)
                {
                    // 字符串优先按枚举写入（TriggerMode/TriggerSource 等都是枚举节点）
                    var e = param as Bsl.IEnumParameter;
                    if (e != null) { e.SetValue((string)value); return null; }
                    var s = param as Bsl.IStringParameter;
                    if (s != null) { s.SetValue((string)value); return null; }
                }
                else if (value is float || value is double || value is decimal)
                {
                    var p = param as Bsl.IFloatParameter;
                    if (p != null) { p.SetValue(Convert.ToDouble(value)); return null; }
                }
                else if (value is int || value is long || value is short || value is byte ||
                         value is uint || value is ulong)
                {
                    var p = param as Bsl.IIntegerParameter;
                    if (p != null) { p.SetValue(Convert.ToInt64(value)); return null; }
                }

                // 兜底：交给 pylon 按节点类型解析字符串
                param.ParseAndSetValue(Convert.ToString(value));
                return null;
            }
            catch (Exception ex)
            {
                return $"写入节点 [{name}] 失败: {ex.Message}";
            }
        }

        /// <summary>读 GenICam 节点（按参数实际类型取值）；读取失败返回 null</summary>
        public object GetNode(string name)
        {
            if (!IsOpen) return null;
            try
            {
                Bsl.IParameter param = _camera.Parameters[name];
                if (param == null) return null;

                var i = param as Bsl.IIntegerParameter;
                if (i != null) return i.GetValue();
                var f = param as Bsl.IFloatParameter;
                if (f != null) return f.GetValue();
                var b = param as Bsl.IBooleanParameter;
                if (b != null) return b.GetValue();
                var e = param as Bsl.IEnumParameter;
                if (e != null) return e.GetValue();
                var s = param as Bsl.IStringParameter;
                if (s != null) return s.GetValue();
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 开启采集流：启动后台取流线程（手动 RetrieveBuffer 循环）。
        /// 比 ImageGrabbed 事件方式更稳，且能直接拿到取流失败原因。
        /// </summary>
        public string StartGrabbing(Action<FrameEventArgs> frameSink)
        {
            if (!IsOpen) return "相机未打开，无法开启采集";

            // 已在采集中：只刷新回调，不重复起线程
            if (_grabbing)
            {
                _frameSink = frameSink;
                System.Diagnostics.Debug.WriteLine("[Basler] 采集流已在运行，仅刷新回调");
                return null;
            }

            _frameSink = frameSink;

            try
            {
                DumpCameraState("开启采集前");

                // 若上一次取流未完全停干净，先确保停下再起新线程
                if (_camera.StreamGrabber.IsGrabbing)
                {
                    try { _camera.StreamGrabber.Stop(); }
                    catch { /* 忽略 */ }
                }

                _grabbing = true;
                _grabThread = new System.Threading.Thread(GrabLoop)
                {
                    IsBackground = true,
                    Name = "BaslerGrab"
                };
                _grabThread.Start();
                System.Diagnostics.Debug.WriteLine("[Basler] 取流线程已启动，等待帧到达…");
                return null;
            }
            catch (Exception ex)
            {
                _grabbing = false;
                return $"开启采集流失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 取流线程主体：StreamGrabber.Start() 后循环 RetrieveResult，
        /// 每收到一帧转换为 MONO8/BGR24 标准格式并通过 frameSink 上抛。
        ///
        /// 2026-09-01 诊断强化：本方法内嵌「RetrieveResult 看门狗」。
        ///   现象已确认——连续采集(TriggerMode=Off)下整个等待期零帧、无取流失败日志，
        ///   直到点【结束采集】才退出并抛 TaskCanceledException。这说明 RetrieveResult
        ///   一直阻塞、相机从未把一帧完整数据推到主机（事件式 ImageGrabbed 同样零帧，
        ///   故可排除取流范式问题，锁定在 GigE 网络/防火墙层）。看门狗在 RetrieveResult
        ///   超过 watchdogMs 仍未返回时打印明确告警，使下一轮日志一锤定音。
        /// </summary>
        private void GrabLoop()
        {
            try
            {
                // ★ 必须显式以 ProvidedByUser 模式启动取流（默认 ProvidedByStreamGrabber
                //   会让 pylon 内部线程吃掉缓冲区，本线程 RetrieveResult 永远拿不到帧）。
                _camera.StreamGrabber.Start(Bsl.GrabStrategy.OneByOne, Bsl.GrabLoop.ProvidedByUser);
                System.Diagnostics.Debug.WriteLine("[Basler] StreamGrabber.Start(OneByOne, ProvidedByUser) 成功，进入取流循环");

                // 诊断：打印相机 IP 与主机 IP，判断是否同一子网（跨子网 GigE 流常收不到）
                System.Diagnostics.Debug.WriteLine($"[Basler] 相机IP={CameraIp()} 主机IPv4=[{HostIpv4s()}]");

                // 进入取流循环前再打印一次相机状态，确认 TriggerMode/AcquisitionMode 符合预期
                DumpCameraState("进入取流循环");

                // ★ 关键诊断：Start() 后 grabber 是否真的在采。
                //   若 IsGrabbing=false → RetrieveResult 立即返回 null → 循环空转（不打日志，
                //   正是用户看到的「线程活着但 50s 无任何日志」现象）→ 根因在采集未真正启动。
                try
                {
                    bool isGrabbing = _camera.StreamGrabber.IsGrabbing;
                    System.Diagnostics.Debug.WriteLine($"[Basler] Start() 后 IsGrabbing={isGrabbing}");
                    if (!isGrabbing)
                    {
                        // 兜底①：显式补发 AcquisitionStart 命令（若 StreamGrabber.Start() 未自动起采集）
                        try
                        {
                            var cmd = _camera.Parameters["AcquisitionStart"] as Bsl.ICommandParameter;
                            if (cmd != null)
                            {
                                cmd.Execute();
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Basler] 已显式执行 AcquisitionStart，IsGrabbing={_camera.StreamGrabber.IsGrabbing}");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("[Basler] 节点 AcquisitionStart 不存在或非命令类型");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Basler] 显式 AcquisitionStart 失败(不致命): {ex.Message}");
                        }

                        // 兜底②（2026-09-02）：AcquisitionStart 仍无效（相机固件锁残留）→
                        // 完整重启取流：Stop → AcquisitionStop → 重新 Start。
                        if (!_camera.StreamGrabber.IsGrabbing)
                        {
                            System.Diagnostics.Debug.WriteLine("[Basler] 兜底：Stop → AcquisitionStop → 重新 Start 取流");
                            try { _camera.StreamGrabber.Stop(); } catch { }
                            try
                            {
                                var acqStop2 = _camera.Parameters["AcquisitionStop"] as Bsl.ICommandParameter;
                                if (acqStop2 != null) { try { acqStop2.Execute(); } catch { } }
                            }
                            catch { }
                            try
                            {
                                _camera.StreamGrabber.Start(Bsl.GrabStrategy.OneByOne, Bsl.GrabLoop.ProvidedByUser);
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Basler] 重启取流后 IsGrabbing={_camera.StreamGrabber.IsGrabbing}");
                            }
                            catch (Exception ex2)
                            {
                                System.Diagnostics.Debug.WriteLine($"[Basler] 重启取流失败: {ex2.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Basler] 采集状态诊断异常(不致命): {ex.Message}");
                }

                // ★ 相机侧 GigE 流诊断：确认相机到底在不在采集、把图像流发往哪个 IP。
                //   若 AcquisitionActive=False → 相机根本没在曝光出帧；
                //   若 GevSCDA ≠ 主机 IP → 流被发到错误地址（陈旧静态配置），主机自然收不到。
                DumpGigEStreamDiagnostics();

                const int timeoutMs = 2000;     // pylon 自身取流超时
                const int watchdogMs = 3000;    // 看门狗：超过此值未返回即判定阻塞
                int blockCount = 0;
                int nullCount = 0;
                long iterCount = 0;
                System.Threading.Tasks.Task<Bsl.IGrabResult> grabTask = null;
                var loopSw = System.Diagnostics.Stopwatch.StartNew();

                while (_grabbing)
                {
                    iterCount++;

                    // 同一时刻只挂一个 RetrieveResult，避免并发领取同一取流队列
                    if (grabTask == null)
                    {
                        grabTask = System.Threading.Tasks.Task.Run(() =>
                        {
                            try
                            {
                                return _camera.StreamGrabber.RetrieveResult(timeoutMs, Bsl.TimeoutHandling.Return);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Basler] RetrieveResult 异常: {ex.GetType().Name}: {ex.Message}");
                                return (Bsl.IGrabResult)null;
                            }
                        });
                    }

                    // 看门狗：RetrieveResult 在 watchdogMs 内未返回 → 卡死（典型 GigE 流被防火墙
                    // 拦截 / 网络丢包 / 相机未在向主机推流）。打印明确告警，继续等待同一任务。
                    if (!grabTask.Wait(watchdogMs))
                    {
                        blockCount++;
                        if (blockCount == 1 || blockCount % 10 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Basler] ⚠ RetrieveResult 已阻塞 >{watchdogMs}ms 仍未返回（第{blockCount}次, iter={iterCount}）— " +
                                $"相机未在向主机推送图像数据。重点排查：① Windows 防火墙是否拦截 GigE Vision 流(UDP 3956/3957)；" +
                                $"② 相机与主机是否同一子网；③ 是否安装 pylon GigE 驱动/过滤器；④ 先用官方 pylon Viewer 连同一台相机验证能否出图");
                        }
                        continue; // 同一个 grabTask 继续等，不重复发起
                    }

                    Bsl.IGrabResult grabResult = grabTask.Result;
                    grabTask = null;

                    if (grabResult == null)
                    {
                        // 空返回计数：RetrieveResult 立即返回 null = 采集未真正运行 / 无缓冲可领。
                        // 若此分支刷屏（每 100 次打一次）即坐实「空转」，与用户日志现象完全一致。
                        nullCount++;
                        if (nullCount == 1 || nullCount % 100 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Basler] RetrieveResult 返回 null（第{nullCount}次, iter={iterCount}, " +
                                $"已运行{(long)loopSw.Elapsed.TotalSeconds}s, IsGrabbing={_camera.StreamGrabber.IsGrabbing}）" +
                                $"← 采集未真正启动或相机无数据，需结合上方 IsGrabbing 判断");
                        }
                        continue;
                    }

                    try
                    {
                        if (!grabResult.GrabSucceeded)
                        {
                            _grabFailureCount++;
                            if (_grabFailureCount <= 3 || _grabFailureCount % 50 == 0)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[Basler] 取流失败 #{_grabFailureCount}: Code={grabResult.ErrorCode} {grabResult.ErrorDescription}");
                            }
                            continue;
                        }

                        // PixelType 是枚举，需从 PixelTypeValue(int) 强转
                        var srcPixelType = (Bsl.PixelType)grabResult.PixelTypeValue;

                        // 统一转换为 MONO8（黑白）或 BGR24（彩色），与海康插件输出对齐
                        byte[] buffer;
                        string pixelFormat;
                        if (Bsl.PixelTypeExtensions.IsMono(srcPixelType))
                        {
                            _converter.OutputPixelFormat = Bsl.PixelType.Mono8;
                            buffer = new byte[_converter.GetBufferSizeForConversion(grabResult)];
                            _converter.Convert(buffer, grabResult);
                            pixelFormat = "MONO8";
                        }
                        else
                        {
                            _converter.OutputPixelFormat = Bsl.PixelType.BGR8packed;
                            buffer = new byte[_converter.GetBufferSizeForConversion(grabResult)];
                            _converter.Convert(buffer, grabResult);
                            pixelFormat = "BGR24";
                        }

                        long no = System.Threading.Interlocked.Increment(ref _frameCounter);
                        if (no == 1 || no % 100 == 0)
                        {
                            // ★ 像素内容诊断（2026-09-02）：采样统计转换后的 BGR24/MONO8 数据，
                            //   判定「数据是黑的」还是「数据有效但显示层没画出来」。
                            //   max=0 → 纯黑数据（转换或相机问题）；max>0 且非零占比高 → 数据有效，问题在显示层。
                            System.Diagnostics.Debug.WriteLine(
                                $"[Basler] 帧 #{no}: {grabResult.Width}x{grabResult.Height} " +
                                $"源格式={srcPixelType} → {pixelFormat}, {buffer.Length} 字节 | {SamplePixels(buffer)}");

                            // 原始帧数据（转换前）前 16 字节，确认相机侧数据本身是否有效
                            try
                            {
                                IntPtr rawPtr = grabResult.PixelDataPointer;
                                long rawSize = grabResult.PayloadSize;
                                if (rawPtr != IntPtr.Zero && rawSize > 0)
                                {
                                    int rawSampleLen = (int)Math.Min(rawSize, 16);
                                    byte[] rawSample = new byte[rawSampleLen];
                                    System.Runtime.InteropServices.Marshal.Copy(rawPtr, rawSample, 0, rawSampleLen);
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[Basler] 帧 #{no} 原始数据: size={rawSize} 前16字节={System.BitConverter.ToString(rawSample)}");
                                }
                            }
                            catch { /* 原始数据采样失败不致命 */ }
                        }

                        var sink = _frameSink;
                        if (sink != null)
                        {
                            sink(new FrameEventArgs
                            {
                                Width = (int)grabResult.Width,
                                Height = (int)grabResult.Height,
                                Buffer = buffer,
                                PixelFormat = pixelFormat,
                                FrameNum = no,
                                Timestamp = DateTime.UtcNow.Ticks
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Basler] 图像回调异常: {ex.Message}");
                    }
                    finally
                    {
                        grabResult.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Basler] 取流线程异常终止: {ex.Message}");
            }
            finally
            {
                try
                {
                    if (_camera != null && _camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing)
                    {
                        _camera.StreamGrabber.Stop();
                    }
                }
                catch { /* 忽略 */ }
                System.Diagnostics.Debug.WriteLine("[Basler] 取流线程已退出");
            }
        }

        /// <summary>停止采集流：置停止标志并停止取流线程</summary>
        public string StopGrabbing()
        {
            try
            {
                _grabbing = false;
                if (_camera != null && _camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing)
                {
                    _camera.StreamGrabber.Stop();
                }
                if (_grabThread != null)
                {
                    // 等取流线程自行退出（RetrieveBuffer 在 Stop 后会很快返回）
                    if (!_grabThread.Join(3000))
                    {
                        System.Diagnostics.Debug.WriteLine("[Basler] 取流线程未能在 3s 内退出");
                    }
                    _grabThread = null;
                }

                // ★ 相机固件层复位（2026-09-02 修复偶发软触发 IsGrabbing=False）：
                //   StreamGrabber.Stop() 只停主机取流；TriggerMode=On（软触发）下相机固件可能仍处于
                //   AcquisitionActive（等触发帧），下一轮 StreamGrabber.Start() 的 AcquisitionStart
                //   会被固件拒绝（"not writable"）→ IsGrabbing=False → 软触发报 "Grabbing has not
                //   been started"。此处显式补发 AcquisitionStop，让固件归零（未在采时执行会抛错，忽略）。
                try
                {
                    var acqStop = _camera?.Parameters["AcquisitionStop"] as Bsl.ICommandParameter;
                    if (acqStop != null)
                    {
                        try { acqStop.Execute(); } catch { /* 未在采集中执行会抛错，忽略 */ }
                    }
                }
                catch { /* 相机参数访问异常忽略 */ }

                return null;
            }
            catch (Exception ex)
            {
                return $"停止采集流失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 软触发（pylon 8 原生方法，比走 TriggerSoftware 命令节点更稳）。
        ///
        /// ⚠ 竞态修复（2026-09-02）：StreamGrabber.Start() 返回后，相机固件"武装"
        /// （AcquisitionActive 置位）是异步的——紧跟在 Start 之后的 ExecuteSoftwareTrigger
        /// 会报 "Grabbing has not been started"。节点执行路径（Start 后毫秒级发软触发）
        /// 100% 踩中；调试界面人工点击有几百 ms 间隔所以正常。修复：失败重试（200ms×2）。
        /// </summary>
        public string TriggerSoftware()
        {
            if (!IsOpen) return "相机未打开，无法软触发";
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    _camera.ExecuteSoftwareTrigger();
                    return null;
                }
                catch (Exception ex)
                {
                    if (attempt < 3)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Basler] 软触发第 {attempt} 次失败（{ex.Message}），等待 200ms 重试…");
                        System.Threading.Thread.Sleep(200);
                        continue;
                    }
                    return $"软触发执行失败: {ex.Message}";
                }
            }
            return null; // 不可达
        }

        public void Dispose()
        {
            Close();
        }
    }
}
#endif
