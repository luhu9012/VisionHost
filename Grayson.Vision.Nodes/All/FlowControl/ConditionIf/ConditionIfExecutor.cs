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

namespace Grayson.Vision.Nodes.All.FlowControl.ConditionIf
{
    [Node(NodeType.ConditionIf, NodeCategory.FlowControl, typeof(ConditionIfParam))]
    [NodePort("InputVal", PortType.In, PortCategory.Data, dataType: "Object", colorHex: "#7C3AED")]
    // 🌟 修改：移除原 Result 端口，拆分为 True 和 False 两个输出端口
    [NodePort("True", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#2ECC71")]
    [NodePort("False", PortType.Out, PortCategory.Data, dataType: "Boolean", colorHex: "#E74C3C")]
    public class ConditionIfExecutor : NodeExecutorBase<ConditionIfParam>
    {
        public const string PORT_IN_VALUE = "InputVal";

        // 🌟 修改：定义两个分支端口常量
        public const string PORT_OUT_TRUE = "True";
        public const string PORT_OUT_FALSE = "False";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ConditionIfParam param, NodeExecutionContext context, CancellationToken token)
        {
            bool isTrue = false;

            if (param.UseExpression)
            {
                context.Log($"🔀 [条件判断] 执行表达式评估: '{param.Expression}'");
                isTrue = EvaluateExpression(param.Expression, node, context);
            }
            else
            {
                // 显式指定泛型 <object>，避免 GetInputValue 编译推导失败
                object inputObj = context.GetInputValue<object>(node, PORT_IN_VALUE, null);
                string inputStr = inputObj != null ? inputObj.ToString() : string.Empty;

                context.Log($"🔀 [条件判断] 执行单值评估: 输入='{inputStr}', 运算符='{param.Operator}', 目标='{param.CompareValue}'");
                isTrue = EvaluateSimpleCondition(inputStr, param.Operator, param.CompareValue);
            }

            // 🌟 修改：分别向两个出口端口输出结果
            // 如果 isTrue 为 true：True 端口输出 true，False 端口输出 false
            // 如果 isTrue 为 false：True 端口输出 false，False 端口输出 true
            context.SetOutputValue(node, PORT_OUT_TRUE, isTrue);
            context.SetOutputValue(node, PORT_OUT_FALSE, !isTrue);

            context.Log($"🔀 [条件判断] 评估完成，判定为: {(isTrue ? "True 分支" : "False 分支")}");

            await Task.CompletedTask;
        }

        private bool EvaluateExpression(string expr, FlowNodeBase node, NodeExecutionContext context)
        {
            if (string.IsNullOrWhiteSpace(expr)) return false;

            object inputObj = context.GetInputValue<object>(node, PORT_IN_VALUE, null);
            if (inputObj is bool bVal)
            {
                return bVal;
            }

            string processedExpr = expr.Trim();
            if (processedExpr.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (processedExpr.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

            if (inputObj != null && double.TryParse(inputObj.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double dInput))
            {
                processedExpr = processedExpr.Replace("InputVal", dInput.ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                if (processedExpr.Contains(">="))
                {
                    string[] parts = processedExpr.Split(new[] { ">=" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double left) && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double right))
                        return left >= right;
                }
                else if (processedExpr.Contains("<="))
                {
                    string[] parts = processedExpr.Split(new[] { "<=" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double left) && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double right))
                        return left <= right;
                }
                else if (processedExpr.Contains("=="))
                {
                    string[] parts = processedExpr.Split(new[] { "==" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                        return parts[0].Trim().Equals(parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
                }
                else if (processedExpr.Contains("!="))
                {
                    string[] parts = processedExpr.Split(new[] { "!=" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2)
                        return !parts[0].Trim().Equals(parts[1].Trim(), StringComparison.OrdinalIgnoreCase);
                }
                else if (processedExpr.Contains(">"))
                {
                    string[] parts = processedExpr.Split(new[] { ">" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double left) && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double right))
                        return left > right;
                }
                else if (processedExpr.Contains("<"))
                {
                    string[] parts = processedExpr.Split(new[] { "<" }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double left) && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double right))
                        return left < right;
                }
            }
            catch (Exception ex)
            {
                context.Log($"⚠️ [条件判断] 表达式解析异常: {ex.Message}");
            }

            return false;
        }

        private bool EvaluateSimpleCondition(string inputVal, string op, string targetVal)
        {
            if (double.TryParse(inputVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dInput) &&
                double.TryParse(targetVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dTarget))
            {
                switch (op)
                {
                    case "==": return Math.Abs(dInput - dTarget) < 1e-6;
                    case "!=": return Math.Abs(dInput - dTarget) >= 1e-6;
                    case ">": return dInput > dTarget;
                    case ">=": return dInput >= dTarget;
                    case "<": return dInput < dTarget;
                    case "<=": return dInput <= dTarget;
                    default: return false;
                }
            }

            if (bool.TryParse(inputVal, out bool bInput) && bool.TryParse(targetVal, out bool bTarget))
            {
                switch (op)
                {
                    case "==": return bInput == bTarget;
                    case "!=": return bInput != bTarget;
                    default: return false;
                }
            }

            switch (op)
            {
                case "==": return string.Equals(inputVal, targetVal, StringComparison.OrdinalIgnoreCase);
                case "!=": return !string.Equals(inputVal, targetVal, StringComparison.OrdinalIgnoreCase);
                default: return false;
            }
        }
    }
}