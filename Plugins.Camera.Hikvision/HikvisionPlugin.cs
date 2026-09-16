using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using MvCamCtrl.NET;
using MvCamCtrl.NET.CameraParams;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Grayson.Vision.Contracts.Infrastructure.Logging;
// 解决 IDevice 命名空间冲突
using IContractDevice = Grayson.Vision.Contracts.Devices.IDevice;

namespace Plugins.Camera.Hikvision
{
    public class HikCamera : ICamera, IIoDevice
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
                    StateChanged?.Invoke(this, _state); // 触发事件
                }
            }
        }
        public DeviceCategory Category { get; set; } = DeviceCategory.Camera;

        /// <summary>最后一次心跳/在线检测时间（UTC）</summary>
        public DateTime LastHeartbeatAt { get; set; }

        /// <summary>
        /// 默认心跳检测：刷新心跳时间并返回当前在线状态。
        /// 具体插件可重写以执行硬件级探测。
        /// </summary>
        public Result Heartbeat()
        {
            return CheckStatus();
        }
        // 高优先级：优先接管海康相机
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.Camera &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本地 UI/配置层参数字典</summary>
        public Dictionary<string, object> ConfigParams = new Dictionary<string, object>();

        /// <summary>最近一帧图像缓存锁</summary>
        private readonly object _latestFrameLock = new object();

        /// <summary>最近一帧图像数据，供 UI/业务使用</summary>
        private FrameEventArgs _latestFrame;

        /// <summary>供 SDK 保存使用的最近一帧 CImage（已包含 Image 指针）</summary>
        private CImage _latestSdkImage;

        /// <summary>供保存使用的 CImage 缓存锁</summary>
        private readonly object _latestSdkImageLock = new object();

        /// <summary>CImage.Image 非公开 IntPtr 字段缓存，避免每次都反射</summary>
        private static readonly System.Reflection.FieldInfo CImageImageField =
            typeof(CImage).GetField("Image", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?? typeof(CImage).GetField("image", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        #endregion

        #region ICamera 图像采集事件
        public event EventHandler<FrameEventArgs> FrameReceived;
        protected virtual void OnFrameReceived(FrameEventArgs args)
        {
            FrameReceived?.Invoke(this, args);
        }
        #endregion

        #region 采集与触发控制接口实现

        /// <summary>
        /// 开启采集流
        /// </summary>
        // 声明海康 SDK 委托变量（必须保存为成员变量，防止被 GC 垃圾回收）
        //        StartGrabbing() → m_ImageCallback == null → 创建委托 → 注册 SDK → 开启采集
        //          ↓（多次调用 StartGrabbing 只重启采集流，不覆盖委托）
        //      StopGrabbing()  → SDK 解绑 → m_ImageCallback = null → 允许下次重建
        private cbOutputExdelegate m_ImageCallback;

        #region ★ 成像参数快照诊断（定位黑白相机黑屏：只读采样，不改变任何采集行为）

        private DateTime _lastBlackDumpAt = DateTime.MinValue;
        private int _blackDumpCount = 0;
        private bool _firstFrameDumped = false;

        /// <summary>采样式扫描缓冲，取采样点最大灰度。用于判断是否全黑，开销可忽略。</summary>
        private static int SampleMaxGray(byte[] buf)
        {
            if (buf == null || buf.Length == 0) return -1;
            int step = buf.Length / 4096;
            if (step < 1) step = 1;
            int max = 0;
            for (int i = 0; i < buf.Length; i += step)
            {
                if (buf[i] > max) max = buf[i];
                if (max >= 255) break;
            }
            return max;
        }

        /// <summary>容错读取任意 GenICam 节点；读不到返回 N/A，绝不抛异常打断采集。</summary>
        private string ReadNode(string key)
        {
            if (m_MyCamera == null) return "N/A(无句柄)";
            try
            {
                CFloatValue fv = new CFloatValue();
                if (m_MyCamera.GetFloatValue(key, ref fv) == CErrorDefine.MV_OK)
                    return fv.CurValue.ToString("0.###");

                CIntValue iv = new CIntValue();
                if (m_MyCamera.GetIntValue(key, ref iv) == CErrorDefine.MV_OK)
                    return iv.CurValue.ToString();

                CEnumValue ev = new CEnumValue();
                if (m_MyCamera.GetEnumValue(key, ref ev) == CErrorDefine.MV_OK)
                {
                    long v = ev.CurValue;
                    if (key == "PixelFormat")
                    {
                        try { return v + "(" + ((MvGvspPixelType)v).ToString() + ")"; }
                        catch { return v.ToString(); }
                    }
                    return v.ToString();
                }

                bool bv = false;
                if (m_MyCamera.GetBoolValue(key, ref bv) == CErrorDefine.MV_OK)
                    return bv ? "True" : "False";

                CStringValue sv = new CStringValue();
                if (m_MyCamera.GetStringValue(key, ref sv) == CErrorDefine.MV_OK)
                    return sv.CurValue;
            }
            catch (Exception ex)
            {
                return "EX:" + ex.Message;
            }
            return "N/A";
        }

        /// <summary>
        /// 把全部成像相关节点打成【一整行】日志。
        /// 目的是让「黑屏」和「正常」两次运行的日志可以直接逐字段 diff，病因会自己跳出来。
        /// </summary>
        public void DumpImagingParams(string tag)
        {
            if (m_MyCamera == null) return;
            try
            {
                string[] keys =
                {
                    "ExposureTime", "ExposureAuto", "Gain", "GainAuto",
                    "BlackLevel", "Gamma", "GammaEnable", "Brightness",
                    "AcquisitionMode", "AcquisitionFrameRate", "AcquisitionFrameRateEnable", "ResultingFrameRate",
                    "TriggerMode", "TriggerSource", "PixelFormat",
                    "Width", "Height", "OffsetX", "OffsetY",
                    "GevSCPSPacketSize", "DeviceLinkThroughputLimit", "DeviceTemperature"
                };

                var sb = new StringBuilder();
                sb.Append("[").Append(tag).Append("] ");
                foreach (var k in keys)
                {
                    sb.Append(k).Append('=').Append(ReadNode(k)).Append("; ");
                }

                // 顺带给出最优包大小，判断是否"该设而没设"
                try
                {
                    int opt = m_MyCamera.GIGE_GetOptimalPacketSize();
                    sb.Append("OptimalPacketSize=").Append(opt);
                }
                catch { /* 非 GigE 机型无此接口，忽略 */ }

                LogBus.Info("HikCam", sb.ToString());
            }
            catch (Exception ex)
            {
                LogBus.Warn("HikCam", $"[{tag}] 参数快照失败: {ex.Message}");
            }
        }

        /// <summary>打印本地待下发字典，用来看"我们准备往相机写什么"。</summary>
        private void DumpConfigParams(string tag)
        {
            var sb = new StringBuilder();
            sb.Append("[").Append(tag).Append("] ");
            foreach (var kvp in ConfigParams)
            {
                sb.Append(kvp.Key).Append('=').Append(kvp.Value).Append("; ");
            }
            LogBus.Info("HikCam", sb.ToString());
        }

        /// <summary>
        /// ★ 把 GigE 相机的 GevSCPSPacketSize 对齐到当前链路的最优值。
        ///
        /// 这是官方 demo 的必备步骤，而我们的 Connect() 此前完全缺失（只在注释里承诺了
        /// "后续包大小调整"，代码从未实现）。
        ///
        /// 后果：若相机里残留着巨帧包大小（如 8164）而主机网卡 MTU 只有 1500，
        /// 流通道的数据包会被丢弃 —— 表现为帧照常回调、宽高与字节数全对，
        /// 但图像内容整帧为 0（黑屏），且不报任何错误。
        /// MVS 每次打开相机都会做这一步，这正是"用 MV 打开过一次再回来就正常"的真正原因。
        ///
        /// 注意：必须在 StartGrabbing 之前设置，采集中该节点不可写。
        /// </summary>
        private void EnsureOptimalPacketSize()
        {
            if (m_MyCamera == null) return;
            if (cCameraInfo == null || cCameraInfo.nTLayerType != CSystem.MV_GIGE_DEVICE) return;

            try
            {
                int optimal = m_MyCamera.GIGE_GetOptimalPacketSize();
                if (optimal <= 0)
                {
                    LogBus.Warn("HikCam", $"[包大小] 获取最优包大小失败(code={optimal})，跳过对齐");
                    return;
                }

                int curVal = -1;
                CIntValue cur = new CIntValue();
                if (m_MyCamera.GetIntValue("GevSCPSPacketSize", ref cur) == CErrorDefine.MV_OK)
                {
                    curVal = (int)cur.CurValue;
                }

                if (curVal == optimal)
                {
                    LogBus.Info("HikCam", $"[包大小] 已对齐，无需调整: {curVal}");
                    return;
                }

                int nRet = m_MyCamera.SetIntValue("GevSCPSPacketSize", (uint)optimal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    LogBus.Warn("HikCam",
                        $"[包大小] 已修正 {curVal} -> {optimal}（原值超过主机 MTU，流数据被丢弃会导致黑屏）");
                }
                else
                {
                    LogBus.Warn("HikCam",
                        $"[包大小] 设置失败 {curVal} -> {optimal}: {Helper.ShowErrorMsg("", nRet)}");
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("HikCam", $"[包大小] 对齐异常（忽略，不影响连接）: {ex.Message}");
            }
        }

        /// <summary>
        /// 黑帧自动告警：帧数据几乎全 0 就把相机成像参数快照打出来（限频，不刷屏）。
        /// 这是"画面全黑"问题的收口——一旦黑屏，日志里必然留下病因线索。
        /// </summary>
        private void CheckBlankFrame(byte[] frameBuffer, string outFormat, int width, int height)
        {
            if (!_firstFrameDumped)
            {
                _firstFrameDumped = true;
                DumpImagingParams("首帧·成像参数");
            }

            int maxGray = SampleMaxGray(frameBuffer);
            if (maxGray > 2) return;            // 画面有内容，正常
            if (_blackDumpCount >= 3) return;   // 一次连接最多报 3 次

            var now = DateTime.Now;
            if (_blackDumpCount > 0 && (now - _lastBlackDumpAt).TotalSeconds < 5) return;

            _blackDumpCount++;
            _lastBlackDumpAt = now;
            LogBus.Warn("HikCam",
                $"[黑帧告警] {width}x{height} {outFormat} 采样最大灰度={maxGray}（画面基本全黑），自动 dump 相机参数");
            DumpImagingParams("黑帧·成像参数");
            DumpConfigParams("黑帧·本地字典");
        }

        #endregion

        #region 开启/停止采集流

        public Result StartGrabbing()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法开启采集！");
            }

            // 仅在首次或回调已被清空时才创建并注册委托，
            // 避免每次调用都覆盖旧委托引用，导致 SDK 底层函数指针
            // 指向已被 GC 回收的委托（CallbackOnCollectedDelegate）
            if (m_ImageCallback == null)
            {
                m_ImageCallback = new cbOutputExdelegate(ImageCallbackEx);
                int regRet = m_MyCamera.RegisterImageCallBackEx(m_ImageCallback, IntPtr.Zero);
                if (regRet != CErrorDefine.MV_OK)
                {
                    m_ImageCallback = null;
                    return Result.Fail(Helper.ShowErrorMsg("注册图像回调失败", regRet));
                }
            }

            // ★ 启动采集前留一份参数快照（诊断用，只读）
            DumpImagingParams("启动采集前");

            // 开启 SDK 采集流
            int nRet = m_MyCamera.StartGrabbing();
            if (nRet != CErrorDefine.MV_OK)
            {
                return Result.Fail(Helper.ShowErrorMsg("开启采集流失败", nRet));
            }

            return Result.Ok();
        }

        #endregion

        #region 海康 SDK 回调处理逻辑 (ImageCallbackEx)

        private void ImageCallbackEx(IntPtr pData, ref MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            if (pData == IntPtr.Zero || pFrameInfo.nFrameLen == 0) return;

            try
            {
                int width = pFrameInfo.nWidth;
                int height = pFrameInfo.nHeight;
                MvGvspPixelType pixelType = pFrameInfo.enPixelType;

                string typeStr = pixelType.ToString().ToUpperInvariant();
                bool isBayer = typeStr.Contains("BAYER");
                bool isMono = typeStr.Contains("MONO");
                bool isYuv = typeStr.Contains("YUV") || typeStr.Contains("YCBCR");

                byte[] frameBuffer;
                string outFormat;

                // ★ 8bit 直通：按精确枚举判断，不能用名字 Contains ——
                //   "MONO8_SIGNED" 同样 Contains "MONO8"，会被误判成无符号直通。
                if (pixelType == MvGvspPixelType.PixelType_Gvsp_Mono8 ||
                    pixelType == MvGvspPixelType.PixelType_Gvsp_BGR8_Packed ||
                    pixelType == MvGvspPixelType.PixelType_Gvsp_RGB8_Packed)
                {
                    frameBuffer = new byte[pFrameInfo.nFrameLen];
                    Marshal.Copy(pData, frameBuffer, 0, (int)pFrameInfo.nFrameLen);
                    outFormat =
                        pixelType == MvGvspPixelType.PixelType_Gvsp_Mono8 ? "MONO8" :
                        pixelType == MvGvspPixelType.PixelType_Gvsp_RGB8_Packed ? "RGB24" : "BGR24";
                }
                else if (isMono || isBayer || isYuv)
                {
                    // ★ 需要转码的格式（黑白高位深 / Bayer / YUV）。
                    //   旧写法把所有 MONO 一律标 "MONO8"：Mono10/12 的缓冲是 W*H*2，
                    //   而显示层按 1 字节/像素只取前 W*H 个字节 → 画面变成上半幅乱纹，
                    //   且不抛任何异常（静默出错）。这里统一先降到 8bit 再标 MONO8。
                    bool wantMono = isMono;
                    var dstType = wantMono
                        ? MvGvspPixelType.PixelType_Gvsp_Mono8
                        : MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                    int dstLen = width * height * (wantMono ? 1 : 3);

                    if (TryConvertPixelType(pData, ref pFrameInfo, dstType, dstLen, out frameBuffer))
                    {
                        outFormat = wantMono ? "MONO8" : "BGR24";
                    }
                    else
                    {
                        var raw = new byte[pFrameInfo.nFrameLen];
                        Marshal.Copy(pData, raw, 0, (int)pFrameInfo.nFrameLen);

                        // SDK 转码不可用时的软件兜底（仅 2 字节/像素的非 packed 黑白格式）
                        var manual = wantMono ? DownshiftU16ToMono8(raw, width, height, typeStr) : null;
                        if (manual != null)
                        {
                            frameBuffer = manual;
                            outFormat = "MONO8";
                        }
                        else
                        {
                            // 转不了也绝不谎报 MONO8：如实给真实格式名，上层至少能按真实位深处理
                            frameBuffer = raw;
                            outFormat = typeStr;
                            System.Diagnostics.Debug.WriteLine(
                                $"[HikCamera] 无法转码的像素格式: {typeStr}（{width}x{height}，{pFrameInfo.nFrameLen} 字节）");
                        }
                    }
                }
                else
                {
                    // 其它未知格式：原样拷贝并按名字归类
                    frameBuffer = new byte[pFrameInfo.nFrameLen];
                    Marshal.Copy(pData, frameBuffer, 0, (int)pFrameInfo.nFrameLen);
                    outFormat = typeStr.Contains("RGB") ? "RGB24" : "BGR24";
                }

                var frameArgs = new FrameEventArgs
                {
                    Width = width,
                    Height = height,
                    Buffer = frameBuffer,
                    PixelFormat = outFormat,
                    Timestamp = pFrameInfo.nDevTimeStampLow,
                    FrameNum = pFrameInfo.nFrameNum
                };

                lock (_latestFrameLock)
                {
                    _latestFrame = frameArgs;
                }

                // ★ 黑帧自动体检（见 CheckBlankFrame 注释）
                CheckBlankFrame(frameBuffer, outFormat, width, height);

                OnFrameReceived(frameArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HikCamera] 图像回调处理异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 调用 SDK 把当前帧转码到目标像素格式（输出到调用方给定的托管数组）。
        /// 输出缓冲用 GCHandle 固定，避免 SDK 内部再分配非托管内存。
        /// 转码失败（机型不支持该转换 / 参数不支持）返回 false，由调用方走软件兜底。
        /// </summary>
        private bool TryConvertPixelType(IntPtr pData, ref MV_FRAME_OUT_INFO_EX pFrameInfo,
                                         MvGvspPixelType dstType, int dstLen, out byte[] buffer)
        {
            buffer = null;
            if (m_MyCamera == null || dstLen <= 0) return false;

            byte[] dst = new byte[dstLen];
            GCHandle handle = GCHandle.Alloc(dst, GCHandleType.Pinned);
            try
            {
                CPixelConvertParam convertParam = new CPixelConvertParam();

                convertParam.InImage.Width = (ushort)pFrameInfo.nWidth;
                convertParam.InImage.Height = (ushort)pFrameInfo.nHeight;
                convertParam.InImage.PixelType = pFrameInfo.enPixelType;
                convertParam.InImage.ImageAddr = pData;
                convertParam.InImage.FrameLen = pFrameInfo.nFrameLen;

                convertParam.OutImage.Width = (ushort)pFrameInfo.nWidth;
                convertParam.OutImage.Height = (ushort)pFrameInfo.nHeight;
                convertParam.OutImage.PixelType = dstType;
                convertParam.OutImage.ImageAddr = handle.AddrOfPinnedObject();
                convertParam.OutImage.ImageSize = (uint)dstLen;

                if (m_MyCamera.ConvertPixelType(ref convertParam) != CErrorDefine.MV_OK) return false;

                buffer = dst;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>
        /// 软件兜底：把每像素 2 字节的黑白高位深（Mono10/12/14/16，小端）降到 8 位——取高字节。
        /// Packed 格式（Mono10Packed / Mono12Packed）不是 2 字节对齐，无法这样处理，返回 null。
        /// </summary>
        private static byte[] DownshiftU16ToMono8(byte[] raw, int width, int height, string typeStr)
        {
            if (raw == null || width <= 0 || height <= 0) return null;
            if (typeStr != null && typeStr.Contains("PACKED")) return null;

            long need = (long)width * height * 2;
            if (raw.LongLength < need) return null;

            byte[] out8 = new byte[width * height];
            for (int i = 0; i < out8.Length; i++)
                out8[i] = raw[i * 2 + 1]; // 小端：高字节在后一位
            return out8;
        }

        #endregion
        /// <summary>
        /// 停止采集流
        /// </summary>
        public Result StopGrabbing()
        {
            if (m_MyCamera != null)
            {
                m_MyCamera.StopGrabbing();
                // 显式解绑回调函数，规避残余回调响应
                m_MyCamera.RegisterImageCallBackEx(null, IntPtr.Zero);
                // 清空委托引用，允许下次 StartGrabbing 重新注册
                m_ImageCallback = null;
            }
            return Result.Ok();
        }

        public Result StartContinuousGrab() => StartGrabbing();
        public Result StopContinuousGrab() => StopGrabbing();
        public Result StartCapture() => StartGrabbing();

        /// <summary>
        /// 发送软触发指令
        /// </summary>
        public Result SoftTrigger()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法发送软触发指令！");
            }

            int nRet = m_MyCamera.SetCommandValue("TriggerSoftware");
            if (nRet != CErrorDefine.MV_OK)
            {
                return Result.Fail(Helper.ShowErrorMsg("软触发指令发送失败", nRet));
            }

            return Result.Ok();
        }

        public Result SoftwareTrigger() => SoftTrigger();

        /// <summary>
        /// 相机侧完整配置为软触发取图（实现 ICamera 契约）。
        /// 标定采样必须「走位 → 软触发 → 本点新帧」，不能依赖连续自由流：
        /// 连续流下"等新帧"等于等一个随机时刻，慢帧必超时，走位后还可能拿到上一位置的帧。
        /// 海康 MVS 三件套：TriggerSelector=FrameStart + TriggerMode=On + TriggerSource=Software，
        /// 并关闭 AcquisitionFrameRateEnable（帧率限制会丢弃超速触发）。
        /// </summary>
        public Result ConfigureSoftwareTrigger()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                // 未连接时只记录意图，等 Connect() 后由 SyncParamsToDevice 下发
                ConfigParams["TriggerModeSelect"] = 1;
                return Result.Ok();
            }

            try
            {
                // 1. 关掉帧率限制：软触发频率由上位机决定，限帧会把触发请求丢掉
                try { m_MyCamera.SetBoolValue("AcquisitionFrameRateEnable", false); }
                catch { /* 个别机型无此节点，忽略 */ }

                // 2. 触发选择器固定帧起始（曝光开始），部分机型无此节点
                try { m_MyCamera.SetEnumValueByString("TriggerSelector", "FrameStart"); }
                catch { /* 同上 */ }

                // 3. 复用既有三态设置完成 TriggerMode=On / TriggerSource=Software
                return SetTriggerMode(1);
            }
            catch (Exception ex)
            {
                return Result.Fail("配置软触发失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 设置触发模式（重载 1：三态模式切换）
        /// </summary>
        /// <param name="mode">0: 连续模式, 1: 软触发模式, 2: 硬件外触发模式(Line0)</param>
        public Result SetTriggerMode(int mode)
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                ConfigParams["TriggerModeSelect"] = mode;
                return Result.Ok();
            }

            int nRet = CErrorDefine.MV_OK;

            switch (mode)
            {
                case 0: // 连续采集模式 (TriggerMode = Off)
                    nRet = m_MyCamera.SetEnumValue("TriggerMode", (uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_OFF);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("关闭触发模式失败", nRet));

                    ConfigParams["TriggerMode"] = false;
                    ConfigParams["TriggerSource"] = "Off";
                    break;

                case 1: // 软触发模式 (TriggerMode = On, TriggerSource = Software)
                    nRet = m_MyCamera.SetEnumValue("TriggerMode", (uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("开启触发模式失败", nRet));

                    nRet = m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("设置软触发源失败", nRet));

                    ConfigParams["TriggerMode"] = true;
                    ConfigParams["TriggerSource"] = "Software";
                    break;

                case 2: // 硬件外触发模式 (TriggerMode = On, TriggerSource = Line0)
                    nRet = m_MyCamera.SetEnumValue("TriggerMode", (uint)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("开启触发模式失败", nRet));

                    nRet = m_MyCamera.SetEnumValue("TriggerSource", (uint)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_LINE0);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("设置硬件触发源(Line0)失败", nRet));

                    ConfigParams["TriggerMode"] = true;
                    ConfigParams["TriggerSource"] = "Line0";
                    break;

                default:
                    return Result.Fail($"不支持的触发模式选项: {mode}");
            }

            ConfigParams["TriggerModeSelect"] = mode;
            return Result.Ok();
        }
        /// <summary>
        /// 设置触发模式（重载 2：契约层 Bool 开关）
        /// </summary>
        public Result SetTriggerMode(bool enable)
        {
            // enable 为 false 时切为连续采集(0)
            if (!enable) return SetTriggerMode(0);

            // enable 为 true 时，如果原本配置了硬触发(2)，保留硬触发；否则切为软触发(1)
            int currentMode = ConfigParams.ContainsKey("TriggerModeSelect") ? Convert.ToInt32(ConfigParams["TriggerModeSelect"]) : 1;
            int targetMode = (currentMode == 2) ? 2 : 1;

            return SetTriggerMode(targetMode);
        }

        /// <summary>
        /// 设置触发模式（重载 2：契约层 Bool 快捷开关）
        /// </summary>

        #endregion

        #region ICamera 曝光增益接口快捷封装
        public Result SetExposureTime(double valueUs)
        {
            return SetParam("ExposureTime", valueUs);
        }

        public Result GetExposureTime()
        {
            return GetParam("ExposureTime") as Result<object>;
        }

        public Result SetGain(double value)
        {
            return SetParam("Gain", value);
        }

        public Result GetGain()
        {
            return GetParam("Gain");
        }
        #endregion

        #region IDevice 设备连接/状态/通用参数
        private CCamera m_MyCamera;
        private CCameraInfo cCameraInfo;

        public Result Connect()
        {
            // 核心修复：如果 cCameraInfo 为空（说明是从 LiteDB 离线恢复的），尝试通过 DeviceId 重新枚举定位
            if (cCameraInfo == null)
            {
                var searchRes = FindCameraInfoByDeviceId(DeviceId);
                if (!searchRes.Success)
                {
                    return Result.Fail($"连接失败：未能在线定位到 SerialNumber 为 [{DeviceId}] 的相机！");
                }
                cCameraInfo = searchRes.Data;
            }

            if (m_MyCamera == null)
            {
                m_MyCamera = new CCamera();
            }

            int nRet = m_MyCamera.CreateHandle(ref cCameraInfo);
            if (CErrorDefine.MV_OK != nRet)
            {
                return Result.Fail("创建相机句柄失败");
            }

            nRet = m_MyCamera.OpenDevice();
            if (CErrorDefine.MV_OK != nRet)
            {
                m_MyCamera.DestroyHandle();
                return Result.Fail(Helper.ShowErrorMsg("Device open fail!", nRet));
            }

            // ★ 包大小对齐：必须在 StartGrabbing 之前、且在任何参数同步之前做。
            //   这一步缺失正是黑白相机"出帧但全黑、需 MV 打开一次才正常"的根因。
            EnsureOptimalPacketSize();

            State = DeviceState.Connected;

            // ★ 黑屏诊断连线：先看相机"本来是什么参数"，再看"我们下发后变成什么参数"。
            //   两份快照一 diff，就能判断是我们写坏了，还是相机自己的默认状态就有问题。
            _blackDumpCount = 0;
            _firstFrameDumped = false;
            _lastBlackDumpAt = DateTime.MinValue;

            DumpImagingParams("连接后·相机原始");
            SyncTriggerModeFromDevice();
            SyncParamsFromDevice();
            DumpConfigParams("下发前·本地字典");

            // 参数下发失败此前被静默忽略（返回值直接丢弃），硬件到底改没改成完全不可见。
            var syncRes = SyncParamsToDevice();
            if (!syncRes.Success)
            {
                LogBus.Warn("HikCam", $"[参数下发] {syncRes.Message}");
            }

            DumpImagingParams("下发后·实际生效");

            return Result.Ok();
        }
        /// <summary>
        /// 辅助方法：连接时动态搜索在线匹配 SN 的硬件结构体
        /// </summary>
        private Result<CCameraInfo> FindCameraInfoByDeviceId(string targetSn)
        {
            List<CCameraInfo> deviceList = new List<CCameraInfo>();
            int nRet = CSystem.EnumDevices(CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE, ref deviceList);
            if (nRet != 0 || deviceList.Count == 0)
            {
                return Result<CCameraInfo>.Fail("未扫描到任何在线海康设备");
            }

            foreach (var dev in deviceList)
            {
                if (dev.nTLayerType == CSystem.MV_GIGE_DEVICE)
                {
                    var gigeInfo = (CGigECameraInfo)dev;
                    if (string.Equals(gigeInfo.chSerialNumber, targetSn, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<CCameraInfo>.Ok(dev);
                    }
                }
                // USB 相机同理支持
                else if (dev.nTLayerType == CSystem.MV_USB_DEVICE)
                {
                    var usbInfo = (CUSBCameraInfo)dev;
                    if (string.Equals(usbInfo.chSerialNumber, targetSn, StringComparison.OrdinalIgnoreCase))
                    {
                        return Result<CCameraInfo>.Ok(dev);
                    }
                }
            }

            return Result<CCameraInfo>.Fail($"未寻找到序列号为 [{targetSn}] 的设备");
        }

        public Result Disconnect()
        {
            if (m_MyCamera != null)
            {
                // ★ 必须先停采集流并注销回调，再关设备。
                //   原实现直接 CloseDevice/DestroyHandle：相机侧残留 Acquisition 状态、
                //   本进程的图像回调也未解绑 —— 重连时容易带着脏状态进来，
                //   且 GigE 相机可能仍处于被占用状态，导致其它程序（包括我们自己）打不开。
                try
                {
                    StopGrabbing();
                }
                catch (Exception ex)
                {
                    LogBus.Warn("HikCam", $"断开前停止采集异常（继续关闭设备）: {ex.Message}");
                }

                m_MyCamera.CloseDevice();
                m_MyCamera.DestroyHandle();
            }
            State = DeviceState.Disconnected;
            return Result.Ok();
        }

        public Result CheckStatus()
        {
            LastHeartbeatAt = DateTime.UtcNow;

            if (m_MyCamera == null || State != DeviceState.Connected)
            {
                State = DeviceState.Disconnected;
                return Result.Fail("相机未连接");
            }

            try
            {
                var widthValue = new CIntValue();
                int nRet = m_MyCamera.GetIntValue("Width", ref widthValue);
                if (nRet != CErrorDefine.MV_OK)
                {
                    State = DeviceState.Disconnected;
                    return Result.Fail(Helper.ShowErrorMsg("相机在线状态检查失败", nRet));
                }

                State = DeviceState.Connected;
                return Result.Ok();
            }
            catch (Exception ex)
            {
                State = DeviceState.Disconnected;
                return Result.Fail($"相机在线状态检查异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新本地字典里的参数值（UI修改时调用）
        /// </summary>
        public Result SetParam(string key, object value)
        {
            if (value == null) return Result.Fail("参数值不能为 null");

            // 写参数时：曝光/增益/目标帧率写入前关闭自动模式，确保设置生效
            if (State == DeviceState.Connected && m_MyCamera != null)
            {
                if (string.Equals(key, "ExposureTime", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "Exposure", StringComparison.OrdinalIgnoreCase))
                {
                    m_MyCamera.SetEnumValue("ExposureAuto", (uint)MV_CAM_EXPOSURE_AUTO_MODE.MV_EXPOSURE_AUTO_MODE_OFF);
                }
                else if (string.Equals(key, "Gain", StringComparison.OrdinalIgnoreCase))
                {
                    m_MyCamera.SetEnumValue("GainAuto", (uint)MV_CAM_GAIN_MODE.MV_GAIN_MODE_OFF);
                }
            }

            ConfigParams[key] = value;

            // ★★ 在线即下发（2026-09-15 修复）：这里原先只写本地字典就返回 Ok，而真正写硬件的
            //   SyncParamsToDevice() **只被 Connect() 调用** ⇒ 运行期（链条节点）设的曝光/增益
            //   **从来没到过相机**，dump 出来的是相机自己的旧值（实测节点要 5000、硬件是 8000）。
            //   现在：在线时立即写硬件；离线时保持"只登记"，由 Connect() → SyncParamsToDevice() 补发。
            if (State == DeviceState.Connected && m_MyCamera != null && !IsNonHardwareKey(key))
            {
                // 与 SyncParamsToDevice 同规则：字典里已有相机回读的真实 ExposureTime 时，
                // 历史别名 "Exposure" 不得覆盖它（谁最后生效取决于调用顺序 —— 不可依赖）。
                if (string.Equals(key, "Exposure", StringComparison.OrdinalIgnoreCase) &&
                    ConfigParams.ContainsKey("ExposureTime"))
                {
                    return Result.Ok();
                }

                var applyRes = WriteNodeToHardware(ToSdkNodeKey(key), value);
                if (!applyRes.Success)
                {
                    // 不回滚本地字典（登记仍然有效），但必须让调用方看见 —— 静默返回 Ok 正是本次缺陷的成因。
                    return Result.Fail($"参数[{key}]已登记，但下发硬件失败：{applyRes.Message}");
                }

                // 每次下发留一条可检索的证据（Debug 级，不淹没正常日志）：
                // 有这一行才能回答"节点要的 5000 到底有没有到相机"，而不是靠人肉比对 dump。
                LogBus.Debug("HikCam", $"[参数下发] {ToSdkNodeKey(key)}={value} 已写入硬件（SN {DeviceId ?? "-"}）");
            }

            return Result.Ok();
        }

        /// <summary>
        /// 从本地字典获取参数值
        /// </summary>
        public Result<object> GetParam(string key)
        {
            if (!ConfigParams.ContainsKey(key))
            {
                return Result<object>.Fail($"{key}键不存在");
            }

            return Result<object>.Ok(ConfigParams[key]);
        }

        #endregion

        #region 参数同步功能（UI点击确认 / 从硬件读取）

        /// <summary>
        /// 界面点击【确定】时调用：检查连接状态，并将 ConfigParams 中的参数批量下发到相机
        /// </summary>
        public Result SyncParamsToDevice()
        {
            // 1. 在线检查：只有连接状态且句柄有效才允许发参数
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接或掉线，无法下发参数到硬件！");
            }

            List<string> errorList = new List<string>();

            // 如果字典包含 TriggerModeSelect 设定的选型，优先下发触发模式
            if (ConfigParams.ContainsKey("TriggerModeSelect"))
            {
                int modeSelect = Convert.ToInt32(ConfigParams["TriggerModeSelect"]);
                Result trigRes = SetTriggerMode(modeSelect);
                if (!trigRes.Success)
                {
                    errorList.Add(trigRes.Message);
                }
            }

            // 2. 遍历字典，将通用参数下发到海康 SDK 节点
            foreach (var kvp in ConfigParams)
            {
                string key = kvp.Key;
                object value = kvp.Value;

                // 排除非海康 GenICam 节点的内部逻辑字段 / 只读回读节点 / 连接信息
                // ★ 清单与 SetParam 在线直写共用（IsNonHardwareKey）
                if (IsNonHardwareKey(key))
                {
                    continue;
                }

                // ★ "Exposure" 只是 "ExposureTime" 的历史别名，二者映射到同一个硬件节点。
                //   当字典里已有相机回读的真实 ExposureTime 时必须跳过别名，
                //   否则 CreateDevice() 写死的默认曝光（3600µs）会把相机上已调好的值冲掉，
                //   而谁最后生效取决于字典遍历顺序 —— 不可依赖。
                if (string.Equals(key, "Exposure", StringComparison.OrdinalIgnoreCase) &&
                    ConfigParams.ContainsKey("ExposureTime"))
                {
                    continue;
                }

                string sdkKey = ToSdkNodeKey(key);

                Result setRes = WriteNodeToHardware(sdkKey, value);
                if (!setRes.Success)
                {
                    errorList.Add(setRes.Message);
                }
            }

            if (errorList.Count > 0)
            {
                return Result.Fail("部分参数下发失败:\n" + string.Join("\n", errorList));
            }

            return Result.Ok();
        }

        /// <summary>
        /// 从相机硬件实时读取触发模式节点，更新 ConfigParams
        /// </summary>
        public Result SyncTriggerModeFromDevice()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法从硬件读取触发模式");
            }

            CEnumValue triggerModeVal = new CEnumValue();
            int nRet = m_MyCamera.GetEnumValue("TriggerMode", ref triggerModeVal);
            if (nRet != CErrorDefine.MV_OK)
            {
                return Result.Fail(Helper.ShowErrorMsg("读取 TriggerMode 节点失败", nRet));
            }

            if (triggerModeVal.CurValue == (long)MV_CAM_TRIGGER_MODE.MV_TRIGGER_MODE_ON)
            {
                ConfigParams["TriggerMode"] = true;

                CEnumValue triggerSourceVal = new CEnumValue();
                nRet = m_MyCamera.GetEnumValue("TriggerSource", ref triggerSourceVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    if (triggerSourceVal.CurValue == (long)MV_CAM_TRIGGER_SOURCE.MV_TRIGGER_SOURCE_SOFTWARE)
                    {
                        ConfigParams["TriggerSource"] = "Software";
                        ConfigParams["TriggerModeSelect"] = 1; // 1: 软触发
                    }
                    else
                    {
                        ConfigParams["TriggerSource"] = "Hardware";
                        ConfigParams["TriggerModeSelect"] = 2; // 2: 硬触发
                    }
                }
            }
            else
            {
                ConfigParams["TriggerMode"] = false;
                ConfigParams["TriggerSource"] = "Off";
                ConfigParams["TriggerModeSelect"] = 0; // 0: 连续
            }

            return Result.Ok();
        }

        /// <summary>
        /// 从相机硬件实时读取常见节点数值，并更新保存到 ConfigParams 字典中
        /// </summary>
        public Result SyncParamsFromDevice()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法从硬件读取参数！");
            }

            // 同步触发模式
            SyncTriggerModeFromDevice();

            // 要同步读取的常见标准节点列表
            string[] keysToRead = new string[] { "ExposureTime", "Gain", "ResultingFrameRate", "AcquisitionFrameRate", "Width", "Height" };

            foreach (var key in keysToRead)
            {
                // 1. 尝试 Float 类型读取
                CFloatValue floatVal = new CFloatValue();
                int nRet = m_MyCamera.GetFloatValue(key, ref floatVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    ConfigParams[key] = floatVal.CurValue;
                    continue;
                }

                // 2. 尝试 Int 类型读取
                CIntValue intVal = new CIntValue();
                nRet = m_MyCamera.GetIntValue(key, ref intVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    ConfigParams[key] = intVal.CurValue;
                    continue;
                }

                // 3. 尝试 Enum 类型读取
                CEnumValue enumVal = new CEnumValue();
                nRet = m_MyCamera.GetEnumValue(key, ref enumVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    ConfigParams[key] = enumVal.CurValue;
                    continue;
                }

                // 4. 尝试 Bool 类型读取
                bool bVal = false;
                nRet = m_MyCamera.GetBoolValue(key, ref bVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    ConfigParams[key] = bVal;
                    continue;
                }

                // 5. 尝试 String 类型读取
                CStringValue strVal = new CStringValue();
                nRet = m_MyCamera.GetStringValue(key, ref strVal);
                if (nRet == CErrorDefine.MV_OK)
                {
                    ConfigParams[key] = strVal.CurValue;
                    continue;
                }
            }

            return Result.Ok();
        }

        /// <summary>
        /// 不能/不必下发到硬件的键：内部逻辑字段（不是 GenICam 节点）、只读回读节点、连接信息。
        /// ★ SyncParamsToDevice（批量下发）与 SetParam（在线直写）**共用这一份清单** ——
        ///   同样的排除规则写两遍，早晚会在某一侧漏掉一项。
        /// </summary>
        private static bool IsNonHardwareKey(string key)
        {
            return string.Equals(key, "IP", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "TriggerModeSelect", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "TriggerSource", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "TriggerMode", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "SaveImageFile", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "PixelFormat", StringComparison.OrdinalIgnoreCase)
                // 只读回读节点：由 SyncParamsFromDevice 读进字典，写回相机必然失败
                || string.Equals(key, "ResultingFrameRate", StringComparison.OrdinalIgnoreCase)
                // 设备池的连接信息：不是相机节点
                || string.Equals(key, "ConnectionString", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>本地键名 → 海康 GenICam 节点名（"Exposure" 是 "ExposureTime" 的历史别名）</summary>
        private static string ToSdkNodeKey(string key)
        {
            return string.Equals(key, "Exposure", StringComparison.OrdinalIgnoreCase) ? "ExposureTime" : key;
        }

        /// <summary>
        /// 私有辅助：将单一键值根据类型安全写入 SDK
        /// </summary>
        private Result WriteNodeToHardware(string sdkKey, object value)
        {
            int nRet = CErrorDefine.MV_OK;

            try
            {
                switch (value)
                {
                    case float fVal:
                        nRet = m_MyCamera.SetFloatValue(sdkKey, fVal);
                        break;

                    case double dVal:
                        nRet = m_MyCamera.SetFloatValue(sdkKey, (float)dVal);
                        break;

                    case int iVal:
                        nRet = m_MyCamera.SetIntValue(sdkKey, (uint)iVal);
                        if (nRet != CErrorDefine.MV_OK)
                        {
                            nRet = m_MyCamera.SetEnumValue(sdkKey, (uint)iVal);
                        }
                        break;

                    case long lVal:
                        nRet = m_MyCamera.SetIntValue(sdkKey, (uint)lVal);
                        break;

                    case uint uVal:
                        nRet = m_MyCamera.SetIntValue(sdkKey, uVal);
                        if (nRet != CErrorDefine.MV_OK)
                        {
                            nRet = m_MyCamera.SetEnumValue(sdkKey, uVal);
                        }
                        break;

                    case bool bVal:
                        nRet = m_MyCamera.SetBoolValue(sdkKey, bVal);
                        break;

                    case string sVal:
                        if (float.TryParse(sVal, out float parsedFloat))
                        {
                            nRet = m_MyCamera.SetFloatValue(sdkKey, parsedFloat);
                        }
                        else
                        {
                            nRet = m_MyCamera.SetStringValue(sdkKey, sVal);
                        }
                        break;

                    default:
                        return Result.Fail($"参数[{sdkKey}]的类型[{value.GetType().Name}]不受支持");
                }

                if (nRet != CErrorDefine.MV_OK)
                {
                    string msg = Helper.ShowErrorMsg($"设置节点[{sdkKey}]失败", nRet);
                    return Result.Fail(msg);
                }

                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail($"下发节点[{sdkKey}]异常: {ex.Message}");
            }
        }

        #endregion

        #region 图像保存

        /// <summary>
        /// 保存当前最新一帧到指定路径，支持 bmp/jpg/png/tif
        /// </summary>
        public Result SaveImageFile(string filePath, string format)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return Result.Fail("保存路径不能为空");

            if (State != DeviceState.Connected || m_MyCamera == null)
                return Result.Fail("相机未连接，无法保存图像");

            byte[] bufferCopy;
            int width, height;
            string pixelFormat;

            // 1. 快速加锁完成【深拷贝快照】，随后立即解锁，绝不长时间占用锁
            lock (_latestFrameLock)
            {
                if (_latestFrame == null || _latestFrame.Buffer == null || _latestFrame.Buffer.Length == 0)
                    return Result.Fail("当前没有可用图像帧");

                width = _latestFrame.Width;
                height = _latestFrame.Height;
                pixelFormat = _latestFrame.PixelFormat;

                // 执行深拷贝！防止后续连续采集写入冲刷此 Buffer
                bufferCopy = new byte[_latestFrame.Buffer.Length];
                Array.Copy(_latestFrame.Buffer, bufferCopy, _latestFrame.Buffer.Length);
            }

            // 2. 匹配海康 SDK 格式枚举
            MV_SAVE_IAMGE_TYPE imageType;
            switch (format?.ToLowerInvariant())
            {
                case "bmp": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_BMP; break;
                case "jpg":
                case "jpeg": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_JPEG; break;
                case "png": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_PNG; break;
                case "tif":
                case "tiff": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_TIF; break;
                default: return Result.Fail($"不支持的保存格式: {format}");
            }

            MvGvspPixelType sdkPixelType;
            string fmtUpper = (pixelFormat ?? "").ToUpperInvariant();
            if (fmtUpper.Contains("BGR")) sdkPixelType = MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
            else if (fmtUpper.Contains("RGB")) sdkPixelType = MvGvspPixelType.PixelType_Gvsp_RGB8_Packed;
            else sdkPixelType = MvGvspPixelType.PixelType_Gvsp_Mono8;

            try
            {
                var cImage = new CImage
                {
                    Width = (ushort)width,
                    Height = (ushort)height,
                    FrameLen = (uint)bufferCopy.Length,
                    PixelType = sdkPixelType
                };

                var imageField = typeof(CImage)
                    .GetFields(System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic |
                               System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(f => (f.Name == "pBufAddr" || f.Name == "Image" || f.Name == "image") && f.FieldType == typeof(IntPtr));

                if (imageField == null) return Result.Fail("无法定位 CImage 指针字段");

                IntPtr imageBuffer = Marshal.AllocHGlobal(bufferCopy.Length);
                imageField.SetValue(cImage, imageBuffer);

                try
                {
                    // 将深拷贝出的独立内存写入非托管区
                    Marshal.Copy(bufferCopy, 0, imageBuffer, bufferCopy.Length);

                    var saveParam = new CSaveImgToFileParam
                    {
                        ImageType = imageType,
                        Image = cImage,
                        Quality = (format.ToLowerInvariant() == "jpg" || format.ToLowerInvariant() == "jpeg") ? (uint)80 : 0,
                        MethodValue = 0,
                        ImagePath = filePath
                    };

                    int nRet = m_MyCamera.SaveImageToFile(ref saveParam);
                    if (nRet != CErrorDefine.MV_OK)
                        return Result.Fail(Helper.ShowErrorMsg("保存图像失败", nRet));
                }
                finally
                {
                    if (imageBuffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(imageBuffer);
                }

                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail($"保存图像异常: {ex.Message}");
            }
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
                FrameReceived = null;
                StopContinuousGrab();
                StopGrabbing();
                Disconnect();
                lock (_latestFrameLock)
                {
                    _latestFrame = null;
                }
            }

            _disposed = true;
        }

        ~HikCamera()
        {
            Dispose(false);
        }
        #endregion


        #region IIoDevice 硬件 GPIO 控制实现

        /// <summary>
        /// 读取相机的硬件输入 IO (DI)
        /// channelIndex 0 对应 Line0, 1 对应 Line1...
        /// </summary>
        public Result<bool> ReadDi(int channelIndex)
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result<bool>.Fail("相机未连接，无法读取 DI 状态");
            }

            try
            {
                // 1. 选择对应的 Line 通道 (例如 Line0)
                uint lineSelectorValue = (uint)channelIndex;
                int nRet = m_MyCamera.SetEnumValue("LineSelector", lineSelectorValue);
                if (nRet != CErrorDefine.MV_OK)
                {
                    return Result<bool>.Fail(Helper.ShowErrorMsg($"设置 LineSelector[{channelIndex}] 失败", nRet));
                }

                // 2. 读取当前 Line 的高低电平状态 (LineStatus)
                bool lineStatus = false;
                nRet = m_MyCamera.GetBoolValue("LineStatus", ref lineStatus);
                if (nRet != CErrorDefine.MV_OK)
                {
                    return Result<bool>.Fail(Helper.ShowErrorMsg($"读取 LineStatus[{channelIndex}] 失败", nRet));
                }

                return Result<bool>.Ok(lineStatus);
            }
            catch (Exception ex)
            {
                return Result<bool>.Fail($"读取相机 Line0/{channelIndex} 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取相机的硬件输出 IO (DO) 状态
        /// </summary>
        public Result<bool> ReadDo(int channelIndex)
        {
            // 对于工业相机，读取 DO 同样查询其 LineStatus 节点
            return ReadDi(channelIndex);
        }

        /// <summary>
        /// 控制相机的硬件输出 IO (DO)
        /// channelIndex: 2 对应 Line2 (通常为 Strobe / UserOutput)
        /// </summary>
        public Result WriteDo(int channelIndex, bool state)
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法写入 DO 状态");
            }

            try
            {
                // 1. 选择对应的 Line 通道 (例如 Line2 为通用输出/频闪控制)
                uint lineSelectorValue = (uint)channelIndex;
                int nRet = m_MyCamera.SetEnumValue("LineSelector", lineSelectorValue);
                if (nRet != CErrorDefine.MV_OK)
                {
                    return Result.Fail(Helper.ShowErrorMsg($"设置 LineSelector[{channelIndex}] 失败", nRet));
                }

                // 2. 设置该 Line 的工作模式为 Output (部分海康型号需明确指定 LineMode)
                m_MyCamera.SetEnumValue("LineMode", 1); // 1 代表 Output 模式

                // 3. 将 LineSource 切换为 UserOutput，支持上位机软件直接控制
                m_MyCamera.SetEnumValue("LineSource", 0); // 0 代表 UserOutput1

                // 4. 写入 UserOutputValue 点位状态
                nRet = m_MyCamera.SetBoolValue("UserOutputValue", state);
                if (nRet != CErrorDefine.MV_OK)
                {
                    return Result.Fail(Helper.ShowErrorMsg($"写入 UserOutputValue[{channelIndex}] 失败", nRet));
                }

                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail($"控制相机 DO[{channelIndex}] 异常: {ex.Message}");
            }
        }

        #endregion

        // 默认无参或带 SerialNumber 构造，支持离线/数据库恢复实例化
        public HikCamera(string deviceId)
        {
            DeviceId = deviceId;
        }

        public HikCamera(CCameraInfo info)
        {
            cCameraInfo = info;
            if (info is CGigECameraInfo gigeInfo)
            {
                DeviceId = gigeInfo.chSerialNumber;
            }
        }
    }

    public class HikvisionPlugin : IHardwarePlugin
    {
        public string BrandName => "Hikvision";
        public DeviceCategory Category => DeviceCategory.Camera;
        public string Version => "1.0.0";
        // 高优先级：优先接管海康相机
        public int Priority => 100;

        public bool Supports(DeviceCategory category, string brand)
        {
            return category == DeviceCategory.Camera &&
                   string.Equals(brand, BrandName, StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize() { }

        List<CCameraInfo> cCameraInfos = new List<CCameraInfo>();

        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            GC.Collect();
            cCameraInfos.Clear();

            int nRet = CSystem.EnumDevices(CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE, ref cCameraInfos);
            if (0 != nRet)
            {
                string errorMsg = Helper.ShowErrorMsg("Enumerate devices fail!", 0);
                return Result<List<DeviceInfo>>.Fail(errorMsg);
            }

            // 厂商过滤（2026-09-01）：海康 MVS 的 GigE 枚举走 GigE Vision 标准发现协议（GVCP），
            // 会把任何支持 GigE Vision 的相机都扫出来——包括巴斯勒等第三方品牌。
            // 现场实测：1 台 Basler 被海康插件误报为"海康相机"。这里按 ManufacturerName 过滤，
            // 只保留海康（HIKVISION / HIK）自家设备，其他品牌留给对应品牌插件接管。
            var hikOnly = cCameraInfos.Where(IsHikvisionDevice).ToList();
            if (cCameraInfos.Count != hikOnly.Count)
            {
                System.Diagnostics.Debug.WriteLine($"[Hikvision] 枚举到 {cCameraInfos.Count} 台 GigE/USB 相机，" +
                                                   $"其中 {cCameraInfos.Count - hikOnly.Count} 台非海康品牌已过滤（留给对应品牌插件）");
            }

            var list = hikOnly.Select(c =>
            {
                string sn = "";
                string model = "";
                if (c.nTLayerType == CSystem.MV_GIGE_DEVICE)
                {
                    var gige = (CGigECameraInfo)c;
                    sn = gige.chSerialNumber;
                    model = gige.chModelName;
                }
                else if (c.nTLayerType == CSystem.MV_USB_DEVICE)
                {
                    var usb = (CUSBCameraInfo)c;
                    sn = usb.chSerialNumber;
                    model = usb.chModelName;
                }

                return new DeviceInfo
                {
                    DeviceId = sn,
                    ModelName = model,
                    Category = Category,
                    BrandName = BrandName
                };
            }).ToList();

            return Result<List<DeviceInfo>>.Ok(list);
        }

        /// <summary>
        /// 判断枚举出的相机是否海康品牌（按结构体 ManufacturerName / VendorName 过滤）。
        /// GigE Vision 是标准协议，海康 MVS 枚举能发现 Basler 等第三方相机；
        /// 只有厂商名含 "HIK" 的设备才归海康插件接管。
        /// </summary>
        private static bool IsHikvisionDevice(CCameraInfo c)
        {
            string manufacturer = null;
            string vendor = null;
            try
            {
                if (c.nTLayerType == CSystem.MV_GIGE_DEVICE)
                {
                    var gige = (CGigECameraInfo)c;
                    manufacturer = gige.chManufacturerName;
                }
                else if (c.nTLayerType == CSystem.MV_USB_DEVICE)
                {
                    var usb = (CUSBCameraInfo)c;
                    manufacturer = usb.chManufacturerName;
                    vendor = usb.chVendorName;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Hikvision] 读取相机厂商信息失败: {ex.Message}");
                return false;
            }

            string text = (manufacturer + " " + vendor).ToUpperInvariant();
            return text.Contains("HIK") || text.Contains("HIKVISION");
        }

        /// <summary>
        /// 无论是从 UI 扫描创建，还是从 LiteDB 恢复，都能顺利返回 IDevice 实例！
        /// </summary>
        public IContractDevice CreateDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return null;

            // 1. 优先查内存中已有的结构体（如果是刚扫描出来的）
            var matchedInfo = cCameraInfos.FirstOrDefault(c =>
            {
                if (c.nTLayerType == CSystem.MV_GIGE_DEVICE) return ((CGigECameraInfo)c).chSerialNumber == deviceId;
                if (c.nTLayerType == CSystem.MV_USB_DEVICE) return ((CUSBCameraInfo)c).chSerialNumber == deviceId;
                return false;
            });

            HikCamera device;
            if (matchedInfo != null)
            {
                device = new HikCamera(matchedInfo);
            }
            else
            {
                // 2. 如果是从数据库恢复的（cCameraInfos 为空），直接通过 DeviceId 建立纯软对象
                device = new HikCamera(deviceId);
            }

            device.BrandName = BrandName;
            device.Category = Category;

            // 默认初始化参数
            device.SetParam("Exposure", 3600.0);
            device.SetParam("Gain", 0.0);
            device.SetParam("TriggerModeSelect", 0);

            return device;
        }

        public void Shutdown() { }
    }

    public class Helper
    {
        #region 通用工具：错误码翻译打印
        public static string ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                errorMsg = csMessage;
            }
            else
            {
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            switch (nErrorNum)
            {
                case CErrorDefine.MV_E_HANDLE: errorMsg += " 句柄错误/设备未创建"; break;
                case CErrorDefine.MV_E_SUPPORT: errorMsg += " 当前相机不支持该功能"; break;
                case CErrorDefine.MV_E_BUFOVER: errorMsg += " SDK图像缓存溢出，帧率过高处理不过来"; break;
                case CErrorDefine.MV_E_CALLORDER: errorMsg += " API调用顺序错误（比如没Open就StartGrab）"; break;
                case CErrorDefine.MV_E_PARAMETER: errorMsg += " 入参非法（曝光填负数、地址不存在）"; break;
                case CErrorDefine.MV_E_RESOURCE: errorMsg += " 申请硬件/内存资源失败"; break;
                case CErrorDefine.MV_E_NODATA: errorMsg += " 超时未收到图像，硬件无数据返回"; break;
                case CErrorDefine.MV_E_PRECONDITION: errorMsg += " 前置条件不满足，环境变动"; break;
                case CErrorDefine.MV_E_VERSION: errorMsg += " SDK版本和相机固件不匹配"; break;
                case CErrorDefine.MV_E_NOENOUGH_BUF: errorMsg += " 内存不足，存不下一帧图像"; break;
                case CErrorDefine.MV_E_UNKNOW: errorMsg += " 未知底层错误"; break;
                case CErrorDefine.MV_E_GC_GENERIC: errorMsg += " GenICam通用底层错误"; break;
                case CErrorDefine.MV_E_GC_ACCESS: errorMsg += " GenICam节点读写权限错误"; break;
                case CErrorDefine.MV_E_ACCESS_DENIED: errorMsg += " 无权限占用相机，别的软件正在占用"; break;
                case CErrorDefine.MV_E_BUSY: errorMsg += " 设备忙/网线断开/USB掉线"; break;
                case CErrorDefine.MV_E_NETER: errorMsg += " 网络异常（GigE相机网线松动、IP不通）"; break;
            }

            return errorMsg;
        }
        #endregion
    }
}