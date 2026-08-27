using Grayson.Vision.Contracts.Infrastructure.Mvvm; 
using System.ComponentModel;

namespace Grayson.Vision.Nodes.Common
{
    public abstract class ParamBase : ViewModelBase, IDataErrorInfo
    {
        public virtual string Error => null;
        public virtual string this[string columnName] => null;

        /// <summary>
        /// 该节点的属性面板是否需要内嵌实时预览视图窗口。
        /// 默认 false（弹窗维持单列纯参数布局，零变化）；
        /// 图像处理类节点（阈值分割、模板匹配、ROI 等）按需覆写为 true，
        /// 由 NodePropertyWindow 据此切换两列布局（左参数 / 右预览）。
        /// 注意：这只是"显示层是否提供预览区"的声明；实际画什么由该节点
        /// Executor 内的 context.Preview 调用决定，两层独立、误判无副作用。
        /// </summary>
        public virtual bool SupportsPreview => false;
    }
}