using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Engine.Execution;
using Grayson.Vision.Contracts.Business.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.PlcReadWrite
{
    [Node(
        type: NodeType.PlcReadWrite,
        category: NodeCategory.DeviceIO,
        displayName: " PLC 读写",
        description: "读取或写入 PLC 寄存器 DB/M 点",
        parameterType: typeof(PlcReadWriteParam)
          , icon: "🔌"
    )]
    //[NodePort("ExecIn", PortType.In, PortCategory.Exec)]
    //[NodePort("ExecOut", PortType.Out, PortCategory.Exec)]
    [NodePort("DataIn", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#E67E22")]
    [NodePort("DataOut", PortType.Out, PortCategory.Data, dataType: "Object", colorHex: "#E67E22")]
    public class PlcReadWriteExecutor : INodeExecutor
    {
        public async Task ExecuteAsync(FlowNodeBase node, NodeExecutionContext context, CancellationToken token)
        {
            var param = node.ParameterModel as PlcReadWriteParam;
            if (param == null) throw new InvalidOperationException($"节点 [{node.DisplayName}] 未正确配置 PlcReadWriteParam 参数。");

            context.Log($"🔌 [PLC 读写] 操作: {param.Operation}, 地址: {param.DbAddress}, PLC: {param.PlcAlias}");

            await Task.Delay(50, token); // 模拟 PLC 通讯延时

            if (param.Operation == PlcOpType.Read)
            {
                // 模拟读取到的 PLC 数据
                object readVal = 123.456;
                context.SetOutputValue(node, "DataOut", readVal);
                context.Log($"✔ [PLC 读取成功] 地址 {param.DbAddress} -> {readVal}");
            }
            else
            {
                // 如果数据端口有上游传值，优先使用上游值，否则使用静态配置参数
                object inputVal = context.GetInputValue<object>(node, "DataIn");
                string finalVal = inputVal != null ? inputVal.ToString() : param.WriteValue;

                context.Log($"✔ [PLC 写入成功] 地址 {param.DbAddress} <- {finalVal}");
            }
        }
    }
}