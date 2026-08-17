using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Model;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class SelectableHardwareModel : ViewModelBase
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public HardwareDeviceModel Device { get; set; }
    }

    public class HardwareSelectViewModel : ViewModelBase
    {
        public ObservableCollection<SelectableHardwareModel> CandidateDevices { get; set; }

        private bool _isSelectAll;
        public bool IsSelectAll
        {
            get => _isSelectAll;
            set
            {
                if (Set(ref _isSelectAll, value))
                {
                    foreach (var item in CandidateDevices)
                    {
                        item.IsSelected = value;
                    }
                }
            }
        }

        public RelayCommand ConfirmCommand { get; }
        public RelayCommand CancelCommand { get; }

        public bool? DialogResult { get; private set; }

        public HardwareSelectViewModel(IEnumerable<HardwareDeviceModel> candidateList)
        {
            CandidateDevices = new ObservableCollection<SelectableHardwareModel>(
                candidateList.Select(d => new SelectableHardwareModel { IsSelected = false, Device = d })
            );

            ConfirmCommand = new RelayCommand(_ => Confirm());
            CancelCommand = new RelayCommand(_ => Cancel());
        }

        public List<HardwareDeviceModel> GetSelectedDevices()
        {
            return CandidateDevices
                .Where(x => x.IsSelected)
                .Select(x => x.Device)
                .ToList();
        }

        private void Confirm()
        {
            DialogResult = true;
            OnRequestClose();
        }

        private void Cancel()
        {
            DialogResult = false;
            OnRequestClose();
        }

        public event System.Action RequestClose;
        private void OnRequestClose() => RequestClose?.Invoke();
    }
}