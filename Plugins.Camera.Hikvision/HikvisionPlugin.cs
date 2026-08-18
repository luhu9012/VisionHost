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
using System.Threading;
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

                byte[] frameBuffer;
                string outFormat;

                if (isBayer)
                {
                    int rgbLen = width * height * 3; // BGR24 内存大小
                    frameBuffer = new byte[rgbLen];

                    // 锁定托管数组指针，避免分配非托管内存开销
                    GCHandle handle = GCHandle.Alloc(frameBuffer, GCHandleType.Pinned);
                    try
                    {
                        IntPtr pDstBuffer = handle.AddrOfPinnedObject();

                        // 1. 实例化海康 SDK 高级封装类 CPixelConvertParam
                        CPixelConvertParam convertParam = new CPixelConvertParam();

                        // 2. 配置输入图像参数[cite: 11]
                        convertParam.InImage.Width = (ushort)width;
                        convertParam.InImage.Height = (ushort)height;
                        convertParam.InImage.PixelType = pixelType;
                        convertParam.InImage.ImageAddr = pData;
                        convertParam.InImage.FrameLen = pFrameInfo.nFrameLen;

                        // 3. 配置输出图像参数（直接绑定托管数组指针 pDstBuffer，防止 SDK 重复 AllocateUnmanagedMemory）
                        convertParam.OutImage.PixelType = MvGvspPixelType.PixelType_Gvsp_BGR8_Packed;
                        convertParam.OutImage.ImageAddr = pDstBuffer;
                        convertParam.OutImage.ImageSize = (uint)rgbLen;

                        // 4. 调用转码[cite: 11]
                        int nRet = m_MyCamera.ConvertPixelType(ref convertParam); 
        if (nRet == CErrorDefine.MV_OK)
                        {
                            outFormat = "BGR24";
                        }
                        else
                        {
                            // 转码失败时降级按原尺寸 Copy
                            frameBuffer = new byte[pFrameInfo.nFrameLen];
                            Marshal.Copy(pData, frameBuffer, 0, (int)pFrameInfo.nFrameLen);
                            outFormat = typeStr;
                        }
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                else
                {
                    // Mono 或已是标准 RGB/BGR 格式
                    frameBuffer = new byte[pFrameInfo.nFrameLen];
                    Marshal.Copy(pData, frameBuffer, 0, (int)pFrameInfo.nFrameLen);
                    outFormat = isMono ? "MONO8" : (typeStr.Contains("RGB") ? "RGB24" : "BGR24");
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

                OnFrameReceived(frameArgs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HikCamera] 图像回调处理异常: {ex.Message}");
            }
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

            // ... 后续包大小调整及参数同步保持不变
            State = DeviceState.Connected;
            SyncTriggerModeFromDevice();
            SyncParamsFromDevice();
            SyncParamsToDevice();

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

                // 排除非海康 GenICam 节点的内部逻辑字段
                if (string.Equals(key, "IP", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "TriggerModeSelect", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "TriggerSource", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "TriggerMode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "SaveImageFile", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "PixelFormat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string sdkKey = key;
                if (key == "Exposure")
                {
                    sdkKey = "ExposureTime";
                }

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

            var list = cCameraInfos.Select(c =>
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