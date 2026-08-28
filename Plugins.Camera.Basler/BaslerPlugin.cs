using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
// 解决 IDevice 命名空间冲突
using IContractDevice = Grayson.Vision.Contracts.Devices.IDevice;

namespace Plugins.Camera.Basler
{
    /// <summary>
    /// 巴斯勒（Basler）工业相机设备实现。
    ///
    /// 与海康插件（HikCamera）保持完全一致的外部行为：
    /// - Connect / Disconnect / StartGrabbing / SoftTrigger 等生命周期方法语义相同；
    /// - 图像统一以 FrameEventArgs(byte[] Buffer + Width/Height/PixelFormat) 上抛，
    ///   PixelFormat 取值 "MONO8" / "BGR24"，与下游 FrameToHImage 转换兼容；
    /// - 参数走「本地 ConfigParams 字典 + SyncParamsToDevice/FromDevice 同步」模式，
    ///   UI 属性面板无需感知品牌差异。
    ///
    /// 硬件访问全部经 BaslerSdkFactory 选择的适配层完成：
    /// - 装有 pylon SDK（DLLLib\Basler.Pylon.dll）→ 真实相机；
    /// - 未装 → 离线仿真相机（合成麻将测试图），流程可先行联调。
    /// </summary>
    public class BaslerCamera : ICamera
    {
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
                    StateChanged?.Invoke(this, _state); // 状态变化通知 UI
                }
            }
        }

        public DeviceCategory Category { get; set; } = DeviceCategory.Camera;

        /// <summary>最后一次心跳/在线检测时间（UTC）</summary>
        public DateTime LastHeartbeatAt { get; set; }

        /// <summary>默认心跳：刷新时间并检查在线状态</summary>
        public Result Heartbeat() => CheckStatus();

        /// <summary>本地 UI/配置层参数字典（曝光、增益、触发模式等）</summary>
        public Dictionary<string, object> ConfigParams = new Dictionary<string, object>();

        #endregion

        #region 内部状态

        /// <summary>SDK 相机适配器（真实 pylon 或离线仿真）</summary>
        private IBaslerSdkCamera _sdk;

        /// <summary>最近一帧缓存锁</summary>
        private readonly object _latestFrameLock = new object();

        /// <summary>最近一帧图像（供保存图像/调试使用）</summary>
        private FrameEventArgs _latestFrame;

        #endregion

        #region ICamera 图像事件

        public event EventHandler<FrameEventArgs> FrameReceived;

        protected virtual void OnFrameReceived(FrameEventArgs args)
        {
            FrameReceived?.Invoke(this, args);
        }

        #endregion

        // ------------------------------------------------------------------
        // 连接 / 断开
        // ------------------------------------------------------------------

        public Result Connect()
        {
            try
            {
                // 已连接则直接返回成功（幂等）
                if (IsSdkOpen) return Result.Ok();

                // 惰性创建 SDK 适配器（离线恢复场景：只有 SN 没有 info）
                if (_sdk == null)
                {
                    _sdk = BaslerSdkFactory.Instance.CreateCamera(DeviceId);
                }

                string err = _sdk.Open(DeviceId);
                if (err != null)
                {
                    return Result.Fail($"巴斯勒相机 [{DeviceId}] 连接失败: {err}");
                }

                State = DeviceState.Connected;

                // 连接后双向同步参数：
                // 先从硬件读回当前值，再把本地配置（如有）覆盖下发
                SyncTriggerModeFromDevice();
                SyncParamsFromDevice();
                SyncParamsToDevice();

                return Result.Ok();
            }
            catch (Exception ex)
            {
                State = DeviceState.Error;
                return Result.Fail($"巴斯勒相机连接异常: {ex.Message}", ex: ex);
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
                return Result.Fail($"断开巴斯勒相机异常: {ex.Message}", ex: ex);
            }
        }

        public Result CheckStatus()
        {
            LastHeartbeatAt = DateTime.UtcNow;

            if (!IsSdkOpen)
            {
                State = DeviceState.Disconnected;
                return Result.Fail("巴斯勒相机未连接");
            }

            try
            {
                // 读一个最基础的节点验证通信是否存活
                object w = _sdk.GetNode("Width");
                if (w == null && !_sdk.IsSimulated)
                {
                    State = DeviceState.Error;
                    return Result.Fail("读取相机节点失败，相机可能掉线");
                }

                State = DeviceState.Connected;
                return Result.Ok();
            }
            catch (Exception ex)
            {
                State = DeviceState.Error;
                return Result.Fail($"相机在线状态检查异常: {ex.Message}");
            }
        }

        private bool IsSdkOpen => _sdk != null && _sdk.IsOpen;

        // ------------------------------------------------------------------
        // 采集 / 触发
        // ------------------------------------------------------------------

        public Result StartGrabbing()
        {
            if (!IsSdkOpen)
            {
                return Result.Fail("相机未连接，无法开启采集！");
            }

            // 帧回调：缓存最新帧 + 上抛 FrameReceived 事件
            string err = _sdk.StartGrabbing(frame =>
            {
                lock (_latestFrameLock)
                {
                    _latestFrame = frame;
                }
                OnFrameReceived(frame);
            });

            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result StopGrabbing()
        {
            if (_sdk != null)
            {
                string err = _sdk.StopGrabbing();
                if (err != null) return Result.Fail(err);
            }
            return Result.Ok();
        }

        public Result StartContinuousGrab() => StartGrabbing();
        public Result StopContinuousGrab() => StopGrabbing();

        /// <summary>软触发单次拍照</summary>
        public Result SoftTrigger()
        {
            if (!IsSdkOpen)
            {
                return Result.Fail("相机未连接，无法发送软触发指令！");
            }
            string err = _sdk.TriggerSoftware();
            return err != null ? Result.Fail(err) : Result.Ok();
        }

        public Result SoftwareTrigger() => SoftTrigger();

        /// <summary>
        /// 设置触发模式。
        /// mode: 0=连续采集, 1=软触发, 2=硬件外触发(Line1)
        /// </summary>
        public Result SetTriggerMode(int mode)
        {
            if (!IsSdkOpen)
            {
                // 未连接时仅记录到本地字典，连接后由 SyncParamsToDevice 下发
                ConfigParams["TriggerModeSelect"] = mode;
                return Result.Ok();
            }

            string err;
            switch (mode)
            {
                case 0: // 连续采集：TriggerMode=Off
                    err = _sdk.SetNode("TriggerMode", "Off");
                    if (err != null) return Result.Fail($"关闭触发模式失败: {err}");
                    ConfigParams["TriggerMode"] = false;
                    ConfigParams["TriggerSource"] = "Off";
                    break;

                case 1: // 软触发：TriggerMode=On + TriggerSource=Software
                    err = _sdk.SetNode("TriggerMode", "On");
                    if (err != null) return Result.Fail($"开启触发模式失败: {err}");
                    err = _sdk.SetNode("TriggerSource", "Software");
                    if (err != null) return Result.Fail($"设置软触发源失败: {err}");
                    ConfigParams["TriggerMode"] = true;
                    ConfigParams["TriggerSource"] = "Software";
                    break;

                case 2: // 硬件外触发：TriggerMode=On + TriggerSource=Line1
                    err = _sdk.SetNode("TriggerMode", "On");
                    if (err != null) return Result.Fail($"开启触发模式失败: {err}");
                    err = _sdk.SetNode("TriggerSource", "Line1");
                    if (err != null) return Result.Fail($"设置硬件触发源(Line1)失败: {err}");
                    ConfigParams["TriggerMode"] = true;
                    ConfigParams["TriggerSource"] = "Line1";
                    break;

                default:
                    return Result.Fail($"不支持的触发模式选项: {mode}");
            }

            ConfigParams["TriggerModeSelect"] = mode;
            return Result.Ok();
        }

        // ------------------------------------------------------------------
        // 曝光 / 增益快捷封装
        // ------------------------------------------------------------------

        public Result SetExposureTime(double valueUs) => SetParam("ExposureTime", valueUs);
        public Result GetExposureTime() => GetParam("ExposureTime") as Result;
        public Result SetGain(double value) => SetParam("Gain", value);
        public Result GetGain() => GetParam("Gain");

        // ------------------------------------------------------------------
        // 通用参数读写（本地字典模式）
        // ------------------------------------------------------------------

        public Result SetParam(string key, object value)
        {
            if (value == null) return Result.Fail("参数值不能为 null");

            // 曝光/增益写入前关闭自动模式，确保手动设置生效
            if (IsSdkOpen)
            {
                if (string.Equals(key, "ExposureTime", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "Exposure", StringComparison.OrdinalIgnoreCase))
                {
                    _sdk.SetNode("ExposureAuto", "Off");
                }
                else if (string.Equals(key, "Gain", StringComparison.OrdinalIgnoreCase))
                {
                    _sdk.SetNode("GainAuto", "Off");
                }
            }

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

        /// <summary>
        /// UI 点击【确定】时调用：将 ConfigParams 批量下发到相机硬件。
        /// </summary>
        public Result SyncParamsToDevice()
        {
            if (!IsSdkOpen)
            {
                return Result.Fail("相机未连接或掉线，无法下发参数到硬件！");
            }

            var errorList = new List<string>();

            // 优先下发触发模式
            if (ConfigParams.ContainsKey("TriggerModeSelect"))
            {
                Result trigRes = SetTriggerMode(Convert.ToInt32(ConfigParams["TriggerModeSelect"]));
                if (!trigRes.Success) errorList.Add(trigRes.Message);
            }

            foreach (var kvp in ConfigParams)
            {
                string key = kvp.Key;
                object value = kvp.Value;

                // 跳过内部逻辑字段，不是 GenICam 节点
                if (key == "IP" || key == "Port" || key == "TriggerModeSelect" ||
                    key == "TriggerSource" || key == "TriggerMode" || key == "SaveImageFile" ||
                    key == "PixelFormat")
                {
                    continue;
                }

                // 通用键名 → pylon 节点名映射
                string node = key;
                if (key == "Exposure")
                {
                    node = "ExposureTime";       // UI 用 Exposure，相机节点为 ExposureTime
                }

                if (node == "ExposureTime")
                {
                    // 曝光：优先整型原始节点 ExposureTimeRaw(μs)，
                    // 部分型号只有浮点 ExposureTime(μs)，失败则降级
                    double us = Convert.ToDouble(value);
                    string err = _sdk.SetNode("ExposureTimeRaw", (long)us);
                    if (err != null) err = _sdk.SetNode("ExposureTime", us);
                    if (err != null) errorList.Add($"曝光设置失败: {err}");
                    continue;
                }

                if (node == "Gain")
                {
                    // 增益：优先整型 GainRaw，失败降级浮点 Gain
                    string err = _sdk.SetNode("GainRaw", (long)Convert.ToDouble(value));
                    if (err != null) err = _sdk.SetNode("Gain", Convert.ToDouble(value));
                    if (err != null) errorList.Add($"增益设置失败: {err}");
                    continue;
                }

                string e = _sdk.SetNode(node, value);
                if (e != null) errorList.Add($"节点[{node}]下发失败: {e}");
            }

            return errorList.Count > 0
                ? Result.Fail("部分参数下发失败:\n" + string.Join("\n", errorList))
                : Result.Ok();
        }

        /// <summary>从相机硬件读取常见节点，刷新 ConfigParams</summary>
        public Result SyncParamsFromDevice()
        {
            if (!IsSdkOpen)
            {
                return Result.Fail("相机未连接，无法从硬件读取参数！");
            }

            SyncTriggerModeFromDevice();

            // 读取常见标准节点（缺失的自动跳过）
            string[] keys = { "ExposureTimeRaw", "ExposureTime", "GainRaw", "Gain",
                              "ResultingFrameRate", "AcquisitionFrameRate", "Width", "Height" };
            foreach (string key in keys)
            {
                object v = _sdk.GetNode(key);
                if (v != null)
                {
                    ConfigParams[key] = v;
                    // 曝光/增益统一回填标准键名
                    if (key == "ExposureTimeRaw" || key == "ExposureTime") ConfigParams["ExposureTime"] = v;
                    if (key == "GainRaw" || key == "Gain") ConfigParams["Gain"] = v;
                }
            }

            return Result.Ok();
        }

        /// <summary>从相机读取当前触发模式，更新 ConfigParams</summary>
        public Result SyncTriggerModeFromDevice()
        {
            if (!IsSdkOpen)
            {
                return Result.Fail("相机未连接，无法读取触发模式");
            }

            object tm = _sdk.GetNode("TriggerMode");
            bool triggerOn = Convert.ToString(tm).Equals("On", StringComparison.OrdinalIgnoreCase) ||
                             Convert.ToString(tm).Equals("1") ||
                             Convert.ToString(tm).Equals("True");

            if (triggerOn)
            {
                ConfigParams["TriggerMode"] = true;
                object ts = _sdk.GetNode("TriggerSource");
                string src = Convert.ToString(ts);
                if (src.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ConfigParams["TriggerSource"] = "Software";
                    ConfigParams["TriggerModeSelect"] = 1;
                }
                else
                {
                    ConfigParams["TriggerSource"] = "Hardware";
                    ConfigParams["TriggerModeSelect"] = 2;
                }
            }
            else
            {
                ConfigParams["TriggerMode"] = false;
                ConfigParams["TriggerSource"] = "Off";
                ConfigParams["TriggerModeSelect"] = 0;
            }

            return Result.Ok();
        }

        // ------------------------------------------------------------------
        // 图像保存（托管 Bitmap 实现，真实/仿真两用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 保存最近一帧到指定路径（bmp/jpg/png），深拷贝快照防止采集冲刷。
        /// </summary>
        public Result SaveImageFile(string filePath, string format)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return Result.Fail("保存路径不能为空");

            byte[] bufferCopy;
            int width, height;
            string pixelFormat;

            lock (_latestFrameLock)
            {
                if (_latestFrame == null || _latestFrame.Buffer == null || _latestFrame.Buffer.Length == 0)
                {
                    return Result.Fail("当前没有可用图像帧");
                }
                width = _latestFrame.Width;
                height = _latestFrame.Height;
                pixelFormat = _latestFrame.PixelFormat;
                bufferCopy = new byte[_latestFrame.Buffer.Length];
                Array.Copy(_latestFrame.Buffer, bufferCopy, bufferCopy.Length);
            }

            try
            {
                using (Bitmap bmp = CreateBitmap(bufferCopy, width, height, pixelFormat))
                {
                    ImageFormat imgFmt;
                    switch ((format ?? "bmp").ToLowerInvariant())
                    {
                        case "jpg":
                        case "jpeg": imgFmt = ImageFormat.Jpeg; break;
                        case "png": imgFmt = ImageFormat.Png; break;
                        case "bmp": imgFmt = ImageFormat.Bmp; break;
                        default: return Result.Fail($"不支持的保存格式: {format}");
                    }
                    bmp.Save(filePath, imgFmt);
                }
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail($"保存图像异常: {ex.Message}");
            }
        }

        /// <summary>把 MONO8/BGR24 裸数据转成 Bitmap（注意 4 字节对齐 Stride）</summary>
        private static Bitmap CreateBitmap(byte[] data, int width, int height, string pixelFormat)
        {
            string fmt = (pixelFormat ?? "MONO8").ToUpperInvariant();
            if (fmt.Contains("BGR") || fmt.Contains("RGB"))
            {
                // BGR24 交织数据 → 24bpp 位图（BGR 顺序与 GDI+ 一致）
                var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                var rect = new Rectangle(0, 0, width, height);
                BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
                try
                {
                    int stride = bd.Stride;
                    // 逐行拷贝（目标 Stride 有 4 字节对齐填充，不能整块拷）
                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            data, y * width * 3,
                            (IntPtr)((long)bd.Scan0 + y * stride), width * 3);
                    }
                    return bmp;
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }
            }

            // MONO8 → 24bpp 灰度位图（灰度复制到 RGB 三通道，避免 8bpp 调色板处理）
            var grayBmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var grayRect = new Rectangle(0, 0, width, height);
            BitmapData gbd = grayBmp.LockBits(grayRect, ImageLockMode.WriteOnly, grayBmp.PixelFormat);
            try
            {
                byte[] row = new byte[width * 3];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte g = data[y * width + x];
                        row[x * 3] = g;
                        row[x * 3 + 1] = g;
                        row[x * 3 + 2] = g;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0,
                        (IntPtr)((long)gbd.Scan0 + y * gbd.Stride), row.Length);
                }
                return grayBmp;
            }
            finally
            {
                grayBmp.UnlockBits(gbd);
            }
        }

        // ------------------------------------------------------------------
        // IDisposable
        // ------------------------------------------------------------------

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
                FrameReceived = null;
                try
                {
                    StopContinuousGrab();
                    StopGrabbing();
                    Disconnect();
                }
                catch { /* 释放过程不抛异常 */ }
                lock (_latestFrameLock) { _latestFrame = null; }
            }
            _disposed = true;
        }

        ~BaslerCamera()
        {
            Dispose(false);
        }

        // ------------------------------------------------------------------
        // 构造函数
        // ------------------------------------------------------------------

        /// <summary>从 LiteDB 离线恢复 / 纯 SN 构造（连接时再在线定位）</summary>
        public BaslerCamera(string deviceId)
        {
            DeviceId = deviceId;
        }
    }

    /// <summary>
    /// 巴斯勒相机插件工厂。
    /// 插件发现机制：程序集名含 "Plugin" 字样，启动时被 DevicePluginManager
    /// 反射扫描加载；Supports(Camera, "Basler") 命中后由本工厂创建设备实例。
    /// </summary>
    public class BaslerPlugin : IHardwarePlugin
    {
        public string BrandName => "Basler";
        public DeviceCategory Category => DeviceCategory.Camera;
        public string Version => "1.0.0";

        /// <summary>品牌专用插件高优先级，优先于通用兜底插件接管</summary>
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.Camera &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize() { }

        /// <summary>最近一次枚举到的设备信息缓存（CreateDevice 时优先匹配）</summary>
        private readonly List<BaslerSdkDeviceInfo> _lastEnumerated = new List<BaslerSdkDeviceInfo>();

        /// <summary>枚举当前在线的巴斯勒相机（GigE / USB）</summary>
        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            try
            {
                var infos = BaslerSdkFactory.Instance.EnumerateDevices();
                _lastEnumerated.Clear();
                _lastEnumerated.AddRange(infos);

                var list = infos.Select(i => new DeviceInfo
                {
                    DeviceId = i.SerialNumber,
                    ModelName = i.ModelName,
                    Category = Category,
                    BrandName = BrandName
                }).ToList();

                return Result<List<DeviceInfo>>.Ok(list);
            }
            catch (Exception ex)
            {
                return Result<List<DeviceInfo>>.Fail($"枚举巴斯勒相机失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 按序列号创建设备实例。
        /// 无论 UI 刚扫描创建，还是 LiteDB 离线恢复，均返回可用 IDevice：
        /// BaslerCamera 内部通过适配层在 Connect 时重新在线定位相机。
        /// </summary>
        public IContractDevice CreateDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;

            var device = new BaslerCamera(deviceId)
            {
                BrandName = BrandName,
                Category = Category
            };

            // 默认初始参数（与海康插件默认值保持一致的习惯）
            device.SetParam("ExposureTime", 5000.0); // μs
            device.SetParam("Gain", 0.0);
            device.SetParam("TriggerModeSelect", 0); // 默认连续采集

            return device;
        }

        public void Shutdown() { }
    }
}
