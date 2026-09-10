using System;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    public static class MatchTool
    {
        /// <summary>寻找 2D 形状模板</summary>
        /// <param name="angleStartDeg">覆盖起始角度（°），null 表示使用模板内建范围</param>
        /// <param name="angleEndDeg">覆盖终止角度（°），null 表示使用模板内建范围</param>
        public static Result<TemplateMatchResult[]> ApplyFindShapeModel(object nativeImage, HalconDotNet.HTuple modelId, double minScore,
            double? angleStartDeg = null, double? angleEndDeg = null)
        {
            var hImg = nativeImage as HalconDotNet.HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("输入的图像句柄无效或未初始化");

            return TemplateMatchTool.FindShapeModel(hImg, modelId, minScore, angleStartDeg, angleEndDeg);
        }

        /// <summary>寻找 2D NCC 灰度模板</summary>
        /// <param name="angleStartDeg">覆盖起始角度（°），null 表示使用模板内建范围</param>
        /// <param name="angleEndDeg">覆盖终止角度（°），null 表示使用模板内建范围</param>
        public static Result<TemplateMatchResult[]> ApplyFindNccModel(object nativeImage, HalconDotNet.HTuple modelId, double minScore,
            double? angleStartDeg = null, double? angleEndDeg = null)
        {
            var hImg = nativeImage as HalconDotNet.HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("输入的图像句柄无效或未初始化");

            return NccMatchTool.FindNccModel(hImg, modelId, minScore, angleStartDeg, angleEndDeg);
        }
    }
}