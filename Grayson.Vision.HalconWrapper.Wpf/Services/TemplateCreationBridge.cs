//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 模板创建桥接服务 —— 把 WPF 显示层持有的 IRenderImage 交给
//        HalconWrapper.TemplateManager 落盘（.shm/.ncc + .json 元数据）。
//        内部解包 HalconRenderImage.HImage，halcondotnet 类型不泄漏到 WpfUI
//        （控件公共 API 的 MC1000 约束）。
//===================================================================================
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Wpf.Services
{
    /// <summary>
    /// 模板创建桥接服务（模板管理界面 → TemplateManager 注册表）。
    /// 公开方法只接受 Contracts 类型与 double，调用方无需引用 halcondotnet。
    /// </summary>
    public static class TemplateCreationBridge
    {
        /// <summary>创建形状模板（轮廓/边缘匹配）
        /// roiShape：基底形状（v2：用户画的圆/旋转矩形等学习框；null=ROI 矩形，旧语义）；
        /// learnMask：学习掩膜（区域涂抹 ∪/∖，null/未启用=全 ROI）；ownerStation：归属工位（可空）；
        /// allowOverwrite：同名覆盖重学（模板编辑"重新学习"路径）；features：模板特征（点/面，可空）；
        /// datum：基准点（绝对坐标；null=默认锚点=学习域中心）；calipers：测量卡尺（可空）；
        /// searchRoiEnabled/SearchRoi*：搜索框（模板资产，匹配时自动裁剪搜索区）；workpieceShape：工件形状选型。</summary>
        public static Result<TemplateInfo> CreateShapeTemplate(
            IRenderImage image, string name,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark = "", string sourceImagePath = "",
            TemplateLearnMask learnMask = null, string ownerStation = null, bool allowOverwrite = false,
            List<TemplateFeature> features = null,
            TemplateMaskShape roiShape = null,
            TemplateDatum datum = null, List<TemplateCaliper> calipers = null,
            bool searchRoiEnabled = false, double searchRoiRow1 = 0, double searchRoiCol1 = 0,
            double searchRoiRow2 = 0, double searchRoiCol2 = 0,
            string workpieceShape = "Generic")
        {
            return CreateTemplate(image, name, TemplateMatchType.Shape,
                roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath,
                learnMask, ownerStation, allowOverwrite, features, roiShape,
                datum, calipers, searchRoiEnabled, searchRoiRow1, searchRoiCol1,
                searchRoiRow2, searchRoiCol2, workpieceShape);
        }

        /// <summary>创建 NCC 灰度模板（纹理/印刷图案匹配，参数含义同 CreateShapeTemplate）</summary>
        public static Result<TemplateInfo> CreateNccTemplate(
            IRenderImage image, string name,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark = "", string sourceImagePath = "",
            TemplateLearnMask learnMask = null, string ownerStation = null, bool allowOverwrite = false,
            List<TemplateFeature> features = null,
            TemplateMaskShape roiShape = null,
            TemplateDatum datum = null, List<TemplateCaliper> calipers = null,
            bool searchRoiEnabled = false, double searchRoiRow1 = 0, double searchRoiCol1 = 0,
            double searchRoiRow2 = 0, double searchRoiCol2 = 0,
            string workpieceShape = "Generic")
        {
            return CreateTemplate(image, name, TemplateMatchType.Ncc,
                roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath,
                learnMask, ownerStation, allowOverwrite, features, roiShape,
                datum, calipers, searchRoiEnabled, searchRoiRow1, searchRoiCol1,
                searchRoiRow2, searchRoiCol2, workpieceShape);
        }

        /// <summary>
        /// 【模板学习·预览】(2026-09-09 学习/落盘分离)：与 CreateShapeTemplate 同一学习链（建模型+自测），
        /// 但**不写盘不登记**。返回 TemplateLearnPreview（Template=自测信息；ContourXld=将落盘模板的
        /// 贴合轮廓 XLD，原图坐标，调用方上屏后负责 Dispose）。用户看到轮廓+自测分满意后，
        /// 再调 CreateShapeTemplate 落盘——两次同参数同链，结果一致。
        /// </summary>
        public static Result<TemplateLearnPreview> CreateShapeTemplatePreview(
            IRenderImage image, string name,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark = "", string sourceImagePath = "",
            TemplateLearnMask learnMask = null, string ownerStation = null,
            List<TemplateFeature> features = null,
            TemplateMaskShape roiShape = null,
            TemplateDatum datum = null, List<TemplateCaliper> calipers = null,
            bool searchRoiEnabled = false, double searchRoiRow1 = 0, double searchRoiCol1 = 0,
            double searchRoiRow2 = 0, double searchRoiCol2 = 0,
            string workpieceShape = "Generic")
        {
            var hImage = (image as HalconRenderImage)?.HImage;
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<TemplateLearnPreview>.Fail("模板图像无效：请先加载本地图片或从相机采集一帧。");
            }
            var manager = new TemplateManager();
            return manager.CreateShapeTemplatePreview(name, hImage,
                roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath,
                learnMask, ownerStation, features,
                datum: datum, calipers: calipers,
                searchRoiEnabled: searchRoiEnabled,
                searchRoiRow1: searchRoiRow1, searchRoiCol1: searchRoiCol1,
                searchRoiRow2: searchRoiRow2, searchRoiCol2: searchRoiCol2,
                workpieceShape: workpieceShape,
                roiShape: roiShape);
        }

        private static Result<TemplateInfo> CreateTemplate(
            IRenderImage image, string name, TemplateMatchType type,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark, string sourceImagePath,
            TemplateLearnMask learnMask, string ownerStation, bool allowOverwrite,
            List<TemplateFeature> features,
            TemplateMaskShape roiShape,
            TemplateDatum datum, List<TemplateCaliper> calipers,
            bool searchRoiEnabled, double searchRoiRow1, double searchRoiCol1,
            double searchRoiRow2, double searchRoiCol2,
            string workpieceShape)
        {
            var hImage = (image as HalconRenderImage)?.HImage;
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<TemplateInfo>.Fail("模板图像无效：请先加载本地图片或从相机采集一帧。");
            }

            var manager = new TemplateManager();
            return type == TemplateMatchType.Shape
                ? manager.CreateShapeTemplate(name, hImage,
                    roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath,
                    learnMask, ownerStation, allowOverwrite, features,
                    datum: datum, calipers: calipers,
                    searchRoiEnabled: searchRoiEnabled,
                    searchRoiRow1: searchRoiRow1, searchRoiCol1: searchRoiCol1,
                    searchRoiRow2: searchRoiRow2, searchRoiCol2: searchRoiCol2,
                    workpieceShape: workpieceShape,
                    roiShape: roiShape)
                : manager.CreateNccTemplate(name, hImage,
                    roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath,
                    learnMask, ownerStation, allowOverwrite, features,
                    datum: datum, calipers: calipers,
                    searchRoiEnabled: searchRoiEnabled,
                    searchRoiRow1: searchRoiRow1, searchRoiCol1: searchRoiCol1,
                    searchRoiRow2: searchRoiRow2, searchRoiCol2: searchRoiCol2,
                    workpieceShape: workpieceShape,
                    roiShape: roiShape);
        }

        /// <summary>
        /// 模板实拍验证（模板体检）：对当前显示的图像跑一次低门槛匹配（MinScore 建议 0.4）。
        /// 用途：创建后自测分只是"模板在源图上认得自己"，实拍验证才是"换个场景还认不认得"——
        /// 配合"两步法"（目标挪位后再验证一次）可检测出 ROI 框过大导致的"锁死背景"故障。
        /// </summary>
        /// <param name="image">验证图像（本地图片或相机新采一帧）</param>
        /// <param name="templateName">模板名</param>
        /// <param name="minScore">验证门槛（建议 0.4，比生产阈值低以暴露真实分数）</param>
        public static Result<Grayson.Vision.HalconWrapper.Match2D.TemplateMatchResult[]> MatchTemplate(
            IRenderImage image, string templateName, double minScore)
        {
            var hImage = (image as HalconRenderImage)?.HImage;
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<Grayson.Vision.HalconWrapper.Match2D.TemplateMatchResult[]>.Fail(
                    "验证图像无效：请先载入图片或从相机采集一帧。");
            }
            return new TemplateManager().MatchByName(templateName, hImage, minScore);
        }

        /// <summary>
        /// v2 统一口径实拍验证/试测：模板资产内建【搜索框】自动生效（无需调用方传搜索区参数），
        /// 匹配输出即基准点（无卡尺=锚点直出；有卡尺=亚像素精测覆盖）。
        /// 与创建链同一引擎入口（TemplateManager.MatchWithDatum），管理页验证与生产/节点消费同一口径。
        /// </summary>
        public static Result<TemplateMeasureOutput> MatchWithDatum(
            IRenderImage image, string templateName, double minScore)
        {
            var hImage = (image as HalconRenderImage)?.HImage;
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<TemplateMeasureOutput>.Fail(
                    "验证图像无效：请先载入图片或从相机采集一帧。");
            }
            return new TemplateManager().MatchWithDatum(templateName, hImage, minScore);
        }

        /// <summary>精测基准点大十字 XLD（红色；CircleCenter/LineIntersection 精测覆盖时画最终基准点）</summary>
        public static object CreateDatumCross(double row, double col)
        {
            HOperatorSet.GenCrossContourXld(out HObject cross, row, col, 28, 0.0);
            return cross;
        }

        /// <summary>
        /// 把全部卡尺的取样边缘点画成小十字 XLD（lime；合并为一个对象，总点数封顶防糊屏）。
        /// 返回 object(HObject)；无边缘点返回 null。所有权归调用方。
        /// </summary>
        public static object CreateEdgePointMarkers(IList<TemplateCaliperMeasurement> measurements)
        {
            if (measurements == null) return null;
            const int maxTotal = 260;
            int drawn = 0;
            HObject merged = null;
            foreach (var m in measurements)
            {
                if (m?.Points == null) continue;
                foreach (var p in m.Points)
                {
                    if (p == null || drawn >= maxTotal) break;
                    HOperatorSet.GenCrossContourXld(out HObject one, p.Row, p.Col, 5, 0.0);
                    if (merged == null)
                    {
                        merged = one;
                    }
                    else
                    {
                        try
                        {
                            HOperatorSet.ConcatObj(merged, one, out HObject joined);
                            merged.Dispose();
                            merged = joined;
                        }
                        finally { one.Dispose(); }
                    }
                    drawn++;
                }
                if (drawn >= maxTotal) break;
            }
            return merged;
        }

        /// <summary>卡尺测量带 XLD（橙色；编辑静态 target=ROI 中心 / 匹配回放 target=匹配位姿），
        /// 调用方负责释放。无有效卡尺返回 null。</summary>
        public static object CreateCaliperBandsAtPose(
            IList<TemplateCaliper> calipers, double targetRow, double targetCol, double angleDeg)
        {
            if (calipers == null || calipers.Count == 0) return null;
            return TemplateCaliperRunner.BuildCaliperOverlay(calipers,
                targetRow, targetCol, angleDeg / 180.0 * System.Math.PI);
        }

        /// <summary>
        /// 生成"贴合匹配结果"的可视化图形（Shape=模型特征轮廓 XLD，逐边贴合目标；
        /// Ncc=模板 ROI 外接旋转矩形），供实拍验证把匹配效果画到视图上。
        /// 返回 object（HObject），所有权归调用方（挂到 ImageOverlay.NativeHandle 后
        /// 随 WpfImageRenderContext.Dispose 统一释放）。
        /// </summary>
        public static Result<object> CreateMatchOverlay(string templateName,
            Grayson.Vision.HalconWrapper.Match2D.TemplateMatchResult match)
        {
            return new TemplateManager().CreateMatchOverlay(templateName, match);
        }

        /// <summary>生成十字标记 XLD（匹配中心可视化，60px 半径），调用方负责释放</summary>
        public static object CreateCross(double row, double col, double size)
        {
            HOperatorSet.GenCrossContourXld(out HObject cross, row, col, size, 0.0);
            return cross;
        }

        /// <summary>
        /// 显示帧深拷贝（P1 学习域预览，2026-09-09）：把源 wrapper 的 HImage 拷贝成一张独立显示帧。
        /// 用途：模板编辑把"显示帧"与"引擎真源"分离——显示帧可随时被学习域预览合成帧替换/释放，
        /// 引擎真源（rawFrame.HImage）不受影响，创建模板始终从真源取图。
        /// 返回新 HalconRenderImage（拥有拷贝句柄，释放安全）；输入无效返回 null。
        /// </summary>
        public static HalconRenderImage CreateDisplayCopy(IRenderImage image)
        {
            var h = (image as HalconRenderImage)?.HImage;
            if (h == null || !h.IsInitialized()) return null;
            return new HalconRenderImage(h.CopyImage());
        }

        /// <summary>
        /// 合成"学习域预览帧"（掩膜可视化，P1）：源 = 引擎真源 rawFrame。
        /// 学习域内保留原像素，域外（ROI 外 + 掩膜排除∖ + ROI 内未被保留∪覆盖处 + 基底形状外）灰化 →
        /// 一眼看清"模板学哪块"。口径与创建链一致（TemplateMaskRegionBuilder.BuildLearnRegion）。
        /// baseShape：基底形状（v2 ROI，用户画的圆/旋转矩形等；null=ROI 窗口语义）。
        /// 含像素运算，须在后台线程调用（VM 内 Task.Run）。返回 wrapper（拥有合成帧，可释放）；失败返回 null。
        /// </summary>
        public static HalconRenderImage ComposeLearnDomainPreviewFrame(
            IRenderImage rawImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            TemplateMaskShape baseShape, TemplateLearnMask mask)
        {
            var h = (rawImage as HalconRenderImage)?.HImage;
            if (h == null || !h.IsInitialized()) return null;
            var preview = TemplatePreviewComposer.ComposeLearnDomainPreview(
                h, roiRow1, roiCol1, roiRow2, roiCol2, baseShape, mask, out _);
            return preview != null ? new HalconRenderImage(preview) : null;
        }

        /// <summary>
        /// 生成 ROI 矩形 region（框选后常驻显示），调用方负责释放</summary>
        public static object CreateRoiRectangle(double row1, double col1, double row2, double col2)
        {
            HOperatorSet.GenRectangle1(out HObject rect, row1, col1, row2, col2);
            return rect;
        }

        /// <summary>
        /// 把单笔掩膜转成 XLD 边界轮廓（模板编辑界面描边显示用：保留=lime 绿 / 排除=red 红）。
        /// 用 XLD 而非 Region 描边：窗口 fill/margin 状态只影响 Region，XLD 恒定线宽描边不受干扰。
        /// 返回 object(HObject)；几何非法时返回 null（内部已记警告日志）。所有权归调用方。
        /// </summary>
        public static object CreateMaskContourOverlay(TemplateMaskShape shape)
        {
            if (!TemplateMaskRegionBuilder.BuildShapeRegion(shape, out HObject region, out _)) return null;
            try
            {
                // 区域 → 边界 XLD（"border"=外边界含孔洞；区域是闭合实心，取 border 即整条外围轮廓）
                HOperatorSet.GenContourRegionXld(region, out HObject boundary, "border");
                return boundary;
            }
            finally
            {
                region.Dispose();
            }
        }

        /// <summary>
        /// 生成"学习域整域"边界轮廓 XLD（2026-09-09 掩膜版图化）：
        /// 口径与创建链/学习域预览帧完全一致（BuildLearnRegion 引擎权威），
        /// 输入 = ROI 窗口 + 基底形状 + 当前掩膜（含可选未收笔临时轨迹），输出 = 版图最终边界一条。
        /// 视觉用途：涂抹/绘制中实时显示整域轮廓（∪ 扩 / ∖ 缩跟手）；收笔后与灰化预览帧同界。
        /// 仅掩膜有笔画时返回轮廓（无掩膜 = 学习框即 ROI/形状本身，由黄框/形状 overlay 表达）。
        /// 返回 object(HObject) 或 null；所有权归调用方。
        /// </summary>
        public static object CreateLearnDomainContourOverlay(
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            TemplateMaskShape baseShape, TemplateLearnMask mask)
        {
            if (mask == null || !mask.Enabled || mask.Shapes == null || mask.Shapes.Count == 0) return null;
            if (!TemplateMaskRegionBuilder.BuildLearnRegion(
                    roiRow1, roiCol1, roiRow2, roiCol2, baseShape, mask, out HObject region))
            {
                return null;
            }
            try
            {
                HOperatorSet.GenContourRegionXld(region, out HObject boundary, "border");
                return boundary;
            }
            finally
            {
                region.Dispose();
            }
        }

        /// <summary>
        /// 把整批模板特征仿射到"目标位姿"并合并成一个 XLD 对象集（P2 特征可视化核心入口）：
        ///   目标位姿 = 匹配返回的 pose（特征跟模板转）或 ROI 中心@0°（编辑器静态钉在模板上）。
        /// 特征几何存的是相对模型原点（ROI 中心）偏移，由 TemplateFeatureBuilder 做 vector_angle_to_rigid 仿射。
        /// 返回 object(HObject)（null=无特征/全部失败）；所有权归调用方。
        /// </summary>
        public static object CreateFeatureOverlayForTarget(IList<TemplateFeature> features,
            double targetRow, double targetCol, double angleDeg)
        {
            if (features == null || features.Count == 0) return null;
            return TemplateFeatureBuilder.BuildBatchTransformed(features, targetRow, targetCol, angleDeg / 180.0 * System.Math.PI);
        }
    }
}
