using Grayson.Vision.Contracts.Infrastructure.Mvvm; 
using System.ComponentModel;

namespace Grayson.Vision.Nodes.Common
{
    public abstract class ParamBase : ViewModelBase, IDataErrorInfo
    {
        public virtual string Error => null;
        public virtual string this[string columnName] => null;
    }
}