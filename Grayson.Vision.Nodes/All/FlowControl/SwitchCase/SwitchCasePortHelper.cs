using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.FlowControl.SwitchCase
{
    public static class SwitchCasePortHelper
    {
        public static void SyncPorts(FlowNodeBase node, SwitchCaseParam param)
        {

            if (node == null || param == null) return;

            // 1. 收集期望存在的输出端口名称列表
            var expectedPortNames = new List<string>();

            // 增加 Case 分支端口
            foreach (var item in param.CaseItems)
            {
                if (!string.IsNullOrEmpty(item.BranchName))
                {
                    expectedPortNames.Add(item.BranchName);
                }
            }

            // 增加 Default 分支端口
            if (!string.IsNullOrEmpty(param.DefaultBranchName))
            {
                expectedPortNames.Add(param.DefaultBranchName);
            }

            // 2. 移除旧的、不在期望列表里的 OutputPort
            for (int i = node.OutputPorts.Count - 1; i >= 0; i--)
            {
                var port = node.OutputPorts[i];
                if (!expectedPortNames.Contains(port.PortName))
                {
                    // 🌟 这一步会触发 OutputPorts.CollectionChanged -> NodeControl.OnPortsChanged
                    node.OutputPorts.RemoveAt(i);
                }
            }

            // 3. 补充新增的 OutputPort
            foreach (var portName in expectedPortNames)
            {
                if (!node.OutputPorts.Any(p => p.PortName == portName))
                {
                    // 🌟 这一步也会触发 OutputPorts.CollectionChanged -> NodeControl.OnPortsChanged
                    node.OutputPorts.Add(new NodePort
                    {
                        PortName = portName,
                        Category = PortCategory.Data,
                        PortType = PortType.Out
                    });
                }
            }
        }
    }

}
