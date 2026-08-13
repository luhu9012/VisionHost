using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Flow;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Nodes.Common;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.All.DeviceIO.PlcReadWrite
{
    [Node(NodeType.PlcReadWrite, NodeCategory.DeviceIO, typeof(PlcReadWriteParam))]
    [NodePort("WriteValIn", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    [NodePort("ReadValOut", PortType.Out, PortCategory.Data, dataType: "Object", colorHex: "#2ECC71")]
    [NodePort("Success", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#3498DB")]
    public class PlcReadWriteExecutor : NodeExecutorBase<PlcReadWriteParam>
    {
        public const string PORT_IN_WRITE_VAL = "WriteValIn";
        public const string PORT_OUT_READ_VAL = "ReadValOut";
        public const string PORT_OUT_IS_SUCCESS = "Success";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, PlcReadWriteParam param, NodeExecutionContext context, CancellationToken token)
        {
            // 从执行上下文中获取挂载的 PLC 逻辑设备对象
            IPlc plc = context.GetHardware<IPlc>(param.PlcAlias);
            if (plc == null)
            {
                context.Log($"❌ [PlcReadWrite] 错误: 未能在系统中找到别名为 [{param.PlcAlias}] 的 PLC 设备对象。");
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, false);
                return;
            }

            string address = param.Address.Trim();
            string type = param.DataType.Trim().ToLowerInvariant();

            if (param.IsWriteMode)
            {
                // ===== 写入模式 =====
                object dynamicWriteObj = context.GetInputValue<object>(node, PORT_IN_WRITE_VAL, null);
                string writeStr = dynamicWriteObj != null ? dynamicWriteObj.ToString() : param.WriteValue;

                context.Log($"📟 [PlcReadWrite] 开始写入 PLC 地址 [{address}], 类型 [{param.DataType}], 目标值: {writeStr}");

                var writeResult = await ExecutePlcWriteAsync(plc, address, type, writeStr);
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, writeResult.Success);

                if (writeResult.Success)
                {
                    context.Log($"✅ [PlcReadWrite] 写入成功: 地址 [{address}] = {writeStr}");
                }
                else
                {
                    context.Log($"❌ [PlcReadWrite] 写入失败: {writeResult.Message}");
                }
            }
            else
            {
                // ===== 读取模式 =====
                context.Log($"📟 [PlcReadWrite] 开始读取 PLC 地址 [{address}], 类型 [{param.DataType}]");

                var readResult = await ExecutePlcReadAsync(plc, address, type);
                context.SetOutputValue(node, PORT_OUT_IS_SUCCESS, readResult.Success);

                if (readResult.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_READ_VAL, readResult.Value);
                    context.Log($"✅ [PlcReadWrite] 读取成功: 地址 [{address}] = {readResult.Value}");
                }
                else
                {
                    context.Log($"❌ [PlcReadWrite] 读取失败: {readResult.Message}");
                }
            }
        }

        private async Task<(bool Success, string Message)> ExecutePlcWriteAsync(IPlc plc, string address, string type, string valStr)
        {
            try
            {
                switch (type)
                {
                    case "bool":
                    case "boolean":
                        bool bVal = bool.TryParse(valStr, out bool b) ? b : valStr == "1";
                        var resBool = await plc.WriteAsync(address, bVal);
                        return (resBool.Success, resBool.Message);

                    case "int16":
                    case "short":
                        short sVal = short.Parse(valStr);
                        var resShort = await plc.WriteAsync(address, sVal);
                        return (resShort.Success, resShort.Message);

                    case "int32":
                    case "int":
                        int iVal = int.Parse(valStr);
                        var resInt = await plc.WriteAsync(address, iVal);
                        return (resInt.Success, resInt.Message);

                    case "float":
                        float fVal = float.Parse(valStr, CultureInfo.InvariantCulture);
                        var resFloat = await plc.WriteAsync(address, fVal);
                        return (resFloat.Success, resFloat.Message);

                    case "double":
                        double dVal = double.Parse(valStr, CultureInfo.InvariantCulture);
                        var resDouble = await plc.WriteAsync(address, dVal);
                        return (resDouble.Success, resDouble.Message);

                    case "string":
                        var resStr = await plc.WriteStringAsync(address, valStr);
                        return (resStr.Success, resStr.Message);

                    default:
                        return (false, $"不支持的数据类型: {type}");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private async Task<(bool Success, object Value, string Message)> ExecutePlcReadAsync(IPlc plc, string address, string type)
        {
            try
            {
                switch (type)
                {
                    case "bool":
                    case "boolean":
                        var rBool = await plc.ReadAsync<bool>(address);
                        return (rBool.Success, rBool.Data, rBool.Message);

                    case "int16":
                    case "short":
                        var rShort = await plc.ReadAsync<short>(address);
                        return (rShort.Success, rShort.Data, rShort.Message);

                    case "int32":
                    case "int":
                        var rInt = await plc.ReadAsync<int>(address);
                        return (rInt.Success, rInt.Data, rInt.Message);

                    case "float":
                        var rFloat = await plc.ReadAsync<float>(address);
                        return (rFloat.Success, rFloat.Data, rFloat.Message);

                    case "double":
                        var rDouble = await plc.ReadAsync<double>(address);
                        return (rDouble.Success, rDouble.Data, rDouble.Message);

                    case "string":
                        var rStr = await plc.ReadStringAsync(address, 32);
                        return (rStr.Success, rStr.Data, rStr.Message);

                    default:
                        return (false, null, $"不支持的数据类型: {type}");
                }
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }
    }
}