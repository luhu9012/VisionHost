// 业务特性、数据模型与执行器命名空间引用
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Flow.Helpers;
using Grayson.Vision.Contracts.Flow.Nodes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Grayson.Vision.Contracts.Flow.Factories
{
    /// <summary>
    /// 流程节点工厂（全局单例静态工厂）
    /// 核心职责：
    /// 1. 扫描程序集解析 [Node] 特性，缓存节点与其执行器的映射关系；
    /// 2. 生成工具箱/菜单栏所需的节点元数据列表（UnitMeta）；
    /// 3. 动态实例化画布可视节点（支持普通 FlowNode 与复合子流程 CompositeFlowNode）；
    /// 4. 反射创建业务执行器 INodeExecutor 供流程引擎调度运行。
    /// 💡 注意：本文件位于 Contracts 契约层，不依赖第三方 JSON 库与文件读写。
    /// </summary>
    public static class NodeFactory
    {
        /// <summary>
        /// 全局节点注册表缓存
        /// Key：节点类型枚举 NodeType
        /// Value元组：(执行器Class类型, 参数配置Model类型, 节点特性元数据)
        /// </summary>
        private static readonly Dictionary<NodeType, (Type ExecutorType, Type ParamType, NodeAttribute Attribute)> _nodeRegistry
            = new Dictionary<NodeType, (Type, Type, NodeAttribute)>();

        /// <summary>
        /// 节点端口配置缓存注册表
        /// Key：节点类型枚举 NodeType
        /// Value：该节点定义的所有输入/输出端口特性列表
        /// </summary>
        private static readonly Dictionary<NodeType, List<NodePortAttribute>> _portRegistry
            = new Dictionary<NodeType, List<NodePortAttribute>>();

        /// <summary>
        /// 初始化标记，防止多次扫描程序集造成的重复解析开销
        /// </summary>
        private static bool _isInitialized = false;

        /// <summary>
        /// 批量扫描并初始化注册节点：反射读取程序集中实现了 INodeExecutor 接口的类
        /// </summary>
        /// <param name="assembliesToScan">待扫描的程序集列表；若不传则默认扫描当前 AppDomain 已加载的所有程序集</param>
        public static void Initialize(params Assembly[] assembliesToScan)
        {
            // 已初始化则直接跳过，避免重复反射扫描损耗性能
            if (_isInitialized) return;

            // 未指定程序集时，默认获取当前应用域加载的所有程序集
            var assemblies = assembliesToScan != null && assembliesToScan.Length > 0
                ? assembliesToScan
                : AppDomain.CurrentDomain.GetAssemblies();

            // 遍历每个程序集提取节点执行器类
            foreach (var assembly in assemblies)
            {
                try
                {
                    // 筛选条件：类、非抽象类、实现了 INodeExecutor 接口
                    var types = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && typeof(INodeExecutor).IsAssignableFrom(t));

                    foreach (var type in types)
                    {
                        RegisterExecutorType(type);
                    }
                }
                catch
                {
                    // 部分第三方 DLL 无法反射读取时直接忽略，保证主框架启动稳定
                }
            }

            // 标记完成初始化
            _isInitialized = true;
        }

        /// <summary>
        /// 手动注册单个节点执行器类型（支持插件热加载）
        /// 当外部动态加载新的 DLL 插件时，可单独调用此接口完成注册，无需重启全量扫描
        /// </summary>
        /// <param name="executorType">实现了 INodeExecutor 接口的类型</param>
        public static void RegisterExecutorType(Type executorType)
        {
            // 校验：必须实现 INodeExecutor 接口
            if (!typeof(INodeExecutor).IsAssignableFrom(executorType)) return;

            // 校验：必须标注 [Node] 特性，否则说明不是可编排的流程节点
            var nodeAttr = executorType.GetCustomAttribute<NodeAttribute>();
            if (nodeAttr == null) return;

            // 存入全局节点注册缓存：保存执行器 Type、参数实体 Type、节点元特性
            _nodeRegistry[nodeAttr.Type] = (executorType, nodeAttr.ParameterType, nodeAttr);

            // 提取类上标注的所有端口特性（NodePortAttribute），存入端口缓存表
            _portRegistry[nodeAttr.Type] = executorType.GetCustomAttributes<NodePortAttribute>().ToList();
        }

        /// <summary>
        /// 生成工具箱节点元数据列表（UnitMeta 集合）
        /// 专服务于左侧工具箱/菜单栏渲染，避开节点的提前实例化
        /// </summary>
        public static List<UnitMeta> GenerateToolboxMetas()
        {
            EnsureInitialized();

            return _nodeRegistry.Select(kvp =>
            {
                var nodeType = kvp.Key;
                var attr = kvp.Value.Attribute;

                // 从 NodeType 枚举字段特性中解析 DisplayName
                string displayName = nodeType.GetDescription();

                // 提取附加元数据（如图标 Emoji、长文本描述等）
                var metaAttr = nodeType.GetAttribute<NodeFieldMetaAttribute>();
                string description = metaAttr?.Description ?? displayName;
                string shortName = metaAttr?.ShortName ?? displayName;
                string icon = metaAttr?.Emoji ?? "🧩";

                return new UnitMeta
                {
                    NodeId = nodeType.ToString(),
                    Type = nodeType,
                    Category = attr.Category,
                    DisplayName = shortName,
                    Description = description,
                    Icon = icon
                };
            }).ToList();
        }

        /// <summary>
        /// 创建画布可视化节点实例（契约层核心工厂）
        /// 根据 NodeType 区分实例化普通的 FlowNode 或子流程复合节点 CompositeFlowNode
        /// </summary>
        /// <param name="type">节点枚举类型标识</param>
        /// <param name="position">节点放置在画布上的坐标</param>
        /// <param name="overrideDisplayName">可选：覆盖显示的节点名称，若为空则取默认定义</param>
        /// <param name="filePath">可选：如果节点为 CompositeFlow，传入模板相对/绝对文件路径</param>
        /// <param name="subProcess">可选：由服务层反序列化好后传入的子流程实体 Model</param>
        /// <returns>抽象基类 FlowNodeBase 实例（多态返回 FlowNode 或 CompositeFlowNode）</returns>
        public static FlowNodeBase CreateNodeInstance(
            NodeType type,
            Point2D position,
            string overrideDisplayName = null,
            string filePath = null,
            FlowProcessModel subProcess = null)
        {
            EnsureInitialized();

            // 解析枚举上的默认显示名称与描述
            string defaultDisplayName = type.GetDescription();
            var metaAttr = type.GetAttribute<NodeFieldMetaAttribute>();
            string defaultDescription = metaAttr?.Description ?? defaultDisplayName;

            // =========================================================================
            // 🌟 分支 1：处理复合子流程节点 (CompositeFlow)
            // =========================================================================
            if (type == NodeType.CompositeFlow)
            {
                string finalName = !string.IsNullOrEmpty(overrideDisplayName) ? overrideDisplayName : "复合子流程";

                var compositeNode = new CompositeFlowNode
                {
                    DisplayName = finalName,
                    RecipeFilePath = filePath,
                    PosX = position.X,
                    PosY = position.Y,
                    Description = defaultDescription
                };

                // 如果外部应用服务层已经解出并传入了 SubProcess，直接挂载[cite: 9]
                if (subProcess != null)
                {
                    compositeNode.SubProcess = subProcess;
                }

                return compositeNode;
            }

            // =========================================================================
            // 🌟 分支 2：处理普通逻辑/算法节点 (FlowNode)
            // =========================================================================

            // 缓存查无此类型，创建安全兜底节点（防止未注册类型导致崩溃）[cite: 8]
            if (!_nodeRegistry.TryGetValue(type, out var regInfo))
            {
                var fallback = new FlowNode(type, defaultDisplayName, NodeCategory.DeviceIO, position, defaultDescription);
                fallback.Type = type;
                fallback.InputPorts.Add(new NodePort { PortName = "ExecIn", PortType = PortType.In, Category = PortCategory.Data });
                fallback.OutputPorts.Add(new NodePort { PortName = "ExecOut", PortType = PortType.Out, Category = PortCategory.Data });
                return fallback;
            }

            var attr = regInfo.Attribute;

            // 通过反射独立实例化该节点的参数配置实体 (ParameterModel)[cite: 8]
            object paramInstance = regInfo.ParamType != null
                ? Activator.CreateInstance(regInfo.ParamType)
                : null;

            string finalDisplayName = !string.IsNullOrEmpty(overrideDisplayName) ? overrideDisplayName : defaultDisplayName;

            // 实例化画布通用节点 FlowNode[cite: 8]
            var node = new FlowNode(attr.Type, finalDisplayName, attr.Category, position, defaultDescription, paramInstance);

            // 挂载节点类型与业务执行器类型[cite: 8]
            node.Type = type;
            node.ExecutorType = regInfo.ExecutorType;

            // 动态创建执行器 INodeExecutor 实例[cite: 8]
            if (regInfo.ExecutorType != null)
            {
                try
                {
                    node.Executor = Activator.CreateInstance(regInfo.ExecutorType) as INodeExecutor;
                }
                catch
                {
                    // 反射创建失败防崩溃兜底
                }
            }

            // 动态构建输入/输出端口集合[cite: 8]
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

                    if (pAttr.PortType == PortType.In)
                        node.InputPorts.Add(port);
                    else
                        node.OutputPorts.Add(port);
                }
            }

            // 🌟 【新增】处理动态端口的联动逻辑 🌟
            if (paramInstance is IDynamicPortParam dynamicParam)
            {
                // 1. 初始化时先执行一次端口同步（根据反序列化出来的 Param 生成端口）
                dynamicParam.SyncPorts(node);

                // 2. 监听参数模型的属性变化，当 UI 修改参数时自动刷新端口
                dynamicParam.PropertyChanged += (sender, e) =>
                {
                    // 调用接口的方法让 Param 自己去决定怎么增删节点端口
                    // 注意：如果你在非 UI 线程修改了 Param，这里可能需要 Application.Current.Dispatcher.Invoke
                    dynamicParam.SyncPorts(node);
                };
            }

            return node;
        }

        /// <summary>
        /// 通过工具箱元数据 (UnitMeta) 快速创建画布节点
        /// </summary>
        /// <param name="meta">工具箱单元元数据</param>
        /// <param name="position">放置落点的画布坐标</param>
        /// <param name="subProcess">可选：反序列化好的子流程对象</param>
        public static FlowNodeBase CreateFromMeta(UnitMeta meta, Point2D position, FlowProcessModel subProcess = null)
        {
            if (meta == null) return null;

            string filePath = meta.Type == NodeType.CompositeFlow ? meta.Description : null;
            return CreateNodeInstance(meta.Type, position, meta.DisplayName, filePath, subProcess);
        }

        /// <summary>
        /// 仅创建节点业务执行器 INodeExecutor
        /// </summary>
        public static INodeExecutor CreateExecutor(NodeType type)
        {
            EnsureInitialized();
            return _nodeRegistry.TryGetValue(type, out var regInfo)
                ? Activator.CreateInstance(regInfo.ExecutorType) as INodeExecutor
                : null;
        }

        /// <summary>
        /// 私有保护校验：确保工厂在使用前已完成反射扫描与缓存初始化
        /// </summary>
        private static void EnsureInitialized()
        {
            if (!_isInitialized) Initialize();
        }

        /// <summary>
        /// 根据节点枚举类型，从注册表中获取其定义的参数 Model 类型 (ParamType)
        /// </summary>
        public static Type GetParameterType(NodeType type)
        {
            EnsureInitialized();
            return _nodeRegistry.TryGetValue(type, out var regInfo) ? regInfo.ParamType : null;
        }
    }
}