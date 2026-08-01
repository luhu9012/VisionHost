using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Logging
{
    public class FileLogSink
    {
        private string _logDirectory;
        private readonly ConcurrentQueue<LogEntry> _queue = new ConcurrentQueue<LogEntry>();
        private readonly System.Threading.AutoResetEvent _signal = new System.Threading.AutoResetEvent(false);
        private bool _isRunning;
        private bool _isHooked;

        public FileLogSink(string customLogDir = null)
        {
            SetDirectory(customLogDir);
        }

        /// <summary>
        /// 动态更改或配置本地存储路径
        /// </summary>
        public void SetDirectory(string customLogDir)
        {
            _logDirectory = string.IsNullOrWhiteSpace(customLogDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs")
                : customLogDir;
        }

        /// <summary>
        /// 开启文件日志存储
        /// </summary>
        public void Enable()
        {
            if (_isRunning) return;

            Directory.CreateDirectory(_logDirectory);
            _isRunning = true;

            if (!_isHooked)
            {
                LogBus.OnLogProduced += EnqueueLog;
                _isHooked = true;
            }

            Task.Run(ProcessLogQueue);
        }

        /// <summary>
        /// 停止写日志
        /// </summary>
        public void Disable()
        {
            _isRunning = false;
            _signal.Set();
        }

        private void EnqueueLog(LogEntry entry)
        {
            if (!_isRunning) return;
            _queue.Enqueue(entry);
            _signal.Set();
        }

        private void ProcessLogQueue()
        {
            while (_isRunning)
            {
                _signal.WaitOne(1000);

                if (_queue.IsEmpty) continue;

                try
                {
                    string fileName = Path.Combine(_logDirectory, $"Vision_{DateTime.Now:yyyy-MM-dd}.log");
                    using (var writer = new StreamWriter(fileName, true, System.Text.Encoding.UTF8))
                    {
                        while (_queue.TryDequeue(out var entry))
                        {
                            writer.WriteLine(entry.ToString());
                        }
                    }
                }
                catch
                {
                    // 忽略落盘临时冲突异常
                }
            }
        }
    }
}