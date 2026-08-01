using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vision.Contracts.Business.Engine.Execution
{
    /// <summary>
    /// 节点执行时持有的上下文 (聚合了全局上下文、单次节拍数据与硬件获取 API)
    /// </summary>
    public class NodeExecutionContext
    {
        /// <summary>
        /// 全局/工位数据管线 (持有 PortValueCache 与 SharedVariables)
        /// </summary>
        public ExecutionContext EngineContext { get; }

        /// <summary>
        /// 当前触发节拍的元数据
        /// </summary>
        public FrameCycleContext CycleContext { get; }

        /// <summary>
        /// 硬件解耦服务委托 (由 StationContext 或 SystemContext 注入)
        /// </summary>
        private readonly Func<string, object> _hardwareResolver;

        #region 💡 新增：控制流分支与节点状态数据结构
        /// <summary>
        /// 记录每个节点当前激活的控制流输出端口（Key: NodeId, Value: PortName）
        /// 调度引擎在节点执行完毕后，读取此字典来决定走哪条 Exec 连线
        /// </summary>
        private readonly Dictionary<string, string> _activePortMap = new Dictionary<string, string>();

        /// <summary>
        /// 节点内部状态暂存表（Key: "NodeId_StateKey", Value: StateValue）
        /// 用于 ForLoop、计数器等需要在单次流程/跨节拍中暂存数据的场景
        /// </summary>
        private readonly Dictionary<string, object> _nodeStateMap = new Dictionary<string, object>();
        #endregion

        public NodeExecutionContext(
            ExecutionContext engineContext,
            FrameCycleContext cycleContext,
            Func<string, object> hardwareResolver = null)
        {
            EngineContext = engineContext ?? throw new ArgumentNullException(nameof(engineContext));
            CycleContext = cycleContext ?? new FrameCycleContext();
            _hardwareResolver = hardwareResolver;
        }

        /// <summary>
        /// 快捷打印日志
        /// </summary>
        public void Log(string message) => EngineContext.Log(message);

        /// <summary>
        /// 根据配方里设置的【逻辑硬件名称】，获取映射后的硬件驱动实例
        /// </summary>
        public TDevice GetHardware<TDevice>(string logicalName) where TDevice : class
        {
            if (_hardwareResolver == null)
            {
                Log($"⚠️ 未配置硬件解析器，无法获取逻辑硬件 [{logicalName}]，请检查工位绑定状态。");
                return null;
            }

            var device = _hardwareResolver.Invoke(logicalName) as TDevice;
            if (device == null)
            {
                Log($"⚠️ 硬件映射失败：逻辑名称 [{logicalName}] 未找到匹配的 [{typeof(TDevice).Name}] 驱动句柄！");
            }

            return device;
        }

        #region 便捷数据端口读写封装
        /// <summary>
        /// 从当前节点的指定输入端口获取上游传过来的数据
        /// </summary>
        public T GetInputValue<T>(FlowNodeBase node, string portName, T defaultValue = default)
        {
            var port = node.InputPorts?.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port != null)
            {
                var val = EngineContext.GetPortValue(port.PortId);
                if (val is T typedVal) return typedVal;
            }
            return defaultValue;
        }

        /// <summary>
        /// 将计算结果输出到当前节点的指定输出端口
        /// </summary>
        public void SetOutputValue(FlowNodeBase node, string portName, object value)
        {
            var port = node.OutputPorts?.FirstOrDefault(p => p.PortName.Equals(portName, StringComparison.OrdinalIgnoreCase));
            if (port != null)
            {
                EngineContext.SetPortValue(port.PortId, value);
            }
        }
        #endregion

        #region 💡 新增：控制流分支激活与节点状态 API

        /// <summary>
        /// 显式设置当前节点执行完毕后，需要激活并调度的下一个控制流端口名称 (例如 "TrueExec", "FalseExec", "Case_0")
        /// </summary>
        /// <param name="node">当前节点</param>
        /// <param name="portName">控制流输出端口名</param>
        public void SetActiveNextPort(FlowNodeBase node, string portName)
        {
            if (node == null || string.IsNullOrEmpty(portName)) return;
            _activePortMap[node.NodeId] = portName;
        }

        /// <summary>
        /// [引擎调用] 获取指定节点激活的控制流端口，若未指定则默认返回第一个 Exec 类型的输出端口
        /// </summary>
        public string GetActiveNextPort(FlowNodeBase node)
        {
            if (node == null) return null;

            if (_activePortMap.TryGetValue(node.NodeId, out string activePort))
            {
                return activePort;
            }

            // 默认兜底逻辑：若算子未明确指定，则自动选取第一个控制流输出端口
            var defaultExecPort = node.OutputPorts?.FirstOrDefault(p => p.Category == PortCategory.Data);
            return defaultExecPort?.PortName;
        }

        /// <summary>
        /// 保存节点的局部临时状态数据 (如 ForLoop 算子的 CurrentIndex)
        /// </summary>
        public void SetState<T>(FlowNodeBase node, string key, T value)
        {
            if (node == null || string.IsNullOrEmpty(key)) return;
            string stateKey = $"{node.NodeId}_{key}";
            _nodeStateMap[stateKey] = value;
        }

        /// <summary>
        /// 读取节点的局部临时状态数据，若不存在则返回默认值
        /// </summary>
        public T GetState<T>(FlowNodeBase node, string key, T defaultValue = default)
        {
            if (node == null || string.IsNullOrEmpty(key)) return defaultValue;
            string stateKey = $"{node.NodeId}_{key}";

            if (_nodeStateMap.TryGetValue(stateKey, out object val) && val is T typedVal)
            {
                return typedVal;
            }
            return defaultValue;
        }

        /// <summary>
        /// 清理节点的状态数据
        /// </summary>
        public void ClearState(FlowNodeBase node, string key = null)
        {
            if (node == null) return;
            if (string.IsNullOrEmpty(key))
            {
                // 清理该节点的所有状态
                var keysToRemove = _nodeStateMap.Keys.Where(k => k.StartsWith($"{node.NodeId}_")).ToList();
                foreach (var k in keysToRemove) _nodeStateMap.Remove(k);
            }
            else
            {
                _nodeStateMap.Remove($"{node.NodeId}_{key}");
            }
        }

        #endregion
    }
}