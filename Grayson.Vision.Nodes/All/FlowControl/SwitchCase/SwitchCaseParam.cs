using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Grayson.Vision.Nodes.All.FlowControl.SwitchCase
{
    public class SwitchCaseItem : ParamBase
    {
        private string _caseValue = "Case1";
        public string CaseValue
        {
            get => _caseValue;
            set => Set(ref _caseValue, value);
        }

        private string _branchName = "Branch1";
        public string BranchName
        {
            get => _branchName;
            set => Set(ref _branchName, value);
        }

        public override string this[string columnName] => null;
    }

    public class SwitchCaseParam : ParamBase, IDynamicPortParam
    {
        private ObservableCollection<SwitchCaseItem> _caseItems = new ObservableCollection<SwitchCaseItem>();
        public ObservableCollection<SwitchCaseItem> CaseItems
        {
            get => _caseItems;
            set => Set(ref _caseItems, value);
        }

        private string _defaultBranchName = "Default";
        public string DefaultBranchName
        {
            get => _defaultBranchName;
            set
            {
                if (Set(ref _defaultBranchName, value))
                {
                    OnPropertyChanged(nameof(DefaultBranchName));
                }
            }
        }

        public SwitchCaseParam()
        {
            if (_caseItems.Count == 0)
            {
                _caseItems.Add(new SwitchCaseItem { CaseValue = "1", BranchName = "Branch1" });
                _caseItems.Add(new SwitchCaseItem { CaseValue = "2", BranchName = "Branch2" });
            }

            // 监听集合变动与子项属性变动
            SubscribeItems(_caseItems);
            _caseItems.CollectionChanged += OnCaseItemsCollectionChanged;
        }

        private void OnCaseItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (INotifyPropertyChanged item in e.OldItems)
                    item.PropertyChanged -= OnItemPropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (INotifyPropertyChanged item in e.NewItems)
                    item.PropertyChanged += OnItemPropertyChanged;
            }

            OnPropertyChanged(nameof(CaseItems));
        }

        private void SubscribeItems(ObservableCollection<SwitchCaseItem> items)
        {
            foreach (var item in items)
            {
                item.PropertyChanged -= OnItemPropertyChanged;
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }

        private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(CaseItems));
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(DefaultBranchName) && string.IsNullOrWhiteSpace(DefaultBranchName))
                    return "默认分支名称不能为空";
                return null;
            }
        }
        #endregion
        // 🌟 实现接口，将自身与 Node 的 OutputPorts 联动逻辑封装在此
        public void SyncPorts(FlowNodeBase node)
        {
            // 直接复用你之前写好的 Helper 方法
            SwitchCasePortHelper.SyncPorts(node, this);
        }
    }
}