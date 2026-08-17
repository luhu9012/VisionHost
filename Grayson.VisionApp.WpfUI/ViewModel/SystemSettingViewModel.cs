//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: SystemSettingViewModel.cs
// 说 明: 系统与运行参数设置 ViewModel
//===================================================================================
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

            BrowseLogFolderCommand = new RelayCommand(_ => BrowseFolder(path => LogPath = path));
            BrowseImageFolderCommand = new RelayCommand(_ => BrowseFolder(path => ImageStorePath = path));
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

        #region 4. 状态与 Commands
        private bool _isDirty;
        public bool IsDirty { get => _isDirty; set => Set(ref _isDirty, value); }

        public ICommand SaveCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand OpenImageFolderCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand BrowseLogFolderCommand { get; }
        public ICommand BrowseImageFolderCommand { get; }
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

                doc.Save(_configPath);
                IsDirty = false;
                _dialogService.ShowInfo("系统与运行参数设置已保存，部分选项需重启后生效。", "保存成功");
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