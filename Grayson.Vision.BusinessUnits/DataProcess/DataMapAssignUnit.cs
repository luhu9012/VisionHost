using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;


namespace Grayson.Vision.BusinessUnits.DataProcess
{
    /// <summary>
    /// 上下文变量赋值映射单元
    /// 用于别名转发：AKey值复制到BKey、常量写入上下文
    /// 解决不同单元输入输出Key不匹配问题，不用写脚本
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "DataMapAssignUnit",
        DisplayName = "变量映射赋值",
        UnitType = BusinessUnitType.DataProcess,
        Category = "数据处理",
        Description = "把上下文A字段复制到B字段，或写入固定常量，适配单元之间数据名不匹配")]
    public class DataMapAssignUnit : IBusinessUnit
    {
        /// <summary>数据源：上下文Key / 固定常量</summary>
        public bool UseConstant { get; set; }
        public string SourceContextKey { get; set; } = string.Empty;
        public object ConstantValue { get; set; }
        /// <summary>目标写入上下文Key</summary>
        public string TargetContextKey { get; set; } = string.Empty;

        public bool Enable { get; set; } = true;
        public string ModuleId => "DataMapAssignUnit";
        public string ModuleName => "变量映射赋值";
        public BusinessUnitType UnitType => BusinessUnitType.DataProcess;

        public IReadOnlyList<string> InputKeys
        {
            get
            {
                if (!UseConstant && !string.IsNullOrEmpty(SourceContextKey))
                    return new List<string> { SourceContextKey };
                return Array.Empty<string>();
            }
        }
        public IReadOnlyList<string> OutputKeys => new List<string> { TargetContextKey };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(TargetContextKey))
            {
                runtime.LogError($"未配置目标上下文Key,,ModuleId={ModuleId}");
                return Result.Fail("缺少目标Key");
            }

            object writeData;
            if (UseConstant)
            {
                writeData = ConstantValue;
            }
            else
            {
                if (!context.SharedData.ContainsKey(SourceContextKey))
                {
                    runtime.LogWarn($"源Key{SourceContextKey}不存在,ModuleId={ModuleId}");
                    return Result.Ok();
                }
                writeData = context.SharedData[SourceContextKey];
            }

            context.SharedData[TargetContextKey] = writeData;
            runtime.LogInfo($"变量赋值完成 {TargetContextKey} = {writeData},,ModuleId={ModuleId}");
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"UseConstant",UseConstant},
                {"SourceContextKey",SourceContextKey},
                {"ConstantValue",ConstantValue},
                {"TargetContextKey",TargetContextKey},
                {"Enable",Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            UseConstant = recipeDict.SafeGet("UseConstant", false);
            SourceContextKey = recipeDict.SafeGet("SourceContextKey", "");
            ConstantValue = recipeDict.SafeGet<Object>("ConstantValue", null);
            TargetContextKey = recipeDict.SafeGet("TargetContextKey", "");
            Enable = recipeDict.SafeGet("Enable", true);
        }

        //public UserControl GetConfigPanel()
        //{
        //    return new DataMapAssignPanel(this);
        //}
    }
}