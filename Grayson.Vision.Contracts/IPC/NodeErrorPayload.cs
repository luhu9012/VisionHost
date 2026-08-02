using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vision.Contracts.IPC
{
    /// <summary>
    /// IPC 跨进程传输节点异常数据的传输模型
    /// </summary>
    public class NodeErrorPayload
    {
        /// <summary>
        /// 触发异常的节点数据模型
        /// </summary>
        public FlowNodeBase Node { get; set; }

        /// <summary>
        /// 异常简要信息
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 异常堆栈追踪
        /// </summary>
        public string StackTrace { get; set; }

        public NodeErrorPayload() { }

        public NodeErrorPayload(FlowNodeBase node, System.Exception ex)
        {
            Node = node;
            ErrorMessage = ex?.Message;
            StackTrace = ex?.StackTrace;
        }
    }
}