using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.Grab;
using Grayson.Vision.BusinessUnits.UIConfigPanel;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.Grab
{
    // 核心修复：标准WPF分部类，父类固定UserControl，无任何自定义继承
    public partial class CameraGrabUnitPanel : UserControl
    {
        /// <summary>绑定的业务单元本体</summary>
        public CameraGrabUnit BindUnit { get; }

        public CameraGrabUnitPanel(CameraGrabUnit unit)
        {
            InitializeComponent();
            BindUnit = unit;
            LoadCurrentValueToUi();
            BindEvent();
            // 打开面板时自动读取全局硬件列表填充下拉
            RefreshCameraComboBox();
        }

        /// <summary>UI加载单元现有参数</summary>
        private void LoadCurrentValueToUi()
        {
            CbxCameraDeviceKey.Text = BindUnit.CameraDeviceKey;
            TxtGrabTimeout.Text = BindUnit.GrabTimeoutMs.ToString();
            ChkUnitEnable.IsChecked = BindUnit.Enable;
        }

        /// <summary>刷新相机下拉，从静态全局取硬件Key</summary>
        public void RefreshCameraComboBox()
        {
            CbxCameraDeviceKey.Items.Clear();
            foreach (var key in PanelHardwareHelper.GlobalCameraKeys)
            {
                CbxCameraDeviceKey.Items.Add(key);
            }
        }

        /// <summary>绑定控件交互事件</summary>
        private void BindEvent()
        {
            CbxCameraDeviceKey.SelectionChanged += (s, e) =>
            {
                if (CbxCameraDeviceKey.SelectedItem != null)
                    BindUnit.CameraDeviceKey = CbxCameraDeviceKey.SelectedItem.ToString();
            };

            TxtGrabTimeout.LostFocus += (s, e) =>
            {
                if (int.TryParse(TxtGrabTimeout.Text.Trim(), out int num) && num > 100)
                {
                    BindUnit.GrabTimeoutMs = num;
                }
                else
                {
                    MessageBox.Show("超时必须是大于100的数字", "参数非法", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtGrabTimeout.Text = BindUnit.GrabTimeoutMs.ToString();
                }
            };

            ChkUnitEnable.Click += (s, e) =>
            {
                BindUnit.Enable = ChkUnitEnable.IsChecked ?? true;
            };
        }
    }
}



//宿主打开任意单元配置面板前，执行一次全局硬件注入：
//    // 宿主拿到当前全部相机、PLC的DeviceKey集合
//List<string> allCameras = hardwareManager.GetAllCameraDeviceKeys();
//List<string> allPlcs = hardwareManager.GetAllPlcDeviceKeys();
//// 全局静态赋值，所有面板可访问
//PanelHardwareHelper.SetAllHardwareList(allCameras, allPlcs);