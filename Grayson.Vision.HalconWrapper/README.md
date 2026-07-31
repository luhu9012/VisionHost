# 阶段4：Grayson.Vision.HalconWrapper 完整落地（全中文注释）
## 基础配置说明
1. 项目类型：.NET Framework 4.7 类库，C#7.4，平台目标**x64**，适配Halcon19.11 x64
2. 必须引用：
   - Grayson.Vision.Contracts
   - Grayson.Vision.Common
   - HalconDotNet（19.11 x64，属性：复制到输出目录=如果较新则复制）
3. 红线约束：上层`Nodes`、插件**禁止直接using HalconDotNet**，所有图像处理必须调用本层封装；本层是全系统唯一允许直接操作HObject的项目。
4. 核心设计：封装内存自动管控、统一返回`Result`、全部异常捕获，对外屏蔽原生Halcon报错细节。

## 完整目录结构
```
Grayson.Vision.HalconWrapper
├─ Core
│  ├─ HalconMemoryGuard.cs     HObject内存安全托管，防止泄漏
│  └─ HalconGlobalHelper.cs    全局算子公共工具、坐标系转换
├─ ImageProc
│  ├─ ImageBasicTool.cs        图像加载、保存、通道转换、裁剪
│  ├─ ImageFilterTool.cs       滤波、平滑、形态学运算
│  └─ ImageThresholdTool.cs    灰度阈值、动态阈值分割
├─ Match2D
│  ├─ TemplateMatchTool.cs     2D模板匹配（基于create_shape_model）
│  └─ ShapeMatchTool.cs        形状匹配进阶封装
├─ Measure2D
│  ├─ EdgeMeasureTool.cs       边缘检测、距离、角度、尺寸测量
└─ Calibration
   └─ Calib2DTool.cs           九点标定、像素转物理毫米换算
```

# 完整可复制代码（全部带中文注释）
## 1. Core/HalconMemoryGuard.cs 内存托管核心（解决HObject内存泄漏）
```csharp
using System;
using System.Collections.Generic;
using Grayson.Vision.Common.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// Halcon资源托管守卫
    /// 统一管理临时HObject生命周期，批量释放，避免零散忘记Dispose造成内存持续上涨
    /// 业务单元执行完毕统一调用Clean，所有临时图像全部回收
    /// </summary>
    public class HalconMemoryGuard : IDisposable
    {
        /// <summary>托管的所有临时图像容器</summary>
        private readonly List<HObject> _managedImages = new List<HObject>();
        private bool _disposed = false;

        /// <summary>将需要管控的HObject加入托管列表</summary>
        public void Register(HObject hoObj)
        {
            if (hoObj == null || hoObj.IsInitialized == false)
                return;
            _managedImages.Add(hoObj);
        }

        /// <summary>批量释放所有托管图像，清空列表</summary>
        public void CleanAll()
        {
            foreach (var img in _managedImages)
            {
                try
                {
                    if (img.IsInitialized)
                        img.Dispose();
                }
                catch (Exception ex)
                {
                    GlobalLogger.Warn($"Halcon图像释放异常", ex.Message, nameof(HalconMemoryGuard));
                }
            }
            _managedImages.Clear();
        }

        #region 标准IDisposable实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                CleanAll();
            }
            _disposed = true;
        }

        ~HalconMemoryGuard()
        {
            Dispose(false);
        }
        #endregion
    }
}
```

## 2. Core/HalconGlobalHelper.cs 全局公共工具
```csharp
using System;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Common.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// 全局通用工具：Pose互转、像素/毫米换算、HWindow绘图基础封装
    /// 所有跨模块通用底层逻辑收拢在此
    /// </summary>
    public static class HalconGlobalHelper
    {
        /// <summary>
        /// Halcon原生HTuple Pose 转为系统统一Pose3D结构体
        /// Halcon姿态：X,Y,Z,Rx,Ry,Rz（角度）
        /// </summary>
        public static Pose3D HtuplePoseToPose3D(HTuple hPose)
        {
            if (hPose == null || hPose.Length < 6)
            {
                GlobalLogger.Warn("Halcon Pose数组长度不足", nameof(HalconGlobalHelper));
                return new Pose3D(0, 0, 0, 0, 0, 0);
            }

            Pose3D pose = new Pose3D(
                hPose[0].D,
                hPose[1].D,
                hPose[2].D,
                hPose[3].D,
                hPose[4].D,
                hPose[5].D
            );
            return pose;
        }

        /// <summary>
        /// 系统Pose3D转为Halcon HTuple，用于算子入参
        /// </summary>
        public static HTuple Pose3DToHtuplePose(Pose3D pose)
        {
            HTuple hPose = new HTuple();
            hPose.Append(pose.X);
            hPose.Append(pose.Y);
            hPose.Append(pose.Z);
            hPose.Append(pose.Rx);
            hPose.Append(pose.Ry);
            hPose.Append(pose.Rz);
            return hPose;
        }

        /// <summary>
        /// 像素坐标转物理毫米坐标（依赖九点标定完成的标定矩阵）
        /// </summary>
        /// <param name="pixelX">图像像素X</param>
        /// <param name="pixelY">图像像素Y</param>
        /// <param name="calibHomMat2D">标定HomMat2D矩阵</param>
        /// <returns>物理XY毫米</returns>
        public static (double worldX, double worldY) PixelToWorldMm(double pixelX, double pixelY, HTuple calibHomMat2D)
        {
            try
            {
                HHomMat2D.HomMat2dProjectPoint(calibHomMat2D, pixelX, pixelY, out HTuple wx, out HTuple wy);
                return (wx.D, wy.D);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("像素转世界坐标失败", ex, nameof(HalconGlobalHelper));
                return (0, 0);
            }
        }

        /// <summary>
        /// 世界毫米坐标转回图像像素坐标
        /// </summary>
        public static (double pixelX, double pixelY) WorldMmToPixel(double worldX, double worldY, HTuple calibHomMat2D)
        {
            try
            {
                HHomMat2D.HomMat2dProjectPointInv(calibHomMat2D, worldX, worldY, out HTuple px, out HTuple py);
                return (px.D, py.D);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("世界坐标转回像素失败", ex, nameof(HalconGlobalHelper));
                return (0, 0);
            }
        }

        /// <summary>
        /// 在HWindow绘制十字定位标记（统一视觉标记样式）
        /// </summary>
        public static void DrawCross(HWindow window, double x, double y, double crossLen = 20, string color = "red")
        {
            if (window == null || window.IsInitialized == false) return;
            try
            {
                window.SetColor(color);
                window.DispLine(y - crossLen, x, y + crossLen, x);
                window.DispLine(y, x - crossLen, y, x + crossLen);
            }
            catch
            {
                // 绘图异常不阻断主流程
            }
        }
    }
}
```

## 3. ImageProc/ImageBasicTool.cs 图像基础IO、裁剪、通道处理
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像基础操作：文件读写、ROI裁剪、灰度/彩色互转、尺寸缩放
    /// 所有对外返回Result封装，托管HObject资源
    /// </summary>
    public static class ImageBasicTool
    {
        /// <summary>
        /// 读取本地图片文件返回HObject
        /// </summary>
        public static Result<HObject> ReadImageFile(string filePath)
        {
            try
            {
                HObject img;
                HOperatorSet.ReadImage(out img, filePath);
                return Result<HObject>.Ok(img);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"读取图片失败:{filePath}", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("图片读取异常", -1, ex);
            }
        }

        /// <summary>
        /// HObject保存到本地文件（png/jpg/tif）
        /// </summary>
        public static Result SaveImageToFile(HObject image, string filePath, string format = "png")
        {
            if (image == null || !image.IsInitialized)
                return Result.Fail("无效图像");
            try
            {
                HOperatorSet.WriteImage(image, format, 0, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"保存图片失败:{filePath}", ex, nameof(ImageBasicTool));
                return Result.Fail("图像保存失败", -1, ex);
            }
        }

        /// <summary>
        /// 彩色图转灰度图
        /// </summary>
        public static Result<HObject> RgbToGray(HObject colorImage)
        {
            if (colorImage == null || !colorImage.IsInitialized)
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject grayImg;
                HOperatorSet.Rgb1ToGray(colorImage, out grayImg);
                return Result<HObject>.Ok(grayImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("彩色转灰度失败", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("灰度转换异常", -1, ex);
            }
        }

        /// <summary>
        /// ROI矩形裁剪图像
        /// </summary>
        /// <param name="row1">起始行Y</param>
        /// <param name="col1">起始列X</param>
        /// <param name="row2">结束行Y</param>
        /// <param name="col2">结束列X</param>
        public static Result<HObject> CropImage(HObject srcImage, double row1, double col1, double row2, double col2)
        {
            if (srcImage == null || !srcImage.IsInitialized)
                return Result<HObject>.Fail("原图无效");
            try
            {
                HObject roiRect, cropImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, row1, col1, row2, col2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(srcImage, roiRect, out cropImg);
                }
                return Result<HObject>.Ok(cropImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("图像ROI裁剪失败", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("裁剪异常", -1, ex);
            }
        }
    }
}
```

## 4. ImageProc/ImageFilterTool.cs 滤波、形态学预处理（产线高频使用）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像滤波平滑 + 形态学开闭运算，适配划痕、斑点、脏点预处理
    /// 封装常用算子：高斯、均值、中值、开运算、闭运算、膨胀腐蚀
    /// </summary>
    public static class ImageFilterTool
    {
        /// <summary>高斯平滑滤波，消除相机噪点</summary>
        /// <param name="maskSize">卷积核尺寸，推荐3/5/7</param>
        public static Result<HObject> GaussFilter(HObject srcImg, int maskSize = 5)
        {
            if (srcImg == null || !srcImg.IsInitialized)
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject outImg;
                HOperatorSet.GaussFilter(srcImg, out outImg, maskSize, maskSize);
                return Result<HObject>.Ok(outImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("高斯滤波执行失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("高斯滤波异常", -1, ex);
            }
        }

        /// <summary>中值滤波，去除椒盐噪点、白点黑点脏污</summary>
        public static Result<HObject> MedianFilter(HObject srcImg, int mask = 3)
        {
            try
            {
                HObject res;
                HOperatorSet.MedianFilter(srcImg, out res, mask, mask, "circle");
                return Result<HObject>.Ok(res);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("中值滤波失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("中值滤波异常", -1, ex);
            }
        }

        /// <summary>腐蚀运算：收缩白色区域，去除细小白点杂讯</summary>
        public static Result<HObject> Erode(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Erode);
        }

        /// <summary>膨胀运算：扩大白色区域，填补细小缝隙</summary>
        public static Result<HObject> Dilate(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Dilate);
        }

        /// <summary>开运算：先腐蚀后膨胀，去除小白点，整体轮廓不变</summary>
        public static Result<HObject> OpenMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Open);
        }

        /// <summary>闭运算：先膨胀后腐蚀，填补小黑洞、缝隙</summary>
        public static Result<HObject> CloseMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Close);
        }

        #region 内部形态学统一入口
        private enum MorphType
        {
            Erode, Dilate, Open, Close
        }

        private static Result<HObject> MorphologyBase(HObject srcImg, int kernel, MorphType type)
        {
            if (srcImg == null || !srcImg.IsInitialized)
                return Result<HObject>.Fail("图像无效");
            try
            {
                HObject kernelRegion, dstImg;
                HOperatorSet.GenCircle(out kernelRegion, kernel, kernel, kernel);
                switch (type)
                {
                    case MorphType.Erode:
                        HOperatorSet.Erosion1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Dilate:
                        HOperatorSet.Dilation1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Open:
                        HOperatorSet.Opening(srcImg, kernelRegion, out dstImg);
                        break;
                    case MorphType.Close:
                        HOperatorSet.Closing(srcImg, kernelRegion, out dstImg);
                        break;
                    default:
                        return Result<HObject>.Fail("不支持的形态学类型");
                }
                kernelRegion.Dispose();
                return Result<HObject>.Ok(dstImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"形态学运算{type}失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("形态学处理异常", -1, ex);
            }
        }
        #endregion
    }
}
```

## 5. ImageProc/ImageThresholdTool.cs 阈值分割（缺陷、有无检测核心）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 灰度阈值分割工具
    /// 固定阈值、反阈值、动态自适应阈值，用于提取亮缺陷、暗缺陷、物料轮廓
    /// </summary>
    public static class ImageThresholdTool
    {
        /// <summary>
        /// 固定灰度阈值分割：低于minGray、高于maxGray保留
        /// 输出：满足灰度区间的Region区域
        /// </summary>
        public static Result<HObject> FixedThreshold(HObject grayImage, int minGray, int maxGray)
        {
            if (grayImage == null || !grayImage.IsInitialized)
                return Result<HObject>.Fail("灰度图无效");
            try
            {
                HObject regionOut;
                HOperatorSet.Threshold(grayImage, out regionOut, minGray, maxGray);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("固定阈值分割失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("阈值分割异常", -1, ex);
            }
        }

        /// <summary>
        /// 自适应动态阈值（明暗不均匀工况必备）
        /// 用局部均值做参考，亮于均值offset则检出
        /// </summary>
        /// <param name="maskSize">局部窗口尺寸</param>
        /// <param name="offset">亮度偏移</param>
        public static Result<HObject> AutoThreshold(HObject grayImage, int maskSize = 15, int offset = 5)
        {
            try
            {
                HObject regionOut;
                HOperatorSet.DynThreshold(grayImage, grayImage, out regionOut, maskSize, maskSize, offset, "light");
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("动态阈值分割失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("动态阈值异常", -1, ex);
            }
        }

        /// <summary>
        /// 区域筛选：按面积过滤小噪点，保留指定面积区间轮廓
        /// </summary>
        public static Result<HObject> SelectRegionByArea(HObject inputRegion, double areaMin, double areaMax)
        {
            try
            {
                HObject regionFiltered;
                HOperatorSet.SelectShape(inputRegion, out regionFiltered, "area", "and", areaMin, areaMax);
                return Result<HObject>.Ok(regionFiltered);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("区域面积筛选失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("区域筛选异常", -1, ex);
            }
        }
    }
}
```

## 6. Match2D/TemplateMatchTool.cs 2D模板匹配（定位核心）
```csharp
using System;
using Grayson.Vision.Common.Logging;
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
            if (templateImage == null || !templateImage.IsInitialized)
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
                        "auto", "use_polarity", 30, 0, out modelId);
                }
                return Result<int>.Ok(modelId.I);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("创建Shape模板失败", ex, nameof(TemplateMatchTool));
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
            if (searchImage == null || !searchImage.IsInitialized)
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            try
            {
                HTuple rows, cols, angles, scores;
                HOperatorSet.FindShapeModel(searchImage, modelId, 0, 0, 0, 0, minScore, 0, 0, out rows, out cols, out angles, out scores);

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
                GlobalLogger.Error("模板匹配查找失败", ex, nameof(TemplateMatchTool));
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
                GlobalLogger.Error("销毁模板失败", ex, nameof(TemplateMatchTool));
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
```

## 7. Measure2D/EdgeMeasureTool.cs 边缘尺寸测量（长宽、间距、角度）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>
    /// 基于measure工具的亚像素级边缘测量
    /// 测长度、宽度、孔间距、两条边夹角，精度优于普通轮廓计算
    /// </summary>
    public static class EdgeMeasureTool
    {
        /// <summary>
        /// 直线矩形测量卡尺，查找两侧边缘，返回两点距离
        /// </summary>
        /// <param name="grayImg">灰度原图</param>
        /// <param name="lineRow1/Col1">卡尺起点</param>
        /// <param name="lineRow2/Col2">卡尺终点</param>
        /// <param name="width">卡尺横向宽度</param>
        /// <param name="edgeSelect">first/last/all 取第一条/最后一条边缘</param>
        public static Result<double> MeasureLineDistance(HObject grayImg, double lineRow1, double lineCol1, double lineRow2, double lineCol2, double width, string edgeSelect = "all")
        {
            if (grayImg == null || !grayImg.IsInitialized)
                return Result<double>.Fail("灰度图无效");
            try
            {
                HTuple measureHandle;
                // 创建测量句柄
                HOperatorSet.GenMeasureRectangle2(lineRow1, lineCol1, lineRow2, lineCol2, width, out measureHandle);
                HTuple edgeRows, edgeCols, amplitudes, distances;
                // 执行边缘查找
                HOperatorSet.MeasurePos(grayImg, measureHandle, 10, "all", edgeSelect, out edgeRows, out edgeCols, out amplitudes, out distances);
                // 释放句柄
                HOperatorSet.CloseMeasure(measureHandle);

                if (distances.Length >= 2)
                {
                    return Result<double>.Ok(distances[1].D - distances[0].D);
                }
                return Result<double>.Fail("未找到足够边缘点");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("直线距离测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 圆形卡尺测量圆孔内径，返回直径
        /// </summary>
        public static Result<double> MeasureCircleDiameter(HObject grayImg, double centerRow, double centerCol, double radiusMin, double radiusMax)
        {
            try
            {
                HTuple rows, cols, radii;
                HOperatorSet.FindCircle(grayImg, centerRow, centerCol, radiusMin, radiusMax, out rows, out cols, out radii);
                if (radii.Length > 0)
                {
                    return Result<double>.Ok(radii[0].D * 2);
                }
                return Result<double>.Fail("未检出圆孔");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("圆孔测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("圆孔测量异常", -1, ex);
            }
        }
    }
}
```

## 8. Calibration/Calib2DTool.cs 九点标定工具
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 2D九点手眼标定（相机平面对标工作台）
    /// 采集9个特征点像素坐标+物理坐标，生成HomMat2D矩阵
    /// 后续所有定位结果依靠矩阵完成像素↔毫米换算
    /// </summary>
    public static class Calib2DTool
    {
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵
        /// </summary>
        /// <param name="pixelXList">9个点像素X数组</param>
        /// <param name="pixelYList">9个点像素Y数组</param>
        /// <param name="worldXList">9个点实际物理X mm</param>
        /// <param name="worldYList">9个点实际物理Y mm</param>
        /// <returns>HomMat2D标定矩阵</returns>
        public static Result<HTuple> CalcNinePointHomMat(HTuple pixelXList, HTuple pixelYList, HTuple worldXList, HTuple worldYList)
        {
            try
            {
                HTuple homMat;
                HOperatorSet.VectorToHomMat2d(pixelXList, pixelYList, worldXList, worldYList, out homMat);
                return Result<HTuple>.Ok(homMat);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("九点标定矩阵计算失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("标定计算异常", -1, ex);
            }
        }

        /// <summary>保存标定矩阵到本地文件</summary>
        public static Result SaveHomMatToFile(HTuple homMat, string filePath)
        {
            try
            {
                HOperatorSet.WriteTuple(homMat, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("标定矩阵保存失败", ex, nameof(Calib2DTool));
                return Result.Fail("矩阵保存异常", -1, ex);
            }
        }

        /// <summary>从文件读取标定矩阵</summary>
        public static Result<HTuple> LoadHomMatFromFile(string filePath)
        {
            try
            {
                HTuple mat;
                HOperatorSet.ReadTuple(out mat, filePath);
                return Result<HTuple>.Ok(mat);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("标定矩阵读取失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("矩阵读取异常", -1, ex);
            }
        }
    }
}
```

# 强制开发规范（必须遵守）
1. **上层业务绝对禁止using HalconDotNet**
BusinessUnits、硬件插件、Shell全部不能直接引用原生Halcon类；所有图像、匹配、测量全部调用本层静态工具。
2. 临时HObject必须纳入`HalconMemoryGuard`托管，函数返回的HObject由调用方自行Dispose。
3. 所有算子执行包裹try-catch，日志完整记录，不会因为算子报错直接崩溃整条流程。
4. 坐标统一流转规则：
   算法内部用像素计算；最终定位结果用HomMat转毫米，转为`Pose3D`给运动/机器人单元。
5. 模板ID、测量句柄必须手动释放，封装层提供Clear方法兜底。

# 下一步可选交付清单
1. Plugins.Camera.Hikvision 海康相机完整插件代码；
2. 第一个业务单元：CameraGrabUnit + WPF配置面板全套带注释；
3. 宿主硬件管理器HardwareManager完整代码。