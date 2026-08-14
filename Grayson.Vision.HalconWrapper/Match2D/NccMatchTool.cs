using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    public static class NccMatchTool
    {
        /// <summary>创建 NCC 灰度模板</summary>
        public static Result<int> CreateNccModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd)
        {
            if (templateImage == null || !templateImage.IsInitialized())
                return Result<int>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(templateImage, roiRect, out roiImg);
                    guard.Register(roiImg);

                    HOperatorSet.CreateNccModel(roiImg, "auto", angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "use_polarity", out modelId);
                }
                return Result<int>.Ok(modelId.I);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("创建 NCC 模板失败", ex, nameof(NccMatchTool));
                return Result<int>.Fail("NCC 模板创建异常", -1, ex);
            }
        }

        /// <summary>查找 NCC 灰度模板</summary>
        public static Result<TemplateMatchResult[]> FindNccModel(HObject searchImage, int modelId, double minScore = 0.7)
        {
            if (searchImage == null || !searchImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            try
            {
                HTuple rows, cols, angles, scores;
                HOperatorSet.FindNccModel(searchImage, modelId, 0, 0, minScore, 1, 0.5, "true", 0, out rows, out cols, out angles, out scores);

                int count = rows.Length;
                TemplateMatchResult[] resultArr = new TemplateMatchResult[count];
                for (int i = 0; i < count; i++)
                {
                    resultArr[i] = new TemplateMatchResult
                    {
                        PixelRow = rows[i].D,
                        PixelCol = cols[i].D,
                        RotateDegree = angles[i].D / Math.PI * 180,
                        Score = scores[i].D
                    };
                }
                return Result<TemplateMatchResult[]>.Ok(resultArr);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("NCC 模板匹配查找失败", ex, nameof(NccMatchTool));
                return Result<TemplateMatchResult[]>.Fail("NCC 匹配运算异常", -1, ex);
            }
        }

        /// <summary>释放 NCC 模板</summary>
        public static Result ClearNccModel(int modelId)
        {
            try
            {
                HOperatorSet.ClearNccModel(modelId);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("销毁 NCC 模板失败", ex, nameof(NccMatchTool));
                return Result.Fail("NCC 模板释放异常", -1, ex);
            }
        }
    }
}