using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Wpf.Controls;
using Grayson.Vision.Nodes.Common;
using Grayson.Vison.FlowEdit.ViewModels;

namespace Grayson.Vison.FlowEdit.Views
{
    /// <summary>
    /// NodePropertyWindow.xaml 的交互逻辑
    /// 左列：参数模板（现有机制不变）；右列：实时预览视图窗口（仅 SupportsPreview 节点展开）。
    /// 预览绘制不走本窗口——适配器把控件包装成 IFlowPreviewContext 注入引擎，
    /// 节点 Executor 执行时"走一步画一步"（与标定管理同一范式）。
    /// </summary>
    public partial class NodePropertyWindow : Window
    {
        public FlowVm ParentFlowVm { get; set; }

        /// <summary>预览适配器（只在代码后台创建，绝不进入 XAML —— MC1000 规避铁律）</summary>
        private HalconDisplayContextAdapter _previewAdapter;

        /// <summary>预览是否已挂接到 FlowVm（Detach 的配对标记）</summary>
        private bool _previewAttached;

        /// <summary>本窗口是否为支持预览的节点（决定布局与窗口宽度）</summary>
        private bool _supportsPreview;

        public NodePropertyWindow()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => UpdatePreviewLayout();
            Loaded += (s, e) => AttachPreview();
        }

        /// <summary>
        /// 按节点 Param 声明（ParamBase.SupportsPreview）切换两列/单列布局。
        /// 不支持预览的节点（PLC 读写、运动控制等）右列宽 0，窗口维持原尺寸，零视觉回归。
        /// </summary>
        private void UpdatePreviewLayout()
        {
            _supportsPreview = (DataContext as FlowNodeBase)?.ParameterModel is ParamBase pb && pb.SupportsPreview;

            if (_supportsPreview)
            {
                PreviewColumn.Width = new GridLength(1.7, GridUnitType.Star);
                if (Width < 980) Width = 980;
                if (Height < 640) Height = 640;
            }
            else
            {
                PreviewColumn.Width = new GridLength(0);
                if (Width > 520) Width = 520;
            }
        }

        /// <summary>
        /// 窗口加载后装配预览链路：适配器包裹预览控件 → 注入 FlowVm（经 WorkerClient/引擎
        /// 传到 NodeExecutionContext.Preview）→ 订阅参数变化防抖重跑 → 首帧立即执行一次。
        /// </summary>
        private void AttachPreview()
        {
            if (!_supportsPreview || ParentFlowVm == null || _previewAttached) return;

            var node = DataContext as FlowNodeBase;
            if (node == null) return;

            _previewAdapter = new HalconDisplayContextAdapter(PreviewHost);
            ParentFlowVm.OnPreviewExecuted += HidePreviewHint;
            ParentFlowVm.AttachPreview(_previewAdapter, node);
            _previewAttached = true;
        }

        private void HidePreviewHint()
        {
            if (PreviewHint != null) PreviewHint.Visibility = Visibility.Collapsed;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_previewAttached)
            {
                if (ParentFlowVm != null)
                {
                    ParentFlowVm.OnPreviewExecuted -= HidePreviewHint;
                    ParentFlowVm.DetachPreview();
                }
                _previewAttached = false;
            }
            _previewAdapter = null;
            base.OnClosed(e);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}
