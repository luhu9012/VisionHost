//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: DataTraceViewModel.cs
// 说 明: 本地追溯与防错：按时间/工位/条码筛选检测结果
//===================================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    // DataTraceEntry 已迁移至 Grayson.Vision.Contracts.Station.Models
    // 此处通过 using 导入保持兼容性

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
            LoadLiveWorkOrdersCommand = new RelayCommand(_ => OnLoadLiveWorkOrders());
            ShowWorkOrderDetailCommand = new RelayCommand(_ => OnShowWorkOrderDetail(), _ => SelectedEntry != null);

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
        public ICommand LoadLiveWorkOrdersCommand { get; }
        public ICommand ShowWorkOrderDetailCommand { get; }

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

        /// <summary>
        /// 从 StationHostRuntime 实时读取最近工单快照，补充到追溯列表中。
        /// </summary>
        private void OnLoadLiveWorkOrders()
        {
            TraceEntries.Clear();
            TotalCount = OkCount = NgCount = 0;

            try
            {
                var manager = Grayson.Vision.Core.Client.StationRuntimeManager.GlobalInstance;
                if (manager == null)
                {
                    MessageBox.Show("实时运行时尚未初始化。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                IEnumerable<string> stationIds;
                if (!string.IsNullOrEmpty(SelectedStationCode) && SelectedStationCode != "全部")
                    stationIds = new[] { SelectedStationCode };
                else
                    stationIds = manager.HostRuntime?.GetStationIds() ?? Enumerable.Empty<string>();

                var liveEntries = new List<DataTraceEntry>();
                foreach (var stationId in stationIds)
                {
                    var tracker = manager.GetWorkOrderTracker(stationId);
                    if (tracker == null) continue;

                    foreach (var wo in tracker.GetRecentWorkOrders(50))
                    {
                        if (wo == null) continue;

                        if (!string.IsNullOrWhiteSpace(BatchIdFilter) &&
                            (wo.BatchId == null || !wo.BatchId.Contains(BatchIdFilter)))
                            continue;

                        liveEntries.Add(new DataTraceEntry
                        {
                            InspectTime = wo.CreatedAt,
                            StationId = wo.StationId,
                            BatchId = wo.BatchId,
                            RecipeName = wo.RecipeName ?? "—",
                            Result = wo.IsOk.HasValue ? (wo.IsOk.Value ? "OK" : "NG") : "—",
                            ErrorMessage = wo.ErrorMessage,
                            CycleTimeMs = wo.CycleTimeMs,
                            ImagePath = wo.ImagePath
                        });
                    }
                }

                var filtered = liveEntries
                    .Where(x => x.InspectTime >= StartTime && x.InspectTime <= EndTime)
                    .OrderByDescending(x => x.InspectTime)
                    .ToList();

                TotalCount = filtered.Count;
                OkCount = filtered.Count(x => x.Result == "OK");
                NgCount = TotalCount - OkCount;

                foreach (var entry in filtered)
                    TraceEntries.Add(entry);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取实时工单失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GetWorkOrderResultText(WorkOrderSnapshot wo)
        {
            if (wo == null) return "—";
            return wo.IsOk.HasValue ? (wo.IsOk.Value ? "OK" : "NG") : "—";
        }

        private string GetWorkOrderErrorMessage(WorkOrderSnapshot wo)
        {
            if (wo == null) return null;
            return wo.ErrorMessage;
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

        /// <summary>
        /// 显示工单详情对话框
        /// </summary>
        private void OnShowWorkOrderDetail()
        {
            if (SelectedEntry == null) return;

            try
            {
                // 创建工单详情窗口
                var detailVm = new WorkOrderDetailDialogViewModel();

                // 填充基本的演示数据
                detailVm.WorkOrderId = $"WO-{SelectedEntry.BatchId}";
                detailVm.StationId = SelectedEntry.StationId;
                detailVm.BatchId = SelectedEntry.BatchId;
                detailVm.CreatedAt = SelectedEntry.InspectTime;
                detailVm.StartedAt = SelectedEntry.InspectTime;
                detailVm.CompletedAt = SelectedEntry.InspectTime.AddSeconds(1);
                detailVm.ResultStatus = SelectedEntry.Result == "OK" ? "✓ OK" : "✗ NG";
                detailVm.ResultStatusColor = SelectedEntry.Result == "OK" ? "#FF00C853" : "#FFFF3D00";
                detailVm.TotalElapsedText = $"{SelectedEntry.CycleTimeMs:F1} ms";
                detailVm.TotalElapsedMs = SelectedEntry.CycleTimeMs;

                // 创建并显示对话框窗口
                var detailView = new View.WorkOrderDetailDialog
                {
                    DataContext = detailVm
                };

                // 简单提示（实际应该在独立窗口中显示完整信息）
                MessageBox.Show($"工单详情\n" +
                                $"工单ID: {detailVm.WorkOrderId}\n" +
                                $"工位: {SelectedEntry.StationId}\n" +
                                $"检验结果: {SelectedEntry.Result}\n" +
                                $"检验时间: {SelectedEntry.InspectTime:yyyy-MM-dd HH:mm:ss}\n" +
                                $"周期时间: {SelectedEntry.CycleTimeMs:F1} ms",
                                "工单详情", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"显示工单详情失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
