
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
        /// <summary>
        /// ★ 日志诊断标签：由绑定的显示宿主（HalconImageDisplayHost.LogTag）注入，
        /// 标识"这是哪一个窗口的图像通道"。
        /// 存在原因（2026-09-15 踩坑）：主视图 / 属性面板预览 / 工位监视页可同时存在多个
        /// ImageDisplayVm，日志里只有节点名 ⇒ 多条「选择图像: [形状匹配]」「ActiveImageContext
        /// 已更新」交织在一起，**判不出哪条属于哪个窗口**（曾据此误判为"主视图没收到帧"）。
        /// </summary>
        public string LogTag { get; set; } = "图像显示";

        /// <summary>日志前缀用的标签（永不为空）</summary>
        private string Tag => string.IsNullOrWhiteSpace(LogTag) ? "图像显示" : LogTag;

        // 缩略图列表，按时间顺序存储最近的图像渲染上下文
        public ObservableCollection<WpfImageRenderContext> ImageHistoryList { get; set; } = new ObservableCollection<WpfImageRenderContext>();
        // 当前激活的图像渲染上下文
        private WpfImageRenderContext _activeImageContext;

        /// <summary>
        /// 标记「本次把 ActiveImageContext 置 null 属于显式清窗动作」。
        /// 仅用于日志归类：清窗指令一律由 Clear() / RequestClearWindow() 显式下发，
        /// setter 本身**永不**因 null 而下发（见 setter 内注释）。
        /// </summary>
        private bool _explicitClearRequested;

        public WpfImageRenderContext ActiveImageContext
        {
            get => _activeImageContext;
            set
            {
                if (Set(ref _activeImageContext, value))
                {
                    LogBus.Info("ImageDisplay", $"[{Tag}] ActiveImageContext 已更新 -> [{value?.NodeName ?? "Null"}]");

                    // ★★ 有图才下发渲染；null **绝不**在这里下发清窗指令。
                    //
                    // 为什么（2026-09-15 踩坑，现象 =「模板匹配效果闪一下就没」）：
                    //   缩略图 ListBox 的 SelectedItem 与本属性双向绑定。AddOrUpdateImageContext
                    //   里的 ImageHistoryList[index] = newContext（集合 Replace）会让 ListBox 认为
                    //   "原选中实例已离开集合" ⇒ 它立即把 SelectedItem 置 null 并**回写本属性**。
                    //   旧实现对此无条件 OnRequestRender(null) ⇒ HalconImageDisplayHost.Display(null)
                    //   ⇒ ClearWindow() + ClearScene()，把节点经 Preview 提交的叠加层（模板匹配轮廓）
                    //   连同底图一起清掉；而叠加层只存在于显示场景里、**不会被任何后续帧重建**
                    //   ⇒ 画面回到"没有效果的底图"，且再也回不来（看着就像被帧冲刷掉了）。
                    //   清窗是**显式动作**，不是"某个属性被写成 null"的副作用。
                    if (value != null)
                    {
                        OnRequestRender?.Invoke(value);
                    }
                    else if (_explicitClearRequested)
                    {
                        LogBus.Debug("ImageDisplay", $"[{Tag}] 激活图像已置空（清窗指令由调用方显式下发）。");
                    }
                    else if (ImageHistoryList.Count > 0)
                    {
                        LogBus.Warn("ImageDisplay",
                            $"[{Tag}] 已忽略非预期的 null 写入（图像历史仍有 {ImageHistoryList.Count} 帧）：缩略图选中态回写不等于清窗指令，照发会连节点叠加层一起清掉。");
                    }

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

            LogBus.Info("ImageDisplay", $"[{Tag}] 选择图像: [{item.NodeName}] (Width:{item.Image?.Width}, Height:{item.Image?.Height})");
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
        /// 清空所有图像历史与当前画面。
        /// 这是「清窗指令」的两个显式出口之一（另一个是 <see cref="RequestClearWindow"/>）：
        /// 只有走这里，渲染端才会收到 null 并把窗口与场景一起擦掉。
        /// </summary>
        public void Clear()
        {
            WpfImageRenderContext previous;
            _explicitClearRequested = true;
            try
            {
                // 先留住"当前帧"引用：ImageHistoryList.Clear() 会触发缩略图 ListBox 回写
                // ActiveImageContext=null，字段当场被清空，之后再取就取不到要释放的对象了。
                previous = ActiveImageContext;

                // 遍历 Dispose 掉历史列表中的所有资源（Dispose 幂等，重复释放安全）
                foreach (var item in ImageHistoryList)
                {
                    item?.Dispose();
                }
                ImageHistoryList.Clear();

                ActiveImageContext = null;
                SelectedImageInfo = "无图像";

                // 当前激活项可能不在历史列表里（外部直接赋值的独立上下文）⇒ 单独释放
                previous?.Dispose();
            }
            finally
            {
                _explicitClearRequested = false;
            }

            // ★ 唯一的画面擦除出口（setter 已不再因 null 下发）
            OnRequestRender?.Invoke(null);

            LogBus.Info("ImageDisplay", $"[{Tag}] 图像历史与内存句柄已完全清空与释放。");
        }

        /// <summary>
        /// 显式清空主视图画面，**保留**图像历史列表及其中的句柄。
        /// 用途：只要求"主视图别再显示这一帧"，但缩略图历史要留着（如模板向导开始新建）。
        /// 与 <see cref="Clear"/> 的区别：不 Dispose 任何图像、不动 ImageHistoryList。
        /// </summary>
        public void RequestClearWindow()
        {
            _explicitClearRequested = true;
            try
            {
                ActiveImageContext = null;
            }
            finally
            {
                _explicitClearRequested = false;
            }

            // ★ 同样的唯一出口：显式下发清窗，而不是靠属性被写成 null 的副作用
            OnRequestRender?.Invoke(null);

            LogBus.Info("ImageDisplay", $"[{Tag}] 已显式请求清空主视图（图像历史保留）。");
        }

        /// <summary>
        /// 🌟 2. 覆盖/更新某个节点的图像时，释放该节点上一张旧图的内存
        ///
        /// ⚠ 两个历史 bug 在此修复（"底图闪现后消失只剩黑屏+轮廓"的根因）：
        ///   ① VM 复用上下文就地换图后再调本方法（existing == newContext 同一实例）：
        ///     原实现无条件 existing.Dispose()，把刚换上的新图当场销毁；
        ///   ② 借用语义图像（MatchImage 端口值 = 相机输出同一 HImage，见
        ///     WpfImageRenderContext.DisposeShell 注释）：同一 HImage 被多个 NodeId
        ///     上下文各包一层 HalconRenderImage，直接 Dispose 会销毁仍在被其他
        ///     上下文/场景底图使用的图像 → 下一周期渲染已销毁句柄 → 黑屏+闪退。
        ///   ③ 替换路径原实现 RefreshActiveImage() 重显「旧激活上下文」：若激活的不是
        ///     场景底图（模板匹配叠加层画在其上），Display 判非同帧 → ClearScene，
        ///     表现为「匹配轮廓显示一瞬即被清掉、只剩底图」。现在自动跟随模式下
        ///     改为 SelectImageItem(newContext) 跟随最新帧——后续节点帧与场景底图是
        ///     同一 HImage（借用语义）时命中 sameFrame，叠加场景得以保留。
        /// </summary>
        public void AddOrUpdateImageContext(WpfImageRenderContext newContext)
        {
            if (newContext == null) return;

            var existing = ImageHistoryList.FirstOrDefault(x => x.NodeId == newContext.NodeId);
            if (existing != null)
            {
                int index = ImageHistoryList.IndexOf(existing);

                if (!ReferenceEquals(existing, newContext))
                {
                    // 如果旧图当前正在显示，先切到新上下文（触发一次渲染，此时旧图尚存活）
                    if (ActiveImageContext == existing)
                    {
                        ActiveImageContext = newContext;
                    }

                    // 释放旧 Context：图像若仍被其他上下文引用（借用语义），只清壳不销毁句柄
                    if (IsImageStillReferenced(existing))
                    {
                        existing.DisposeShell();
                    }
                    else
                    {
                        existing.Dispose();
                    }

                    // 替换为新 Context
                    ImageHistoryList[index] = newContext;

                    // 自动跟随最新帧（而非重显可能已过时的旧激活图），保留场景叠加层
                    if (IsAutoSwitchEnabled)
                    {
                        SelectImageItem(newContext);
                    }
                    else
                    {
                        RefreshActiveImage();
                    }

                    // ★ 兜底：缩略图 ListBox（SelectedItem 双向绑定）在集合 Replace 的同步
                    //    通知里可能把选中项清空并回写 ActiveImageContext=null（旧实例已不在
                    //    集合中）。此时若自动跟随未生效/被外部拉空，主视图将停在空白——恢复
                    //    指向刚推入的新帧，保证「执行完 → 主视图切到当前节点输出」不丢。
                    if (ActiveImageContext == null && newContext?.Image != null)
                    {
                        SelectImageItem(newContext);
                    }
                }
                else
                {
                    // existing == newContext（VM 就地换图路径）：上下文已携带新图，绝不 Dispose
                    RefreshActiveImage();// 触发主视图绘制
                }
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

        /// <summary>
        /// 判断上下文的底层图像（按 NativeHandle 比较，而非包装对象——WrapImage 对
        /// 同一 HImage 每次产生新 HalconRenderImage 包装）是否仍被列表中其他上下文
        /// 或当前激活上下文引用。仍被引用 → 释放时只能 DisposeShell。
        /// </summary>
        private bool IsImageStillReferenced(WpfImageRenderContext context)
        {
            var handle = context?.Image?.NativeHandle;
            if (handle == null) return false;

            if (ActiveImageContext != null && ActiveImageContext != context &&
                ReferenceEquals(ActiveImageContext.Image?.NativeHandle, handle))
            {
                return true;
            }

            foreach (var item in ImageHistoryList)
            {
                if (item == null || ReferenceEquals(item, context)) continue;
                if (ReferenceEquals(item.Image?.NativeHandle, handle)) return true;
            }
            return false;
        }
    }
}