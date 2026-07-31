using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Business.Engine.Execution;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 节点算子执行器接口
    /// </summary>
    public interface INodeExecutor
    {
        /// <summary>
        /// 节点核心计算与业务逻辑
        /// </summary>
        /// <param name="node">当前节点元数据及端口定义</param>
        /// <param name="context">运行时执行上下文（包含数据管线、硬件服务映射、节拍信息）</param>
        /// <param name="token">取消令牌</param>
        Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token);
    }
}
