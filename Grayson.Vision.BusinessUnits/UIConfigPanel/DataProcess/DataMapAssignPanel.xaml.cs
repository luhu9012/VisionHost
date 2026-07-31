using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.DataProcess;
using Grayson.Vision.BusinessUnits.UIConfigPanel;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.DataProcess
{
    /// <summary>上下文变量赋值映射配置面板</summary>
    public partial class DataMapAssignPanel : UserControl
    {
        /// <summary>绑定的变量赋值单元</summary>
        public DataMapAssignUnit BindUnit { get; }

        public DataMapAssignPanel(DataMapAssignUnit unit)
        {
            InitializeComponent();
            BindUnit = unit;
            LoadUiFromBindUnit();
            BindAllUiEvents();
        }

        /// <summary>单元参数同步到界面控件</summary>
        private void LoadUiFromBindUnit()
        {
            RdoSrcContext.IsChecked = !BindUnit.UseConstant;
            RdoSrcConst.IsChecked = BindUnit.UseConstant;

            TxtSourceValue.Text = BindUnit.UseConstant
                ? BindUnit.ConstantValue?.ToString()
                : BindUnit.SourceContextKey;

            TxtTargetKey.Text = BindUnit.TargetContextKey;
            ChkEnable.IsChecked = BindUnit.Enable;

            RefreshSourceLabelText();
        }

        /// <summary>根据来源类型修改左侧标签文字</summary>
        private void RefreshSourceLabelText()
        {
            if (BindUnit.UseConstant)
                TxtSourceLabel.Text = "固定常量值：";
            else
                TxtSourceLabel.Text = "源上下文Key：";
        }

        /// <summary>数据源单选切换事件</summary>
        private void SourceModeChanged(object sender, RoutedEventArgs e)
        {
            BindUnit.UseConstant = RdoSrcConst.IsChecked.Value;
            RefreshSourceLabelText();
        }

        /// <summary>绑定所有输入框、勾选框事件</summary>
        private void BindAllUiEvents()
        {
            // 来源输入框失去焦点回写数据
            TxtSourceValue.LostFocus += (s, e) =>
            {
                string inputText = TxtSourceValue.Text.Trim();
                if (BindUnit.UseConstant)
                {
                    BindUnit.ConstantValue = inputText;
                }
                else
                {
                    BindUnit.SourceContextKey = inputText;
                }
            };

            // 目标Key修改
            TxtTargetKey.LostFocus += (s, e) =>
            {
                BindUnit.TargetContextKey = TxtTargetKey.Text.Trim();
            };

            // 启用开关
            ChkEnable.Click += (s, e) =>
            {
                BindUnit.Enable = ChkEnable.IsChecked.Value;
            };
        }
    }
}