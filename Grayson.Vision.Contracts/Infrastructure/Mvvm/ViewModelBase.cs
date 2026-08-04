using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Grayson.Vision.Contracts.Infrastructure.Mvvm
{
    /// <summary>
    /// 抽象 ViewModel 基类，提供属性变更通知能力
    /// </summary>
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    /// <summary>
    /// 通用、无 UI 依赖的 RelayCommand，可用于 Core 层、WinForm、WPF、MAUI 等
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Predicate<object> _canExecute;

        // 标准的事件定义，脱离 CommandManager
        public event EventHandler CanExecuteChanged;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
     : this(_ => execute(), canExecute == null ? (Predicate<object>)null : _ => canExecute())
        {
        }

        public RelayCommand(Action<object> execute, Predicate<object> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute(parameter);
        }

        public void Execute(object parameter)
        {
            _execute(parameter);
        }

        /// <summary>
        /// 当决定按钮是否可用的状态发生变化时，由业务代码主动调用此方法刷新 UI
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }


    /// 泛型 RelayCommand，支持参数安全转换与 CanExecute 条件控制
    /// 完全脱离 WPF 依赖，可直接放 Core 核心库
    /// </summary>
    /// <typeparam name="T">命令参数类型</typeparam>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Predicate<T> _canExecute;

        public event EventHandler CanExecuteChanged;

        /// <summary>
        /// 构造函数（默认始终可执行）
        /// </summary>
        public RelayCommand(Action<T> execute) : this(execute, null)
        {
        }

        /// <summary>
        /// 构造函数（带条件控制）
        /// </summary>
        public RelayCommand(Action<T> execute, Predicate<T> canExecute)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        /// <summary>
        /// 判断命令是否可执行（安全校验参数）
        /// </summary>
        public bool CanExecute(object parameter)
        {
            if (_canExecute == null) return true;

            if (TryCastParameter(parameter, out T val))
            {
                return _canExecute(val);
            }

            return false;
        }

        /// <summary>
        /// 执行命令（安全类型转换）
        /// </summary>
        public void Execute(object parameter)
        {
            if (CanExecute(parameter))
            {
                if (TryCastParameter(parameter, out T val))
                {
                    _execute(val);
                }
            }
        }

        /// <summary>
        /// 手动刷新命令的可执行状态
        /// </summary>
        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 安全强转参数类型，防止 WPF/WinForm 传入 null 或类型不匹配时崩溃
        /// </summary>
        private static bool TryCastParameter(object parameter, out T result)
        {
            if (parameter == null)
            {
                // 如果 T 是值类型（如 int, double），null 无法转换；如果是引用类型，null 是合法的
                if (default(T) == null)
                {
                    result = default;
                    return true;
                }

                result = default;
                return false;
            }

            if (parameter is T typedParam)
            {
                result = typedParam;
                return true;
            }

            // 处理可能发生的万能类型转换（例如 UI 传了 string "123"，但 T 是 int）
            try
            {
                result = (T)Convert.ChangeType(parameter, typeof(T));
                return true;
            }
            catch
            {
                result = default;
                return false;
            }
        }
    }
}