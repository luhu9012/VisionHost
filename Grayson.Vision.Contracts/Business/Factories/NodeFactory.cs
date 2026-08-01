// 业务特性、数据模型命名空间引用
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Grayson.Vision.Contracts.Business.Factories
{
    /// <summary>
    /// 流程节点工厂（全局单例静态工厂）
    /// 核心职责：
    /// 1. 扫描程序集、自动读取节点[Node]特性，缓存所有节点元信息
    /// 2. 生成UI工具箱所需的节点元数据UnitMeta
    /// 3. 根据NodeType创建画布可视节点FlowNode（带端口、独立参数实例）
    /// 4. 创建流程引擎执行器INodeExecutor，用于业务逻辑运行
    /// 5. 支持插件动态手动注册节点类型，适配热加载插件架构
    /// </summary>
    public static class NodeFactory
    {
        /// <summary>
        /// 全局节点注册表缓存
        /// Key：节点枚举类型NodeType
        /// Value元组：(执行器类型, 参数配置实体类型, 节点特性元数据)
        /// </summary>
        private static readonly Dictionary<NodeType, (Type ExecutorType, Type ParamType, NodeAttribute Attribute)> _nodeRegistry
            = new Dictionary<NodeType, (Type, Type, NodeAttribute)>();

        /// <summary>
        /// 节点端口缓存注册表
        /// Key：节点枚举NodeType
        /// Value：该节点所有输入输出端口特性配置集合
        /// </summary>
        private static readonly Dictionary<NodeType, List<NodePortAttribute>> _portRegistry
            = new Dictionary<NodeType, List<NodePortAttribute>>();

        /// <summary>
        /// 初始化标记，防止重复扫描程序集、重复注册节点
        /// </summary>
        private static bool _isInitialized = false;

        /// <summary>
        /// 批量初始化：扫描指定程序集，自动注册所有流程节点执行器
        /// </summary>
        /// <param name="assembliesToScan">需要扫描的程序集；不传则扫描当前AppDomain所有加载程序集</param>
        public static void Initialize(params Assembly[] assembliesToScan)
        {
            // 已初始化直接退出，避免重复反射扫描损耗性能
            if (_isInitialized) return;

            // 判断是否传入指定程序集，无传入则取当前域全部程序集
            var assemblies = assembliesToScan != null && assembliesToScan.Length > 0
                ? assembliesToScan
                : AppDomain.CurrentDomain.GetAssemblies();

            // 遍历每个程序集进行反射解析
            foreach (var assembly in assemblies)
            {
                try
                {
                    // 筛选条件：类、非抽象、实现了INodeExecutor节点执行器接口
                    var types = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && typeof(INodeExecutor).IsAssignableFrom(t));

                    // 遍历筛选后的执行器类型，统一调用注册方法
                    foreach (var type in types)
                    {
                        RegisterExecutorType(type);
                    }
                }
                catch
                {
                    // 反射读取程序集异常直接忽略（部分第三方dll无法反射解析）
                }
            }

            // 标记初始化完成
            _isInitialized = true;
        }

        /// <summary>
        /// 手动注册单个节点执行器类型（插件热加载专用）
        /// 支持外部插件dll动态加载后，单独调用此方法注册节点，无需重新全量扫描程序集
        /// </summary>
        /// <param name="executorType">节点执行器Type</param>
        public static void RegisterExecutorType(Type executorType)
        {
            // 过滤：不是INodeExecutor实现类直接返回
            if (!typeof(INodeExecutor).IsAssignableFrom(executorType)) return;

            // 读取类上标记的Node特性，无特性说明不是流程节点，跳过
            var nodeAttr = executorType.GetCustomAttribute<NodeAttribute>();
            if (nodeAttr == null) return;

            // 写入全局节点缓存：执行器类型、参数实体、节点展示配置
            _nodeRegistry[nodeAttr.Type] = (executorType, nodeAttr.ParameterType, nodeAttr);
            // 读取该节点所有端口特性，存入端口缓存
            _portRegistry[nodeAttr.Type] = executorType.GetCustomAttributes<NodePortAttribute>().ToList();
        }

        /// <summary>
        /// 功能1：生成工具箱节点元数据集合
        /// 供给WPF工具箱界面绑定使用，展示所有可拖拽节点
        /// </summary>
        /// <returns>所有节点UI元数据列表UnitMeta</returns>
        public static List<UnitMeta> GenerateToolboxMetas()
        {
            // 确保工厂已完成程序集扫描初始化
            EnsureInitialized();
            // 将缓存注册表转换为UI工具箱专用实体
            return _nodeRegistry.Select(kvp => new UnitMeta
            {
                NodeId = kvp.Key.ToString(),
                Type = kvp.Value.Attribute.Type,
                Category = kvp.Value.Attribute.Category,
                DisplayName = kvp.Value.Attribute.DisplayName,
                Description = kvp.Value.Attribute.Description,
                Icon = kvp.Value.Attribute.Icon
            }).ToList();
        }

        /// <summary>
        /// 功能2：创建画布可视FlowNode节点对象
        /// 每次调用都会生成全新独立节点实例，参数对象互不共享
        /// </summary>
        /// <param name="type">节点枚举标识</param>
        /// <param name="position">节点在画布上的坐标</param>
        /// <param name="overrideDisplayName">可选：自定义节点显示名称，不传则使用特性默认名称</param>
        /// <returns>画布可视化节点FlowNode</returns>
        public static FlowNode CreateNodeInstance(NodeType type, Point2D position, string overrideDisplayName = null)
        {
            EnsureInitialized();

            // 缓存中找不到对应节点类型，返回兜底默认节点
            if (!_nodeRegistry.TryGetValue(type, out var regInfo))
            {
                // 兜底节点自带标准执行输入/输出端口，保证流程连线不会报错
                var fallback = new FlowNode(type, type.ToString(), NodeCategory.DeviceIO, position);
                fallback.InputPorts.Add(new NodePort { PortName = "ExecIn", PortType = PortType.In, Category = PortCategory.Exec });
                fallback.OutputPorts.Add(new NodePort { PortName = "ExecOut", PortType = PortType.Out, Category = PortCategory.Exec });
                return fallback;
            }

            var attr = regInfo.Attribute;

            // 关键逻辑：每个节点独立实例化参数配置对象，多节点之间参数互不干扰
            object paramInstance = regInfo.ParamType != null
                ? Activator.CreateInstance(regInfo.ParamType)
                : null;

            // 优先使用自定义名称，无自定义则取特性定义的展示名
            string displayName = !string.IsNullOrEmpty(overrideDisplayName) ? overrideDisplayName : attr.DisplayName;
            // 实例化画布节点基础对象
            var node = new FlowNode(attr.Type, displayName, attr.Category, position, attr.Description, paramInstance);

            // 从端口缓存读取配置，动态生成输入输出端口挂载到节点
            if (_portRegistry.TryGetValue(type, out var portAttrs))
            {
                foreach (var pAttr in portAttrs)
                {
                    var port = new NodePort
                    {
                        PortName = pAttr.PortName,
                        PortType = pAttr.PortType,
                        Category = pAttr.Category,
                        DataType = pAttr.DataType,
                        ColorHex = pAttr.ColorHex
                    };

                    // 根据端口类型区分添加到输入/输出集合
                    if (pAttr.PortType == PortType.In)
                        node.InputPorts.Add(port);
                    else
                        node.OutputPorts.Add(port);
                }
            }

            return node;
        }

        /// <summary>
        /// 通过工具箱元数据快速创建画布节点
        /// 拖拽工具箱节点时直接调用，自动携带工具箱定义的显示名称
        /// </summary>
        /// <param name="meta">工具箱节点元数据</param>
        /// <param name="position">画布放置坐标</param>
        /// <returns>画布FlowNode实例</returns>
        public static FlowNode CreateFromMeta(UnitMeta meta, Point2D position)
        {
            // 封装调用创建节点方法，自动传入元数据内的显示名覆盖
            return CreateNodeInstance(meta.Type, position, meta.DisplayName);
        }

        /// <summary>
        /// 功能3：创建流程执行器INodeExecutor
        /// 流程引擎运行时调用，生成真实业务逻辑执行对象
        /// </summary>
        /// <param name="type">节点枚举标识</param>
        /// <returns>节点业务执行器实例，无匹配节点返回null</returns>
        public static INodeExecutor CreateExecutor(NodeType type)
        {
            EnsureInitialized();
            // 缓存存在则反射创建执行器实例并强转接口，不存在返回null
            return _nodeRegistry.TryGetValue(type, out var regInfo)
                ? Activator.CreateInstance(regInfo.ExecutorType) as INodeExecutor
                : null;
        }

        /// <summary>
        /// 私有保障方法：如果未初始化，则自动执行一次全程序集扫描初始化
        /// 所有对外公共方法入口统一调用，避免使用者忘记初始化导致缓存为空
        /// </summary>
        private static void EnsureInitialized()
        {
            if (!_isInitialized) Initialize();
        }
    }
}