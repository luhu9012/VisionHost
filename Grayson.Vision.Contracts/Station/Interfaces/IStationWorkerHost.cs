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
        Idle,           // 初始空闲状态
        Stopped,        // 处于停止状态
        Running,        // 正在运行/准备响应触发
        Paused,         // 已暂停
        Faulted,        // 发生异常报错（软错误，可复位恢复）
        Resetting,      // 工位软复位中（运行 ResetBlueprint）
        ErrorLocked     // 硬件不可恢复故障或急停，已上锁，需人工确认
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
        /// 完整执行视觉链一次（编辑器「运行」；不进入业务过程分流）。
        /// </summary>
        Task RunContinuousAsync(string batchId = null);

        /// <summary>
        /// 视觉链单步（编辑器「单步」；不进入业务过程分流）。
        /// </summary>
        Task StepChainAsync(string batchId = null);

        /// <summary>
        /// 工位软复位（生产复位）：运行 ResetBlueprint，回安全点、IO 复位、清空队列，不重新初始化硬件句柄
        /// </summary>
        Task SoftResetAsync();

        /// <summary>
        /// 硬件全复位：关闭所有设备句柄并重新 Open，用于断连后恢复
        /// </summary>
        Task HardwareResetAsync();

        /// <summary>
        /// 急停信号触发，进入 ErrorLocked 并终止所有工单
        /// </summary>
        Task EmergencyStopAsync(string reason = null);

        /// <summary>
        /// 单步执行指定节点（调试用）。
        /// </summary>
        Task StepNodeAsync(FlowNodeBase node);

        /// <summary>
        /// 暂停工位（保持当前状态，不响应新触发）。
        /// </summary>
        Task PauseAsync();

        /// <summary>
        /// 从暂停状态恢复。
        /// </summary>
        Task ResumeAsync();

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