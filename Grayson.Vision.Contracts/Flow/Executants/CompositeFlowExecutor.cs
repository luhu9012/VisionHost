using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Flow.Executants
{
    /// <summary>
    /// CompositeFlow（Group 子流程）节点的执行器。
    ///
    /// 背景（2026-09-11 补齐）：此前 CompositeFlowNode 仅有编辑器折叠壳、无 Executor，
    /// FlowExecutor 执行到它时走"未挂执行器跳过"分支，子流程内的视觉链（AcquireImage →
    /// ShapeMatch → CalibrationApply）根本不会运行。本执行器让 Group 子流程在运行时被
    /// 真正展开执行，支撑"上相机引导 + 下相机纠偏"这类复合工位的多段视觉链。
    ///
    /// 实现策略：子流程内部用独立的 <see cref="FlowExecutor"/> 实例驱动，共享同一个
    /// <see cref="ExecutionContext"/>（= 父流程的全局数据管线），因此：
    ///   - 子节点之间的连线数据传递（PassDataThroughConnection）与顶层节点完全同源；
    ///   - 子节点的 AcquireImage 经同一 hardwareResolver 解析相机别名；
    ///   - 子节点的 OnNodeExecuting/Executed 事件照常触发 → 图像按节点推送到监视界面。
    /// 子实例的 State/OnChainCompleted 无人订阅，零副作用（不影响顶层调度器的执行状态）。
    /// </summary>
    public class CompositeFlowExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            if (!(node is CompositeFlowNode composite))
            {
                return;
            }

            var sub = composite.SubProcess;
            if (sub == null || sub.Nodes == null || sub.Nodes.Count == 0)
            {
                context?.Log($"⚠️ 子流程节点 [{composite.DisplayName}] 内部为空，跳过执行。");
                return;
            }

            context?.Log($"▶ 展开执行子流程 [{composite.DisplayName}]（{sub.Nodes.Count} 个节点）...");

            // 1. 对子流程做拓扑排序 + 校验（与顶层配方同一套 BuildAndValidate）
            var buildResult = ExecutionChain.BuildAndValidate(sub);
            if (!buildResult.IsSuccess)
            {
                context?.Log($"⚠️ 子流程 [{composite.DisplayName}] 校验未通过，跳过执行：{buildResult.ErrorMessage}");
                return;
            }

            // 2. 用独立的 FlowExecutor 实例驱动子链，共享同一全局数据管线。
            //    context.EngineContext 即父流程的 ExecutionContext，端口缓存全局共享，
            //    子流程内的连线数据传递与顶层节点天然打通。
            var subExecutor = new FlowExecutor(buildResult.Chain, context.EngineContext);
            subExecutor.SetNodeExecutionContext(context);
            await subExecutor.RunContinuousAsync(token).ConfigureAwait(false);

            context?.Log($"✔ 子流程 [{composite.DisplayName}] 执行完毕。");
        }
    }
}
