using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.ConditionIf
{
    [Node(
        type: NodeType.ConditionIf,
        category: NodeCategory.Logic,
        displayName: "条件分支",
        description: "基于布尔表达式或比较结果决定控制流分支",
        parameterType: typeof(ConditionIfParam)
          , icon: "🔀"
    )]
    [NodePort("ExecIn", PortType.In, PortCategory.Data)]
    // 💡 核心设计：显式拆分为 True 和 False 两个控制分支
    [NodePort("TrueOut", PortType.Out, PortCategory.Data)]
    [NodePort("FalseOut", PortType.Out, PortCategory.Data)]
    [NodePort("BoolIn", PortType.In, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("ValueIn", PortType.In, PortCategory.Data, dataType: "Double", colorHex: "#3498DB")]
    public class ConditionIfExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as ConditionIfParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 参数未配置。");

            bool result = false;

            if (param.UseInputPort)
            {
                // 方式 A：直接读取 BoolIn 输入
                bool? boolVal = context.GetInputValue<bool?>(node, "BoolIn");
                result = boolVal ?? false;
            }
            else
            {
                // 方式 B：数值比较
                double? val = context.GetInputValue<double?>(node, "ValueIn");
                double actualVal = val ?? 0.0;

                switch (param.Operator)
                {
                    case CompareOp.Greater: result = actualVal > param.TargetValue; break;
                    case CompareOp.GreaterEqual: result = actualVal >= param.TargetValue; break;
                    case CompareOp.Equal: result = Math.Abs(actualVal - param.TargetValue) < 0.00001; break;
                    case CompareOp.NotEqual: result = Math.Abs(actualVal - param.TargetValue) >= 0.00001; break;
                    case CompareOp.Less: result = actualVal < param.TargetValue; break;
                    case CompareOp.LessEqual: result = actualVal <= param.TargetValue; break;
                }
            }

            context.Log($"🔀 [If 分支判断] 结果: 【{result}】");

            // 💡 告知执行引擎下一节点激活哪个分支端口
            context.SetActiveNextPort(node, result ? "TrueExec" : "FalseExec");
            await Task.CompletedTask;
        }
    }
}