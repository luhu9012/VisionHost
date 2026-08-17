using System.Collections.ObjectModel;
using System.Windows.Input;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Model;


namespace Grayson.Vision.WpfUI.ViewModel
{
    public class HardwareAllocationViewModel : ViewModelBase
    {
        private readonly StationManageViewModel _parent;

        public HardwareAllocationViewModel(StationManageViewModel parent)
        {
            _parent = parent;
            AddHardwareCommand = new RelayCommand(_ => _parent.AddHardwareCommand.Execute(null), _ => _parent.SelectedStation != null);
            RemoveHardwareCommand = new RelayCommand(_ => _parent.RemoveHardwareCommand.Execute(null), _ => _parent.SelectedStation != null && _parent.SelectedHardware != null);
        }

        public StationModel SelectedStation => _parent.SelectedStation;
        public ObservableCollection<HardwareDeviceModel> HardwareDevices => SelectedStation?.HardwareDevices;
        public HardwareDeviceModel SelectedHardware => _parent.SelectedHardware;

        public ICommand AddHardwareCommand { get; }
        public ICommand RemoveHardwareCommand { get; }
    }
}
