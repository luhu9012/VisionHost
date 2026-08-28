//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TemplateManagerViewModel.cs
// 说 明: 视觉模板管理 VM —— 左侧模板列表（增删）+ 右侧创建向导
//        （本地图片 / 相机采集 → 框选 ROI → 设角度 → 创建落盘）。
//        模板被 ShapeMatch / NccMatch 节点按名称（TemplateName）引用，
//        与标定管理（CalibrationProfileName）的引用模式一致。
//===================================================================================
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.Services;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 模板管理（TemplateManagerView 的 DataContext）。
    /// 相机采集只取一帧（快照模式），避免长期占用取流所有权。
    /// </summary>
    public class TemplateManagerViewModel : ViewModelBase
    {
        private readonly TemplateManager _templateManager;
        private readonly HalconImageRenderService _renderService;

        /// <summary>当前展示/创建用的图像上下文（持有 HImage 句柄，替换前释放旧图）</summary>
        private WpfImageRenderContext _currentContext;
        private ICamera _pendingCamera;
        private bool _pendingAcquire;
        private bool _weStartedGrab;
        /// <summary>快照前若相机处于软/硬触发模式，记录原模式，收帧/超时后恢复</summary>
        private int? _triggerModeToRestore;
        private DispatcherTimer _acquireTimeoutTimer;

        #region 模板列表

        public ObservableCollection<TemplateInfo> Templates { get; } = new ObservableCollection<TemplateInfo>();

        private TemplateInfo _selectedTemplate;
        public TemplateInfo SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (Set(ref _selectedTemplate, value))
                {
                    // 列表选中项变化后刷新删除按钮（未选中时禁用）
                    DeleteTemplateCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region 图像显示

        /// <summary>Halcon 图像显示 VM（HalconImageDisplayHost 绑定）</summary>
        public ImageDisplayVm DisplayVm { get; }

        private bool _hasImage;
        public bool HasImage
        {
            get => _hasImage;
            private set
            {
                if (Set(ref _hasImage, value))
                {
                    // 该命令的 CanExecute 依赖 HasImage，必须显式刷新（RelayCommand 不监听属性变化）
                    CreateTemplateCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region 源图模式与相机

        private bool _isFileMode = true;
        public bool IsFileMode
        {
            get => _isFileMode;
            set { if (Set(ref _isFileMode, value) && value) OnPropertyChanged(nameof(IsCameraMode)); }
        }

        public bool IsCameraMode
        {
            get => !_isFileMode;
            set { if (Set(ref _isFileMode, !value)) OnPropertyChanged(nameof(IsFileMode)); }
        }

        public ObservableCollection<ICamera> Cameras { get; } = new ObservableCollection<ICamera>();

        private ICamera _selectedCamera;
        public ICamera SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                if (Set(ref _selectedCamera, value))
                {
                    // 相机下拉切换后刷新采集按钮（无相机时禁用）
                    AcquireImageCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region 创建参数表单

        private string _templateName;
        public string TemplateName
        {
            get => _templateName;
            set => Set(ref _templateName, value);
        }

        private TemplateMatchType _templateType = TemplateMatchType.Shape;
        public TemplateMatchType TemplateType
        {
            get => _templateType;
            set => Set(ref _templateType, value);
        }

        /// <summary>模板类型下拉数据源（Shape / Ncc）</summary>
        public Array TemplateTypeOptions => Enum.GetValues(typeof(TemplateMatchType));

        private double _angleStart = -20.0;
        public double AngleStart
        {
            get => _angleStart;
            set => Set(ref _angleStart, value);
        }

        private double _angleEnd = 20.0;
        public double AngleEnd
        {
            get => _angleEnd;
            set => Set(ref _angleEnd, value);
        }

        private string _remark;
        public string Remark
        {
            get => _remark;
            set => Set(ref _remark, value);
        }

        private double _roiRow1;
        public double RoiRow1
        {
            get => _roiRow1;
            set => Set(ref _roiRow1, value);
        }

        private double _roiCol1;
        public double RoiCol1
        {
            get => _roiCol1;
            set => Set(ref _roiCol1, value);
        }

        private double _roiRow2;
        public double RoiRow2
        {
            get => _roiRow2;
            set => Set(ref _roiRow2, value);
        }

        private double _roiCol2;
        public double RoiCol2
        {
            get => _roiCol2;
            set => Set(ref _roiCol2, value);
        }

        #endregion

        #region 状态

        private string _statusText = "就绪：请选择源图像并框选 ROI";
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (Set(ref _isBusy, value))
                {
                    // IsBusy 同时影响创建/采集两个命令，刷新后按钮状态才恢复（如采集完成、创建完成）
                    CreateTemplateCommand?.RaiseCanExecuteChanged();
                    AcquireImageCommand?.RaiseCanExecuteChanged();
                }
            }
        }

        #endregion

        #region 命令

        public RelayCommand LoadImageCommand { get; }
        public RelayCommand AcquireImageCommand { get; }
        public RelayCommand CreateTemplateCommand { get; }
        public RelayCommand DeleteTemplateCommand { get; }
        public RelayCommand NewTemplateCommand { get; }
        public RelayCommand RefreshCommand { get; }

        #endregion

        public TemplateManagerViewModel()
        {
            _templateManager = new TemplateManager();
            _renderService = new HalconImageRenderService();
            DisplayVm = new ImageDisplayVm(_renderService);

            LoadImageCommand = new RelayCommand(_ => LoadLocalImage());
            AcquireImageCommand = new RelayCommand(_ => AcquireCameraSnapshot(), _ => SelectedCamera != null && !IsBusy);
            CreateTemplateCommand = new RelayCommand(async _ => await CreateTemplateAsync(), _ => HasImage && !IsBusy);
            DeleteTemplateCommand = new RelayCommand(_ => DeleteSelectedTemplate(), _ => SelectedTemplate != null);
            NewTemplateCommand = new RelayCommand(_ => ResetWizard());
            RefreshCommand = new RelayCommand(_ => RefreshTemplates());

            LoadCameras();
            RefreshTemplates();
        }

        /// <summary>视图加载完成时调用（刷新列表）</summary>
        public void OnViewLoaded()
        {
            RefreshTemplates();
        }

        #region 模板列表管理

        private void RefreshTemplates()
        {
            var res = _templateManager.GetAll();
            Templates.Clear();
            if (res.Success)
            {
                foreach (var t in res.Data)
                {
                    Templates.Add(t);
                }
            }
            else
            {
                StatusText = "加载模板列表失败: " + res.Message;
            }

            if (SelectedTemplate == null)
            {
                SelectedTemplate = Templates.FirstOrDefault();
            }
        }

        private void DeleteSelectedTemplate()
        {
            if (SelectedTemplate == null) return;

            var result = MessageBox.Show(
                $"确定要删除模板【{SelectedTemplate.Name}】吗？\n" +
                "删除后，引用该模板的 ShapeMatch / NccMatch 节点将无法匹配（需在节点参数中改选其他模板）！",
                "警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            var name = SelectedTemplate.Name;
            var res = _templateManager.Delete(name);
            if (res.Success)
            {
                StatusText = $"模板 [{name}] 已删除";
            }
            else
            {
                MessageBox.Show("删除模板失败: " + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            RefreshTemplates();
        }

        #endregion

        #region 源图像获取

        private void LoadCameras()
        {
            Cameras.Clear();
            try
            {
                var pool = App.StationHostRuntime?.DevicePool;
                if (pool != null)
                {
                    foreach (var cam in pool.GetAllDevices().OfType<ICamera>())
                    {
                        Cameras.Add(cam);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TemplateManager] 加载相机列表失败: {ex.Message}");
            }
            SelectedCamera = Cameras.FirstOrDefault();
        }

        /// <summary>本地图片：OpenFileDialog → WrapImage(filePath) → 上屏</summary>
        private void LoadLocalImage()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择模板源图像",
                Filter = "图像文件 (*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff)|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件 (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var renderImage = _renderService.WrapImage(dlg.FileName) as HalconRenderImage;
                if (renderImage == null)
                {
                    MessageBox.Show("图像加载失败：无法解析该文件。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                ShowImage(renderImage, "模板源图");
                StatusText = $"已加载图片: {dlg.FileName}（{renderImage.Width}x{renderImage.Height}）";
            }
            catch (Exception ex)
            {
                MessageBox.Show("加载图片失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 相机快照：连接 → 订阅帧事件 → 确保连续采集模式 → 开启取流 → 收到第一帧即显示并停流。
        /// 5 秒无帧则提示检查触发模式/曝光（不长期占用取流）。
        /// 注意：软触发/硬触发模式下 StartGrabbing 不会自动出帧，必须先 SetTriggerMode(0) 切连续
        /// （与 CameraLiveWindowViewModel.TryStartGrabbing 的约定一致），收帧/超时后恢复原模式。
        /// </summary>
        private void AcquireCameraSnapshot()
        {
            if (IsBusy) return;
            var camera = SelectedCamera;
            if (camera == null)
            {
                StatusText = "设备池中未发现相机设备";
                return;
            }

            IsBusy = true;
            StatusText = "正在采集相机图像...";
            _pendingAcquire = true;
            _weStartedGrab = false;
            _triggerModeToRestore = null;
            _pendingCamera = camera;

            // 确保已连接
            if (camera.State != DeviceState.Connected)
            {
                var connectRes = camera.Connect();
                if (!connectRes.Success)
                {
                    _pendingAcquire = false;
                    _pendingCamera = null;
                    IsBusy = false;
                    StatusText = "相机连接失败: " + connectRes.Message;
                    return;
                }
            }

            camera.FrameReceived += OnAcquireFrameReceived;

            // 开流前确保连续采集模式：软/硬触发模式下 SDK 不会自动出帧
            var modeRes = camera.GetParam("TriggerModeSelect");
            if (modeRes.Success && int.TryParse(modeRes.Data?.ToString(), out int triggerMode) && triggerMode != 0)
            {
                var setRes = camera.SetTriggerMode(0);
                if (setRes.Success)
                {
                    _triggerModeToRestore = triggerMode;
                    StatusText = $"相机处于触发模式({triggerMode})，已临时切换连续采集...";
                }
                else
                {
                    StatusText = "切换连续采集模式失败: " + setRes.Message;
                }
            }

            // 优先连续取流（快照一帧后立即停止）
            var res = camera.StartContinuousGrab();
            if (res.Success)
            {
                _weStartedGrab = true;
            }
            else
            {
                // 已被其他页面取流 → 搭车等待下一帧
                StatusText = "相机已在取流，等待帧...";
            }

            // 5 秒超时保护（长曝光/首帧延迟兜底）
            _acquireTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _acquireTimeoutTimer.Tick += (s, e) =>
            {
                _acquireTimeoutTimer.Stop();
                _acquireTimeoutTimer = null;
                if (!_pendingAcquire) return;
                _pendingAcquire = false;
                bool switchedTriggerMode = _triggerModeToRestore.HasValue;
                CompleteAcquire();
                StatusText = switchedTriggerMode
                    ? "未收到相机图像：已自动切换连续采集仍无帧，请检查曝光时间与相机连接。"
                    : "未收到相机图像：请检查相机触发模式、曝光与连接。";
            };
            _acquireTimeoutTimer.Start();
        }

        private void OnAcquireFrameReceived(object sender, FrameEventArgs e)
        {
            if (!_pendingAcquire || sender != _pendingCamera) return;
            if (e?.Buffer == null || e.Width <= 0 || e.Height <= 0) return;

            // 只取一帧：回调线程（相机 SDK 线程）仅置标志位，
            // DispatcherTimer 等 UI 对象操作与收尾必须 marshal 到 UI 线程，
            // 否则后台线程访问 DispatcherObject 抛 InvalidOperationException
            //（此前 _acquireTimeoutTimer?.Stop() 直接写在回调线程 → 跨线程异常被 HikCamera 插件捕获）。
            _pendingAcquire = false;

            var frame = e;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 停止超时保护并统一收尾（停流/恢复触发模式/解绑事件）
                    _acquireTimeoutTimer?.Stop();
                    _acquireTimeoutTimer = null;
                    CompleteAcquire();

                    var renderImage = _renderService.WrapImage(frame) as HalconRenderImage;
                    if (renderImage == null)
                    {
                        StatusText = "相机帧转换失败。";
                        return;
                    }
                    ShowImage(renderImage, "相机快照");
                    StatusText = $"已采集相机图像（{renderImage.Width}x{renderImage.Height}）";
                }
                catch (Exception ex)
                {
                    StatusText = "相机帧处理异常: " + ex.Message;
                }
                finally
                {
                    IsBusy = false;
                }
            }), DispatcherPriority.Render);
        }

        /// <summary>收帧或超时后的统一收尾：停自己发起的流 + 恢复触发模式 + 解绑事件</summary>
        private void CompleteAcquire()
        {
            var camera = _pendingCamera;
            _pendingCamera = null;
            if (camera == null) return;

            camera.FrameReceived -= OnAcquireFrameReceived;
            if (_weStartedGrab)
            {
                try { camera.StopContinuousGrab(); } catch { }
                _weStartedGrab = false;
            }
            if (_triggerModeToRestore.HasValue)
            {
                try { camera.SetTriggerMode(_triggerModeToRestore.Value); } catch { }
                _triggerModeToRestore = null;
            }
            IsBusy = false;
        }

        /// <summary>把渲染图像上屏（释放上一张旧图句柄）</summary>
        private void ShowImage(HalconRenderImage renderImage, string nodeName)
        {
            var old = _currentContext;
            var context = new WpfImageRenderContext
            {
                NodeId = "template-editor",
                NodeName = nodeName,
                Image = renderImage
            };
            _currentContext = context;
            DisplayVm.ActiveImageContext = context;
            old?.Dispose();

            HasImage = true;
        }

        #endregion

        #region 创建模板

        /// <summary>
        /// 创建模板（后台线程跑 HALCON 算子，避免 UI 卡顿 —— 铁律：算子绝不能在 UI 线程同步跑）。
        /// </summary>
        private async Task CreateTemplateAsync()
        {
            if (!HasImage || _currentContext?.Image == null)
            {
                MessageBox.Show("请先加载本地图片或从相机采集一帧！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrWhiteSpace(TemplateName))
            {
                MessageBox.Show("请填写模板名称！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (RoiRow2 <= RoiRow1 || RoiCol2 <= RoiCol1)
            {
                MessageBox.Show("ROI 无效：请在图像上框选目标区域（或用右侧数值手动修正）。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            IsBusy = true;
            StatusText = $"正在创建模板 [{TemplateName}] ...";

            // 捕获参数，后台执行（_currentContext 在操作期间不会被替换）
            var image = _currentContext.Image;
            var name = TemplateName.Trim();
            var type = TemplateType;
            double r1 = RoiRow1, c1 = RoiCol1, r2 = RoiRow2, c2 = RoiCol2;
            double aStart = AngleStart, aEnd = AngleEnd;
            var remark = Remark ?? "";

            var result = await Task.Run(() =>
                type == TemplateMatchType.Shape
                    ? TemplateCreationBridge.CreateShapeTemplate(image, name, r1, c1, r2, c2, aStart, aEnd, remark)
                    : TemplateCreationBridge.CreateNccTemplate(image, name, r1, c1, r2, c2, aStart, aEnd, remark));

            IsBusy = false;

            if (result.Success)
            {
                StatusText = $"模板 [{name}] 创建成功";
                MessageBox.Show(
                    $"模板 [{name}] 创建成功！\n类型: {(type == TemplateMatchType.Shape ? "形状匹配 (Shape)" : "灰度匹配 (NCC)")}\n" +
                    $"模板文件: {result.Data.ModelFilePath}\n\n" +
                    "可在编辑器 ShapeMatch / NccMatch 节点的参数面板中选择引用该模板。",
                    "创建成功", MessageBoxButton.OK, MessageBoxImage.Information);
                ResetWizard();
                RefreshTemplates();
            }
            else
            {
                StatusText = "创建失败: " + result.Message;
                MessageBox.Show("模板创建失败：" + result.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>清空创建向导表单（保留当前源图？不 —— 新建模板从选图重新开始）</summary>
        private void ResetWizard()
        {
            TemplateName = string.Empty;
            TemplateType = TemplateMatchType.Shape;
            AngleStart = -20.0;
            AngleEnd = 20.0;
            Remark = string.Empty;
            ClearRoi();

            var old = _currentContext;
            _currentContext = null;
            DisplayVm.ActiveImageContext = null;
            old?.Dispose();
            HasImage = false;

            SelectedTemplate = null;
            StatusText = "就绪：请选择源图像并框选 ROI";
        }

        /// <summary>清空 ROI（供视图"清除 ROI"按钮调用）</summary>
        public void ClearRoi()
        {
            RoiRow1 = 0;
            RoiCol1 = 0;
            RoiRow2 = 0;
            RoiCol2 = 0;
        }

        /// <summary>视图框选 ROI 完成后回调（更新状态栏反馈）</summary>
        public void NotifyRoiSelected()
        {
            StatusText = $"ROI 已框选: Row {RoiRow1:0.0} ~ {RoiRow2:0.0}, Col {RoiCol1:0.0} ~ {RoiCol2:0.0}";
        }

        #endregion
    }
}
