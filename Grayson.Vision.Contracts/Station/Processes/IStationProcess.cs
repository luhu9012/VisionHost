using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Processes
{
    /// <summary>
    /// 工位业务过程契约。
    ///
    /// 职责分层约定（与 FlowEdit 编辑器协同）：
    /// - 编辑器节点流（配方）只编排「视觉段」——采图 → 匹配 → 标定换算，
    ///   换产品只调视觉参数，节点编排交给 FlowEdit；
    /// - 业务过程用代码编排「运动/IO 时序」——拍照位/吸取/放料节拍由机械结构决定、
    ///   不随产品变化，时序调整直接改过程实现。
    ///
    /// 装配方式：由 StationHostRuntime 在创建工位时读取工位配置的
    /// ProcessKey + ProcessConfigJson，经 StationProcessFactory 实例化后
    /// AttachProcess 到 StationWorker。挂载后，Worker.TriggerOnceAsync
    /// 的所有入口（UI 按钮、PLC/IO 触发源、手动触发）自动跑完整业务周期；
    /// 未挂载时回退为原视觉链单次触发（FlowEdit 调试不受影响）。
    /// </summary>
    public interface IStationProcess
    {
        /// <summary>过程唯一键（如 "MahjongPick" / "MahjongDualNozzle"），用于注册表解析与 UI 展示。</summary>
        string ProcessKey { get; }

        /// <summary>
        /// 执行一次完整业务循环（从安全检查到回待机）。
        /// 返回 true = 成功；false = 业务 NG（如视觉分数低于阈值，属正常业务判定，不触发 Faulted）。
        /// 中途抛异常会被 Worker 记为 Faulted；抛 OperationCanceledException 视为被停止/急停打断。
        /// 实现必须保证：无论成功/失败/异常，结束时 Z 轴抬至安全高度、真空关闭（防撞机/带料悬停）。
        /// </summary>
        Task<bool> RunAsync(CancellationToken token = default);

        /// <summary>
        /// 安全收尾：停止/急停时由 Worker 调用，尽力回到安全姿态（抬 Z、关真空）。
        /// 默认幂等，可在当前周期已由异常兜底完成后再次调用。
        /// </summary>
        Task SafeStopAsync(CancellationToken token = default);

        /// <summary>
        /// 急停：控制器级全轴急停（RapidStop），立即停住所有已下发的轴运动。
        /// 与 SafeStopAsync 的区别：急停语义 = 立即停住并锁定，**不再下发任何运动指令**
        /// （SafeStopAsync 的"抬 Z"是正常运动指令，急停后不应再执行，由操作员手动复位）。
        /// 实现建议：先关闭真空等负载保护，再下发全轴急停；异常自行吞掉并记录日志，不得抛出。
        /// </summary>
        Task EmergencyStopAsync(CancellationToken token = default);

        /// <summary>
        /// 工位复位通知：Worker.SoftResetAsync 复位完成后调用（急停/故障解除、操作员复位）。
        /// 业务过程可在此恢复指示状态（如清除报警灯/蜂鸣器、回待机灯），默认空实现。
        /// 不得抛异常（Worker 侧已包 try-catch，此处仅作日志兜底）。
        /// </summary>
        Task OnResetAsync(CancellationToken token = default);
    }
}
