using Grayson.Vision.Contracts.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Business.Models
{
    public class Point2D
    {
        public double X { get; set; }
        public double Y { get; set; }
        public Point2D() { }
        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// 工业视觉节点的 6 大核心业务分类
    /// </summary>
    public enum NodeCategory
    {
        [Description("⚙️ 设备与 IO 控制类")]
        DeviceIO,

        [Description("👁️ Halcon 算法与视觉处理类")]
        Vision,

        [Description("🧠 逻辑控制与数据流类")]
        Logic,

        [Description("📊 数据处理与转换类")]
        DataProcess,

        [Description("🏭 生产与数据对接类")]
        SystemMES,

        [Description("🛑 复合子流程与异常处理类")]
        CompositeEx
    }




    /// <summary>
    /// 23 种全量具体节点类型枚举
    /// </summary>
    public enum NodeType
    {
        // --- 1. 设备与 IO 控制类 ---
        AcquireImage,       // 相机采集
        PlcReadWrite,       // PLC 读写
        AxisMove,           // 运动轴移动
        DigitalOutput,      // 数字 IO 输出
        LightControl,       // 光照控制

        // --- 2. Halcon 算法与视觉处理类 ---
        TemplateMatch,      // 模板匹配
        Calib2D,            // 九点标定/手眼标定
        Measurement,        // 几何测量
        DefectDetect,       // 缺陷检测
        ReadBarcode,        // 条码/二维码识别
        DlInference,        // 深度学习推理

        // --- 3. 逻辑控制与数据流类 ---
        ConditionIf,        // 条件分支 (If/Else)
        SwitchCase,         // 多路分支
        ForLoop,            // 循环控制
        Delay,              // 延时等待
        WaitSignal,         // 状态信号等待
        Merge,              // 数据汇聚/合并

        // --- 4. 数据处理与转换类 ---
        OffsetMath,         // 坐标计算/偏移
        ScriptMath,         // 公式计算
        VarMapper,          // 变量映射
        StringFormat,       // 字符串格式化

        // --- 5. 生产与数据对接类 ---
        MesReport,          // MES 上报
        SaveData,           // 数据存盘
        SaveImage,          // 图像保存

        // --- 6. 复合子流程与异常处理类 ---
        CompositeFlow,      // 子流程节点
        TryCatch,           // 异常捕获
        TerminateFlow       // 流程终止
    }
    /// <summary>
    /// 所有流程节点的基类
    /// </summary>
    public abstract class FlowNodeBase : ViewModelBase
    {
        private string _nodeId = Guid.NewGuid().ToString("N");
        // 节点全局唯一 ID，配方序列化标识节点
        public string NodeId { get => _nodeId; set => Set(ref _nodeId, value); }
        // 节点全局唯一 ID，配方序列化标识节点
        public string Id
        {
            get => NodeId;
            set => NodeId = value;
        }
        // 节点类型枚举
        private NodeType _type;
        public NodeType Type { get => _type; set => Set(ref _type, value); }
        // 节点在画布上显示的名称
        private string _displayName;
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

        // 节点是否启用，关闭则跳过执行
        private bool _enable = true;
        public bool Enable { get => _enable; set => Set(ref _enable, value); }
        // 节点是否正在执行中
        private bool _isRunning;
        public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }
        // 节点在画布上的 X 坐标位置
        private double _posX;
        public double PosX
        {
            get => _posX;
            set { Set(ref _posX, value); OnPositionChanged?.Invoke(this, EventArgs.Empty); }
        }
        // 节点在画布上的 Y 坐标位置

        private double _posY;
        public double PosY
        {
            get => _posY;
            set { Set(ref _posY, value); OnPositionChanged?.Invoke(this, EventArgs.Empty); }
        }
        // 节点分类，用于在节点面板中进行分组显示
        private NodeCategory _category;
        public NodeCategory Category { get => _category; set => Set(ref _category, value); }
        // 节点分类名称，用于在节点面板中进行分组显示
        private NodeCategory _categoryName;
        public NodeCategory CategoryName { get => _categoryName; set => Set(ref _categoryName, value); }


        //节点描述信息，主要用于在属性面板中显示节点的功能说明
        private string _description;
        public string Description { get => _description; set => Set(ref _description, value); }
        // 节点参数模型，主要用于在属性面板中显示节点的参数配置

        private object _parameterModel;
        public object ParameterModel { get => _parameterModel; set => Set(ref _parameterModel, value); }

        // 节点使用指南，主要用于在属性面板中显示节点的使用说明
        private string _usageGuide;
        public string UsageGuide
        {
            get => _usageGuide;
            set { _usageGuide = value; OnPropertyChanged(nameof(UsageGuide)); }
        }
        // 节点位置变化事件，用于通知连线更新
        public event EventHandler OnPositionChanged;

        // 连线端口集合
        public ObservableCollection<NodePort> InputPorts { get; set; } = new ObservableCollection<NodePort>();
        public ObservableCollection<NodePort> OutputPorts { get; set; } = new ObservableCollection<NodePort>();

        private double _height = 65.0; // 默认给一个基础高度
        /// <summary>
        /// 节点在画布上的高度（根据端口数量由 AutoLayoutNodePorts 自动动态计算撑开）
        /// </summary>
        public double Height
        {
            get => _height;
            set => Set(ref _height, value); // 如果使用了 Set 方法，自动触发 OnPropertyChanged
        }

        /// <summary>
        /// 全量端口集合（自动合并输入和输出端口，供 NodeControl 统一渲染）
        /// </summary>
        public IEnumerable<NodePort> AllPorts
        {
            get
            {
                if (InputPorts != null)
                    foreach (var p in InputPorts) yield return p;

                if (OutputPorts != null)
                    foreach (var p in OutputPorts) yield return p;
            }
        }

    }
}
