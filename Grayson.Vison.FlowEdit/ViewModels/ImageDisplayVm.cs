using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vison.FlowEdit.ViewModels
{
    public class ImageDisplayVm : ViewModelBase
    {
        public ObservableCollection<WpfImageRenderContext> ImageHistoryList { get; set; } = new ObservableCollection<WpfImageRenderContext>();

        private WpfImageRenderContext _activeImageContext;
        public WpfImageRenderContext ActiveImageContext
        {
            get => _activeImageContext;
            set
            {
                if (Set(ref _activeImageContext, value))
                {
                    OnRequestRender?.Invoke(value);
                    UpdateImageInfo();
                }
            }
        }

        private bool _isAutoSwitchEnabled = true;
        public bool IsAutoSwitchEnabled
        {
            get => _isAutoSwitchEnabled;
            set => Set(ref _isAutoSwitchEnabled, value);
        }

        private string _selectedImageInfo;
        public string SelectedImageInfo
        {
            get => _selectedImageInfo;
            set => Set(ref _selectedImageInfo, value);
        }

        public Action<ImageRenderContext> OnRequestRender;

        public ICommand PreviousImageCmd { get; }
        public ICommand NextImageCmd { get; }
        public ICommand SelectImageItemCmd { get; }

        private readonly ExecutionContext _engineContext;
        private readonly HalconImageRenderService _renderService;

        public ImageDisplayVm(ExecutionContext engineContext, HalconImageRenderService renderService)
        {
            _engineContext = engineContext ?? throw new ArgumentNullException(nameof(engineContext));
            _renderService = renderService ?? throw new ArgumentNullException(nameof(renderService));

            PreviousImageCmd = new RelayCommand(SelectPreviousImage);
            NextImageCmd = new RelayCommand(SelectNextImage);
            SelectImageItemCmd = new RelayCommand<WpfImageRenderContext>(SelectImageItem);

            _engineContext.OnNodeExecuted += EngineContext_OnNodeExecuted;
        }

        private void EngineContext_OnNodeExecuted(object sender, FlowNodeBase node)
        {
            // 确保回到 UI 线程更新
            App.Current.Dispatcher.InvokeAsync(() =>
            {
                // 1. 从节点的 OutputPorts 寻找是否有图像类型的数据
                var imagePort = node.OutputPorts?.FirstOrDefault(p =>
                    (p.DataType == "HImage" || p.DataType == "Image") && p.DataValue != null);

                if (imagePort != null)
                {
                    // 2. 组装 UI 渲染上下文
                    var renderImage = _renderService.WrapImage(imagePort.DataValue);
                    var renderContext = new WpfImageRenderContext
                    {
                        NodeId = node.NodeId,
                        NodeName = node.DisplayName,
                        Image = renderImage,
                        Thumbnail = _renderService.CreateThumbnail(renderImage)
                    };

                    // 3. 更新历史列表及主图
                    ImageHistoryList.Add(renderContext);
                    if (IsAutoSwitchEnabled)
                    {
                        SelectImageItem(renderContext);
                    }
                }
            });
        }

        //private Task OnNodeParamChanged(NodeParamChangedEvent e)
        //{
        //    if (e?.RenderContext == null) return Task.CompletedTask;

        //    var target = ImageHistoryList.FirstOrDefault(x => x.NodeId == e.NodeId);
        //    if (target != null)
        //    {
        //        int index = ImageHistoryList.IndexOf(target);
        //        ImageHistoryList[index] = e.RenderContext;

        //        // 如果修改的刚好是当前正在显示的大图节点，立即刷新大图
        //        if (ActiveImageContext?.NodeId == e.NodeId)
        //        {
        //            SelectImageItem(e.RenderContext);
        //        }
        //    }

        //    return Task.CompletedTask;
        //}

        public void SelectImageItem(WpfImageRenderContext item)
        {
            if (item == null) return;

            foreach (var img in ImageHistoryList) img.IsSelected = false;
            item.IsSelected = true;

            ActiveImageContext = item;
        }

        private void SelectPreviousImage()
        {
            if (ImageHistoryList.Count == 0 || ActiveImageContext == null) return;
            int currentIndex = ImageHistoryList.IndexOf(ActiveImageContext);
            if (currentIndex > 0)
            {
                SelectImageItem(ImageHistoryList[currentIndex - 1]);
            }
        }

        private void SelectNextImage()
        {
            if (ImageHistoryList.Count == 0 || ActiveImageContext == null) return;
            int currentIndex = ImageHistoryList.IndexOf(ActiveImageContext);
            if (currentIndex < ImageHistoryList.Count - 1)
            {
                SelectImageItem(ImageHistoryList[currentIndex + 1]);
            }
        }

        public void UpdateCursorPixelInfo(int x, int y)
        {
            if (ActiveImageContext?.Image == null) return;
            try
            {
                if (x >= 0 && x < ActiveImageContext.Image.Width && y >= 0 && y < ActiveImageContext.Image.Height)
                {
                    var pixelInfo = _renderService.GetPixelInfo(ActiveImageContext.Image, x, y);
                    SelectedImageInfo = $"[{ActiveImageContext.NodeName}]  X:{x}, Y:{y} | {pixelInfo}";
                }
            }
            catch { }
        }

        private void UpdateImageInfo()
        {
            if (ActiveImageContext != null)
            {
                SelectedImageInfo = $"当前节点: {ActiveImageContext.NodeName}";
            }
        }

        public void RefreshActiveImage()
        {
            if (ActiveImageContext != null)
            {
                OnRequestRender?.Invoke(ActiveImageContext);
            }
        }

        // 记得在 ViewModel 销毁时解绑，防止内存泄漏
        //public  void Cleanup()
        //{
        //    _engineContext.OnNodeExecuted -= EngineContext_OnNodeExecuted;
        //    base.Cleanup();
        //}

    }
}