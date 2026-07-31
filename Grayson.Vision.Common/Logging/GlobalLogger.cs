using System;
using System.IO;
using System.Text;
using System.Threading;
using Grayson.Vision.Common.Helpers;

namespace Grayson.Vision.Common.Logging
{
    /// <summary>
    /// 全局静态日志工具类
    /// 统一所有模块日志输出格式，支持落地本地文件+控制台打印
    /// 按日期分文件夹存储日志，自动滚动，防止单个日志文件过大
    /// 所有业务单元、硬件插件、宿主禁止自建日志写入，统一调用此类
    /// </summary>
    public static class GlobalLogger
    {
        #region 静态配置项
        /// <summary>日志根目录路径</summary>
        private static string _logRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>当前生效最低日志级别，低于该级别不会输出</summary>
        public static LogLevel MinLogLevel { get; set; } = LogLevel.Trace;

        /// <summary>是否开启控制台打印日志，调试开，产线可关闭</summary>
        public static bool EnableConsoleOutput { get; set; } = true;

        /// <summary>是否写入本地日志文件</summary>
        public static bool EnableFileOutput { get; set; } = true;

        /// <summary>多线程写入文件锁，防止并发日志错乱、文件占用</summary>
        private static readonly object _fileWriteLock = new object();
        #endregion

        #region 对外静态打印方法（全系统统一入口）
        public static void Trace(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Trace, sender, message, null);
        }

        public static void Info(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Info, sender, message, null);
        }

        public static void Warn(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Warn, sender, message, null);
        }

        public static void Error(string message, Exception ex = null, string sender = "Unknown")
        {
            WriteLog(LogLevel.Error, sender, message, ex);
        }
        #endregion

        #region 内部日志拼接写入逻辑
        /// <summary>统一日志组装、分发到控制台/文件</summary>
        private static void WriteLog(LogLevel level, string sender, string msg, Exception ex)
        {
            // 低于配置最低级别，直接丢弃日志
            if (level < MinLogLevel)
                return;

            // 拼装完整日志内容
            DateTime now = DateTime.Now;
            StringBuilder sb = new StringBuilder();
            sb.Append($"[{now:yyyy-MM-dd HH:mm:ss.fff}]");
            sb.Append($"[{level.ToString().ToUpper()}]");
            sb.Append($"[{sender}] ");
            sb.Append(msg);

            // 追加异常堆栈
            if (ex != null)
            {
                sb.AppendLine();
                sb.Append($"异常详情：{ex.Message}");
                sb.AppendLine();
                sb.Append($"堆栈：{ex.StackTrace}");
            }

            string fullLogText = sb.ToString();

            // 控制台输出
            if (EnableConsoleOutput)
            {
                Console.WriteLine(fullLogText);
            }

            // 文件落地，加锁保证线程安全
            if (EnableFileOutput)
            {
                WriteLogToFile(now, fullLogText);
            }
        }

        /// <summary>按天拆分日志文件写入</summary>
        private static void WriteLogToFile(DateTime logTime, string content)
        {
            try
            {
                lock (_fileWriteLock)
                {
                    // 按日期创建子目录
                    string dayFolder = Path.Combine(_logRootPath, logTime.ToString("yyyy-MM-dd"));
                    FileHelper.EnsureDirectoryExists(dayFolder);
                    // 每日一个日志文件
                    string logFilePath = Path.Combine(dayFolder, "Runtime.log");

                    // 追加写入
                    File.AppendAllText(logFilePath, content + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 日志写入失败兜底，防止日志异常导致主程序报错
                Console.WriteLine($"日志文件写入失败：{ex.Message}");
            }
        }
        #endregion

        #region 外部配置修改接口
        /// <summary>动态修改日志根路径</summary>
        public static void SetLogRootPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _logRootPath = path;
            }
        }
        #endregion
    }
}