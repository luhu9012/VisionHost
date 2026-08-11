using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class AddDeviceDialogViewModel : ViewModelBase
    {
        public List<DeviceCategory> Categories { get; } = Enum.GetValues(typeof(DeviceCategory)).Cast<DeviceCategory>().ToList();

        private DeviceCategory _selectedCategory = DeviceCategory.Camera;
        public DeviceCategory SelectedCategory
        {
            get => _selectedCategory;
            set
            {
                if (Set(ref _selectedCategory, value))
                {
                    UpdateAvailableBrands();
                }
            }
        }

        private List<string> _availableBrands = new List<string>();
        public List<string> AvailableBrands
        {
            get => _availableBrands;
            set => Set(ref _availableBrands, value);
        }

        private string _selectedBrand;
        public string SelectedBrand
        {
            get => _selectedBrand;
            set => Set(ref _selectedBrand, value);
        }

        private string _deviceKey = "PLC_01";
        public string DeviceKey
        {
            get => _deviceKey;
            set => Set(ref _deviceKey, value);
        }

        private string _connectionString = "Protocol=SiemensS7;IP=192.168.1.100;Port=102;Extra=S1200";
        public string ConnectionString
        {
            get => _connectionString;
            set => Set(ref _connectionString, value);
        }

        public ICommand SaveCommand { get; }

        public AddDeviceDialogViewModel()
        {
            SaveCommand = new RelayCommand(OnSave);
            UpdateAvailableBrands();
        }

        private void UpdateAvailableBrands()
        {
            var brands = new List<string>();

            // 1. 从当前已加载的插件池获取对应 Category 的所有专有品牌
            var loadedPlugins = DevicePoolManager.Instance.GetAllPlugins()
                .Where(p => p.Category == SelectedCategory)
                .Select(p => p.BrandName)
                .ToList();

            brands.AddRange(loadedPlugins);

            // 2. 动态追加通用协议/网关选项（确保手动添加时随时可选）
            if (!brands.Contains("UniversalProtocol"))
            {
                brands.Add("UniversalProtocol");
            }

            AvailableBrands = brands;
            SelectedBrand = AvailableBrands.FirstOrDefault();
        }

        private void OnSave(object parameter)
        {
            if (parameter is Window window)
            {
                window.DialogResult = true;
                window.Close();
            }
        }
    }
}