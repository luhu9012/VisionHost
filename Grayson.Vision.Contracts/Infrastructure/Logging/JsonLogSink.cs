using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 结构化日志输出（NDJSON：每行一条 JSON 对象，文件后缀 .jsonl）。
    /// 目的：为清洗 / AI 分析提供机器可读的原料——工单 / 工位 / 节点维度可检索，
    /// Python pandas、jq、日志分析平台可直接消费，无需人工解析文本。
    /// 零第三方依赖：字段均为简单类型，手写 JSON 序列化；
    /// JsonData 若以 { 或 [ 开头则按「对象 / 数组」原样嵌入（需为合法 JSON），否则按字符串转义。
    /// TODO 扩展：后续若需复杂嵌套字段，可切换到 Newtonsoft.Json / System.Text.Json。
    /// 滚动 / 清理 / 失败上报逻辑与 FileLogSink 完全对称（可后续抽取公共基类）。
    /// </summary>
    public class JsonLogSink : ILogSink
    {
        private readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly object _writeLock = new object();

        private string _logDirectory;
        private int _retentionDays = 30;
        private long _maxFileSizeBytes = 50L * 1024 * 1024;

        private volatile bool _isRunning;
        private Thread _worker;
        private StreamWriter _writer;
        private string _currentFileName;
        private int _currentFileIndex;
        private int _writeFailures;
        private DateTime _lastCleanupDate;

        public string Name => "json";

        public JsonLogSink(string customLogDir = null)
        {
            _logDirectory = string.IsNullOrWhiteSpace(customLogDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : customLogDir;
        }

        /// <summary>应用最新配置（与 FileLogSink 相同语义：目录变更自动滚动新文件）</summary>
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
                lock (_writeLock) { CloseWriter(); }
            }
        }

        /// <summary>启动后台写盘线程（幂等）</summary>
        public void Enable()
        {
            if (_isRunning) return;
            _isRunning = true;
            _worker = new Thread(ProcessQueueLoop) { IsBackground = true, Name = "LogJsonSink" };
            _worker.Start();
        }

        /// <summary>停止写盘（退出前冲刷剩余队列）</summary>
        public void Disable()
        {
            _isRunning = false;
            _signal.Set();
        }

        /// <summary>立即冲刷缓冲</summary>
        public void Flush()
        {
            lock (_writeLock)
            {
                try { _writer?.Flush(); } catch { }
            }
        }

        /// <summary>优雅关闭</summary>
        public void Shutdown()
        {
            Disable();
            try { _worker?.Join(2000); } catch { }
            lock (_writeLock) { CloseWriter(); }
        }

        /// <summary>写入一条日志（入队并唤醒后台线程）</summary>
        public void Write(LogEntry entry)
        {
            if (!_isRunning || entry == null) return;
            _queue.Enqueue(entry);
            _signal.Set();
        }

        // ==================== 后台写盘循环（与 FileLogSink 对称） ====================

        private void ProcessQueueLoop()
        {
            while (_isRunning)
            {
                _signal.WaitOne(1000);
                DrainQueue();
                CleanupOldLogs();
            }
            DrainQueue();
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
                        _writer.WriteLine(ToJsonLine(entry));
                    }
                }
                catch (Exception ex)
                {
                    int count = Interlocked.Increment(ref _writeFailures);
                    LogBus.RaiseLoggingFailed(Name, count, ex);
                    break;
                }
            }
            Flush();
        }

        /// <summary>跨天 / 超大小滚动（文件名 {进程名}_{yyyy-MM-dd}.jsonl[.{n}]）</summary>
        private void EnsureWriter()
        {
            DateTime now = DateTime.Now;
            string baseName = Path.Combine(_logDirectory,
                $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}_{now:yyyy-MM-dd}.jsonl");

            if (_currentFileName == null || !string.Equals(_currentFileName, baseName, StringComparison.OrdinalIgnoreCase))
            {
                CloseWriter();
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

        /// <summary>清理超过保留天数的 .jsonl 文件（每天最多一次）</summary>
        private void CleanupOldLogs()
        {
            if (_retentionDays <= 0 || _lastCleanupDate == DateTime.Now.Date) return;
            _lastCleanupDate = DateTime.Now.Date;

            try
            {
                if (!Directory.Exists(_logDirectory)) return;
                string processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                DateTime cutoff = DateTime.Now.Date.AddDays(-_retentionDays);

                foreach (var file in Directory.GetFiles(_logDirectory, $"{processName}_*.jsonl*"))
                {
                    string name = Path.GetFileName(file);
                    string[] parts = name.Split('_');
                    if (parts.Length < 2) continue;
                    string datePart = parts[1].Substring(0, Math.Min(10, parts[1].Length));
                    if (DateTime.TryParse(datePart, out DateTime fileDate) && fileDate < cutoff)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
                // 清理失败静默（下次重试）
            }
        }

        // ==================== JSON 序列化（零依赖手写） ====================

        /// <summary>条目 → 单行 JSON（NDJSON）。字段名稳定，供外部解析。</summary>
        internal static string ToJsonLine(LogEntry e)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendString(sb, "Timestamp", e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            AppendString(sb, "Level", e.Level.ToString());
            AppendString(sb, "Category", e.Category);
            AppendString(sb, "StationId", e.StationId);
            AppendString(sb, "WorkOrderId", e.WorkOrderId);
            AppendString(sb, "NodeId", e.NodeId);
            AppendString(sb, "TraceId", e.TraceId);
            AppendString(sb, "ThreadId", e.ThreadId);
            AppendString(sb, "UserName", e.UserName);
            AppendRawOrString(sb, "JsonData", e.JsonData); // JSON 对象 / 数组原样嵌入，普通文本转义
            AppendString(sb, "Message", e.Message);
            if (e.Exception != null) AppendString(sb, "Exception", e.Exception.ToString());
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            if (value == null)
            {
                sb.Append("null,");
                return;
            }
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); // 控制字符转义
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\",");
        }

        /// <summary>JsonData 字段：以 { / [ 开头视为合法 JSON 原样嵌入（省去二次解析），否则按字符串转义</summary>
        private static void AppendRawOrString(StringBuilder sb, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && (value[0] == '{' || value[0] == '['))
            {
                sb.Append('"').Append(key).Append("\":").Append(value).Append(',');
            }
            else
            {
                AppendString(sb, key, value);
            }
        }
    }
}
