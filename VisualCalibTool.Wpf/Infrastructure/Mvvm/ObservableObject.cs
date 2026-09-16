using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VisualCalibTool.Infrastructure.Mvvm
{
    /// <summary>
    /// 本工具自持的 MVVM 基础设施（不依赖主项目的 ViewModelBase）。
    /// 目标：整个工具体积小、可独立搬运，因此凡属"通用基础设施"的一律自己带一份。
    /// </summary>
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        /// <summary>赋值并在变化时通知。返回是否真的发生了变化。</summary>
        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
