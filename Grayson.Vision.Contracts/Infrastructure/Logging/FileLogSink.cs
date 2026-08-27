using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 文件日志输出（文本，人类可读）。实现 ILogSink，由 LogRouter 注册驱动。
    /// 相比旧版的可靠化改进：
    /// 1. 滚动：按天（跨日切新文件）+ 按大小（超过 MaxFileSizeMB 追加 .1/.2 序号）；
    /// 2. 清理：超过 RetentionDays 的旧日志文件自动删除（防日志占满磁盘——这正是写失败的高发诱因）；
    /// 3. 失败上报：写盘异常累计计数 → LogBus.LoggingFailed（磁盘满时有人知晓，不再静默吞掉）；
    /// 4. 优雅关闭：Disable / Shutdown 冲刷剩余队列，不丢日志；
    /// 5. 句柄复用：后台线程持续持有写入器（每批写完 Flush），不再每次写都开关文件。
    /// 结构说明：与 JsonLogSink 对称（队列 + 后台线程 + 滚动清理），TODO：后续可抽取 RollingFileSinkBase 公共基类。
    /// </summary>
    public class FileLogSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly object _writeLock = new object();

        private string _logDirectory;
        private int _retentionDays = 30;
        private long _maxFileSizeBytes = 50L * 1024 * 1024;

        private volatile bool _isRunning;
        private Thread _worker;
        private StreamWriter _writer;      // 持续持有的写入器（滚动判定与批量写共用）
        private string _currentFileName;   // 当前打开文件的基础名（跨天 / 目录变更时滚动）
        private int _currentFileIndex;     // 当前文件序号（0 = 主文件，超过大小后递增）
        private int _writeFailures;        // 累计写失败次数（上报告警用）
        private DateTime _lastCleanupDate; // 每天最多做一次旧文件清理

        public string Name => "file";

        public FileLogSink(string customLogDir = null)
        {
            _logDirectory = string.IsNullOrWhiteSpace(customLogDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : customLogDir;
        }

        /// <summary>
        /// 兼容旧调用点：动态设置日志目录（等价于只改目录的 Configure）。
        /// </summary>
        public void SetDirectory(string customLogDir)
        {
            Configure(new LogConfig { LogPath = customLogDir, RetentionDays = _retentionDays, MaxFileSizeMB = (int)(_maxFileSizeBytes / (1024 * 1024)) });
        }

        /// <summary>应用最新配置（热生效：下一批写入使用新目录 / 新阈值；目录变更自动关闭旧文件）</summary>
        public void Configure(LogConfig config)
        {
            if (config == null) return;
            _retentionDays = config.RetentionDays > 0 ? config.RetentionDays : 30;
            _maxFileSizeBytes = config.MaxFileSizeMB > 0 ? (long)config.MaxFileSizeMB * 1024 * 1024 : 50L * 1024 * 1024;

            string newDir = string.IsNullOrWhiteSpace(config.LogPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : config.LogPath;
            if (!string.Equals(_logDirectory, newDir, StringComparison.OrdinalIgnoreCase))
            {
                _logDirectory = newDir;
                lock (_writeLock) { CloseWriter(); } // 目录变更 → 关闭旧文件，下一批写入在新目录重建
            }
        }

        /// <summary>启动后台写盘线程（幂等，重复调用无副作用）</summary>
        public void Enable()
        {
            if (_isRunning) return;
            _isRunning = true;
            _worker = new Thread(ProcessQueueLoop) { IsBackground = true, Name = "LogFileSink" };
            _worker.Start();
        }

        /// <summary>停止写盘（线程退出前会冲刷剩余队列）</summary>
        public void Disable()
        {
            _isRunning = false;
            _signal.Set();
        }

        /// <summary>立即冲刷当前缓冲到磁盘</summary>
        public void Flush()
        {
            lock (_writeLock)
            {
                try { _writer?.Flush(); } catch { }
            }
        }

        /// <summary>优雅关闭：停止 + 等待线程冲刷 + 释放写入器</summary>
        public void Shutdown()
        {
            Disable();
            try { _worker?.Join(2000); } catch { }
            lock (_writeLock) { CloseWriter(); }
        }

        /// <summary>写入一条日志（入队并唤醒后台线程；未 Enable 时直接丢弃，与旧版行为一致）</summary>
        public void Write(LogEntry entry)
        {
            if (!_isRunning || entry == null) return;
            _queue.Enqueue(entry);
            _signal.Set();
        }

        // ==================== 后台写盘循环 ====================

        private void ProcessQueueLoop()
        {
            while (_isRunning)
            {
                _signal.WaitOne(1000);   // 有日志立即唤醒；空闲最多 1s 兜底（处理跨天滚动）
                DrainQueue();
                CleanupOldLogs();        // 每天兜底清理一次旧文件
            }
            DrainQueue();                // 退出前冲刷剩余
            lock (_writeLock) { CloseWriter(); }
        }

        private void DrainQueue()
        {
            while (_queue.TryDequeue(out var entry))
            {
                try
                {
                    lock (_writeLock)
                    {
                        EnsureWriter();
                        _writer.WriteLine(entry.ToString());
                    }
                }
                catch (Exception ex)
                {
                    // 写失败（磁盘满 / 目录不可写等）：计数并上报，不再静默吞掉
                    int count = Interlocked.Increment(ref _writeFailures);
                    LogBus.RaiseLoggingFailed(Name, count, ex);
                    break; // 磁盘异常时退出本轮，避免空转重试
                }
            }
            Flush();
        }

        /// <summary>
        /// 确保写入器可用：跨天 / 目录变更 → 序号清零重建；超过大小上限 → 滚动序号 +1。
        /// 文件名：{进程名}_{yyyy-MM-dd}.log（滚动后 {进程名}_{yyyy-MM-dd}.log.1 / .2 ...）
        /// </summary>
        private void EnsureWriter()
        {
            DateTime now = DateTime.Now;
            string baseName = Path.Combine(_logDirectory,
                $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}_{now:yyyy-MM-dd}.log");

            if (_currentFileName == null || !string.Equals(_currentFileName, baseName, StringComparison.OrdinalIgnoreCase))
            {
                CloseWriter();          // 跨天或首次：关闭旧文件（若有），序号清零
                _currentFileName = baseName;
                _currentFileIndex = 0;
            }

            if (_writer == null)
            {
                Directory.CreateDirectory(_logDirectory);
                _writer = new StreamWriter(baseName, true, Encoding.UTF8) { AutoFlush = false };
            }
            else if (_writer.BaseStream.Length >= _maxFileSizeBytes)
            {
                CloseWriter();
                _currentFileIndex++;
                Directory.CreateDirectory(_logDirectory);
                _writer = new StreamWriter($"{baseName}.{_currentFileIndex}", true, Encoding.UTF8) { AutoFlush = false };
            }
        }

        private void CloseWriter()
        {
            if (_writer == null) return;
            try { _writer.Flush(); } catch { }
            _writer.Dispose();
            _writer = null;
        }

        /// <summary>
        /// 清理超过保留天数的日志文件（每天最多执行一次）。
        /// 按文件名第 2 段日期解析（{进程名}_{yyyy-MM-dd}.log[.{n}]），只清理本进程的日志文件。
        /// </summary>
        private void CleanupOldLogs()
        {
            if (_retentionDays <= 0 || _lastCleanupDate == DateTime.Now.Date) return;
            _lastCleanupDate = DateTime.Now.Date;

            try
            {
                if (!Directory.Exists(_logDirectory)) return;
                string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                DateTime cutoff = DateTime.Now.Date.AddDays(-_retentionDays);

                foreach (var file in Directory.GetFiles(_logDirectory, $"{processName}_*.log*"))
                {
                    string name = Path.GetFileName(file);
                    string[] parts = name.Split('_');
                    if (parts.Length < 2) continue;
                    string datePart = parts[1].Substring(0, Math.Min(10, parts[1].Length)); // "yyyy-MM-dd"
                    if (DateTime.TryParse(datePart, out DateTime fileDate) && fileDate < cutoff)
                    {
                        try { File.Delete(file); } catch { /* 个别文件删除失败不影响其他（可能正被占用） */ }
                    }
                }
            }
            catch
            {
                // 清理失败静默（下次循环重试，绝不影响日志主链路）
            }
        }
    }
}
