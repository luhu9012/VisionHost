namespace Grayson.Vision.Contracts.Flow.Nodes
{
    /// <summary>
    /// 节点内部异常处理策略
    /// </summary>
    public enum NodeExceptionPolicy
    {
        /// <summary>终止当前工单</summary>
        AbortWorkOrder = 0,

        /// <summary>跳过当前节点继续执行</summary>
        SkipAndContinue = 1,

        /// <summary>重试 N 次后仍失败则终止工单</summary>
        RetryThenAbort = 2
    }
}
