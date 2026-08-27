using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

namespace Grayson.Vision.Contracts.Flow.Nodes
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

    // 端口模型：节点上每个端口的定义

    public class NodePort : ViewModelBase
    {
        // 🌟 改为默认以 PortName 或固定 Key 作为 ID
        // 🌟 保证 PortId 只要初始化一次就不会在 get 中反复变动
        private string _portId = Guid.NewGuid().ToString("N");
        public string PortId
        {
            get => _portId;
            set => Set(ref _portId, value);
        }
        public string PortName { get; set; }        // 端口显示的名称 (如 "In", "OK", "NG", "Pass", "Fail")
        public PortType PortType { get; set; }     // 输入还是输出
        public ConnectorType Connector { get; set; }// 端口对应的连接语义

        // ===== 【新增】数据端口扩展 =====
        private PortCategory _category = PortCategory.Data;
        public PortCategory Category
        {
            get => _category;
            set => Set(ref _category, value);
        }

        /// <summary>
        /// 数据端口的数据类型（如 "HImage", "Point2D", "double", "string"）
        /// </summary>
        public string DataType { get; set; } = "object";

        /// <summary>
        /// 数据端口当前持有的数据缓存或默认值
        /// </summary>
        private object _dataValue;
        public object DataValue
        {
            get => _dataValue;
            set => Set(ref _dataValue, value);
        }

        // UI 渲染坐标辅助：记录该端口在节点上的 Y 轴相对相对偏移（如 15, 35, 55）
        private PortPosition _position = PortPosition.Top;
        public PortPosition Position
        {
            get => _position;
            set => Set(ref _position, value);
        }

        private double _relativeX;
        public double RelativeX
        {
            get => _relativeX;
            set => Set(ref _relativeX, value);
        }

        private double _relativeY;
        public double RelativeY
        {
            get => _relativeY;
            set => Set(ref _relativeY, value);
        }

        // 端口颜色（如 蓝色代表普通数据，紫色代表图像等）
        public string ColorHex { get; set; } = "#1890FF";
        // 🌟 新增：运行时持有的必填状态，由 NodeFactory 根据 Attribute 赋值
        public bool IsRequired { get; set; }

    }


  




    /// <summary>
    /// 工作流容器模型
    /// </summary>
    public class FlowProcessModel : ViewModelBase
    {
        // 🌟 改为默认 或固定 Key 作为 ID
        private string _processId= Guid.NewGuid().ToString("N");
        public string ProcessId
        {
            get => _processId;
            set => _processId = value;
        }
        public string ProcessName { get; set; } = "主流程";

        public ObservableCollection<FlowNodeBase> Nodes { get; set; } = new ObservableCollection<FlowNodeBase>();
        public ObservableCollection<ConnectionModel> Connections { get; set; } = new ObservableCollection<ConnectionModel>();
     
    }




    public class ConnectionModel : ViewModelBase
    {

        // 🌟 改为默认 或固定 Key 作为 ID
        private string _connectionId = Guid.NewGuid().ToString("N");
        public string ConnectionId
        {
            get =>  _connectionId;
            set => _connectionId = value;
        }
        // 连线起点节点
        public FlowNodeBase SourceNode { get; set; }
        // 连线起点端口类型
        public ConnectorType SourceConnector { get; set; }
        public string SourcePortId { get; set; }

        /// <summary>🌟 创建连线时缓存的源端口名，保存时即使端口对象被动态端口机制替换，也能持久化正确端口名</summary>
        public string SourcePortName { get; set; }


        public FlowNodeBase TargetNode { get; set; }
        public ConnectorType TargetConnector { get; set; } = ConnectorType.Input;
        public string TargetPortId { get; set; }

        /// <summary>🌟 创建连线时缓存的目标端口名，保存时兜底使用</summary>
        public string TargetPortName { get; set; }

        private double _startX;
        public double StartX { get => _startX; set => Set(ref _startX, value); }

        private double _startY;
        public double StartY { get => _startY; set => Set(ref _startY, value); }

        private double _endX;
        public double EndX { get => _endX; set => Set(ref _endX, value); }

        private double _endY;
        public double EndY { get => _endY; set => Set(ref _endY, value); }

        public ConnectionModel() { }

        // ===== 【新增】关联的具体端口 ID 与类型 =====
        public PortCategory Category { get; set; } = PortCategory.Data;
        public string DataType { get; set; }

        public double SourceRelativeX { get; set; } = 0;
        public double SourceRelativeY { get; set; } = 0;

        public double TargetRelativeX { get; set; } = 0;
        public double TargetRelativeY { get; set; } = 0;

        public ConnectionModel(FlowNodeBase source, NodePort sourcePort, FlowNodeBase target, NodePort targetPort)
        {
            SourceNode = source;
            TargetNode = target;

            if (sourcePort != null)
            {
                SourcePortId = sourcePort.PortId;
                SourcePortName = sourcePort.PortName; // 🌟 缓存端口名，防止后续端口对象被替换后保存时丢失
                SourceConnector = sourcePort.Connector;
                SourceRelativeX = sourcePort.RelativeX; // 🌟 记录 RelativeX
                SourceRelativeY = sourcePort.RelativeY;
                Category = sourcePort.Category;
                DataType = sourcePort.DataType;
            }

            if (targetPort != null)
            {
                TargetPortId = targetPort.PortId;
                TargetPortName = targetPort.PortName; // 🌟 缓存端口名
                TargetConnector = targetPort.Connector;
                TargetRelativeX = targetPort.RelativeX; // 🌟 记录 RelativeX
                TargetRelativeY = targetPort.RelativeY;
            }

            UpdatePoints();
            SourceNode.OnPositionChanged += (s, e) => UpdatePoints();
            TargetNode.OnPositionChanged += (s, e) => UpdatePoints();
        }
        /// <summary>
        /// 数据流连线颜色（可根据 DataType 动态扩展颜色）
        /// </summary>
        public string ConnectionColor
        {
            get
            {
                if (DataType == "HImage" || DataType == "Image") return "#722ED1"; // 图像专用紫线
                return "#1890FF"; // 默认标准数据蓝线
            }
        }

        /// <summary>
        /// 🌟 精准更新连线的两端坐标：结合 Node 位置 + Port 相对位置
        /// </summary>
        public void UpdatePoints()
        {
            if (SourceNode == null || TargetNode == null) return;

            // 起点坐标 = 源节点位置 + 端点相对 X/Y (不再写死 +160)
            StartX = SourceNode.PosX + SourceRelativeX;
            StartY = SourceNode.PosY + SourceRelativeY;

            // 终点坐标 = 目标节点位置 + 端点相对 X/Y (不再写死 +0)
            EndX = TargetNode.PosX + TargetRelativeX;
            EndY = TargetNode.PosY + TargetRelativeY;
        }
        // 🌟 新增：动态获取源端口对象（源端口一定是输出端口，优先在 OutputPorts 中查找）
        public NodePort SourcePort => SourceNode?.OutputPorts.Concat(SourceNode.InputPorts)
                                                .FirstOrDefault(p => p.PortId == SourcePortId);

        // 🌟 新增：动态获取目标端口对象（目标端口一定是输入端口，优先在 InputPorts 中查找）
        public NodePort TargetPort => TargetNode?.InputPorts.Concat(TargetNode.OutputPorts)
                                                .FirstOrDefault(p => p.PortId == TargetPortId);

        /// <summary>
        /// 🌟 新增：显式绑定节点位置监听事件，并即刻刷新连线两端坐标
        /// </summary>
        public void BindAndUpdate()
        {
            if (SourceNode == null || TargetNode == null) return;

            // 1. 尝试从节点端口列表中匹配最新的端口对象并刷新 Relative 偏移
            var sPort = SourcePort;
            if (sPort != null)
            {
                SourceRelativeX = sPort.RelativeX;
                SourceRelativeY = sPort.RelativeY;
            }

            var tPort = TargetPort;
            if (tPort != null)
            {
                TargetRelativeX = tPort.RelativeX;
                TargetRelativeY = tPort.RelativeY;
            }

            // 2. 解绑防重复，再重新绑定节点移动事件
            SourceNode.OnPositionChanged -= OnNodePositionChanged;
            TargetNode.OnPositionChanged -= OnNodePositionChanged;

            SourceNode.OnPositionChanged += OnNodePositionChanged;
            TargetNode.OnPositionChanged += OnNodePositionChanged;

            // 3. 立刻刷新端点坐标
            UpdatePoints();
        }

        private void OnNodePositionChanged(object sender, EventArgs e)
        {
            UpdatePoints();
        }
    }
           
    public class SharedDataItem : ViewModelBase
    {
        public string Key { get; set; }
        private object _value;
        public object Value
        {
            get => _value;
            set { Set(ref _value, value); OnPropertyChanged(nameof(Type)); }
        }
        public string Type => Value?.GetType().Name ?? "null";
    }
    /// <summary>
    /// 工具箱Item模型：FlowNodeBase的元数据（描述信息），避免“未加载先实例化”的内存与性能浪费
    /// UnitMeta 专服务于“工具箱/菜单栏”：FlowNodeBase 专服务于“流程画布与执行”：
    /// （轻量元数据）：专服务于工具箱/菜单栏/选择列表，只存静态描述信息，避免“未加载先实例化”造成内存开销。
    /// </summary>
    public class UnitMeta
    {
        private NodeCategory _category;

        public string NodeId { get; set; }
        public string DisplayName { get; set; }

        public string Description { get; set; }

        /// <summary>分类展示名，不用手动赋值，修改Category自动刷新</summary>
        public string CategoryName { get; set; }

        public string Icon { get; set; }

        public NodeCategory Category
        {
            get => _category;
            set
            {
                _category = value;
                // 赋值枚举后自动读取Description，回填CategoryName
                CategoryName = GetEnumDescription(_category);
            }
        }

        public NodeType Type { get; set; }

        /// <summary>内部工具：读取枚举Description</summary>
        public static string GetEnumDescription(Enum enumVal)
        {
            FieldInfo field = enumVal.GetType().GetField(enumVal.ToString());
            DescriptionAttribute attr = field.GetCustomAttribute<DescriptionAttribute>();
            return attr?.Description ?? enumVal.ToString();
        }
    }


}