using Grayson.Vision.Contracts.Flow.Executants;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
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
        /// <summary>
        /// 当前执行周期持有的节点上下文缓存（仅在 ExecuteAsync 执行期间有效，
        /// 供 Preview 便捷属性读取；执行结束自动置空）。
        /// </summary>
        private NodeExecutionContext _currentContext;

        /// <summary>
        /// 实时预览显示上下文便捷入口（null 安全）。
        /// 编辑器属性面板调试期为注入的 IFlowPreviewContext；生产运行为 null，
        /// 所有调用天然跳过。用法与标定服务的场景式绘制完全一致：
        /// Preview?.BeginScene() → Preview?.AddBorrowed(底图) → Preview?.Add(显示副本)。
        /// </summary>
        protected IFlowPreviewContext Preview => _currentContext?.Preview;

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

            // 缓存上下文供 Preview 便捷属性使用
            _currentContext = context;
            try
            {
                // 调用子类核心业务
                await ExecuteCoreAsync(node, param, context, token);
            }
            finally
            {
                _currentContext = null;
            }
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