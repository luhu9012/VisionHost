//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardViewModel.cs
//===================================================================================
using System;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Grayson.Vision.Contracts.Calibration.Models; // 引入契约层模型
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.ViewModel.Steps;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class CalibrationWizardViewModel : ViewModelBase
    {
        private readonly Window _owner;
        private readonly StringBuilder _log = new StringBuilder();
        private ICalibrationStepStrategy _strategy;

        public ICalibrationService CalibService { get; }

        #region 属性绑定

        public CalibrationProfile TargetProfile { get; }

        // 直接统一使用契约层 (Contracts) 的数据模型
        public ObservableCollection<CalibrationPointModel> CalibrationPoints { get; }
            = new ObservableCollection<CalibrationPointModel>();

        public ObservableCollection<RotationPointModel> RotationPoints { get; }
            = new ObservableCollection<RotationPointModel>();

        private string _rotationCenterResult = "Cx: --, Cy: --";
        public string RotationCenterResult
        {
            get => _rotationCenterResult;
            set => Set(ref _rotationCenterResult, value);
        }

        // 九点/多点走位参数
        public double GridStepX { get; set; } = 10.0;
        public double GridStepY { get; set; } = 10.0;

        // 棋盘格标定参数
        private int _checkerboardRows = 7;
        public int CheckerboardRows
        {
            get => _checkerboardRows;
            set => Set(ref _checkerboardRows, value);
        }

        private int _checkerboardCols = 7;
        public int CheckerboardCols
        {
            get => _checkerboardCols;
            set => Set(ref _checkerboardCols, value);
        }

        private double _checkerboardSpacingMm = 10.0;
        public double CheckerboardSpacingMm
        {
            get => _checkerboardSpacingMm;
            set => Set(ref _checkerboardSpacingMm, value);
        }

        // 像素比例参数
        private double _measuredPixelDistance = 100.0;
        public double MeasuredPixelDistance
        {
            get => _measuredPixelDistance;
            set => Set(ref _measuredPixelDistance, value);
        }

        private double _knownPhysicalDistanceMm = 10.0;
        public double KnownPhysicalDistanceMm
        {
            get => _knownPhysicalDistanceMm;
            set => Set(ref _knownPhysicalDistanceMm, value);
        }

        public string OutputHomMatPath { get; private set; }

        private double _calculatedRms;
        public double CalculatedRms
        {
            get => _calculatedRms;
            set => Set(ref _calculatedRms, value);
        }

        public string LogText => _log.ToString();

        #endregion

        #region 步骤控制

        private int _currentStep = 0;
        public int CurrentStep
        {
            get => _currentStep;
            set
            {
                if (Set(ref _currentStep, value))
                {
                    OnPropertyChanged(nameof(IsNotLastStep));
                    OnPropertyChanged(nameof(IsLastStep));
                    OnPropertyChanged(nameof(StepGuideTip));
                    RefreshStepBackgrounds();

                    (PrevStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                    (NextStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsNotLastStep => CurrentStep < 3;
        public bool IsLastStep => CurrentStep == 3;

        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        private static readonly SolidColorBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));

        public SolidColorBrush Step0Background => CurrentStep >= 0 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step1Background => CurrentStep >= 1 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step2Background => CurrentStep >= 2 ? ActiveBrush : InactiveBrush;
        public SolidColorBrush Step3Background => CurrentStep >= 3 ? ActiveBrush : InactiveBrush;

        private void RefreshStepBackgrounds()
        {
            OnPropertyChanged(nameof(Step0Background));
            OnPropertyChanged(nameof(Step1Background));
            OnPropertyChanged(nameof(Step2Background));
            OnPropertyChanged(nameof(Step3Background));
        }

        public string StepGuideTip => _strategy?.GetStepGuideTip(CurrentStep + 1);

        #endregion

        #region 命令声明

        public ICommand TriggerSampleCommand { get; }
        public ICommand AutoRunAllCommand { get; }
        public ICommand RunCalibrationCommand { get; }
        public ICommand SaveResultCommand { get; }
        public ICommand PrevStepCommand { get; }
        public ICommand NextStepCommand { get; }

        #endregion

        public CalibrationWizardViewModel(Window owner, CalibrationProfile profile = null)
        {
            _owner = owner;
            TargetProfile = profile ?? new CalibrationProfile { Name = "新建标定方案", Type = CalibrationType.NinePointHandEye };
            CalibService = new CalibrationService();

            // 动态挂载算法策略
            SelectStrategy(TargetProfile.Type);
            _strategy.InitializePoints(this);

            TriggerSampleCommand = new RelayCommand(_ => _strategy.TriggerSample(this));
            AutoRunAllCommand = new RelayCommand(_ => _strategy.AutoRunAll(this));
            RunCalibrationCommand = new RelayCommand(_ => _strategy.ExecuteCalibration(this));
            SaveResultCommand = new RelayCommand(_ => SaveResult());

            PrevStepCommand = new RelayCommand(_ => PrevStep(), _ => CurrentStep > 0);
            NextStepCommand = new RelayCommand(_ => NextStep(), _ => CurrentStep < 3);

            AppendLog($"标定向导已就绪，标定类型: {TargetProfile.Type}");
        }

        private void SelectStrategy(CalibrationType type)
        {
            switch (type)
            {
                case CalibrationType.HandEyeWithRotation:
                    _strategy = new HandEyeWithRotationCalibrationStrategy();
                    break;
                case CalibrationType.Checkerboard2D:
                    _strategy = new CheckerboardCalibrationStrategy();
                    break;
                case CalibrationType.PixelScale:
                    _strategy = new PixelScaleCalibrationStrategy();
                    break;
                default:
                    _strategy = new NinePointCalibrationStrategy();
                    break;
            }
        }

        public void ProcessCalibrationResult(dynamic res)
        {
            if (res.Success)
            {
                OutputHomMatPath = res.Data.SavedFilePath;
                CalculatedRms = res.Data.RmsError;
                AppendLog($"[计算成功] RMS 拟合误差: {CalculatedRms:F5} mm");
                MessageBox.Show($"标定计算成功！\nRMS 重投影误差: {CalculatedRms:F5} mm", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                AppendLog($"[计算失败] {res.Message}");
                MessageBox.Show("计算失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveResult()
        {
            if (TargetProfile.Type != CalibrationType.PixelScale && string.IsNullOrEmpty(OutputHomMatPath))
            {
                MessageBox.Show("尚未生成有效的标定矩阵文件！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_owner != null)
            {
                _owner.DialogResult = true;
                _owner.Close();
            }
        }

        private void PrevStep() { if (CurrentStep > 0) CurrentStep--; }
        private void NextStep() { if (CurrentStep < 3) CurrentStep++; }

        public void AppendLog(string message)
        {
            _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            OnPropertyChanged(nameof(LogText));
        }
    }
}