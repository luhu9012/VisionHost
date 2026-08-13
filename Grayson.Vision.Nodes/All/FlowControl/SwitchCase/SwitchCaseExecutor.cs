using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.SwitchCase
{
    [Node(NodeType.SwitchCase, NodeCategory.FlowControl, typeof(SwitchCaseParam))]
    [NodePort("InputVal", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    public class SwitchCaseExecutor : NodeExecutorBase<SwitchCaseParam>
    {
        public const string PORT_IN_VALUE = "InputVal";

        /// <summary>
        /// 🌟 动态同步 Switch 节点的输出分支端口
        /// </summary>
        public static void SyncPorts(FlowNodeBase node, SwitchCaseParam param)
        {
            if (node == null || param == null) return;

            // 1. 收集目标端口名称列表
            var requiredBranches = param.CaseItems
                .Where(x => !string.IsNullOrWhiteSpace(x.BranchName))
                .Select(x => x.BranchName.Trim())
                .ToList();

            if (!string.IsNullOrWhiteSpace(param.DefaultBranchName))
            {
                requiredBranches.Add(param.DefaultBranchName.Trim());
            }

            // 2. 清理多余的输出端口
            var portsToRemove = node.OutputPorts
                .Where(p => !requiredBranches.Contains(p.PortName))
                .ToList();

            foreach (var port in portsToRemove)
            {
                node.OutputPorts.Remove(port);
            }

            // 3. 动态补全 Missing 的分支输出端口
            foreach (var branchName in requiredBranches)
            {
                if (!node.OutputPorts.Any(p => p.PortName == branchName))
                {
                    node.OutputPorts.Add(new NodePort
                    {
                        PortName = branchName,
                        PortType = PortType.Out,
                        Category = PortCategory.Data,
                        DataType = "String",
                        ColorHex = "#A21CAF"
                    });
                }
            }
        }

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, SwitchCaseParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 🌟 执行前自动强行同步一次端口（确保 UI 或配置变更后端口对齐）
            SyncPorts(node, param);

            object inputObj = context.GetInputValue<object>(node, PORT_IN_VALUE, null);
            string inputStr = inputObj != null ? inputObj.ToString().Trim() : string.Empty;

            context.Log($"🔀 [多路分支] 开始评估输入值: '{inputStr}'");

            string matchedBranch = param.DefaultBranchName;

            if (param.CaseItems != null)
            {
                foreach (var item in param.CaseItems)
                {
                    if (item == null || string.IsNullOrEmpty(item.CaseValue)) continue;

                    if (string.Equals(inputStr, item.CaseValue.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        matchedBranch = item.BranchName;
                        context.Log($"🔀 [多路分支] 成功匹配项 CaseValue='{item.CaseValue}' -> 跳转分支: '{matchedBranch}'");
                        break;
                    }
                }
            }

            // 🌟 修正：调用 NodeExecutionContext 中真实的 SetActiveNextPort 方法激活控制流
            context.SetActiveNextPort(node, matchedBranch);

            await Task.CompletedTask;
        }
    }

  
}