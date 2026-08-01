using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

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

    // 端口模型：节点上每个端口的定义

    public class NodePort : ViewModelBase
    {
        public string PortId { get; set; } = Guid.NewGuid().ToString("N");
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

    }


  




    /// <summary>
    /// 工作流容器模型
    /// </summary>
    public class FlowProcessModel : ViewModelBase
    {
        public string ProcessId { get; set; } = Guid.NewGuid().ToString("N");
        public string ProcessName { get; set; } = "主流程";

        public ObservableCollection<FlowNodeBase> Nodes { get; set; } = new ObservableCollection<FlowNodeBase>();
        public ObservableCollection<ConnectionModel> Connections { get; set; } = new ObservableCollection<ConnectionModel>();
     
    }




    public class ConnectionModel : ViewModelBase
    {
        // 连线唯一ID
        public string ConnectionId { get; set; } = Guid.NewGuid().ToString("N");
        // 连线起点节点
        public FlowNodeBase SourceNode { get; set; }
        // 连线起点端口类型
        public ConnectorType SourceConnector { get; set; }
        public string SourcePortId { get; set; }


        public FlowNodeBase TargetNode { get; set; }
        public ConnectorType TargetConnector { get; set; } = ConnectorType.Input;
        public string TargetPortId { get; set; }

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
                SourceConnector = sourcePort.Connector;
                SourceRelativeX = sourcePort.RelativeX; // 🌟 记录 RelativeX
                SourceRelativeY = sourcePort.RelativeY;
                Category = sourcePort.Category;
                DataType = sourcePort.DataType;
            }

            if (targetPort != null)
            {
                TargetPortId = targetPort.PortId;
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
        // 🌟 新增：动态获取源端口对象
        public NodePort SourcePort => SourceNode?.InputPorts.Concat(SourceNode.OutputPorts)
                                                .FirstOrDefault(p => p.PortId == SourcePortId);

        // 🌟 新增：动态获取目标端口对象
        public NodePort TargetPort => TargetNode?.InputPorts.Concat(TargetNode.OutputPorts)
                                                .FirstOrDefault(p => p.PortId == TargetPortId);
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