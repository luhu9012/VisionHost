using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Grayson.Vision.Contracts.Infrastructure.Logging
{
    /// <summary>
    /// 日志全局配置（单例，热生效）。
    /// 消费方：
    ///   - App 启动：LogConfig.Instance.LoadFromAppSettings() 读取 exe.config → 装配 LogRouter；
    ///   - SystemSettingView「📝 日志与诊断」Tab：保存设置后更新 Instance 并调用 Apply() 热生效；
    ///   - LogRouter：级别过滤（MinLevel）与各 Sink 配置（LogPath / RetentionDays / MaxFileSizeMB / JsonEnabled）。
    /// 配置键与 SystemSettingViewModel 读写逻辑保持一致（同源同键，键名见 LoadFromAppSettings）。
    /// </summary>
    public sealed class LogConfig
    {
        /// <summary>全局单例（进程内唯一配置源）</summary>
        public static LogConfig Instance { get; } = new LogConfig();

        /// <summary>日志文件存储目录（默认：运行目录\Logs）</summary>
        public string LogPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>最低记录级别：低于此级别的日志被丢弃（默认 Info，产线不建议开 Debug 防爆盘）</summary>
        public LogLevel MinLevel { get; set; } = LogLevel.Info;

        /// <summary>日志文件保留天数：超过此天数的旧日志自动删除（默认 30 天，防磁盘占满）</summary>
        public int RetentionDays { get; set; } = 30;

        /// <summary>单个日志文件大小上限（MB，超过后滚动新文件，默认 50MB）</summary>
        public int MaxFileSizeMB { get; set; } = 50;

        /// <summary>是否启用结构化 JSON 落盘（NDJSON，供清洗 / AI 分析消费）</summary>
        public bool JsonEnabled { get; set; }

        /// <summary>配置变更事件：Apply() 触发 → LogRouter.ApplyConfig 热更新所有 Sink（保存即生效，无需重启）</summary>
        public event Action ConfigChanged;

        /// <summary>通知配置已变更（热生效入口，由 SystemSettingViewModel 保存成功后调用）</summary>
        public void Apply()
        {
            try { ConfigChanged?.Invoke(); }
            catch { /* 配置订阅者异常不影响调用方 */ }
        }

        /// <summary>
        /// 从 exe.config 的 appSettings 加载日志配置。
        /// 键：LogPath / LogLevel / LogRetentionDays / LogMaxFileSizeMB / LogJsonEnabled。
        /// 读取失败时静默使用默认值——日志模块绝不允许成为应用启动的故障源。
        /// </summary>
        public void LoadFromAppSettings()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Grayson.Vision.WpfUI.exe.config");
                if (!File.Exists(configPath)) return;
                var doc = XDocument.Load(configPath);

                string Read(string key)
                {
                    return doc.Root?.Element("appSettings")?.Elements("add")
                        .FirstOrDefault(e => (string)e.Attribute("key") == key)?.Attribute("value")?.Value;
                }

                string logPath = Read("LogPath");
                if (!string.IsNullOrWhiteSpace(logPath)) LogPath = logPath;

                if (Enum.TryParse(Read("LogLevel"), true, out LogLevel level)) MinLevel = level;

                if (int.TryParse(Read("LogRetentionDays"), out int retention) && retention > 0) RetentionDays = retention;

                if (int.TryParse(Read("LogMaxFileSizeMB"), out int maxMb) && maxMb > 0) MaxFileSizeMB = maxMb;

                if (bool.TryParse(Read("LogJsonEnabled"), out bool json)) JsonEnabled = json;
            }
            catch
            {
                // 读取失败 → 保持默认值（日志模块绝不允许成为启动故障源）
            }
        }
    }
}
