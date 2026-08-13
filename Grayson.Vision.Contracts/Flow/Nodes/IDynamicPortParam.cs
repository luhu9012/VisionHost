using System.ComponentModel;

namespace Grayson.Vision.Contracts.Flow.Nodes
{
    /// <summary>
    /// 支持动态端口生成的参数模型接口
    /// </summary>
    public interface IDynamicPortParam : INotifyPropertyChanged
    {
        /// <summary>
        /// 触发端口同步逻辑
        /// </summary>
        /// <param name="node">当前参数归属的节点实例</param>
        void SyncPorts(FlowNodeBase node);
    }
}