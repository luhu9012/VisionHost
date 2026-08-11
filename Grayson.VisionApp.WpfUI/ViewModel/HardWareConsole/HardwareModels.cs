using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{


    public class AxisInfoModel
    {
        public int AxisIndex { get; set; }
        public string AxisName { get; set; }
    }

    public enum IoType
    {
        Input,  // DI 数字量输入
        Output  // DO 数字量输出
    }

    public class IoPointModel : ViewModelBase
    {
        public int ChannelIndex { get; set; }
        public IoType Type { get; set; } = IoType.Input;
        public string Name { get; set; }
        public string Address { get; set; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    OnPropertyChanged(nameof(StateText));
                }
            }
        }

        public string StateText => IsActive ? "ON" : "OFF";
    }
}
