#if BASLER_PYLON
// ============================================================================
// 巴斯勒 pylon .NET SDK 真实适配层
//
// 启用条件：解决方案根目录 DLLLib\Basler.Pylon.dll 存在
//（编译宏 BASLER_PYLON 由 csproj 按该文件是否存在自动定义）。
//
// 安装步骤（装真实相机时）：
//   1. 安装 Basler pylon 6 (或更高版本) Camera Software Suite；
//   2. 将安装目录 (如 C:\Program Files\Basler\pylon 6\Runtime\Win64\x64\)
//      下的 Basler.Pylon.dll 复制到本解决方案的 DLLLib\ 文件夹；
//   3. 重新编译本项目，本文件即被编入，BaslerSdkFactory 自动切换真实 SDK。
//
// API 依据：Basler pylon .NET API (Basler.Pylon 命名空间)
// ============================================================================

using Basler.Pylon;
using Grayson.Vision.Contracts.Devices;
using System;
using System.Collections.Generic;

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
            foreach (ICameraInfo info in CameraFactory.EnumerateDevices())
            {
                list.Add(new BaslerSdkDeviceInfo
                {
                    SerialNumber = SafeGet(info, CameraInfoKey.SerialNumber),
                    ModelName = SafeGet(info, CameraInfoKey.ModelName),
                    FriendlyName = SafeGet(info, CameraInfoKey.FriendlyName),
                });
            }
            return list;
        }

        private static string SafeGet(ICameraInfo info, string key)
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
        private Camera _camera;
        private PixelDataConverter _converter;
        private long _frameCounter;

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

        /// <summary>打开相机（按序列号创建设备并 Open）</summary>
        public string Open(string serialNumber)
        {
            try
            {
                string sn = string.IsNullOrEmpty(serialNumber) ? _serial : serialNumber;
                if (string.IsNullOrEmpty(sn))
                {
                    // 未指定序列号：打开枚举到的第一台设备
                    _camera = CameraFactory.CreateFirstDevice();
                }
                else
                {
                    _camera = new Camera(sn);
                }
                _converter = new PixelDataConverter();
                _camera.Open();
                return null;
            }
            catch (Exception ex)
            {
                return $"打开巴斯勒相机失败: {ex.Message}";
            }
        }

        /// <summary>关闭相机并释放资源</summary>
        public string Close()
        {
            try
            {
                if (_camera != null)
                {
                    if (_camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing)
                    {
                        _camera.StreamGrabber.Stop();
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

        /// <summary>写 GenICam 节点（按值类型分发到 pylon 的强类型 SetValue 重载）</summary>
        public string SetNode(string name, object value)
        {
            if (!IsOpen) return "相机未打开，无法写入节点";
            try
            {
                IParameter param = _camera.Parameters[name];
                if (param == null) return $"节点 [{name}] 不存在";

                if (value is bool b)
                {
                    param.SetValue(b);
                }
                else if (value is string s)
                {
                    param.TrySetValue(s);
                }
                else if (value is float || value is double || value is decimal)
                {
                    param.SetValue(Convert.ToDouble(value));
                }
                else if (value is int || value is long || value is short || value is byte || value is uint || value is ulong)
                {
                    param.SetValue(Convert.ToInt64(value));
                }
                else
                {
                    param.TrySetValue(Convert.ToString(value));
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"写入节点 [{name}] 失败: {ex.Message}";
            }
        }

        /// <summary>读 GenICam 节点（按参数类型取值）</summary>
        public object GetNode(string name)
        {
            if (!IsOpen) return null;
            try
            {
                IParameter param = _camera.Parameters[name];
                if (param == null) return null;

                if (param is IIntegerParameter) return ((IIntegerParameter)param).GetValue();
                if (param is IFloatParameter) return ((IFloatParameter)param).GetValue();
                if (param is IEnumParameter) return ((IEnumParameter)param).GetValue();
                if (param is IBoolParameter) return ((IBoolParameter)param).GetValue();
                if (param is IStringParameter) return ((IStringParameter)param).GetValue();
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>开启采集流并订阅帧回调</summary>
        public string StartGrabbing(Action<FrameEventArgs> frameSink)
        {
            if (!IsOpen) return "相机未打开，无法开启采集";
            try
            {
                _camera.StreamGrabber.ImageGrabbed += (s, e) =>
                {
                    try
                    {
                        IGrabResult grabResult = e.GrabResult;
                        if (grabResult == null || !grabResult.GrabSucceeded) return;

                        // 统一转换为 MONO8（黑白相机）或 BGR24（彩色相机），
                        // 与海康插件输出格式对齐，下游 FrameToHImage 无差别处理。
                        byte[] buffer;
                        string pixelFormat;
                        if (grabResult.PixelType.IsMono())
                        {
                            _converter.OutputPixelFormat = PixelType.Mono8;
                            buffer = _converter.Convert<byte>(grabResult);
                            pixelFormat = "MONO8";
                        }
                        else
                        {
                            _converter.OutputPixelFormat = PixelType.BGR8packed;
                            buffer = _converter.Convert<byte>(grabResult);
                            pixelFormat = "BGR24";
                        }

                        long no = System.Threading.Interlocked.Increment(ref _frameCounter);
                        frameSink(new FrameEventArgs
                        {
                            Width = (int)grabResult.Width,
                            Height = (int)grabResult.Height,
                            Buffer = buffer,
                            PixelFormat = pixelFormat,
                            FrameNum = no,
                            Timestamp = DateTime.UtcNow.Ticks,
                            NativePointer = grabResult.Buffer
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Basler] 图像回调异常: {ex.Message}");
                    }
                };

                _camera.StreamGrabber.Start();
                return null;
            }
            catch (Exception ex)
            {
                return $"开启采集流失败: {ex.Message}";
            }
        }

        /// <summary>停止采集流</summary>
        public string StopGrabbing()
        {
            try
            {
                if (_camera != null && _camera.StreamGrabber != null && _camera.StreamGrabber.IsGrabbing)
                {
                    _camera.StreamGrabber.Stop();
                }
                return null;
            }
            catch (Exception ex)
            {
                return $"停止采集流失败: {ex.Message}";
            }
        }

        /// <summary>软触发（执行 TriggerSoftware 命令节点）</summary>
        public string TriggerSoftware()
        {
            if (!IsOpen) return "相机未打开，无法软触发";
            try
            {
                _camera.Parameters[PLCamera.TriggerSoftware].Execute();
                return null;
            }
            catch (Exception ex)
            {
                return $"软触发执行失败: {ex.Message}";
            }
        }

        public void Dispose()
        {
            Close();
        }
    }
}
#endif
