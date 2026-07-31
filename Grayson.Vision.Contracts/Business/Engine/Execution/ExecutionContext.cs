using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Grayson.Vision.Contracts.Business.Engine.Execution
{
    /// <summary>
    /// 节点执行异常事件参数
    /// </summary>
    public class NodeExecutionErrorEventArgs : EventArgs
    {
        public FlowNodeBase Node { get; }
        public Exception Exception { get; }
        public bool Handled { get; set; } // 是否被 TryCatch 节点捕获

        public NodeExecutionErrorEventArgs(FlowNodeBase node, Exception ex)
        {
            Node = node;
            Exception = ex;
        }
    }

    /// <summary>
    /// 执行状态与全局数据上下文 (线程安全数据管线)
    /// </summary>
    public class ExecutionContext
    {
        // 全局共享数据 (如 WatchData / 跨节点共享变量)
        public ConcurrentDictionary<string, object> SharedVariables { get; } = new ConcurrentDictionary<string, object>();

        // 节点输出端口数据缓存: PortId -> Value
        private readonly ConcurrentDictionary<string, object> _portValueCache = new ConcurrentDictionary<string, object>();

        // 事件总线
        public event EventHandler<FlowNodeBase> OnNodeExecuting;
        public event EventHandler<FlowNodeBase> OnNodeExecuted;
        public event EventHandler<NodeExecutionErrorEventArgs> OnExecutionError;
        public event EventHandler<string> OnLogProduced;

        public void Log(string message) => OnLogProduced?.Invoke(this, $"[{DateTime.Now:HH:mm:ss.fff}] {message}");

        public void NotifyNodeExecuting(FlowNodeBase node) => OnNodeExecuting?.Invoke(this, node);
        public void NotifyNodeExecuted(FlowNodeBase node) => OnNodeExecuted?.Invoke(this, node);

        public bool RaiseExecutionError(FlowNodeBase node, Exception ex)
        {
            var args = new NodeExecutionErrorEventArgs(node, ex);
            OnExecutionError?.Invoke(this, args);
            return args.Handled;
        }

        #region 数据管线写入与传递
        public void SetPortValue(string portId, object value)
        {
            if (!string.IsNullOrEmpty(portId))
                _portValueCache[portId] = value;
        }

        public object GetPortValue(string portId)
        {
            if (!string.IsNullOrEmpty(portId) && _portValueCache.TryGetValue(portId, out var val))
            {
                return val;
            }
            return null;
        }

        /// <summary>
        /// 数据连接传递：根据连接线，从上游端口获取数据灌入下游端口
        /// </summary>
        public void PassDataThroughConnection(ConnectionModel conn)
        {
            if (conn == null || conn.Category != PortCategory.Data) return;

            var val = GetPortValue(conn.SourcePortId);
            SetPortValue(conn.TargetPortId, val);

            // 更新到 TargetPort 模型以便 UI 刷新
            var targetPort = conn.TargetNode?.InputPorts.FirstOrDefault(p => p.PortId == conn.TargetPortId);
            if (targetPort != null)
            {
                targetPort.DataValue = val;
            }
        }
        #endregion
    }
}