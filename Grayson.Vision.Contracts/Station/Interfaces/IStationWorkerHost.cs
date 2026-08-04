using System;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Nodes;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// 工位 Worker 状态枚举
    /// </summary>
    public enum StationState
    {
        Idle,       // 初始空闲状态
        Stopped,    // 处于停止/空闲状态
        Running,    // 正在运行/准备响应触发
        Paused,     // 已暂停
        Faulted     // 发生异常报错
    }

    /// <summary>
    /// 工位 Worker 宿主通用接口契约
    /// (无论是控制台进程 WorkerHost，还是 UI 内嵌的 EmbeddedWorkerHost 都需要实现此接口)
    /// </summary>
    public interface IStationWorkerHost : IDisposable
    {
        /// <summary>
        /// 当前工位的唯一标识 (例如: Station_01)
        /// </summary>
        string StationId { get; }

        /// <summary>
        /// 当前工位运行状态
        /// </summary>
        StationState State { get; }

        /// <summary>
        /// 加载/更新配方 (包含流程图拓扑与节点参数)
        /// </summary>
        Task LoadRecipeAsync(FlowProcessModel recipe);

        /// <summary>
        /// 启动工位 (进入就绪/运行状态)
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// 停止工位
        /// </summary>
        Task StopAsync();

        /// <summary>
        /// 触发单次节拍运行 (如接收到 PLC 触发信号或手动测试)
        /// </summary>
        Task TriggerOnceAsync(string batchId = null);

        /// <summary>
        /// 工位状态变更事件通知
        /// </summary>
        event EventHandler<StationState> OnStateChanged;

        /// <summary>
        /// 渲染帧数据完成事件 (向 UI 推送渲染图像/测量结果)
        /// </summary>
        event EventHandler<ImageRenderEventArgs> OnFrameRendered;
    }

    /// <summary>
    /// 图像渲染推送事件参数
    /// </summary>
    public class ImageRenderEventArgs : EventArgs
    {
        public string StationId { get; set; }
        public string NodeId { get; set; }
        public string NodeName { get; set; } = string.Empty;
        public string ImagePathOrBufferId { get; set; } // 图像缓存 ID 或共享内存路径
        public object RenderData { get; set; }          // 额外 ROI 或检测框数据
    }
}