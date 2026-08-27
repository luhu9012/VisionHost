using System;
using System.Collections.Generic;
using System.Threading;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 日志路由核心（LogBus 门面与各 Sink 之间的调度层）。
    /// 职责：
    /// 1. 过滤：级别阈值（LogConfig.MinLevel）+ 分类静音（LogBus.SuppressedCategories）；
    /// 2. 隔离：单个 Sink 写入异常只累计计数并触发 LoggingFailed 告警，不中断其他 Sink、
    ///    不反抛给业务调用点——日志模块绝不允许成为业务故障源（修复原 OnLogProduced 直接遍历的断链问题）；
    /// 3. 扩展：ILogProcessor 处理管道（预留）+ ILogSink 广播（可插拔）；
    /// 4. 生命周期：ApplyConfig 热更新 / FlushAll / ShutdownAll（程序退出前调用，防丢日志）。
    /// </summary>
    public static class LogRouter
    {
        private static readonly object _sync = new object();
        private static readonly List<ILogSink> _sinks = new List<ILogSink>();
        private static readonly List<ILogProcessor> _processors = new List<ILogProcessor>();
        private static LogConfig _config = LogConfig.Instance;
        private static int _sinkWriteFailures; // Sink 写入失败累计（告警用）

        /// <summary>注册 Sink（自动应用当前配置并启动后台资源）。例：LogRouter.AddSink(new FileLogSink(...))</summary>
        public static void AddSink(ILogSink sink)
        {
            if (sink == null) return;
            lock (_sync)
            {
                _sinks.Add(sink);
                try { sink.Configure(_config); } catch { /* 配置失败不影响注册，后续 ApplyConfig 会重试 */ }
                try { sink.Enable(); } catch { /* 启动失败不影响其他 Sink */ }
            }
        }

        /// <summary>移除 Sink（并优雅关闭其资源）</summary>
        public static void RemoveSink(ILogSink sink)
        {
            if (sink == null) return;
            lock (_sync)
            {
                if (_sinks.Remove(sink))
                {
                    try { sink.Shutdown(); } catch { }
                }
            }
        }

        /// <summary>注册处理管道（TODO 扩展点：清洗 / 脱敏 / 指标提取 / AI 预处理，见 ILogProcessor）</summary>
        public static void AddProcessor(ILogProcessor processor)
        {
            if (processor == null) return;
            lock (_sync) _processors.Add(processor);
        }

        /// <summary>应用最新配置（热生效入口：LogConfig.Apply 触发，或启动装配时调用）</summary>
        public static void ApplyConfig(LogConfig config)
        {
            if (config == null) return;
            _config = config;
            ILogSink[] sinks;
            lock (_sync) sinks = _sinks.ToArray();
            foreach (var sink in sinks)
            {
                try { sink.Configure(config); } catch { /* 单个 Sink 配置失败不影响其他 */ }
            }
        }

        /// <summary>
        /// 发布一条日志：过滤 → 处理器管道 → Sink 广播（隔离）→ 兼容旧 UI 订阅者。
        /// 线程安全：可被任意业务线程并发调用。
        /// </summary>
        public static void Publish(LogEntry entry)
        {
            if (entry == null) return;

            // 1. 级别过滤（低于配置阈值直接丢弃）
            if (_config != null && entry.Level < _config.MinLevel) return;

            // 2. 分类静音（兼容旧 LogBus.SuppressedCategories，如关闭图像类大日志）
            if (LogBus.SuppressedCategories.Contains(entry.Category ?? string.Empty)) return;

            // 3. 自动补全线程 ID（并发排查维度，可选字段）
            if (entry.ThreadId == null) entry.ThreadId = Thread.CurrentThread.ManagedThreadId.ToString();

#if DEBUG
            // VS 开发调试：默认直接输出到「输出(Output)」窗口，方便开发期观察
            System.Diagnostics.Debug.WriteLine(entry.ToString());
#endif

            // 4. 处理器管道（预留，当前为空；单个处理器异常已隔离）
            ILogProcessor[] processors;
            lock (_sync) processors = _processors.ToArray();
            foreach (var processor in processors)
            {
                try { processor.Process(entry); }
                catch { /* 处理器异常不影响日志主链路 */ }
            }

            // 5. Sink 广播（订阅者隔离：单点写失败只告警，不中断其他 Sink、不反抛）
            ILogSink[] sinks;
            lock (_sync) sinks = _sinks.ToArray();
            foreach (var sink in sinks)
            {
                try
                {
                    sink.Write(entry);
                }
                catch (Exception ex)
                {
                    int failures = Interlocked.Increment(ref _sinkWriteFailures);
                    LogBus.RaiseLoggingFailed(sink.Name, failures, ex);
                }
            }

            // 6. 兼容旧 UI 订阅者（如 FlowVm 日志列表、IpcLogSink），同样隔离异常
            LogBus.RaiseOnLogProduced(entry);
        }

        /// <summary>冲刷所有 Sink（程序退出前调用，防丢缓冲日志）</summary>
        public static void FlushAll()
        {
            ILogSink[] sinks;
            lock (_sync) sinks = _sinks.ToArray();
            foreach (var sink in sinks)
            {
                try { sink.Flush(); } catch { }
            }
        }

        /// <summary>关闭所有 Sink（程序退出时调用：冲刷剩余队列并释放资源）</summary>
        public static void ShutdownAll()
        {
            ILogSink[] sinks;
            lock (_sync) sinks = _sinks.ToArray();
            foreach (var sink in sinks)
            {
                try { sink.Shutdown(); } catch { }
            }
        }
    }
}
