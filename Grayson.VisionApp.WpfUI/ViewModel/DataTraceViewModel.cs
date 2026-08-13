//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: DataTraceViewModel.cs
// 说 明: 本地追溯与防错：按时间/工位/条码筛选检测结果
//===================================================================================
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class DataTraceEntry : ViewModelBase
    {
        public DateTime InspectTime { get; set; }
        public string StationId { get; set; }
        public string BatchId { get; set; }
        public string RecipeName { get; set; }
        public string Result { get; set; }
        public string ErrorMessage { get; set; }
        public double CycleTimeMs { get; set; }
        public string ImagePath { get; set; }

        public string ResultText => Result;
        public string ResultBrushKey => Result == "OK" ? "SuccessBrush" : "DangerBrush";
    }

    public class DataTraceViewModel : ViewModelBase
    {
        private readonly IInspectionLogRepository _logRepo;
        private readonly StationConfigService _configService;

        public DataTraceViewModel(IInspectionLogRepository logRepo = null, StationConfigService configService = null)
        {
            _logRepo = logRepo ?? StorageFactory.CreateInspectionLogRepository();
            _configService = configService ?? new StationConfigService();

            TraceEntries = new ObservableCollection<DataTraceEntry>();
            AvailableStations = new ObservableCollection<string>();

            SearchCommand = new RelayCommand(_ => OnSearch());
            ExportCommand = new RelayCommand(_ => OnExport());
            OpenImageCommand = new RelayCommand(_ => OnOpenImage(), _ => SelectedEntry != null && !string.IsNullOrEmpty(SelectedEntry.ImagePath));

            StartTime = DateTime.Today.AddDays(-1);
            EndTime = DateTime.Today.AddDays(1).AddSeconds(-1);

            LoadStations();
            OnSearch();
        }

        public ObservableCollection<DataTraceEntry> TraceEntries { get; set; }
        public ObservableCollection<string> AvailableStations { get; set; }

        private DataTraceEntry _selectedEntry;
        public DataTraceEntry SelectedEntry
        {
            get => _selectedEntry;
            set
            {
                if (Set(ref _selectedEntry, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _selectedStationCode;
        public string SelectedStationCode
        {
            get => _selectedStationCode;
            set => Set(ref _selectedStationCode, value);
        }

        private string _batchIdFilter;
        public string BatchIdFilter
        {
            get => _batchIdFilter;
            set => Set(ref _batchIdFilter, value);
        }

        private DateTime _startTime;
        public DateTime StartTime
        {
            get => _startTime;
            set => Set(ref _startTime, value);
        }

        private DateTime _endTime;
        public DateTime EndTime
        {
            get => _endTime;
            set => Set(ref _endTime, value);
        }

        private int _totalCount;
        public int TotalCount
        {
            get => _totalCount;
            set => Set(ref _totalCount, value);
        }

        private int _okCount;
        public int OkCount
        {
            get => _okCount;
            set => Set(ref _okCount, value);
        }

        private int _ngCount;
        public int NgCount
        {
            get => _ngCount;
            set => Set(ref _ngCount, value);
        }

        public ICommand SearchCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand OpenImageCommand { get; }

        private void LoadStations()
        {
            AvailableStations.Clear();
            AvailableStations.Add("全部");

            var lines = _configService.LoadAllLines();
            foreach (var station in lines.SelectMany(l => l.Stations))
            {
                AvailableStations.Add(station.StationCode);
            }

            SelectedStationCode = AvailableStations.FirstOrDefault();
        }

        private void OnSearch()
        {
            TraceEntries.Clear();

            try
            {
                var allLogs = _logRepo.GetAll().ToList();

                var query = allLogs.AsEnumerable();

                if (!string.IsNullOrEmpty(SelectedStationCode) && SelectedStationCode != "全部")
                {
                    query = query.Where(x => x.StationId == SelectedStationCode);
                }

                query = query.Where(x => x.InspectTime >= StartTime && x.InspectTime <= EndTime);

                if (!string.IsNullOrWhiteSpace(BatchIdFilter))
                {
                    query = query.Where(x => x.BatchId != null && x.BatchId.Contains(BatchIdFilter));
                }

                var filtered = query.OrderByDescending(x => x.InspectTime).ToList();

                TotalCount = filtered.Count;
                OkCount = filtered.Count(x => x.IsOk);
                NgCount = TotalCount - OkCount;

                foreach (var log in filtered)
                {
                    TraceEntries.Add(MapToEntry(log));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查询失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private DataTraceEntry MapToEntry(InspectionLogPo log)
        {
            return new DataTraceEntry
            {
                InspectTime = log.InspectTime,
                StationId = log.StationId,
                BatchId = log.BatchId,
                RecipeName = log.RecipeName,
                Result = log.IsOk ? "OK" : "NG",
                ErrorMessage = log.ErrorMessage,
                CycleTimeMs = log.CycleTimeMs,
                ImagePath = log.ImagePath
            };
        }

        private void OnExport()
        {
            MessageBox.Show("导出功能将在后续对接报表服务后完善。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OnOpenImage()
        {
            try
            {
                if (SelectedEntry == null || string.IsNullOrEmpty(SelectedEntry.ImagePath)) return;

                if (!System.IO.File.Exists(SelectedEntry.ImagePath))
                {
                    MessageBox.Show("图片文件不存在或已被清理。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                System.Diagnostics.Process.Start(SelectedEntry.ImagePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开图片失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
