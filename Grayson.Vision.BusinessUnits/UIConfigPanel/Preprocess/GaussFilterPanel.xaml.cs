using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.Preprocess;
using Grayson.Vision.BusinessUnits.UIConfigPanel;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.Preprocess
{
    /// <summary>高斯滤波单元配置面板，父类固定UserControl，无自定义继承</summary>
    public partial class GaussFilterPanel : UserControl
    {
        /// <summary>绑定的高斯滤波业务单元实例</summary>
        public GaussFilterUnit BindUnit { get; }

        public GaussFilterPanel(GaussFilterUnit unit)
        {
            InitializeComponent();
            BindUnit = unit;
            LoadUiValueFromUnit();
            BindAllControlEvents();
        }

        /// <summary>将单元现有参数赋值到界面控件</summary>
        private void LoadUiValueFromUnit()
        {
            CbxKernelSize.Text = BindUnit.KernelSize.ToString();
            ChkEnable.IsChecked = BindUnit.Enable;
        }

        /// <summary>绑定所有控件交互事件</summary>
        private void BindAllControlEvents()
        {
            // 卷积核选择变更，同步到单元属性
            CbxKernelSize.SelectionChanged += (sender, args) =>
            {
                if (int.TryParse(CbxKernelSize.SelectedItem.ToString(), out int kernelVal))
                {
                    BindUnit.KernelSize = kernelVal;
                }
            };

            // 启用开关勾选变更
            ChkEnable.Click += (sender, args) =>
            {
                BindUnit.Enable = ChkEnable.IsChecked.Value;
            };
        }
    }
}