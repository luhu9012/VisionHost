
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace Grayson.Vision.HalconWrapper.Wpf.ViewModels
{
    public class ImageDisplayVm : ViewModelBase
    {
        // 缩略图列表，按时间顺序存储最近的图像渲染上下文
        public ObservableCollection<WpfImageRenderContext> ImageHistoryList { get; set; } = new ObservableCollection<WpfImageRenderContext>();
        // 当前激活的图像渲染上下文
        private WpfImageRenderContext _activeImageContext;
        public WpfImageRenderContext ActiveImageContext
        {
            get => _activeImageContext;
            set
            {
                if (Set(ref _activeImageContext, value))
                {
                    LogBus.Info("ImageDisplay", $"ActiveImageContext 已更新 -> [{value?.NodeName ?? "Null"}]");
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
        public ICommand FitImageCmd { get; }

        public Action OnRequestFitImage;

        private readonly HalconImageRenderService _renderService;

        public ImageDisplayVm(HalconImageRenderService renderService)
        {
            _renderService = renderService ?? throw new ArgumentNullException(nameof(renderService));

            PreviousImageCmd = new RelayCommand(SelectPreviousImage);
            NextImageCmd = new RelayCommand(SelectNextImage);
            SelectImageItemCmd = new RelayCommand<WpfImageRenderContext>(SelectImageItem);
            FitImageCmd = new RelayCommand(() => OnRequestFitImage?.Invoke());


            // 渲染完全统一交给 WorkerClient 的 OnFrameRendered 进行路由更新，避免双重渲染和重复包装
            LogBus.Debug("ImageDisplay", "ImageDisplayVm 初始化完成。");
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

            LogBus.Info("ImageDisplay", $"选择图像: [{item.NodeName}] (Width:{item.Image?.Width}, Height:{item.Image?.Height})");
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
                    SelectedImageInfo = $" X:{x}, Y:{y} | {pixelInfo}";
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
                LogBus.Debug("ImageDisplay", $"手动/事件驱动刷新 ActiveImageContext: [{ActiveImageContext.NodeName}] (NodeId: {ActiveImageContext.NodeId})");
                OnRequestRender?.Invoke(ActiveImageContext); // 触发主视图绘制
            }
            else
            {
                LogBus.Warn("ImageDisplay", "请求刷新 ActiveImageContext 但当前 ActiveImageContext 为 null");
            }
        }
        /// <summary>
        /// 清空所有图像历史与当前画面
        /// </summary>
        public void Clear()
        {
            // 遍历 Dispose 掉历史列表中的所有资源
            foreach (var item in ImageHistoryList)
            {
                item?.Dispose();
            }
            ImageHistoryList.Clear();

            ActiveImageContext?.Dispose();
            ActiveImageContext = null;
            SelectedImageInfo = "无图像";

            // 通知 View 擦除画布
            OnRequestRender?.Invoke(null);

            LogBus.Info("ImageDisplay", "图像历史与内存句柄已完全清空与释放。");
        }

        /// <summary>
        /// 🌟 2. 覆盖/更新某个节点的图像时，释放该节点上一张旧图的内存
        /// </summary>
        public void AddOrUpdateImageContext(WpfImageRenderContext newContext)
        {
            if (newContext == null) return;

            var existing = ImageHistoryList.FirstOrDefault(x => x.NodeId == newContext.NodeId);
            if (existing != null)
            {
                int index = ImageHistoryList.IndexOf(existing);

                // 如果旧图当前正在显示，先清空引用
                if (ActiveImageContext == existing)
                {
                    ActiveImageContext = newContext;
                }

                // 释放旧 Context 的内存句柄
                existing.Dispose();

                // 替换为新 Context
                ImageHistoryList[index] = newContext;

                RefreshActiveImage();// 触发主视图绘制
            }
            else
            {
                ImageHistoryList.Add(newContext);
                if (IsAutoSwitchEnabled || ActiveImageContext == null)
                {
                    SelectImageItem(newContext);// context 变化会触发视图更新
                }
            }
           
        }
    }
}