//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: SystemSettingViewModel.cs
// 说 明: 系统与运行参数设置 ViewModel
//===================================================================================
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class SystemSettingViewModel : ViewModelBase
    {
        private readonly WpfDialogService _dialogService;
        private readonly string _configPath;

        public SystemSettingViewModel() : this(new WpfDialogService())
        {
        }

        public SystemSettingViewModel(WpfDialogService dialogService)
        {
            _dialogService = dialogService ?? new WpfDialogService();
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Grayson.Vision.WpfUI.exe.config");

            LoadSettings();

            SaveCommand = new RelayCommand(_ => OnSave());
            ResetCommand = new RelayCommand(_ => LoadSettings());
            OpenLogFolderCommand = new RelayCommand(_ => OpenFolder(LogPath));
            OpenImageFolderCommand = new RelayCommand(_ => OpenFolder(ImageStorePath));
            OpenDataFolderCommand = new RelayCommand(_ => OpenFolder(DataPath));
            ClearProductionStatsCommand = new RelayCommand(_ => OnClearProductionStats());

            BrowseLogFolderCommand = new RelayCommand(_ => BrowseFolder(path => LogPath = path));
            BrowseImageFolderCommand = new RelayCommand(_ => BrowseFolder(path => ImageStorePath = path));
        }

        /// <summary>
        /// 清除所有工位生产统计数据（工位监视页头部的 总数/OK/NG/良率 累计）。
        /// 删除 Data\StationStats\*.stats.json；运行中的监视页下次轮询（500ms）以当前
        /// Worker 计数为新基线重建，已跑过的周期不会重新计入。
        /// </summary>
        private void OnClearProductionStats()
        {
            if (!_dialogService.ShowConfirm(
                "确认清除所有工位的生产统计数据（总数 / OK / NG / 良率累计）？\n该操作不可恢复；正在运行的工位将从当前计数重新累计。",
                "清除生产统计"))
            {
                return;
            }

            try
            {
                var statsService = new StationStatisticsService();
                int count = statsService.ClearAll();
                _dialogService.ShowInfo($"已清除 {count} 个工位的生产统计数据。", "清除完成");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"清除失败: {ex.Message}", "错误");
            }
        }

        #region 1. 存储与路径属性
        private string _logPath;
        public string LogPath { get => _logPath; set { Set(ref _logPath, value); MarkDirty(); } }

        private string _imageStorePath;
        public string ImageStorePath { get => _imageStorePath; set { Set(ref _imageStorePath, value); MarkDirty(); } }

        private string _dataPath;
        public string DataPath { get => _dataPath; set => Set(ref _dataPath, value); }

        private int _imageSaveModeIndex;
        public int ImageSaveModeIndex { get => _imageSaveModeIndex; set { Set(ref _imageSaveModeIndex, value); MarkDirty(); } }

        private int _imageRetentionDays;
        public int ImageRetentionDays { get => _imageRetentionDays; set { Set(ref _imageRetentionDays, value); MarkDirty(); } }

        private int _diskWarningThreshold;
        public int DiskWarningThreshold { get => _diskWarningThreshold; set { Set(ref _diskWarningThreshold, value); MarkDirty(); } }

        private bool _autoPurgeOnLowDisk;
        public bool AutoPurgeOnLowDisk { get => _autoPurgeOnLowDisk; set { Set(ref _autoPurgeOnLowDisk, value); MarkDirty(); } }
        #endregion

        #region 2. 运行与工位策略属性
        private bool _autoStartStations;
        public bool AutoStartStations { get => _autoStartStations; set { Set(ref _autoStartStations, value); MarkDirty(); } }

        private int _stationStartTimeoutMs;
        public int StationStartTimeoutMs { get => _stationStartTimeoutMs; set { Set(ref _stationStartTimeoutMs, value); MarkDirty(); } }

        private int _workOrderHistoryCountIndex;
        public int WorkOrderHistoryCountIndex { get => _workOrderHistoryCountIndex; set { Set(ref _workOrderHistoryCountIndex, value); MarkDirty(); } }

        public int[] WorkOrderHistoryCounts { get; } = { 100, 500, 1000, 5000 };
        #endregion

        #region 3. 渲染性能属性
        private int _renderFpsIndex;
        public int RenderFpsIndex { get => _renderFpsIndex; set { Set(ref _renderFpsIndex, value); MarkDirty(); } }

        private bool _optimizeOverlays;
        public bool OptimizeOverlays { get => _optimizeOverlays; set { Set(ref _optimizeOverlays, value); MarkDirty(); } }
        #endregion

        #region 4. 日志与诊断属性（与 LogConfig 单例同源同键，保存后热生效）
        /// <summary>日志级别索引：0=Debug 1=Info 2=Warn 3=Error（与 LogLevel 枚举序一致）</summary>
        private int _logLevelIndex;
        public int LogLevelIndex { get => _logLevelIndex; set { Set(ref _logLevelIndex, value); MarkDirty(); } }

        /// <summary>日志保留天数（超过自动清理，防磁盘占满）</summary>
        private int _logRetentionDays;
        public int LogRetentionDays { get => _logRetentionDays; set { Set(ref _logRetentionDays, value); MarkDirty(); } }

        /// <summary>单个日志文件大小上限（MB，超过滚动新文件）</summary>
        private int _logMaxFileSizeMB;
        public int LogMaxFileSizeMB { get => _logMaxFileSizeMB; set { Set(ref _logMaxFileSizeMB, value); MarkDirty(); } }

        /// <summary>是否启用结构化 JSON 落盘（.jsonl，供清洗 / AI 分析消费）</summary>
        private bool _logJsonEnabled;
        public bool LogJsonEnabled { get => _logJsonEnabled; set { Set(ref _logJsonEnabled, value); MarkDirty(); } }
        #endregion

        #region 5. 状态与 Commands
        private bool _isDirty;
        public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand OpenImageFolderCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand BrowseLogFolderCommand { get; }
        public ICommand BrowseImageFolderCommand { get; }
        /// <summary>清除所有工位生产统计数据（OK/NG 累计）</summary>
        public ICommand ClearProductionStatsCommand { get; }
        #endregion

        private void MarkDirty()
        {
            IsDirty = true;
        }

        private void LoadSettings()
        {
            LogPath = ReadAppSetting("LogPath") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            ImageStorePath = ReadAppSetting("ImageStorePath") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
            DataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");

            AutoStartStations = string.Equals(ReadAppSetting("AutoStartStations"), "true", StringComparison.OrdinalIgnoreCase);
            StationStartTimeoutMs = ParseInt(ReadAppSetting("StationStartTimeoutMs"), 5000);
            WorkOrderHistoryCountIndex = ParseInt(ReadAppSetting("WorkOrderHistoryCountIndex"), 2);

            ImageSaveModeIndex = ParseInt(ReadAppSetting("ImageSaveModeIndex"), 0);
            ImageRetentionDays = ParseInt(ReadAppSetting("ImageRetentionDays"), 30);
            DiskWarningThreshold = ParseInt(ReadAppSetting("DiskWarningThreshold"), 15);
            AutoPurgeOnLowDisk = !string.Equals(ReadAppSetting("AutoPurgeOnLowDisk"), "false", StringComparison.OrdinalIgnoreCase);

            RenderFpsIndex = ParseInt(ReadAppSetting("RenderFpsIndex"), 1);
            OptimizeOverlays = !string.Equals(ReadAppSetting("OptimizeOverlays"), "false", StringComparison.OrdinalIgnoreCase);

            // 日志与诊断（默认 Info 级别、保留 30 天、单文件 50MB、不开 JSON）
            LogLevelIndex = Enum.TryParse(ReadAppSetting("LogLevel"), true, out LogLevel logLevel) ? (int)logLevel : 1;
            LogRetentionDays = ParseInt(ReadAppSetting("LogRetentionDays"), 30);
            LogMaxFileSizeMB = ParseInt(ReadAppSetting("LogMaxFileSizeMB"), 50);
            LogJsonEnabled = string.Equals(ReadAppSetting("LogJsonEnabled"), "true", StringComparison.OrdinalIgnoreCase);

            IsDirty = false;
        }

        private void OnSave()
        {
            try
            {
                XDocument doc;
                if (File.Exists(_configPath))
                {
                    doc = XDocument.Load(_configPath);
                }
                else
                {
                    doc = new XDocument(new XElement("configuration", new XElement("appSettings")));
                }

                var appSettings = doc.Root?.Element("appSettings");
                if (appSettings == null)
                {
                    doc.Root?.Add(new XElement("appSettings"));
                    appSettings = doc.Root?.Element("appSettings");
                }

                SetXmlAppSetting(appSettings, "LogPath", LogPath);
                SetXmlAppSetting(appSettings, "ImageStorePath", ImageStorePath);
                SetXmlAppSetting(appSettings, "AutoStartStations", AutoStartStations ? "true" : "false");
                SetXmlAppSetting(appSettings, "StationStartTimeoutMs", StationStartTimeoutMs.ToString());
                SetXmlAppSetting(appSettings, "WorkOrderHistoryCountIndex", WorkOrderHistoryCountIndex.ToString());

                SetXmlAppSetting(appSettings, "ImageSaveModeIndex", ImageSaveModeIndex.ToString());
                SetXmlAppSetting(appSettings, "ImageRetentionDays", ImageRetentionDays.ToString());
                SetXmlAppSetting(appSettings, "DiskWarningThreshold", DiskWarningThreshold.ToString());
                SetXmlAppSetting(appSettings, "AutoPurgeOnLowDisk", AutoPurgeOnLowDisk ? "true" : "false");

                SetXmlAppSetting(appSettings, "RenderFpsIndex", RenderFpsIndex.ToString());
                SetXmlAppSetting(appSettings, "OptimizeOverlays", OptimizeOverlays ? "true" : "false");

                // 日志与诊断配置（键与 LogConfig.LoadFromAppSettings 保持一致）
                SetXmlAppSetting(appSettings, "LogLevel", ((LogLevel)LogLevelIndex).ToString());
                SetXmlAppSetting(appSettings, "LogRetentionDays", LogRetentionDays.ToString());
                SetXmlAppSetting(appSettings, "LogMaxFileSizeMB", LogMaxFileSizeMB.ToString());
                SetXmlAppSetting(appSettings, "LogJsonEnabled", LogJsonEnabled ? "true" : "false");

                doc.Save(_configPath);
                IsDirty = false;

                // 🌟 热更新日志配置（无需重启）：同步到 LogConfig 单例 → LogRouter.ApplyConfig → 各 Sink 即时生效
                try
                {
                    var cfg = LogConfig.Instance;
                    cfg.LogPath = LogPath;
                    cfg.MinLevel = (LogLevel)LogLevelIndex;
                    cfg.RetentionDays = LogRetentionDays;
                    cfg.MaxFileSizeMB = LogMaxFileSizeMB;
                    cfg.JsonEnabled = LogJsonEnabled;
                    cfg.Apply();
                }
                catch
                {
                    // 热更新失败不影响配置文件已保存（下次启动按新配置装配）
                }

                _dialogService.ShowInfo("系统与运行参数设置已保存，部分选项需重启后生效（日志配置已即时生效）。", "保存成功");
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"保存失败: {ex.Message}", "错误");
            }
        }

        private void BrowseFolder(Action<string> setPathAction)
        {
            string selectedPath = _dialogService.ShowFolderBrowserDialog();
            if (!string.IsNullOrEmpty(selectedPath))
            {
                setPathAction(selectedPath);
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ReadAppSetting(string key)
        {
            try
            {
                if (!File.Exists(_configPath)) return null;
                var doc = XDocument.Load(_configPath);
                return doc.Root?.Element("appSettings")?.Elements("add")
                    .FirstOrDefault(e => (string)e.Attribute("key") == key)?.Attribute("value")?.Value;
            }
            catch { return null; }
        }

        private static void SetXmlAppSetting(XElement appSettings, string key, string value)
        {
            var element = appSettings.Elements("add").FirstOrDefault(e => (string)e.Attribute("key") == key);
            if (element == null)
                appSettings.Add(new XElement("add", new XAttribute("key", key), new XAttribute("value", value)));
            else
                element.SetAttributeValue("value", value);
        }

        private static int ParseInt(string value, int defaultValue)
        {
            return int.TryParse(value, out int result) ? result : defaultValue;
        }
    }
}