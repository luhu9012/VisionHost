using Grayson.Vision.Contracts.ViewModels;
using Grayson.Vison.FlowEdit.Services; // 包含 EventBus
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Grayson.Vison.FlowEdit.Model;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using HalconDotNet;

namespace Grayson.Vison.FlowEdit.ViewModels
{
    public class ImageDisplayVm : ViewModelBase
    {

        public ObservableCollection<HalconRenderContext> ImageHistoryList { get; set; } = new ObservableCollection<HalconRenderContext>();

        private HalconRenderContext _activeImageContext;
        public HalconRenderContext ActiveImageContext
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

        public Action<HalconRenderContext> OnRequestRender;

        public ICommand PreviousImageCmd { get; }
        public ICommand NextImageCmd { get; }
        public ICommand SelectImageItemCmd { get; }

        private readonly ExecutionContext _engineContext;

        public ImageDisplayVm(ExecutionContext engineContext)
        {
            PreviousImageCmd = new RelayCommand(SelectPreviousImage);
            NextImageCmd = new RelayCommand(SelectNextImage);
            SelectImageItemCmd = new RelayCommand<HalconRenderContext>(SelectImageItem);


            _engineContext = engineContext ?? throw new ArgumentNullException(nameof(engineContext));

            // 订阅节点执行完成事件
            _engineContext.OnNodeExecuted += EngineContext_OnNodeExecuted;

     

            // 2. 订阅“节点参数修改”事件 -> 实时刷新图像 TODO
            //EventBus.Instance.Subscribe<NodeParamChangedEvent>(OnNodeParamChanged);
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
                    // 2. 组装 UI 渲染上下文 (HalconRenderContext 定义在 UI 层即可)
                    var renderContext = new HalconRenderContext
                    {
                        NodeId = node.NodeId,
                        NodeName = node.DisplayName,
                        Image = imagePort.DataValue as HalconDotNet.HImage // 转换为 Halcon 对象
                    };

                    // 3. 更新历史列表及主图
                    //UpdateOrAddHistory(renderContext);

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

        public void SelectImageItem(HalconRenderContext item)
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
                ActiveImageContext.Image.GetImageSize(out int w, out int h);
                if (x >= 0 && x < w && y >= 0 && y < h)
                {
                    HTuple g = ActiveImageContext.Image.GetGrayval(y, x);
                    SelectedImageInfo = $"[{ActiveImageContext.NodeName}]  X:{x}, Y:{y} | Gray:{g}";
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