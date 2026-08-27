namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 日志输出目标（Sink）抽象。
    /// 所有落盘 / 上报通道都实现此接口，可插拔、可热配置——这是日志模块扩展性的核心扩展点：
    ///   - 现有实现：FileLogSink（文本，人读）、JsonLogSink（NDJSON 结构化，机器读）
    ///   - 预留扩展：DbLogSink（SQLite，可检索查询）、AnalysisSink（AI 分析），实现本接口后
    ///     LogRouter.AddSink(...) 即可接入，业务代码零改动。
    /// 约束：Write 实现内部自行缓冲 / 批量 / 异步；禁止向调用方抛异常（LogRouter 已兜底隔离并计数告警）。
    /// </summary>
    public interface ILogSink
    {
        /// <summary>Sink 名称（用于失败告警标识，如 "file" / "json"）</summary>
        string Name { get; }

        /// <summary>
        /// 启动后台资源（写盘线程等），必须幂等（重复调用无副作用）。
        /// 由 LogRouter.AddSink 自动调用——注册即启动，调用方无需关心内部线程。
        /// </summary>
        void Enable();

        /// <summary>
        /// 应用最新配置（LogPath / 保留天数 / 单文件大小等）。
        /// 由 LogRouter.ApplyConfig 在启动装配与配置热更新时调用，实现应尽量做到"保存即生效"。
        /// </summary>
        void Configure(LogConfig config);

        /// <summary>写入一条日志（内部自行入队 / 批量落盘）</summary>
        void Write(LogEntry entry);

        /// <summary>立即冲刷缓冲到目标（程序退出前调用，防丢日志）</summary>
        void Flush();

        /// <summary>优雅关闭：冲刷剩余队列并释放资源（程序退出时由 LogRouter.ShutdownAll 调用）</summary>
        void Shutdown();
    }
}
