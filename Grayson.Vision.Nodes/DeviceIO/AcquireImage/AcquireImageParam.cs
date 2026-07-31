namespace Grayson.Vision.Nodes.DeviceIO.AcquireImage
{
    public class AcquireImageParam
    {
        /// <summary>
        /// 配方引用的逻辑相机别名（映射到物理相机，如 "TopCam"）
        /// </summary>
        public string CameraAlias { get; set; } = "TopCam";

        /// <summary>
        /// 触发模式 (Software / Hardware / Continuous)
        /// </summary>
        public string TriggerMode { get; set; } = "Software"; // <-- 补齐这个缺失的属性！

        /// <summary>
        /// 曝光时间 (us)
        /// </summary>
        public double ExposureTime { get; set; } = 5000;

        /// <summary>
        /// 增益
        /// </summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>
        /// 超时时间 (ms)
        /// </summary>
        public int TimeoutMs { get; set; } = 3000;

        public AcquireImageParam() { }
    }
}