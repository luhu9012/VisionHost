using Grayson.Vision.Contracts.Core;
using System;

namespace Grayson.Vision.Contracts.Devices
{
    public class FrameEventArgs : EventArgs
    {
        public byte[] Buffer { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelFormat { get; set; } // 如 "Mono8", "RGB8"
        public long Timestamp { get; set; }
        public long FrameNum { get; set; }
        public IntPtr NativePointer { get; set; } // 原生指针，供零拷贝处理
    }
    /// <summary>工业相机通用接口，所有品牌相机统一规范</summary>
    public interface ICamera : IDevice
    {
        /// <summary>
        /// 图像采集回调事件
        /// </summary>
        event EventHandler<FrameEventArgs> FrameReceived;


        Result StartGrabbing();
        Result StopGrabbing();

        // ----- 常用通用参数（统一标准） -----
        Result SetExposureTime(double valueUs);
        Result GetExposureTime();
        Result SetGain(double value);
        Result GetGain();
        Result SetTriggerMode(int mode);
        Result SoftwareTrigger();

        /// <summary>
        /// 相机侧完整配置为软触发取图（TriggerSelector=FrameStart / TriggerMode=On /
        /// TriggerSource=Software / 关闭帧率限制）。
        /// 标定采样必须「走位 → 软触发 → 本点新帧」，不能依赖连续自由流：
        /// 连续流下"等新帧"等于等一个随机时刻，慢帧必超时，走位后还可能拿到上一位置的帧。
        /// </summary>
        Result ConfigureSoftwareTrigger();

        /// <summary>软触发单次拍照</summary>
        Result SoftTrigger();

        /// <summary>开启连续流采集</summary>
        Result StartContinuousGrab();

        /// <summary>停止连续采集</summary>
        Result StopContinuousGrab();

        Result SaveImageFile(string path, string format);
    }
}