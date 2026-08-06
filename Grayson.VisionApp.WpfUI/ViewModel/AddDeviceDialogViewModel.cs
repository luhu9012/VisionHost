using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
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
            List<string> brands;
            switch (SelectedCategory)
            {
                case DeviceCategory.Camera:
                    brands = new List<string> { "Hikvision", "Dahua", "Basler" };
                    break;
                case DeviceCategory.PLC:
                    brands = new List<string> { "Siemens", "Omron", "ModbusTCP" };
                    break;
                case DeviceCategory.MotionCard:
                    brands = new List<string> { "ZMotion", "Advantech" };
                    break;
                case DeviceCategory.LightController:
                    brands = new List<string> { "OPT", "CST" };
                    break;
                default:
                    brands = new List<string> { "Generic" };
                    break;
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