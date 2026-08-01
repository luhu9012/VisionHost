using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Common
{
    /// <summary>
    /// 强类型节点执行器抽象基类
    /// 消除各节点 Executor 中的参数强转与判空冗余代码
    /// </summary>
    /// <typeparam name="TParam">节点的参数模型类型</typeparam>
    public abstract class NodeExecutorBase<TParam> : INodeExecutor
        where TParam : class, new()
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // 安全类型强转
            var param = node.ParameterModel as TParam;
            if (param == null)
            {
                throw new InvalidOperationException($"节点 [{node.DisplayName}] 未配置或参数模型类型不匹配，期望类型: {typeof(TParam).Name}");
            }

            // 调用子类核心业务
            await ExecuteCoreAsync(node, param, context, token);
        }

        /// <summary>
        /// 强类型节点业务逻辑实现接口
        /// </summary>
        /// <param name="node">当前节点元数据</param>
        /// <param name="param">类型安全的参数模型</param>
        /// <param name="context">节点执行上下文</param>
        /// <param name="token">取消令牌</param>
        protected abstract Task ExecuteCoreAsync(FlowNodeBase node, TParam param, NodeExecutionContext context, CancellationToken token);
    }
}