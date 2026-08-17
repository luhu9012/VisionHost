//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: AlarmViewModel.cs
// 创 建: 2026-07-18
// 说 明: 报警日志界面的 ViewModel
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Alarm;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Core.Infrastructure.Alarm;
using Grayson.Vision.WpfUI.Common;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class AlarmRecordModel : ViewModelBase
    {
        private DateTime _time;
        public DateTime Time
        {
            get => _time;
            set => Set(ref _time, value);
        }

        private AlarmLevel _level;
        public AlarmLevel Level
        {
            get => _level;
            set => Set(ref _level, value);
        }

        private string _source;
        public string Source
        {
            get => _source;
            set => Set(ref _source, value);
        }

        private string _message;
        public string Message
        {
            get => _message;
            set => Set(ref _message, value);
        }

        private bool _isAcknowledged;
        public bool IsAcknowledged
        {
            get => _isAcknowledged;
            set => Set(ref _isAcknowledged, value);
        }

        private string _acknowledgedBy;
        public string AcknowledgedBy
        {
            get => _acknowledgedBy;
            set => Set(ref _acknowledgedBy, value);
        }
    }

    public class AlarmViewModel : ViewModelBase, IAlarmSink
    {
        public AlarmViewModel()
        {
            AcknowledgeCommand = new RelayCommand(OnAcknowledge);
            AcknowledgeAllCommand = new RelayCommand(_ => OnAcknowledgeAll());
            ClearHistoryCommand = new RelayCommand(_ => OnClearHistory());
            ExportCommand = new RelayCommand(_ => OnExport());

            AlarmList = new ObservableCollection<AlarmRecordModel>();
            AlarmList.CollectionChanged += (s, e) => UpdateAlarmCounts();

            // 注册为 Core 告警总线的 UI Sink
            AlarmBus.Instance.RegisterSink(this);
        }

        /// <summary>
        /// 页面离开或 VM 释放时，从 AlarmBus 注销，避免内存泄漏。
        /// </summary>
        public void Dispose()
        {
            AlarmBus.Instance.UnregisterSink(this);
        }

        /// <summary>
        /// Core 告警总线回调入口。
        /// </summary>
        public void Raise(AlarmItem alarm)
        {
            if (alarm == null) return;

            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var record = new AlarmRecordModel
                {
                    Time = alarm.Timestamp.ToLocalTime(),
                    Level = MapSeverity(alarm.Severity),
                    Source = string.IsNullOrEmpty(alarm.StationId)
                        ? alarm.Source
                        : $"[{alarm.StationId}] {alarm.Source}",
                    Message = $"[{alarm.FaultCode}] {alarm.Message}",
                    IsAcknowledged = false
                };

                AlarmList.Insert(0, record);
                UpdateAlarmCounts();
            });
        }

        private static AlarmLevel MapSeverity(AlarmSeverity severity)
        {
            switch (severity)
            {
                case AlarmSeverity.Warning: return AlarmLevel.Warning;
                case AlarmSeverity.Fault: return AlarmLevel.Error;
                default: return AlarmLevel.Info;
            }
        }

        #region 属性

        private ObservableCollection<AlarmRecordModel> _alarmList;
        public ObservableCollection<AlarmRecordModel> AlarmList
        {
            get => _alarmList;
            set => Set(ref _alarmList, value);
        }

        private AlarmRecordModel _selectedAlarm;
        public AlarmRecordModel SelectedAlarm
        {
            get => _selectedAlarm;
            set => Set(ref _selectedAlarm, value);
        }

        public int TotalAlarms => AlarmList?.Count ?? 0;
        public int CriticalCount => AlarmList?.Count(a => a.Level == AlarmLevel.Critical && !a.IsAcknowledged) ?? 0;
        public int ErrorCount => AlarmList?.Count(a => a.Level == AlarmLevel.Error && !a.IsAcknowledged) ?? 0;
        public int WarningCount => AlarmList?.Count(a => a.Level == AlarmLevel.Warning && !a.IsAcknowledged) ?? 0;

        #endregion

        #region 命令

        public ICommand AcknowledgeCommand { get; }
        public ICommand AcknowledgeAllCommand { get; }
        public ICommand ClearHistoryCommand { get; }
        public ICommand ExportCommand { get; }

        #endregion

        #region 方法

        private void OnAcknowledge(object parameter)
        {
            if (parameter is AlarmRecordModel alarm)
            {
                alarm.IsAcknowledged = true;
                alarm.AcknowledgedBy = GlobalData.Instance.CurrentUserName;
                MessageBox.Show($"已确认报警: {alarm.Message}");
                UpdateAlarmCounts();
            }
        }

        private void OnAcknowledgeAll()
        {
            foreach (var alarm in AlarmList.Where(a => !a.IsAcknowledged))
            {
                alarm.IsAcknowledged = true;
                alarm.AcknowledgedBy = GlobalData.Instance.CurrentUserName;
            }
            MessageBox.Show("已确认所有报警");
            UpdateAlarmCounts();
        }

        private void OnClearHistory()
        {
            //if (ShowConfirm("确定要清除已确认的历史报警吗?"))
            //{
            //    var toRemove = AlarmList.Where(a => a.IsAcknowledged).ToList();
            //    foreach (var alarm in toRemove)
            //    {
            //        AlarmList.Remove(alarm);
            //    }
            //    MessageBox.Show($"已清除 {toRemove.Count} 条历史报警");
            //}
        }

        private void OnExport()
        {
            MessageBox.Show("导出报警日志 (TODO: 对接Excel导出功能)");
        }

        private void UpdateAlarmCounts()
        {
            OnPropertyChanged(nameof(TotalAlarms));
            OnPropertyChanged(nameof(CriticalCount));
            OnPropertyChanged(nameof(ErrorCount));
            OnPropertyChanged(nameof(WarningCount));
            GlobalData.Instance.AlarmCount = CriticalCount + ErrorCount;
        }

        #endregion
    }
}
