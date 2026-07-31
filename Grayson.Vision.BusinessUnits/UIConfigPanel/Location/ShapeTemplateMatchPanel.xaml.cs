using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Controls;
using Grayson.Vision.BusinessUnits.Location;
using Grayson.Vision.BusinessUnits.UIConfigPanel;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel.Location
{
    /// <summary>形状模板定位单元配置面板</summary>
    public partial class ShapeTemplateMatchPanel : UserControl
    {
        /// <summary>绑定的模板定位单元</summary>
        public ShapeTemplateMatchUnit BindUnit { get; }

        public ShapeTemplateMatchPanel(ShapeTemplateMatchUnit unit)
        {
            InitializeComponent();
            BindUnit = unit;
            LoadParamToUi();
            BindControlEvents();
        }

        /// <summary>加载单元参数到界面</summary>
        private void LoadParamToUi()
        {
            TxtTemplatePath.Text = BindUnit.TemplateFilePath;
            TxtMinScore.Text = BindUnit.MinScore.ToString("0.00");
            ChkEnable.IsChecked = BindUnit.Enable;
        }

        /// <summary>绑定按钮、输入框事件</summary>
        private void BindControlEvents()
        {
            // 打开文件选择框选择模板
            BtnSelectFile.Click += (s, e) =>
            {
                OpenFileDialog dialog = new OpenFileDialog
                {
                    Filter = "Halcon模板文件(*.shm)|*.shm|全部文件|*.*",
                    Title = "选择形状模板文件"
                };
                if (dialog.ShowDialog() == true)
                {
                    TxtTemplatePath.Text = dialog.FileName;
                    BindUnit.TemplateFilePath = dialog.FileName;
                }
            };

            // 匹配分数失去焦点校验范围
            TxtMinScore.LostFocus += (s, e) =>
            {
                if (double.TryParse(TxtMinScore.Text, out double score) && score >= 0 && score <= 1)
                {
                    BindUnit.MinScore = score;
                }
                else
                {
                    MessageBox.Show("匹配分数必须介于0~1之间", "参数非法", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtMinScore.Text = BindUnit.MinScore.ToString("0.00");
                }
            };

            // 手动填写路径同步
            TxtTemplatePath.LostFocus += (s, e) =>
            {
                BindUnit.TemplateFilePath = TxtTemplatePath.Text.Trim();
            };

            // 启用开关
            ChkEnable.Click += (s, e) =>
            {
                BindUnit.Enable = ChkEnable.IsChecked.Value;
            };
        }
    }
}