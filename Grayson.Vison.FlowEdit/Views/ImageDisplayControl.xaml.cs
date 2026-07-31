using System;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Grayson.Vison.FlowEdit.ViewModels;
using Grayson.Vison.FlowEdit.Model; 

namespace Grayson.Vison.FlowEdit.Views
{
    public partial class ImageDisplayControl : UserControl
    {
        private HWindow _hWindow;

        public ImageDisplayControl()
        {
            InitializeComponent();
            this.DataContextChanged += ImageDisplayControl_DataContextChanged;
        }

        private void ImageDisplayControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ImageDisplayVm oldVm)
            {
                oldVm.OnRequestRender -= RenderHalconContext;
            }
            if (e.NewValue is ImageDisplayVm newVm)
            {
                newVm.OnRequestRender += RenderHalconContext;
            }
        }

        private void SmartWindow_HInitWindow(object sender, EventArgs e)
        {
            _hWindow = SmartWindow.HalconWindow;
            _hWindow.SetDraw("margin");
            _hWindow.SetLineWidth(2);

            // 句柄初始化完成后通知 ViewModel 进行初次渲染
            if (DataContext is ImageDisplayVm vm)
            {
                vm.OnRequestRender += RenderHalconContext;
                vm.RefreshActiveImage();
            }
        }

        private void RenderHalconContext(HalconRenderContext context)
        {
            if (_hWindow == null || context == null) return;

            Dispatcher.InvokeAsync(() =>
            {
                _hWindow.ClearWindow();

                // 1. 渲染 HImage 图像
                if (context.Image != null && context.Image.IsInitialized())
                {
                    _hWindow.DispObj(context.Image);
                    SmartWindow.SetFullImagePart(context.Image);
                }

                // 2. 叠加渲染 Regions/XLD (检测框/缺陷区域)
                if (context.Regions != null)
                {
                    _hWindow.SetColor("red");
                    foreach (var reg in context.Regions)
                    {
                        if (reg != null && reg.IsInitialized())
                        {
                            _hWindow.DispObj(reg);
                        }
                    }
                }

                // 3. 叠加渲染文本
                if (context.Texts != null)
                {
                    foreach (var txt in context.Texts)
                    {
                        _hWindow.SetColor(txt.Color ?? "green");
                        _hWindow.SetTposition((int)txt.Row, (int)txt.Column);
                        _hWindow.WriteString(txt.Text);
                    }
                }
            });
        }

        private void BtnFitImage_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is ImageDisplayVm vm && vm.ActiveImageContext?.Image != null)
            {
                SmartWindow.SetFullImagePart(vm.ActiveImageContext.Image);
            }
        }

        private void SmartWindow_HMove(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
        {
            // 处理坐标及灰度值显示
            if (DataContext is ImageDisplayVm vm)
            {
                vm.UpdateCursorPixelInfo((int)e.X, (int)e.Y);
            }
        }
    }
}