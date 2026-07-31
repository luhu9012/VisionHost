using System;
using DynamicExpresso;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Common.Logging;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 流程条件表达式执行工具
    /// 专门给ConditionFlowNode、LoopFlowNode做C#语法字符串表达式计算
    /// 运行时注入VisionContext.SharedData，支持读写上下文字典变量
    /// 语法完全贴近C#，不用学习新语法，工程师上手无门槛
    /// </summary>
    public static class ExpressionHelper
    {
        /// <summary>
        /// 执行布尔表达式，返回true/false
        /// </summary>
        /// <param name="expr">表达式字符串，例：SharedData["Match.IsSuccess"] == true</param>
        /// <param name="context">当前流程上下文，表达式可读取SharedData</param>
        public static bool ExecuteBoolExpression(string expr, VisionContext context)
        {
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            try
            {
                Interpreter interpreter = new Interpreter();
                // 把共享字典注入表达式环境，表达式内可直接使用SharedData
                interpreter.SetVariable("SharedData", context.SharedData);
                // 执行表达式强制转布尔
                object resultObj = interpreter.Eval(expr);
                if (resultObj is bool resBool)
                {
                    return resBool;
                }
                GlobalLogger.Warn($"表达式返回非布尔值，表达式：{expr}", nameof(ExpressionHelper));
                return false;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"条件表达式解析失败：{expr}", ex, nameof(ExpressionHelper));
                return false;
            }
        }
    }
}