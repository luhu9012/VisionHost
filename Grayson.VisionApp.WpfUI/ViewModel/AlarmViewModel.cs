//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: AlarmViewModel.cs
// 创 建: 2026-07-18
// 说 明: 报警日志界面的 ViewModel
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
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

    public class AlarmViewModel : ViewModelBase
    {
        private System.Windows.Threading.DispatcherTimer _timer;

        public AlarmViewModel()
        {


            AcknowledgeCommand = new RelayCommand(OnAcknowledge);
            AcknowledgeAllCommand = new RelayCommand(_ => OnAcknowledgeAll());
            ClearHistoryCommand = new RelayCommand(_ => OnClearHistory());
            ExportCommand = new RelayCommand(_ => OnExport());

            InitializeMockData();

            StartMockAlarmTimer();
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

        private void InitializeMockData()
        {
            AlarmList = new ObservableCollection<AlarmRecordModel>
            {
                new AlarmRecordModel { Time = DateTime.Now.AddMinutes(-45), Level = AlarmLevel.Critical, Source = "相机2-检测工位", Message = "相机连接断开,无法采集图像", IsAcknowledged = false },
                new AlarmRecordModel { Time = DateTime.Now.AddMinutes(-30), Level = AlarmLevel.Error, Source = "运动控制卡", Message = "X轴运动超限,触发硬限位", IsAcknowledged = false },
                new AlarmRecordModel { Time = DateTime.Now.AddMinutes(-20), Level = AlarmLevel.Warning, Source = "视觉检测", Message = "连续10个产品检测NG,请检查配方参数", IsAcknowledged = true, AcknowledgedBy = "admin" },
                new AlarmRecordModel { Time = DateTime.Now.AddMinutes(-15), Level = AlarmLevel.Error, Source = "PLC通信", Message = "PLC通信超时,3秒未收到响应", IsAcknowledged = false },
                new AlarmRecordModel { Time = DateTime.Now.AddMinutes(-10), Level = AlarmLevel.Warning, Source = "光源控制", Message = "光源1亮度异常,实际亮度低于设定值", IsAcknowledged = true, AcknowledgedBy = "engineer" }
            };

            AlarmList.CollectionChanged += (s, e) => UpdateAlarmCounts();
        }

        private void StartMockAlarmTimer()
        {
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _timer.Tick += (s, e) =>
            {
                var random = new Random();
                if (random.Next(100) < 30)
                {
                    var levels = new[] { AlarmLevel.Info, AlarmLevel.Warning, AlarmLevel.Error };
                    var sources = new[] { "相机1", "相机2", "PLC", "运动卡", "视觉算法" };
                    var messages = new[] { "设备通信延迟", "参数超限", "检测异常", "连接不稳定" };

                    var newAlarm = new AlarmRecordModel
                    {
                        Time = DateTime.Now,
                        Level = levels[random.Next(levels.Length)],
                        Source = sources[random.Next(sources.Length)],
                        Message = messages[random.Next(messages.Length)],
                        IsAcknowledged = false
                    };

                    AlarmList.Insert(0, newAlarm);
                    GlobalData.Instance.AlarmCount = AlarmList.Count(a => !a.IsAcknowledged);
                }
            };
            _timer.Start();
        }

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
