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
    public class HikCamera : ICamera
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
        private cbOutputExdelegate m_ImageCallback;

        #region 开启/停止采集流

        public Result StartGrabbing()
        {
            if (State != DeviceState.Connected || m_MyCamera == null)
            {
                return Result.Fail("相机未连接，无法开启采集！");
            }

            // 1. 实例化并注册 SDK 图像回调函数
            m_ImageCallback = new cbOutputExdelegate(ImageCallbackEx);
            int nRet = m_MyCamera.RegisterImageCallBackEx(m_ImageCallback, IntPtr.Zero);
            if (nRet != CErrorDefine.MV_OK)
            {
                return Result.Fail(Helper.ShowErrorMsg("注册图像回调失败", nRet));
            }

            // 2. 开启 SDK 采集流
            nRet = m_MyCamera.StartGrabbing();
            if (nRet != CErrorDefine.MV_OK)
            {
                return Result.Fail(Helper.ShowErrorMsg("开启采集流失败", nRet));
            }

            return Result.Ok();
        }

        #endregion

        #region 海康 SDK 回调处理逻辑 (ImageCallbackEx)

        /// <summary>
        /// 海康 SDK 图像采集回调方法（运行在底层 SDK 独立线程中）
        /// </summary>
        private void ImageCallbackEx(IntPtr pData, ref MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
        {
            if (pData == IntPtr.Zero || pFrameInfo.nFrameLen == 0) return;

            try
            {
                int width = pFrameInfo.nWidth;
                int height = pFrameInfo.nHeight;
                MvGvspPixelType pixelType = pFrameInfo.enPixelType;

                // 定义用于 UI 渲染的 Mono8 / RGB24 内存缓冲区
                byte[] rawBuffer = new byte[pFrameInfo.nFrameLen];
                Marshal.Copy(pData, rawBuffer, 0, (int)pFrameInfo.nFrameLen);

                // 构建标准 FrameEventArgs 数据包并抛出事件
                var frameArgs = new FrameEventArgs
                {
                    Width = width,
                    Height = height,
                    Buffer = rawBuffer,
                    PixelFormat = pixelType.ToString(),
                    Timestamp = pFrameInfo.nDevTimeStampLow,
                    FrameNum = pFrameInfo.nFrameNum
                };

                lock (_latestFrameLock)
                {
                    _latestFrame = frameArgs;
                }

                // 缓存当前像素格式到字典，方便 UI 读取
                ConfigParams["PixelFormat"] = pixelType.ToString();

                OnFrameReceived(frameArgs);
            }
            catch (Exception ex)
            {
                // 日志记录回调异常，避免底层崩溃
                System.Diagnostics.Debug.WriteLine($"[HikCamera] 图像回调解析异常: {ex.Message}");
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
        /// 设置触发模式（重载 2：契约层 Bool 快捷开关）
        /// </summary>
        public Result SetTriggerMode(bool enable)
        {
            // enable 为 true 默认开启软触发(1)，false 为连续模式(0)
            return SetTriggerMode(enable ? 1 : 0);
        }

        #endregion

        #region ICamera 曝光增益接口快捷封装
        public Result SetExposureTime(double valueUs)
        {
            return SetParam("ExposureTime", valueUs);
        }

        public Result GetExposureTime()
        {
            return GetParam("ExposureTime");
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
            if (null == cCameraInfo)
            {
                string errorMsg = Helper.ShowErrorMsg("No device!", 0);
                return Result.Fail(errorMsg);
            }

            if (null == m_MyCamera)
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
                string errorMsg = Helper.ShowErrorMsg("Device open fail!", nRet);
                return Result.Fail(errorMsg);
            }

            if (cCameraInfo.nTLayerType == CSystem.MV_GIGE_DEVICE)
            {
                int nPacketSize = m_MyCamera.GIGE_GetOptimalPacketSize();
                if (nPacketSize > 0)
                {
                    nRet = m_MyCamera.SetIntValue("GevSCPSPacketSize", (uint)nPacketSize);
                    if (nRet != CErrorDefine.MV_OK)
                    {
                        string errorMsg = Helper.ShowErrorMsg("Set Packet Size failed!", nRet);
                    }
                }
            }

            State = DeviceState.Connected;

            // 连接成功后，同步硬件当前触发状态、参数到本地字典
            SyncTriggerModeFromDevice();
            SyncParamsFromDevice();

            // 若本地有配置参数则下发到硬件
            SyncParamsToDevice();

            return Result.Ok();
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

        public Result CheckStatus() => Result.Ok();

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

            FrameEventArgs frame;
            lock (_latestFrameLock)
            {
                frame = _latestFrame;
            }

            if (frame == null || frame.Buffer == null || frame.Buffer.Length == 0)
                return Result.Fail("当前没有可用图像帧");

            MV_SAVE_IAMGE_TYPE imageType;
            switch (format?.ToLowerInvariant())
            {
                case "bmp": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_BMP; break;
                case "jpg":
                case "jpeg": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_JPEG; break;
                case "png": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_PNG; break;
                case "tif":
                case "tiff": imageType = MV_SAVE_IAMGE_TYPE.MV_IMAGE_TIF; break;
                default:
                    return Result.Fail($"不支持的保存格式: {format}");
            }

            try
            {
                // 构造 SDK 保存所需的 CImage
                var cImage = new CImage
                {
                    Width = (ushort)frame.Width,
                    Height = (ushort)frame.Height,
                    FrameLen = (uint)frame.Buffer.Length,
                    PixelType = (MvGvspPixelType)Enum.Parse(typeof(MvGvspPixelType), frame.PixelFormat)
                };

                // CImage 内部用 pBufAddr 字段保存非托管缓冲区指针，通过反射定位并写入
                var imageField = typeof(CImage)
                    .GetFields(System.Reflection.BindingFlags.Public |
                               System.Reflection.BindingFlags.NonPublic |
                               System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.FlattenHierarchy)
                    .FirstOrDefault(f => f.Name == "pBufAddr" && f.FieldType == typeof(IntPtr));

                if (imageField == null)
                    return Result.Fail("无法定位 CImage.pBufAddr 字段，保存图像失败");

                IntPtr imageBuffer = Marshal.AllocHGlobal(frame.Buffer.Length);
                imageField.SetValue(cImage, imageBuffer);

                try
                {
                    Marshal.Copy(frame.Buffer, 0, imageBuffer, frame.Buffer.Length);

                    var saveParam = new CSaveImgToFileParam
                    {
                        ImageType = imageType,
                        Image = cImage,
                        Quality = format.ToLowerInvariant() == "jpg" || format.ToLowerInvariant() == "jpeg" ? (uint)80 : 0,
                        MethodValue = 2,
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

        public HikCamera(CCameraInfo info)
        {
            cCameraInfo = info;
        }
    }

    public class HikvisionPlugin : IHardwarePlugin
    {
        public string BrandName => "Hikvision";
        public DeviceCategory Category => DeviceCategory.Camera;
        public string Version => "1.0.0";

        public void Initialize() { }

        List<CCameraInfo> cCameraInfos = new List<CCameraInfo>();

        public Result<List<DeviceInfo>> EnumerateDevices()
        {
            GC.Collect();

            int nRet = CSystem.EnumDevices(CSystem.MV_GIGE_DEVICE | CSystem.MV_USB_DEVICE, ref cCameraInfos);
            if (0 != nRet)
            {
                string errorMsg = Helper.ShowErrorMsg("Enumerate devices fail!", 0);
                return Result<List<DeviceInfo>>.Fail(errorMsg);
            }

            string jsonConvert = JsonConvert.SerializeObject(cCameraInfos);

            var list = cCameraInfos.Select(c =>
            {
                CGigECameraInfo cGigECameraInfo = (CGigECameraInfo)c;
                return new DeviceInfo
                {
                    DeviceId = cGigECameraInfo.chSerialNumber,
                    ModelName = cGigECameraInfo.chModelName,
                    Category = Category,
                    BrandName = BrandName,
                    ExtraInfo = jsonConvert
                };
            }).ToList();

            return Result<List<DeviceInfo>>.Ok(list);
        }

        public IContractDevice CreateDevice(string deviceId)
        {
            var gigeInfo = cCameraInfos.OfType<CGigECameraInfo>()
                .FirstOrDefault(item => item.chSerialNumber == deviceId);

            if (gigeInfo != null)
            {
                var device = new HikCamera(gigeInfo)
                {
                    DeviceId = deviceId,
                    BrandName = BrandName,
                    Category = Category
                };

                uint nIp = gigeInfo.nCurrentIp;
                string ipAddress = $"{(nIp >> 24) & 0xFF}.{(nIp >> 16) & 0xFF}.{(nIp >> 8) & 0xFF}.{nIp & 0xFF}";

                device.SetParam("IP", ipAddress);
                device.SetParam("Port", "3596");
                device.SetParam("Exposure", 3600.0);
                device.SetParam("Gain", 0.0);
                device.SetParam("TriggerModeSelect", 0); // 默认连续采集模式
                return device;
            }
            return null;
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