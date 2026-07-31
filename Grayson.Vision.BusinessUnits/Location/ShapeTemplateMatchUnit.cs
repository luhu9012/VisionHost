using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.HalconWrapper.Match2D;
using Grayson.Vision.HalconWrapper.Core;


namespace Grayson.Vision.BusinessUnits.Location
{
    /// <summary>
    /// 2D形状模板匹配定位单元
    /// 读取上下文原图做模板搜索，输出匹配OK状态、工件位姿Pose3D
    /// 支持最低匹配分数阈值控制良莠过滤
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "ShapeTemplateMatchUnit",
        DisplayName = "形状模板定位",
        UnitType = BusinessUnitType.Location,
        Category = "定位引导",
        Description = "加载本地模板文件匹配工件，输出工件坐标角度，存入Match.Pose3D")]
    public class ShapeTemplateMatchUnit : IBusinessUnit
    {
        /// <summary>模板文件本地路径</summary>
        public string TemplateFilePath { get; set; } = string.Empty;
        /// <summary>最低匹配相似度 0~1</summary>
        public double MinScore { get; set; } = 0.7;
        public bool Enable { get; set; } = true;

        public string ModuleId => "ShapeTemplateMatchUnit";
        public string ModuleName => "形状模板定位";
        public BusinessUnitType UnitType => BusinessUnitType.Location;

        public IReadOnlyList<string> InputKeys => new List<string> { ContextDataKeys.Grab_SourceImage };
        public IReadOnlyList<string> OutputKeys => new List<string>
        {
            ContextDataKeys.Match_IsSuccess,
            ContextDataKeys.Match_WorkPose
        };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            if (context.SourceImage == null || !context.SourceImage.IsInitialized())
            {
                runtime.LogError("定位：无采集图像",null, ModuleId);
                return Result.Fail("缺少图像");
            }
            if (string.IsNullOrWhiteSpace(TemplateFilePath))
            {
                runtime.LogError("未配置模板文件路径", null,ModuleId);
                return Result.Fail("模板路径为空");
            }

            // 读取提前训练好的模板ID（工程实际可把模板ID缓存进配方，这里简化）
            // 执行匹配
            var matchResult = TemplateMatchTool.FindShapeModel(context.SourceImage, 0, MinScore);
            if (!matchResult.Success)
            {
                context.SharedData[ContextDataKeys.Match_IsSuccess] = false;
                runtime.LogError("模板匹配运算失败", null, ModuleId);
                return matchResult;
            }

            var matchArray = matchResult.Data;
            if (matchArray.Length == 0)
            {
                // 无满足分数的匹配结果
                context.SharedData[ContextDataKeys.Match_IsSuccess] = false;
                runtime.LogWarn("未找到符合分数的工件", ModuleId);
                return Result.Ok();
            }

            // 取匹配分数最高第一个工件
            var bestMatch = matchArray[0];
            context.SharedData[ContextDataKeys.Match_IsSuccess] = true;

            // 像素坐标转为世界毫米位姿（依赖标定矩阵，此处预留全局标定参数读取）
            Pose3D pose = new Pose3D(bestMatch.PixelCol, bestMatch.PixelRow, 0, 0, 0, bestMatch.RotateDegree);
            context.WorkpiecePose = pose;
            context.SharedData[ContextDataKeys.Match_WorkPose] = pose;

            runtime.LogInfo($"定位成功 X:{pose.X:F2} Y:{pose.Y:F2} Rz:{pose.Rz:F2} 分数:{bestMatch.Score:F3}", ModuleId);
            // 推送UI绘制定位十字
            runtime.PublishUiEvent("Ui.DrawMatchCross", pose);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"TemplateFilePath", TemplateFilePath},
                {"MinScore", MinScore},
                {"Enable", Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            TemplateFilePath = recipeDict.SafeGet("TemplateFilePath", string.Empty);
            MinScore = recipeDict.SafeGet("MinScore", 0.7);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        //public UserControl GetConfigPanel()
        //{
        //    return new ShapeTemplateMatchPanel(this);
        //}
    }
}