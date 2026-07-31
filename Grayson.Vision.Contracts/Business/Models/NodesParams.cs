using Grayson.Vision.Contracts.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Business.Models
{

    /// <summary>
    /// 节点内部参数模型,已经废弃？
    /// </summary>
    public class NodeParam : ViewModelBase
    {
        private string _key;
        public string Key { get => _key; set => Set(ref _key, value); }

        private object _value;
        public object Value { get => _value; set => Set(ref _value, value); }

        public NodeParam() { }
        public NodeParam(string key, object value) { Key = key; Value = value; }
    }
    // =========================================================================
    // 23 个节点的专属 Inspector 属性参数 Model 集合
    // =========================================================================


    #region 1. 图像采集与输入 (Image Acquisition)

    // 1.1 相机采集
    public class AcquireImageParam : INotifyPropertyChanged
    {
        private string _cameraName = "Cam_01";
        public string CameraName { get => _cameraName; set { _cameraName = value; OnPropertyChanged(nameof(CameraName)); } }

        private string _triggerMode = "Hardware"; // Hardware / Software / Continuous
        public string TriggerMode { get => _triggerMode; set { _triggerMode = value; OnPropertyChanged(nameof(TriggerMode)); } }

        private double _exposureTime = 5000.0; // μs
        public double ExposureTime { get => _exposureTime; set { _exposureTime = value; OnPropertyChanged(nameof(ExposureTime)); } }

        private double _gain = 1.0;
        public double Gain { get => _gain; set { _gain = value; OnPropertyChanged(nameof(Gain)); } }

        private int _timeoutMs = 3000;
        public int TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; OnPropertyChanged(nameof(TimeoutMs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 1.2 本地图像加载
    public class LoadImageParam : INotifyPropertyChanged
    {
        private string _filePath = @"C:\Images\test.bmp";
        public string FilePath { get => _filePath; set { _filePath = value; OnPropertyChanged(nameof(FilePath)); } }

        private bool _isBatchFolder = false;
        public bool IsBatchFolder { get => _isBatchFolder; set { _isBatchFolder = value; OnPropertyChanged(nameof(IsBatchFolder)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 1.3 3D点云采集
    public class AcquirePointCloudParam : INotifyPropertyChanged
    {
        private string _sensorIp = "192.168.1.100";
        public string SensorIp { get => _sensorIp; set { _sensorIp = value; OnPropertyChanged(nameof(SensorIp)); } }

        private double _zSamplingInterval = 0.02; // mm
        public double ZSamplingInterval { get => _zSamplingInterval; set { _zSamplingInterval = value; OnPropertyChanged(nameof(ZSamplingInterval)); } }

        private int _exposureTimeUs = 200;
        public int ExposureTimeUs { get => _exposureTimeUs; set { _exposureTimeUs = value; OnPropertyChanged(nameof(ExposureTimeUs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 2. 预处理与 ROI (Preprocessing & ROI)

    // 2.1 图像预处理 (滤波/灰度化/增强)
    public class ImagePreprocessParam : INotifyPropertyChanged
    {
        private string _filterType = "GaussianBlur"; // GaussianBlur / Median / EqualizeHist
        public string FilterType { get => _filterType; set { _filterType = value; OnPropertyChanged(nameof(FilterType)); } }

        private int _kernelSize = 5;
        public int KernelSize { get => _kernelSize; set { _kernelSize = value; OnPropertyChanged(nameof(KernelSize)); } }

        private double _contrastGamma = 1.2;
        public double ContrastGamma { get => _contrastGamma; set { _contrastGamma = value; OnPropertyChanged(nameof(ContrastGamma)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 2.2 ROI 裁剪/掩膜
    public class RoiCropParam : INotifyPropertyChanged
    {
        private int _x = 100;
        public int X { get => _x; set { _x = value; OnPropertyChanged(nameof(X)); } }

        private int _y = 100;
        public int Y { get => _y; set { _y = value; OnPropertyChanged(nameof(Y)); } }

        private int _width = 640;
        public int Width { get => _width; set { _width = value; OnPropertyChanged(nameof(Width)); } }

        private int _height = 480;
        public int Height { get => _height; set { _height = value; OnPropertyChanged(nameof(Height)); } }

        private bool _useFollowMatrix = true; // 是否跟随定位基准矩阵
        public bool UseFollowMatrix { get => _useFollowMatrix; set { _useFollowMatrix = value; OnPropertyChanged(nameof(UseFollowMatrix)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 3. 定位与测量 (Locating & Measurement)

    // 3.1 形状/模板匹配 (Shape / Model Match)
    public class TemplateMatchParam : INotifyPropertyChanged
    {
        private string _modelPath = @"C:\Models\part.shm";
        public string ModelPath { get => _modelPath; set { _modelPath = value; OnPropertyChanged(nameof(ModelPath)); } }

        private double _minScore = 0.7;
        public double MinScore { get => _minScore; set { _minScore = value; OnPropertyChanged(nameof(MinScore)); } }

        private double _angleStart = -180.0;
        public double AngleStart { get => _angleStart; set { _angleStart = value; OnPropertyChanged(nameof(AngleStart)); } }

        private double _angleExtent = 360.0;
        public double AngleExtent { get => _angleExtent; set { _angleExtent = value; OnPropertyChanged(nameof(AngleExtent)); } }

        private int _numToFind = 1;
        public int NumToFind { get => _numToFind; set { _numToFind = value; OnPropertyChanged(nameof(NumToFind)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 3.2 边缘测量 / 卡尺 (Caliper Tool)
    public class CaliperParam : INotifyPropertyChanged
    {
        private string _edgeTransition = "Negative"; // Positive / Negative / Both
        public string EdgeTransition { get => _edgeTransition; set { _edgeTransition = value; OnPropertyChanged(nameof(EdgeTransition)); } }

        private double _threshold = 30.0;
        public double Threshold { get => _threshold; set { _threshold = value; OnPropertyChanged(nameof(Threshold)); } }

        private int _caliperCount = 20;
        public int CaliperCount { get => _caliperCount; set { _caliperCount = value; OnPropertyChanged(nameof(CaliperCount)); } }

        private int _projectionLength = 30;
        public int ProjectionLength { get => _projectionLength; set { _projectionLength = value; OnPropertyChanged(nameof(ProjectionLength)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 3.3 Blob 连通域分析
    public class BlobAnalysisParam : INotifyPropertyChanged
    {
        private int _thresholdMin = 100;
        public int ThresholdMin { get => _thresholdMin; set { _thresholdMin = value; OnPropertyChanged(nameof(ThresholdMin)); } }

        private int _thresholdMax = 255;
        public int ThresholdMax { get => _thresholdMax; set { _thresholdMax = value; OnPropertyChanged(nameof(ThresholdMax)); } }

        private double _areaMin = 50.0;
        public double AreaMin { get => _areaMin; set { _areaMin = value; OnPropertyChanged(nameof(AreaMin)); } }

        private double _areaMax = 999999.0;
        public double AreaMax { get => _areaMax; set { _areaMax = value; OnPropertyChanged(nameof(AreaMax)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 3.4 几何关系测量 (线线夹角/点到线距离等)
    public class GeometryMeasureParam : INotifyPropertyChanged
    {
        private string _measureType = "PointToLine"; // PointToLine / LineToLine / CircleToCircle
        public string MeasureType { get => _measureType; set { _measureType = value; OnPropertyChanged(nameof(MeasureType)); } }

        private double _upperTolerance = 0.05; // 上公差 mm
        public double UpperTolerance { get => _upperTolerance; set { _upperTolerance = value; OnPropertyChanged(nameof(UpperTolerance)); } }

        private double _lowerTolerance = -0.05; // 下公差 mm
        public double LowerTolerance { get => _lowerTolerance; set { _lowerTolerance = value; OnPropertyChanged(nameof(LowerTolerance)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 4. 识别与标定 (Identification & Calibration)

    // 4.1 条码/二维码识别
    public class ReadBarcodeParam : INotifyPropertyChanged
    {
        private string _codeType = "Data Matrix"; // QR Code / Data Matrix / Code128
        public string CodeType { get => _codeType; set { _codeType = value; OnPropertyChanged(nameof(CodeType)); } }

        private int _timeoutMs = 1000;
        public int TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; OnPropertyChanged(nameof(TimeoutMs)); } }

        private bool _autoPolarity = true; // 反色极性自动判断
        public bool AutoPolarity { get => _autoPolarity; set { _autoPolarity = value; OnPropertyChanged(nameof(AutoPolarity)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.2 OCR 字符识别
    public class OcrReadParam : INotifyPropertyChanged
    {
        private string _fontModel = "Industrial_0-9_A-Z.omc";
        public string FontModel { get => _fontModel; set { _fontModel = value; OnPropertyChanged(nameof(FontModel)); } }

        private string _charPattern = ""; // 正则校验表达式，如 [0-9]{8}
        public string CharPattern { get => _charPattern; set { _charPattern = value; OnPropertyChanged(nameof(CharPattern)); } }

        private int _charWidth = 20;
        public int CharWidth { get => _charWidth; set { _charWidth = value; OnPropertyChanged(nameof(CharWidth)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.3 九点/手眼标定 (Calib2D)
    public class Calib2DParam : INotifyPropertyChanged
    {
        private string _calibFilePath = @"C:\Calib\Cam01_9Point.cal";
        public string CalibFilePath { get => _calibFilePath; set { _calibFilePath = value; OnPropertyChanged(nameof(CalibFilePath)); } }

        private string _calibType = "NinePoint"; // NinePoint / EyeInHand / EyeToHand
        public string CalibType { get => _calibType; set { _calibType = value; OnPropertyChanged(nameof(CalibType)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 5. AI 深度学习 (Deep Learning)

    // 5.1 AI 缺陷检测 (Defect Detection)
    public class AiDefectDetectParam : INotifyPropertyChanged
    {
        private string _onnxModelPath = @"C:\Models\Defect_YOLOv8.onnx";
        public string OnnxModelPath { get => _onnxModelPath; set { _onnxModelPath = value; OnPropertyChanged(nameof(OnnxModelPath)); } }

        private double _confidenceThresh = 0.5;
        public double ConfidenceThresh { get => _confidenceThresh; set { _confidenceThresh = value; OnPropertyChanged(nameof(ConfidenceThresh)); } }

        private double _iouThresh = 0.45;
        public double IouThresh { get => _iouThresh; set { _iouThresh = value; OnPropertyChanged(nameof(IouThresh)); } }

        private string _gpuDevice = "GPU:0"; // CPU / GPU:0
        public string GpuDevice { get => _gpuDevice; set { _gpuDevice = value; OnPropertyChanged(nameof(GpuDevice)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 5.2 AI 目标分类 (Classification)
    public class AiClassifyParam : INotifyPropertyChanged
    {
        private string _modelPath = @"C:\Models\Classify.onnx";
        public string ModelPath { get => _modelPath; set { _modelPath = value; OnPropertyChanged(nameof(ModelPath)); } }

        private double _passThreshold = 0.85;
        public double PassThreshold { get => _passThreshold; set { _passThreshold = value; OnPropertyChanged(nameof(PassThreshold)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 6. 流程逻辑与控制 (Logic Control)

    // 6.1 If 条件分支
    public class ConditionIfParam : INotifyPropertyChanged
    {
        private string _compareVariable = "MatchScore";
        public string CompareVariable { get => _compareVariable; set { _compareVariable = value; OnPropertyChanged(nameof(CompareVariable)); } }

        private string _operator = ">="; // ==, !=, >, <, >=, <=
        public string Operator { get => _operator; set { _operator = value; OnPropertyChanged(nameof(Operator)); } }

        private string _compareValue = "0.8";
        public string CompareValue { get => _compareValue; set { _compareValue = value; OnPropertyChanged(nameof(CompareValue)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.2 For 循环控制
    public class LoopForParam : INotifyPropertyChanged
    {
        private int _startIndex = 0;
        public int StartIndex { get => _startIndex; set { _startIndex = value; OnPropertyChanged(nameof(StartIndex)); } }

        private int _endIndex = 5;
        public int EndIndex { get => _endIndex; set { _endIndex = value; OnPropertyChanged(nameof(EndIndex)); } }

        private int _step = 1;
        public int Step { get => _step; set { _step = value; OnPropertyChanged(nameof(Step)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.3 Break 中断
    public class BreakParam : INotifyPropertyChanged
    {
        private string _breakReason = "异常直接跳出";
        public string BreakReason { get => _breakReason; set { _breakReason = value; OnPropertyChanged(nameof(BreakReason)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.4 Delay 延迟等待
    public class DelayParam : INotifyPropertyChanged
    {
        private int _delayTimeMs = 100;
        public int DelayTimeMs { get => _delayTimeMs; set { _delayTimeMs = value; OnPropertyChanged(nameof(DelayTimeMs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.5 C# Script 动态脚本定义
    public class ScriptParam : INotifyPropertyChanged
    {
        private string _scriptCode = "// C# 表达式或逻辑\nvar result = GetValue(\"Area\") > 500 ? \"OK\" : \"NG\";\nSetGlobal(\"FinalResult\", result);";
        public string ScriptCode { get => _scriptCode; set { _scriptCode = value; OnPropertyChanged(nameof(ScriptCode)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 7. 通讯与 I/O (Communication & I/O)

    // 7.1 PLC 读写 (Modbus / S7 / MC)
    public class PlcReadWriteParam : INotifyPropertyChanged
    {
        private string _protocol = "SiemensS7"; // SiemensS7 / ModbusTCP / MitsubishiMC / OmronFINS
        public string Protocol { get => _protocol; set { _protocol = value; OnPropertyChanged(nameof(Protocol)); } }

        private string _plcIp = "192.168.1.10";
        public string PlcIp { get => _plcIp; set { _plcIp = value; OnPropertyChanged(nameof(PlcIp)); } }

        private int _plcPort = 102;
        public int PlcPort { get => _plcPort; set { _plcPort = value; OnPropertyChanged(nameof(PlcPort)); } }

        private string _registerAddress = "DB1.DBD0";
        public string RegisterAddress { get => _registerAddress; set { _registerAddress = value; OnPropertyChanged(nameof(RegisterAddress)); } }

        private string _operateType = "Write"; // Read / Write
        public string OperateType { get => _operateType; set { _operateType = value; OnPropertyChanged(nameof(OperateType)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 7.2 TCP/IP 客户端/服务端通讯
    public class SocketCommParam : INotifyPropertyChanged
    {
        private string _mode = "Client"; // Client / Server
        public string Mode { get => _mode; set { _mode = value; OnPropertyChanged(nameof(Mode)); } }

        private string _ipAddress = "127.0.0.1";
        public string IpAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(nameof(IpAddress)); } }

        private int _port = 8080;
        public int Port { get => _port; set { _port = value; OnPropertyChanged(nameof(Port)); } }

        private string _sendTemplate = "X={X};Y={Y};A={Angle};Result={Result};";
        public string SendTemplate { get => _sendTemplate; set { _sendTemplate = value; OnPropertyChanged(nameof(SendTemplate)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 7.3 数字 I/O 板卡控制
    public class DigitalIoParam : INotifyPropertyChanged
    {
        private int _cardIndex = 0;
        public int CardIndex { get => _cardIndex; set { _cardIndex = value; OnPropertyChanged(nameof(CardIndex)); } }

        private int _ioChannel = 1; // DO 通道号
        public int IoChannel { get => _ioChannel; set { _ioChannel = value; OnPropertyChanged(nameof(IoChannel)); } }

        private bool _setOutputState = true; // High / Low
        public bool SetOutputState { get => _setOutputState; set { _setOutputState = value; OnPropertyChanged(nameof(SetOutputState)); } }

        private int _pulseWidthMs = 50; // 脉冲输出时长(毫秒)
        public int PulseWidthMs { get => _pulseWidthMs; set { _pulseWidthMs = value; OnPropertyChanged(nameof(PulseWidthMs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion

    #region 8. 数据与数据存储 (Data & Storage)

    // 8.1 MES 工厂系统交互
    public class MesReportParam : INotifyPropertyChanged
    {
        private string _apiUrl = "http://10.20.100.5/api/v1/mes/upload";
        public string ApiUrl { get => _apiUrl; set { _apiUrl = value; OnPropertyChanged(nameof(ApiUrl)); } }

        private string _stationCode = "OP10_VIS_01";
        public string StationCode { get => _stationCode; set { _stationCode = value; OnPropertyChanged(nameof(StationCode)); } }

        private int _timeoutMs = 2000;
        public int TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; OnPropertyChanged(nameof(TimeoutMs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 8.2 数据与图像存盘 (Save Data & Image)
    public class SaveDataParam : INotifyPropertyChanged
    {
        private string _saveDir = @"D:\VisionData\Logs";
        public string SaveDir { get => _saveDir; set { _saveDir = value; OnPropertyChanged(nameof(SaveDir)); } }

        private bool _saveCsv = true;
        public bool SaveCsv { get => _saveCsv; set { _saveCsv = value; OnPropertyChanged(nameof(SaveCsv)); } }

        private bool _saveNgImageOnly = true; // 是否仅保存 NG 图像
        public bool SaveNgImageOnly { get => _saveNgImageOnly; set { _saveNgImageOnly = value; OnPropertyChanged(nameof(SaveNgImageOnly)); } }

        private string _imageFormat = "JPG"; // JPG / PNG / BMP
        public string ImageFormat { get => _imageFormat; set { _imageFormat = value; OnPropertyChanged(nameof(ImageFormat)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion


    #region 补充缺失的 23 个 NodeType 专属 Model 参数类

    // 1.4 运动轴移动参数
    public class AxisMoveParam : INotifyPropertyChanged
    {
        private string _axisName = "X_Axis";
        public string AxisName { get => _axisName; set { _axisName = value; OnPropertyChanged(nameof(AxisName)); } }

        private double _targetPosition = 100.0; // mm
        public double TargetPosition { get => _targetPosition; set { _targetPosition = value; OnPropertyChanged(nameof(TargetPosition)); } }

        private double _speed = 50.0; // mm/s
        public double Speed { get => _speed; set { _speed = value; OnPropertyChanged(nameof(Speed)); } }

        private bool _isRelative = false; // 是否为相对运动
        public bool IsRelative { get => _isRelative; set { _isRelative = value; OnPropertyChanged(nameof(IsRelative)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 1.5 光照控制参数
    public class LightControlParam : INotifyPropertyChanged
    {
        private int _channel = 1;
        public int Channel { get => _channel; set { _channel = value; OnPropertyChanged(nameof(Channel)); } }

        private int _brightness = 128; // 0 - 255
        public int Brightness { get => _brightness; set { _brightness = value; OnPropertyChanged(nameof(Brightness)); } }

        private bool _enableLight = true;
        public bool EnableLight { get => _enableLight; set { _enableLight = value; OnPropertyChanged(nameof(EnableLight)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 3.2 多路分支参数
    public class SwitchCaseParam : INotifyPropertyChanged
    {
        private string _switchVariable = "ProductType";
        public string SwitchVariable { get => _switchVariable; set { _switchVariable = value; OnPropertyChanged(nameof(SwitchVariable)); } }

        private string _cases = "TypeA,TypeB,TypeC";
        public string Cases { get => _cases; set { _cases = value; OnPropertyChanged(nameof(Cases)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 3.5 状态信号等待参数
    public class WaitSignalParam : INotifyPropertyChanged
    {
        private string _signalName = "PLC_Trigger_Ready";
        public string SignalName { get => _signalName; set { _signalName = value; OnPropertyChanged(nameof(SignalName)); } }

        private int _timeoutMs = 10000;
        public int TimeoutMs { get => _timeoutMs; set { _timeoutMs = value; OnPropertyChanged(nameof(TimeoutMs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.1 坐标计算与偏移参数
    public class OffsetMathParam : INotifyPropertyChanged
    {
        private double _offsetX = 0.0;
        public double OffsetX { get => _offsetX; set { _offsetX = value; OnPropertyChanged(nameof(OffsetX)); } }

        private double _offsetY = 0.0;
        public double OffsetY { get => _offsetY; set { _offsetY = value; OnPropertyChanged(nameof(OffsetY)); } }

        private double _offsetAngle = 0.0;
        public double OffsetAngle { get => _offsetAngle; set { _offsetAngle = value; OnPropertyChanged(nameof(OffsetAngle)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.2 公式计算/脚本参数
    public class ScriptMathParam : INotifyPropertyChanged
    {
        private string _expression = "Width * Height / 100.0";
        public string Expression { get => _expression; set { _expression = value; OnPropertyChanged(nameof(Expression)); } }

        private string _resultVar = "CalcResult";
        public string ResultVar { get => _resultVar; set { _resultVar = value; OnPropertyChanged(nameof(ResultVar)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.3 变量映射参数
    public class VarMapperParam : INotifyPropertyChanged
    {
        private string _sourceVar = "MatchX";
        public string SourceVar { get => _sourceVar; set { _sourceVar = value; OnPropertyChanged(nameof(SourceVar)); } }

        private string _targetVar = "Robot_Target_X";
        public string TargetVar { get => _targetVar; set { _targetVar = value; OnPropertyChanged(nameof(TargetVar)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 4.4 字符串格式化参数
    public class StringFormatParam : INotifyPropertyChanged
    {
        private string _formatPattern = "SN_{0:yyyyMMdd}_{1}";
        public string FormatPattern { get => _formatPattern; set { _formatPattern = value; OnPropertyChanged(nameof(FormatPattern)); } }

        private string _inputArgs = "CurrentDate, BarCode";
        public string InputArgs { get => _inputArgs; set { _inputArgs = value; OnPropertyChanged(nameof(InputArgs)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 5.3 独立图像保存参数
    public class SaveImageParam : INotifyPropertyChanged
    {
        private string _savePath = @"D:\Images\Cam01";
        public string SavePath { get => _savePath; set { _savePath = value; OnPropertyChanged(nameof(SavePath)); } }

        private string _format = "PNG";
        public string Format { get => _format; set { _format = value; OnPropertyChanged(nameof(Format)); } }

        private bool _onlySaveNg = true;
        public bool OnlySaveNg { get => _onlySaveNg; set { _onlySaveNg = value; OnPropertyChanged(nameof(OnlySaveNg)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.1 复合子流程参数
    public class CompositeFlowParam : INotifyPropertyChanged
    {
        private string _subProcessId = "";
        public string SubProcessId { get => _subProcessId; set { _subProcessId = value; OnPropertyChanged(nameof(SubProcessId)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.2 异常捕获参数
    public class TryCatchParam : INotifyPropertyChanged
    {
        private string _exceptionVar = "LastError";
        public string ExceptionVar { get => _exceptionVar; set { _exceptionVar = value; OnPropertyChanged(nameof(ExceptionVar)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // 6.3 流程终止参数
    public class TerminateFlowParam : INotifyPropertyChanged
    {
        private string _exitCode = "NG_EXIT";
        public string ExitCode { get => _exitCode; set { _exitCode = value; OnPropertyChanged(nameof(ExitCode)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    #endregion
}
