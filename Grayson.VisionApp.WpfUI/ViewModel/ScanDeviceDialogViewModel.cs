using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Service;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class ScannedDeviceItemViewModel : ViewModelBase
    {
        public DeviceInfo RawInfo { get; }

        private bool _isSelected = true;
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public string Category => RawInfo.Category.ToString();
        public string BrandName => RawInfo.BrandName;
        public string DeviceId => RawInfo.DeviceId;

        private string _targetDeviceKey;
        public string TargetDeviceKey
        {
            get => _targetDeviceKey;
            set => Set(ref _targetDeviceKey, value);
        }

        public ScannedDeviceItemViewModel(DeviceInfo info)
        {
            RawInfo = info;
            TargetDeviceKey = $"{info.BrandName}_{info.Category}_{info.DeviceId}";
        }
    }

    public class ScanDeviceDialogViewModel : ViewModelBase
    {
        public ObservableCollection<ScannedDeviceItemViewModel> ScannedDevices { get; } = new ObservableCollection<ScannedDeviceItemViewModel>();

        public ICommand ConfirmCommand { get; }

        public ScanDeviceDialogViewModel(IEnumerable<DeviceInfo> scannedInfos)
        {
            foreach (var info in scannedInfos)
            {
                ScannedDevices.Add(new ScannedDeviceItemViewModel(info));
            }

            ConfirmCommand = new RelayCommand(OnConfirm);
        }

        private void OnConfirm(object parameter)
        {
            // 1. 筛选出勾选选中的设备
            var selectedDevices = ScannedDevices.Where(x => x.IsSelected).ToList();
            if (!selectedDevices.Any())
            {
                MessageBox.Show("请至少勾选一个需要导入的设备！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 校验通过，设置 DialogResult 并关闭弹窗
            if (parameter is Window window)
            {
                window.DialogResult = true;
                window.Close();
            }
        }
    }
}