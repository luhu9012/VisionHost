//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationManagerViewModel.cs
//===================================================================================
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Service;
using Grayson.Vision.WpfUI.View;

namespace Grayson.Vision.WpfUI.ViewModel
{


 

    /// <summary>
    /// 工位跳转导航参数载体
    /// </summary>
    public class StationNavigationContext
    {
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public string DeviceId { get; set; }
    }

    public class CalibrationManagerViewModel : ViewModelBase, INavigationAware
    {
        private readonly ICalibrationService _calibService;
        private readonly ICalibrationProfileRepository _profileRepository;

        #region 绑定属性

        public ObservableCollection<CalibrationProfile> CalibrationProfiles { get; }
            = new ObservableCollection<CalibrationProfile>();

        private CalibrationProfile _selectedCalibrationProfile;
        public CalibrationProfile SelectedCalibrationProfile
        {
            get => _selectedCalibrationProfile;
            set
            {
                if (Set(ref _selectedCalibrationProfile, value))
                {
                    ExecuteTestMap();
                }
            }
        }

        private double _testPixelX;
        public double TestPixelX
        {
            get => _testPixelX;
            set { if (Set(ref _testPixelX, value)) ExecuteTestMap(); }
        }

        private double _testPixelY;
        public double TestPixelY
        {
            get => _testPixelY;
            set { if (Set(ref _testPixelY, value)) ExecuteTestMap(); }
        }

        private string _testWorldResult = "X: 0.000, Y: 0.000";
        public string TestWorldResult
        {
            get => _testWorldResult;
            set => Set(ref _testWorldResult, value);
        }

        #endregion

        #region 命令

        public ICommand NewProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand OpenWizardCommand { get; }
        public ICommand SaveToRecipeCommand { get; }
        public ICommand TestMapCommand { get; }

        #endregion

        public CalibrationManagerViewModel(ICalibrationService calibService = null)
        {
            _calibService = calibService ?? new CalibrationService();
            _profileRepository = StorageFactory.CreateCalibrationProfileRepository();

            NewProfileCommand = new RelayCommand(p =>
            {
                CalibrationType type = CalibrationType.NinePointHandEye;
                if (p is CalibrationType t)
                {
                    type = t;
                }
                CreateNewProfile(type);
            });

            DeleteProfileCommand = new RelayCommand(_ => DeleteSelectedProfile(), _ => SelectedCalibrationProfile != null);
            OpenWizardCommand = new RelayCommand(_ => OpenWizard());
            SaveToRecipeCommand = new RelayCommand(_ => SaveToRecipe());
            TestMapCommand = new RelayCommand(_ => ExecuteTestMap());

            LoadProfiles();
        }

        #region INavigationAware 接口实现

        public void OnNavigatedTo(object parameter)
        {
            if (parameter is StationNavigationContext context)
            {
                var existing = CalibrationProfiles.FirstOrDefault(p => p.BoundStationCode == context.StationCode);
                if (existing != null)
                {
                    SelectedCalibrationProfile = existing;
                }
                else
                {
                    var newProfile = new CalibrationProfile
                    {
                        Name = $"{context.StationName}_九点手眼标定",
                        Type = CalibrationType.NinePointHandEye,
                        BoundStationCode = context.StationCode,
                        BoundDeviceId = context.DeviceId,
                        BindingInfo = $"工位: {context.StationName} ({context.StationCode})"
                    };
                    CalibrationProfiles.Add(newProfile);
                    SelectedCalibrationProfile = newProfile;
                    SaveProfileToRepository(newProfile);
                }
            }
        }

        public void OnNavigatedFrom()
        {
        }

        #endregion

        private void LoadProfiles()
        {
            CalibrationProfiles.Clear();

            try
            {
                foreach (var po in _profileRepository.GetAll().OrderBy(x => x.ProfileName))
                {
                    if (po.Model != null)
                    {
                        CalibrationProfiles.Add(po.Model);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载标定方案失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (CalibrationProfiles.Count == 0)
            {
                CalibrationProfiles.Add(new CalibrationProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "工位1_Top相机九点标定",
                    Type = CalibrationType.NinePointHandEye,
                    BindingInfo = "工位: ST_01 / 平台1",
                    IsCalibrated = false
                });
            }

            SelectedCalibrationProfile = CalibrationProfiles.FirstOrDefault();
        }

        /// <summary>
        /// 新建标定方案（支持传入标定类型）
        /// </summary>
        private void CreateNewProfile(CalibrationType selectedType = CalibrationType.NinePointHandEye)
        {
            string stationName = SelectedCalibrationProfile != null ? SelectedCalibrationProfile.BoundStationCode : "ST_01";

            var newProfile = new CalibrationProfile
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = GetDefaultNameForType(selectedType, stationName),
                Type = selectedType,
                CameraId = "Cam_01",
                AxisId = "Axis_X",
                UpdatedAt = DateTime.Now
            };

            CalibrationProfiles.Add(newProfile);
            SelectedCalibrationProfile = newProfile;
            SaveProfileToRepository(newProfile);
        }

        private string GetDefaultNameForType(CalibrationType type, string station)
        {
            switch (type)
            {
                case CalibrationType.NinePointHandEye:
                    return $"{station}_九点手眼标定";
                case CalibrationType.HandEyeWithRotation:
                    return $"{station}_12/15点含旋转手眼标定";
                case CalibrationType.Checkerboard2D:
                    return $"{station}_2D棋盘格标定";
                case CalibrationType.CameraLensDistortion:
                    return $"{station}_相机畸变内参标定";
                case CalibrationType.PixelScale:
                    return $"{station}_像素比例标定";
                default:
                    return $"{station}_标定方案";
            }
        }

        private void DeleteSelectedProfile()
        {
            if (SelectedCalibrationProfile == null) return;

            var result = MessageBox.Show(
                $"确定要删除标定方案【{SelectedCalibrationProfile.Name}】吗？\n删除后绑定该标定的业务节点可能无法正常工作！",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var profileToRemove = SelectedCalibrationProfile;
                int currentIndex = CalibrationProfiles.IndexOf(profileToRemove);

                if (!string.IsNullOrWhiteSpace(profileToRemove.Id))
                {
                    _profileRepository.Delete(profileToRemove.Id);
                }

                CalibrationProfiles.Remove(profileToRemove);

                if (CalibrationProfiles.Count > 0)
                {
                    SelectedCalibrationProfile = CalibrationProfiles[Math.Min(currentIndex, CalibrationProfiles.Count - 1)];
                }
                else
                {
                    SelectedCalibrationProfile = null;
                }
            }
        }

        private void OpenWizard()
        {
            if (SelectedCalibrationProfile == null) return;

            try
            {
                var win = new CalibrationWizardWindow(SelectedCalibrationProfile)
                {
                    Owner = Application.Current.MainWindow
                };

                if (win.ShowDialog() == true && win.DataContext is CalibrationWizardViewModel wizardVm)
                {
                    SelectedCalibrationProfile = wizardVm.TargetProfile;
                    SelectedCalibrationProfile.IsCalibrated = true;
                    SelectedCalibrationProfile.RmsError = wizardVm.CalculatedRms;
                    SelectedCalibrationProfile.HomMatFilePath = wizardVm.OutputHomMatPath;
                    SaveProfileToRepository(SelectedCalibrationProfile);
                    OnPropertyChanged(nameof(SelectedCalibrationProfile));
                    ExecuteTestMap();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开标定向导失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExecuteTestMap()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                TestWorldResult = "未选择有效标定文件";
                return;
            }

            var res = _calibService.MapPixelToWorld(SelectedCalibrationProfile.HomMatFilePath, TestPixelX, TestPixelY);
            if (res.Success)
            {
                TestWorldResult = $"X: {res.Data.WorldX:F3}, Y: {res.Data.WorldY:F3}";
            }
            else
            {
                TestWorldResult = "映射失败: " + res.Message;
            }
        }

        private void SaveToRecipe()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                MessageBox.Show("当前没有有效的标定矩阵可应用！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", "Default", "Calib");
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string targetFile = Path.Combine(targetDir, $"{SelectedCalibrationProfile.Name}_HandEye.tup");

            var saveRes = _calibService.SaveHomMatFile(SelectedCalibrationProfile.HomMatFilePath, targetFile);
            if (saveRes.Success)
            {
                SelectedCalibrationProfile.HomMatFilePath = targetFile;
                SaveProfileToRepository(SelectedCalibrationProfile);
                MessageBox.Show($"标定矩阵已成功保存并应用：\n{targetFile}", "应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"保存矩阵失败：" + saveRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveProfileToRepository(CalibrationProfile profile)
        {
            if (profile == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.Id))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            var po = new CalibrationProfilePo
            {
                Id = profile.Id,
                ProfileName = profile.Name,
                CalibrationType = profile.Type,
                BoundStationCode = profile.BoundStationCode,
                BoundDeviceId = profile.BoundDeviceId,
                IsCalibrated = profile.IsCalibrated,
                Model = profile
            };

            var existing = _profileRepository.GetById(po.Id) ?? _profileRepository.GetByName(profile.Name);
            if (existing == null)
            {
                _profileRepository.Insert(po);
            }
            else
            {
                _profileRepository.Update(po);
            }
        }
    }
}
