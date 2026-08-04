using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Flow.Contexts
{
    /// <summary>
    /// 单次节拍/触发生命周期上下文 (随一次 Run/Trigger 产生并销毁)
    /// </summary>
    public class FrameCycleContext
    {
        /// <summary>
        /// 节拍唯一 ID (如: GUID 或 自增序列)
        /// </summary>
        public string CycleId { get; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 触发时间戳
        /// </summary>
        public DateTime TriggerTime { get; } = DateTime.Now;

        /// <summary>
        /// 生产批次号 / SN 码 (由 PLC 或扫码枪传入)
        /// </summary>
        public string BatchId { get; set; } = string.Empty;

        /// <summary>
        /// 本次节拍内部的临时共享数据 (如单次采图的 Bitmap/HImage 句柄)
        /// </summary>

        public ConcurrentDictionary<string, object> TransientItems { get; } = new ConcurrentDictionary<string, object>();
    }
}
