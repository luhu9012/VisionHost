namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 日志处理管道（预留扩展点，当前未注册任何实现）。
    /// 用途（TODO：后续按需实现）：
    /// 1. 清洗：过滤噪音、合并重复、对高频 Debug 降采样；
    /// 2. 脱敏：掩码工单号 / 用户名的敏感位（合规要求）；
    /// 3. 指标提取：从 JsonData 抽取耗时、缺陷数做聚合统计；
    /// 4. AI 分析前置预处理（打标签、补全上下文）。
    /// 注册方式：LogRouter.AddProcessor(...)，按注册顺序执行；
    /// 单个处理器异常已被 LogRouter 隔离，不影响日志主链路。
    /// </summary>
    public interface ILogProcessor
    {
        /// <summary>处理器名称（日志排查 / 告警时标识）</summary>
        string Name { get; }

        /// <summary>处理单条日志（可修改 entry，处理完成后进入 Sink 阶段）</summary>
        void Process(LogEntry entry);
    }
}
