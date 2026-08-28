using System;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    public static class MatchTool
    {
        /// <summary>寻找 2D 形状模板</summary>
        public static Result<TemplateMatchResult[]> ApplyFindShapeModel(object nativeImage, HalconDotNet.HTuple modelId, double minScore)
        {
            var hImg = nativeImage as HalconDotNet.HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("输入的图像句柄无效或未初始化");

            return TemplateMatchTool.FindShapeModel(hImg, modelId, minScore);
        }

        /// <summary>寻找 2D NCC 灰度模板</summary>
        public static Result<TemplateMatchResult[]> ApplyFindNccModel(object nativeImage, HalconDotNet.HTuple modelId, double minScore)
        {
            var hImg = nativeImage as HalconDotNet.HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("输入的图像句柄无效或未初始化");

            return NccMatchTool.FindNccModel(hImg, modelId, minScore);
        }
    }
}