using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.PlcIo;
using Grayson.Vision.BusinessUnits.UIConfigPanel;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.PlcIo
{
    /// <summary>PLC布尔点位写入配置面板，读取全局静态PLC硬件列表</summary>
    public partial class PlcWriteBitPanel : UserControl
    {
        /// <summary>绑定的PLC写入单元</summary>
        public PlcWriteBitUnit BindUnit { get; }

        public PlcWriteBitPanel(PlcWriteBitUnit unit)
        {
            InitializeComponent();
            BindUnit = unit;
            LoadAllConfigToUi();
            BindAllEvents();
            // 打开面板自动刷新PLC下拉框
            RefreshPlcComboBox();
        }

        /// <summary>从单元加载所有参数到界面</summary>
        private void LoadAllConfigToUi()
        {
            CbxPlcKey.Text = BindUnit.PlcDeviceKey;
            TxtPlcAddr.Text = BindUnit.PlcAddress;

            RdoFixed.IsChecked = !BindUnit.UseContextBool;
            RdoContext.IsChecked = BindUnit.UseContextBool;

            ChkFixedValue.IsChecked = BindUnit.FixedWriteValue;
            TxtContextKey.Text = BindUnit.ContextBoolKey;
            ChkEnable.IsChecked = BindUnit.Enable;

            RefreshPanelVisibility();
        }

        /// <summary>根据数据源模式切换面板显示隐藏</summary>
        private void RefreshPanelVisibility()
        {
            if (RdoFixed.IsChecked == true)
            {
                PanelFixed.Visibility = Visibility.Visible;
                PanelContext.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanelFixed.Visibility = Visibility.Collapsed;
                PanelContext.Visibility = Visibility.Visible;
            }
        }

        /// <summary>切换数据源单选按钮触发</summary>
        private void RadioSourceModeChanged(object sender, RoutedEventArgs e)
        {
            BindUnit.UseContextBool = RdoContext.IsChecked.Value;
            RefreshPanelVisibility();
        }

        /// <summary>从静态帮助类刷新PLC下拉选项</summary>
        private void RefreshPlcComboBox()
        {
            CbxPlcKey.Items.Clear();
            foreach (string plcKey in PanelHardwareHelper.GlobalPlcKeys)
            {
                CbxPlcKey.Items.Add(plcKey);
            }
        }

        /// <summary>绑定全部控件交互</summary>
        private void BindAllEvents()
        {
            // PLC下拉选择赋值
            CbxPlcKey.SelectionChanged += (s, e) =>
            {
                if (CbxPlcKey.SelectedItem != null)
                    BindUnit.PlcDeviceKey = CbxPlcKey.SelectedItem.ToString();
            };

            // PLC地址手动输入
            TxtPlcAddr.LostFocus += (s, e) => BindUnit.PlcAddress = TxtPlcAddr.Text.Trim();

            // 固定布尔值勾选
            ChkFixedValue.Click += (s, e) => BindUnit.FixedWriteValue = ChkFixedValue.IsChecked.Value;

            // 上下文Key填写
            TxtContextKey.LostFocus += (s, e) => BindUnit.ContextBoolKey = TxtContextKey.Text.Trim();

            // 单元启用开关
            ChkEnable.Click += (s, e) => BindUnit.Enable = ChkEnable.IsChecked.Value;
        }
    }
}