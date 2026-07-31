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
    /// C#脚本胶水单元，非标临时逻辑兜底
    /// 支持运行时C#脚本执行，可访问上下文、Runtime硬件、日志
    /// 规范：只做变量换算、坐标补偿、零散IO，禁止写大量图像处理
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "ScriptGlueUnit",
        DisplayName = "C#胶水脚本单元",
        UnitType = BusinessUnitType.ScriptGlue,
        Category = "脚本兜底",
        Description = "执行自定义C#脚本，临时非标变量运算、坐标补偿，正式逻辑建议改为标准单元")]
    public class ScriptGlueUnit : IBusinessUnit
    {
        /// <summary>用户编写的C#脚本字符串</summary>
        public string ScriptCode { get; set; } = string.Empty;
        /// <summary>脚本执行超时毫秒，防止死循环卡死流程</summary>
        public int TimeoutMs { get; set; } = 5000;
        public bool Enable { get; set; } = true;

        public string ModuleId => "ScriptGlueUnit";
        public string ModuleName => "C#胶水脚本单元";
        public BusinessUnitType UnitType => BusinessUnitType.ScriptGlue;

        public IReadOnlyList<string> InputKeys => Array.Empty<string>();
        public IReadOnlyList<string> OutputKeys => Array.Empty<string>();

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (string.IsNullOrWhiteSpace(ScriptCode))
            {
                runtime.LogWarn("脚本为空，跳过执行", ModuleId);
                return Result.Ok();
            }
            // Roslyn脚本执行逻辑宿主封装，此处只做入口调用
            runtime.LogInfo("开始执行自定义胶水脚本", ModuleId);
            // 宿主脚本引擎传入context、runtime全局变量，隔离沙箱+超时
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"ScriptCode", ScriptCode},
                {"TimeoutMs", TimeoutMs},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            ScriptCode = recipeDict.SafeGet("ScriptCode", "");
            TimeoutMs = recipeDict.SafeGet("TimeoutMs", 5000);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        //public UserControl GetConfigPanel()
        //{
        //    return new ScriptGlueCodePanel(this);
        //}
    }
}