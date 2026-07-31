using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.HalconWrapper.ImageProc;


namespace Grayson.Vision.BusinessUnits.Preprocess
{
    /// <summary>
    /// 高斯平滑滤波预处理单元
    /// 读取上下文原图，滤波后覆盖原图，消除相机高频噪点
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "GaussFilterUnit",
        DisplayName = "高斯滤波",
        UnitType = BusinessUnitType.DataProcess,
        Category = "图像预处理",
        Description = "高斯模糊降噪，可配置卷积核3/5/7，处理后更新上下文原图")]
    public class GaussFilterUnit : IBusinessUnit
    {
        /// <summary>滤波核尺寸，只允许奇数：3、5、7</summary>
        public int KernelSize { get; set; } = 5;
        public bool Enable { get; set; } = true;

        public string ModuleId => "GaussFilterUnit";
        public string ModuleName => "高斯滤波";
        public BusinessUnitType UnitType => BusinessUnitType.DataProcess;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 校验是否存在原图
            if (context.SourceImage == null || !context.SourceImage.IsInitialized())
            {
                runtime.LogError("高斯滤波：上下文无原始图像", null, ModuleId);
                return Result.Fail("缺少待处理图像");
            }

            // 调用Halcon封装层，本层禁止直接调用HalconDotNet
            var filterResult = ImageFilterTool.GaussFilter(context.SourceImage, KernelSize);
            if (!filterResult.Success)
            {
                runtime.LogError($"高斯滤波算法失败：{filterResult.Message}", null, ModuleId);
                return filterResult;
            }

            // 释放旧图像，替换为滤波后新图
            context.SourceImage.Dispose();
            context.SourceImage = filterResult.Data;

            runtime.LogInfo($"高斯滤波完成，核大小{KernelSize}",  ModuleId);
            runtime.PublishUiEvent("Ui.ShowMainImage", context.SourceImage);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"KernelSize", KernelSize},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            KernelSize = recipeDict.SafeGet("KernelSize", 5);
            Enable = recipeDict.SafeGet("Enable", true);
            // 强制限制只能为奇数
            if (KernelSize % 2 == 0) KernelSize += 1;
        }

        //public UserControl GetConfigPanel()
        //{
        //    return new GaussFilterPanel(this);
        //}
    }
}