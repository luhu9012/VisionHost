using Grayson.Vision.Contracts.Devices;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Core;
using System;

namespace Plugins.Camera.Hikvision
{

    public class HikCamera : ICamera
    {
        #region IDevice 基础属性
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceKey { get; set; }
        public string BrandName { get; set; }
        public DeviceState State { get; set; }
        public DeviceCategory Category { get; set; } = DeviceCategory.Camera;
        #endregion

        #region ICamera 图像采集事件
        public event EventHandler<FrameEventArgs> FrameReceived;
        // 模拟触发图像回调（测试用）
        protected virtual void OnFrameReceived(FrameEventArgs args)
        {
            FrameReceived?.Invoke(this, args);
        }
        #endregion

        #region 你原有保留方法
        public Result StartCapture()
        {
            // 模拟调用海康 SDK 开始采集
            return Result.Ok();
        }
        #endregion

        #region ICamera 采集控制接口
        public Result StartGrabbing()
        {
            // 海康单帧采集启动 mock
            return Result.Ok();
        }

        public Result StopGrabbing()
        {
            return Result.Ok();
        }

        public Result StartContinuousGrab()
        {
            // 连续采集流开启
            return Result.Ok();
        }

        public Result StopContinuousGrab()
        {
            return Result.Ok();
        }

        public Result SoftTrigger()
        {
            // 软触发单次拍照
            return Result.Ok();
        }

        public Result SoftwareTrigger()
        {
            return Result.Ok();
        }
        #endregion

        #region ICamera 曝光增益参数
        public Result SetExposureTime(double valueUs)
        {
            return Result.Ok();
        }

        public Result GetExposureTime()
        {
            return Result.Ok();
        }

        public Result SetGain(double value)
        {
            return Result.Ok();
        }

        public Result GetGain()
        {
            return Result.Ok();
        }

        public Result SetTriggerMode(bool enable)
        {
            return Result.Ok();
        }
        #endregion

        #region ICamera GenICam 泛型参数读写
        public Result SetParamString(string key, string value)
        {
            return Result.Ok();
        }

        public Result SetParamFloat(string key, float value)
        {
            return Result.Ok();
        }

        public Result SetParamInt(string key, long value)
        {
            return Result.Ok();
        }
        #endregion

        #region IDevice 设备连接/状态/通用参数
        public Result Connect()
        {
            // 模拟海康SDK登录相机
            State = DeviceState.Connected;
            return Result.Ok();
        }

        public Result Disconnect()
        {
            // 模拟注销SDK、释放句柄
            State = DeviceState.Disconnected;
            return Result.Ok();
        }

        public Result CheckStatus()
        {
            return Result.Ok();
        }

        public Result SetParam(string key, object value)
        {
            return Result.Ok();
        }

        public Result<object> GetParam(string key)
        {
            // mock 返回空对象
            return Result<object>.Ok(null);
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
                // 释放托管资源：取消事件、停止采集
                FrameReceived = null;
                StopContinuousGrab();
                StopGrabbing();
                Disconnect();
            }

            // 释放海康SDK非托管句柄 mock
            _disposed = true;
        }

        ~HikCamera()
        {
            Dispose(false);
        }
        #endregion
    }
    public class HikvisionPlugin : IHardwarePlugin
    {
        public string BrandName => "Hikvision";
        public DeviceCategory Category => DeviceCategory.Camera;
        public string Version => "1.0.0";

        public void Initialize() { /* 海康 MVS SDK 初始化 */ }

        public List<DeviceInfo> EnumerateDevices()
        {
            var list = new List<DeviceInfo>();
            // 调用海康 SDK 查线上的 GigE/USB 相机
            list.Add(new DeviceInfo { DeviceId = "DA1234567", ModelName = "MV-CS060-10GC", Category = Category, BrandName = BrandName });
            return list;
        }

        public IDevice CreateDevice(string deviceId)
        {
            // 创建实现了 ICamera 的 HikCamera 对象并返回为 IDevice
            return new HikCamera()
            {
                DeviceId = deviceId,
                BrandName = BrandName,
                Category = Category
            };
        }

        public void Shutdown() { }
    }
}