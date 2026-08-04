using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class HardwareConsoleViewModel : ViewModelBase
    {
        public HardwareConsoleViewModel()
        {
            InitCommands();
            LoadMockData();
        }

        #region 设备池下拉与级联属性

        // 1. 相机设备集合
        public ObservableCollection<DeviceInfoModel> CameraDeviceList { get; set; } = new ObservableCollection<DeviceInfoModel>();

        private DeviceInfoModel _selectedCameraDevice;
        public DeviceInfoModel SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                if (Set(ref _selectedCameraDevice, value) && value != null)
                {
                    OnCameraDeviceChanged(value);
                }
            }
        }

        // 2. 运动控制卡设备集合
        public ObservableCollection<DeviceInfoModel> MotionDeviceList { get; set; } = new ObservableCollection<DeviceInfoModel>();

        private DeviceInfoModel _selectedMotionDevice;
        public DeviceInfoModel SelectedMotionDevice
        {
            get => _selectedMotionDevice;
            set
            {
                if (Set(ref _selectedMotionDevice, value) && value != null)
                {
                    OnMotionDeviceChanged(value);
                }
            }
        }

        // 当前控制卡下的轴列表
        public ObservableCollection<AxisInfoModel> AxisList { get; set; } = new ObservableCollection<AxisInfoModel>();

        private AxisInfoModel _selectedAxis;
        public AxisInfoModel SelectedAxis
        {
            get => _selectedAxis;
            set => Set(ref _selectedAxis, value);
        }

        // 3. IO 板卡设备集合
        public ObservableCollection<DeviceInfoModel> IoDeviceList { get; set; } = new ObservableCollection<DeviceInfoModel>();

        private DeviceInfoModel _selectedIoDevice;
        public DeviceInfoModel SelectedIoDevice
        {
            get => _selectedIoDevice;
            set
            {
                if (Set(ref _selectedIoDevice, value) && value != null)
                {
                    OnIoDeviceChanged(value);
                }
            }
        }

        // 当前 IO 板卡下的点位
        public ObservableCollection<IoPointModel> InputIOList { get; set; } = new ObservableCollection<IoPointModel>();
        public ObservableCollection<IoPointModel> OutputIOList { get; set; } = new ObservableCollection<IoPointModel>();

        #endregion

        #region 调试参数属性

        private BitmapSource _cameraImageSource;
        public BitmapSource CameraImageSource
        {
            get => _cameraImageSource;
            set => Set(ref _cameraImageSource, value);
        }

        private bool _isGrabbing;
        public bool IsGrabbing
        {
            get => _isGrabbing;
            set => Set(ref _isGrabbing, value);
        }

        public int ExposureTime { get; set; } = 8000;
        public double GainValue { get; set; } = 2.0;

        public double JogStep { get; set; } = 5.0;
        public double AxisSpeed { get; set; } = 100.0;

        public double AxisXPos { get; set; } = 152.360;
        public double AxisYPos { get; set; } = 88.025;
        public double AxisZPos { get; set; } = -5.500;

        #endregion

        #region 命令声明

        public ICommand SetExposureTimeCommand { get; private set; }
        public ICommand SetGainCommand { get; private set; }
        public ICommand SoftwareTriggerCommand { get; private set; }
        public ICommand StartGrabCommand { get; private set; }
        public ICommand StopGrabCommand { get; private set; }
        public ICommand HomeAxisCommand { get; private set; }
        public ICommand JogCommand { get; private set; }

        #endregion

        #region 模拟数据填充函数 (Mock)

        /// <summary>
        /// 模拟从 DevicePoolManager 读取硬件设备池，填充选单
        /// </summary>
        public void LoadMockData()
        {
            // TODO: [source: 5] 接入 DevicePoolManager.ActiveDevices.OfType<ICamera>() 加载相机[cite: 5]
            CameraDeviceList.Clear();
            CameraDeviceList.Add(new DeviceInfoModel { DeviceId = "CAM_01", DisplayName = "📷 海康 MV-CS060 (工位一顶视)" });
            CameraDeviceList.Add(new DeviceInfoModel { DeviceId = "CAM_02", DisplayName = "📷 大华 A5030G (工位二侧视)" });
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault();

            // TODO: [source: 5] 接入 DevicePoolManager.ActiveDevices.OfType<IMotionController>() 加载板卡[cite: 5]
            MotionDeviceList.Clear();
            MotionDeviceList.Add(new DeviceInfoModel { DeviceId = "MOTION_01", DisplayName = "⚙️ 固高 GTS-800 运动控制卡 (主线卡01)" });
            MotionDeviceList.Add(new DeviceInfoModel { DeviceId = "MOTION_02", DisplayName = "⚙️ 正运动 ZMC408 运动控制卡 (模组卡02)" });
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault();

            // TODO: [source: 5] 接入 DevicePoolManager.ActiveDevices.OfType<IIoService>() 加载 IO 扩展模块[cite: 5]
            IoDeviceList.Clear();
            IoDeviceList.Add(new DeviceInfoModel { DeviceId = "IO_01", DisplayName = "🔌 主控板总线 IO 模块 (板载)" });
            IoDeviceList.Add(new DeviceInfoModel { DeviceId = "IO_02", DisplayName = "🔌 远程 EtherCAT IO 扩展端子 (工位B箱)" });
            SelectedIoDevice = IoDeviceList.FirstOrDefault();
        }

        #endregion

        #region 设备切换响应逻辑 (级联更新)

        private void OnCameraDeviceChanged(DeviceInfoModel device)
        {
            // TODO: [source: 5] 切换选中的相机对象，更新当前相机的 ExposureTime/GainValue 参数[cite: 5]
        }

        private void OnMotionDeviceChanged(DeviceInfoModel device)
        {
            // TODO: [source: 5] 根据选中的板卡 (device.DeviceId)，动态查询并加载该板卡拥有的轴[cite: 5]
            AxisList.Clear();
            if (device.DeviceId == "MOTION_01")
            {
                AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "Axis_0: X轴 (Platform_Horiz)" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "Axis_1: Y轴 (Platform_Vert)" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "Axis_2: Z轴 (Camera_Focus)" });
            }
            else
            {
                AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "Axis_0: R轴 (Rotary_Table)" });
                AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "Axis_1: W轴 (Feeder_Push)" });
            }
            SelectedAxis = AxisList.FirstOrDefault();
        }

        private void OnIoDeviceChanged(DeviceInfoModel device)
        {
            // TODO: [source: 5] 根据选中的 IO 模块，读取并加载对应的 DI / DO 点位列表[cite: 5]
            InputIOList.Clear();
            OutputIOList.Clear();

            if (device.DeviceId == "IO_01")
            {
                InputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "DI_00 急停按钮", IsActive = false });
                InputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "DI_01 安全光幕", IsActive = true });
                InputIOList.Add(new IoPointModel { ChannelIndex = 2, Name = "DI_02 主气压检测", IsActive = true });

                OutputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "DO_00 三色灯-红", IsActive = false });
                OutputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "DO_01 三色灯-绿", IsActive = true });
                OutputIOList.Add(new IoPointModel { ChannelIndex = 2, Name = "DO_02 主气阀电磁阀", IsActive = true });
            }
            else
            {
                InputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "DI_00 扩展卡物料感应", IsActive = true });
                InputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "DI_01 夹爪到位传感器", IsActive = false });

                OutputIOList.Add(new IoPointModel { ChannelIndex = 0, Name = "DO_00 气缸吸盘电磁阀", IsActive = false });
                OutputIOList.Add(new IoPointModel { ChannelIndex = 1, Name = "DO_01 环形光源供电", IsActive = true });
            }
        }

        #endregion

        #region 命令回调

        private void InitCommands()
        {
            SetExposureTimeCommand = new RelayCommand(_ => { /* TODO: [source: 5] SelectedCameraDevice.SetExposureTime() */ });
            SetGainCommand = new RelayCommand(_ => { /* TODO: [source: 5] SelectedCameraDevice.SetGain() */ });
            StartGrabCommand = new RelayCommand(_ => { IsGrabbing = true; });
            StopGrabCommand = new RelayCommand(_ => { IsGrabbing = false; });
            SoftwareTriggerCommand = new RelayCommand(_ => { });

            HomeAxisCommand = new RelayCommand(_ =>
            {
                // TODO: [source: 5] SelectedMotionDevice.HomeAxis(SelectedAxis.AxisIndex)[cite: 5]
            });

            JogCommand = new RelayCommand(param =>
            {
                // TODO: [source: 5] SelectedMotionDevice.Jog(SelectedAxis.AxisIndex, param?.ToString(), JogStep)[cite: 5]
            });
        }

        #endregion
    }

    #region 辅助数据模型 (严格兼容 C# 7.3)

    /// <summary>
    /// 统一设备信息封装模型
    /// </summary>
    public class DeviceInfoModel
    {
        public string DeviceId { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>
    /// 板卡轴信息封装模型
    /// </summary>
    public class AxisInfoModel
    {
        public int AxisIndex { get; set; }
        public string AxisName { get; set; }
    }

    /// <summary>
    /// 数字量 IO 点位模型 (支持双向绑定和硬件状态切换)
    /// </summary>
    public class IoPointModel : ViewModelBase
    {
        public int ChannelIndex { get; set; }
        public string Name { get; set; }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    OnPropertyChanged(nameof(StateText));
                    // TODO: [source: 5] 触发硬件 IO 写入，例如 IIoService.WriteDO(ChannelIndex, value)[cite: 5]
                }
            }
        }

        public string StateText => IsActive ? "ON" : "OFF";
    }

    #endregion
}