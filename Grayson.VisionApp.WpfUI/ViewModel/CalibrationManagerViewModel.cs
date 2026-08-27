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
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.Repository.Services;
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

        private int _selectedScopeIndex;
        /// <summary>
        /// 发布目标域：0=当前设备(Device) 1=工位全局(Workstation) 2=特定配方(Recipe)
        /// </summary>
        public int SelectedScopeIndex
        {
            get => _selectedScopeIndex;
            set => Set(ref _selectedScopeIndex, value);
        }

        #endregion

        #region 命令

        public ICommand NewProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand OpenWizardCommand { get; }
        public ICommand SaveToDeviceCommand { get; }
        public ICommand TestMapCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand ExportCommand { get; }

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
            SaveToDeviceCommand = new RelayCommand(_ => SaveToDevice());
            TestMapCommand = new RelayCommand(_ => ExecuteTestMap());
            ImportCommand = new RelayCommand(_ => ImportMatrixFile(), _ => SelectedCalibrationProfile != null);
            ExportCommand = new RelayCommand(_ => ExportMatrixFile(), _ => SelectedCalibrationProfile != null);

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

        private void SaveToDevice()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                MessageBox.Show("当前没有有效的标定矩阵可应用！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string targetDir = GetScopeTargetDirectory();
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string targetFile = Path.Combine(targetDir, $"{SelectedCalibrationProfile.Name}_HandEye.tup");

            // ⚠ 关键修复 1：首次应用成功后 HomMatFilePath 已指向目标文件，
            // 重复点击会变成 File.Copy(A, A)——Windows 返回共享冲突，
            // 表现为"文件正由另一进程使用"（其实是自己复制自己）。这里做幂等处理。
            string sourceFile = SelectedCalibrationProfile.HomMatFilePath;
            bool copied = false;
            if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
            {
                copied = true; // 源即目标：文件已在位，无需复制
            }
            else
            {
                var saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                if (!saveRes.Success)
                {
                    // ⚠ 关键修复 2：目标文件可能正被在线校验/流程节点的 ReadTuple 短暂读取，
                    // 覆盖时偶发占用冲突——等待后重试一次
                    System.Threading.Thread.Sleep(300);
                    saveRes = _calibService.SaveHomMatFile(sourceFile, targetFile);
                }

                if (!saveRes.Success)
                {
                    MessageBox.Show("保存矩阵失败：" + saveRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                copied = true;
            }

            if (copied)
            {
                // 更新模型（CalibrationProfile 自带 INPC，属性赋值会触发子绑定刷新）
                SelectedCalibrationProfile.HomMatFilePath = targetFile;
                SelectedCalibrationProfile.UpdatedAt = DateTime.Now;
                SaveProfileToRepository(SelectedCalibrationProfile);

                // ⚠ 关键修复 3：发布到"特定配方"时，同步写入配方的标定数据路径，
                // 否则配方管理界面的"标定数据路径"字段永远是空的
                string recipeNote = string.Empty;
                if (SelectedScopeIndex == 2)
                {
                    recipeNote = SyncCalibrationPathToActiveRecipe(targetFile);
                }

                // 强制刷新所有 SelectedCalibrationProfile.* 绑定（KPI 卡片、标题等）
                OnPropertyChanged(nameof(SelectedCalibrationProfile));
                // 刷新左侧列表项（绿点/Tag 等以同一实例为源的绑定）
                RefreshProfileListItem(SelectedCalibrationProfile);
                // 立即用新矩阵路径重算在线校验结果
                ExecuteTestMap();

                MessageBox.Show(
                    $"标定矩阵已成功保存并应用：\n{targetFile}\n{recipeNote}",
                    "应用成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        /// <summary>
        /// 发布到"特定配方"域时，将矩阵路径回写到当前激活配方的
        /// ProcessParameters.CalibrationDataPath 并持久化，使配方管理界面可见。
        /// </summary>
        private string SyncCalibrationPathToActiveRecipe(string matrixFilePath)
        {
            try
            {
                var storage = RecipeStorageFactory.CreateRecipeStorageService();
                var recipe = storage.GetAllRecipes().FirstOrDefault(r => r.IsActive);
                if (recipe == null)
                {
                    return "\n⚠ 未找到激活配方，矩阵路径未写入配方（仅保存到磁盘）。";
                }

                if (recipe.ProcessParameters == null)
                {
                    recipe.ProcessParameters = new ProcessParameterSet
                    {
                        ParameterSetId = Guid.NewGuid().ToString("N"),
                        Name = recipe.RecipeName,
                        ProductCategory = recipe.ProductCategory
                    };
                }

                recipe.ProcessParameters.CalibrationDataPath = matrixFilePath;
                if (storage.SaveRecipe(recipe))
                {
                    return $"\n已同步到激活配方【{recipe.RecipeName}】的标定数据路径。";
                }
                return "\n⚠ 配方保存失败，标定数据路径未写入配方。";
            }
            catch (Exception ex)
            {
                return $"\n⚠ 写入配方失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 根据发布目标域 (SelectedScopeIndex) 计算矩阵文件的目标目录。
        /// 三个作用域目录互相独立，避免"设备"与"配方"写同一处导致选项形同虚设：
        ///   Device      → Recipes\Devices\{设备ID}\Calib      （本机设备级，所有配方共享）
        ///   Workstation → Recipes\Workstations\{工位码}\Calib （工位全局，跨配方跨设备）
        ///   Recipe      → Recipes\{配方编号}\Calib            （随配方走，并回写配方标定数据路径）
        /// </summary>
        private string GetScopeTargetDirectory()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            switch (SelectedScopeIndex)
            {
                case 1: // 工位全局 (Workstation)
                    string station = string.IsNullOrWhiteSpace(SelectedCalibrationProfile?.BoundStationCode)
                        ? "Workstation"
                        : SelectedCalibrationProfile.BoundStationCode;
                    return Path.Combine(baseDir, "Recipes", "Workstations", station, "Calib");

                case 2: // 特定配方 (Recipe)——落到激活配方的专属目录
                    string recipeCode = GetActiveRecipeCode() ?? "Default";
                    return Path.Combine(baseDir, "Recipes", recipeCode, "Calib");

                default: // 当前设备 (Device)
                    string device = string.IsNullOrWhiteSpace(SelectedCalibrationProfile?.BoundDeviceId)
                        ? "Default"
                        : SelectedCalibrationProfile.BoundDeviceId;
                    return Path.Combine(baseDir, "Recipes", "Devices", device, "Calib");
            }
        }

        /// <summary>获取当前激活配方的编号（无激活配方时返回 null）</summary>
        private string GetActiveRecipeCode()
        {
            try
            {
                var storage = RecipeStorageFactory.CreateRecipeStorageService();
                var recipe = storage.GetAllRecipes().FirstOrDefault(r => r.IsActive);
                return string.IsNullOrWhiteSpace(recipe?.RecipeCode)
                    ? recipe?.RecipeId
                    : recipe.RecipeCode;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 让 ListBox 中该 Profile 的行模板重新求值（名称等非 INPC 属性或外部替换场景）
        /// </summary>
        private void RefreshProfileListItem(CalibrationProfile profile)
        {
            if (profile == null) return;
            int index = CalibrationProfiles.IndexOf(profile);
            if (index >= 0)
            {
                CalibrationProfiles[index] = profile;
            }
        }

        /// <summary>
        /// 导入外部标定矩阵文件 (.tup) 到当前选中方案
        /// </summary>
        private void ImportMatrixFile()
        {
            if (SelectedCalibrationProfile == null) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入标定矩阵文件",
                Filter = "标定矩阵文件 (*.tup)|*.tup|所有文件 (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
            {
                SelectedCalibrationProfile.HomMatFilePath = dlg.FileName;
                SelectedCalibrationProfile.IsCalibrated = true;
                SelectedCalibrationProfile.UpdatedAt = DateTime.Now;
                SaveProfileToRepository(SelectedCalibrationProfile);

                OnPropertyChanged(nameof(SelectedCalibrationProfile));
                ExecuteTestMap();
            }
        }

        /// <summary>
        /// 导出当前方案的标定矩阵文件到指定位置
        /// </summary>
        private void ExportMatrixFile()
        {
            if (SelectedCalibrationProfile == null || string.IsNullOrEmpty(SelectedCalibrationProfile.HomMatFilePath))
            {
                MessageBox.Show("当前方案没有可导出的标定矩阵！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出标定矩阵文件",
                Filter = "标定矩阵文件 (*.tup)|*.tup",
                FileName = $"{SelectedCalibrationProfile.Name}_HandEye.tup"
            };

            if (dlg.ShowDialog() == true)
            {
                var res = _calibService.SaveHomMatFile(SelectedCalibrationProfile.HomMatFilePath, dlg.FileName);
                if (res.Success)
                {
                    MessageBox.Show($"标定矩阵已导出：\n{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("导出矩阵失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
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
