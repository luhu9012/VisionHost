using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    /// <summary>
    /// 基于shape_model的2D刚性模板匹配
    /// 工业最常用工件定位：平移+旋转匹配，输出坐标、角度、匹配分数
    /// 统一封装模板创建、在线匹配、参数归一化
    /// </summary>
    public static class TemplateMatchTool
    {
        /// <summary>
        /// 创建形状模板（离线训练模板使用）
        /// </summary>
        /// <param name="templateImage">模板原图</param>
        /// <param name="roiRow1/Col1/Row2/Col2">模板ROI范围</param>
        /// <param name="angleStart">起始旋转角度(°)</param>
        /// <param name="angleEnd">终止旋转角度(°)</param>
        /// <returns>模板ID，匹配时传入使用</returns>
        public static Result<int> CreateShapeModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd)
        {
            if (templateImage == null || !templateImage.IsInitialized())
                return Result<int>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                // 截取ROI区域创建模板
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(templateImage, roiRect, out roiImg);
                    guard.Register(roiImg);

                    // 创建形状模板，角度转弧度
                    HOperatorSet.CreateShapeModel(roiImg, 4, angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "auto", "use_polarity", 30, 0, out modelId);
                }
                return Result<int>.Ok(modelId.I);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "创建Shape模板失败", ex);
                return Result<int>.Fail("模板创建异常", -1, ex);
            }
        }

        /// <summary>
        /// 执行模板搜索匹配
        /// </summary>
        /// <param name="searchImage">待搜索大图</param>
        /// <param name="modelId">模板ID</param>
        /// <param name="minScore">最低匹配分数（0~1）</param>
        /// <returns>匹配结果集合：坐标、角度、分数</returns>
        public static Result<TemplateMatchResult[]> FindShapeModel(HObject searchImage, int modelId, double minScore = 0.7)
        {
            if (searchImage == null || !searchImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            try
            {
                HTuple rows, cols, angles, scores;
                HOperatorSet.FindShapeModel(searchImage, modelId, 0, 0, 0, 0, minScore, 0, 0, 0, out rows, out cols, out angles, out scores);

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
                LogBus.Error(nameof(TemplateMatchTool), "模板匹配查找失败", ex);
                return Result<TemplateMatchResult[]>.Fail("匹配运算异常", -1, ex);
            }
        }

        /// <summary>释放模板内存，用完必须销毁</summary>
        public static Result ClearShapeModel(int modelId)
        {
            try
            {
                HOperatorSet.ClearShapeModel(modelId);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "销毁模板失败", ex);
                return Result.Fail("模板释放异常", -1, ex);
            }
        }
    }

    /// <summary>单条模板匹配结果实体</summary>
    public class TemplateMatchResult
    {
        /// <summary>匹配中心行像素Y</summary>
        public double PixelRow { get; set; }
        /// <summary>匹配中心列像素X</summary>
        public double PixelCol { get; set; }
        /// <summary>旋转角度 角度制°</summary>
        public double RotateDegree { get; set; }
        /// <summary>匹配相似度 0~1</summary>
        public double Score { get; set; }
    }
}