//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 模板创建桥接服务 —— 把 WPF 显示层持有的 IRenderImage 交给
//        HalconWrapper.TemplateManager 落盘（.shm/.ncc + .json 元数据）。
//        内部解包 HalconRenderImage.HImage，halcondotnet 类型不泄漏到 WpfUI
//        （控件公共 API 的 MC1000 约束）。
//===================================================================================
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;

namespace Grayson.Vision.HalconWrapper.Wpf.Services
{
    /// <summary>
    /// 模板创建桥接服务（模板管理界面 → TemplateManager 注册表）。
    /// 公开方法只接受 Contracts 类型与 double，调用方无需引用 halcondotnet。
    /// </summary>
    public static class TemplateCreationBridge
    {
        /// <summary>创建形状模板（轮廓/边缘匹配）</summary>
        public static Result<TemplateInfo> CreateShapeTemplate(
            IRenderImage image, string name,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark = "", string sourceImagePath = "")
        {
            return CreateTemplate(image, name, TemplateMatchType.Shape,
                roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath);
        }

        /// <summary>创建 NCC 灰度模板（纹理/印刷图案匹配）</summary>
        public static Result<TemplateInfo> CreateNccTemplate(
            IRenderImage image, string name,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark = "", string sourceImagePath = "")
        {
            return CreateTemplate(image, name, TemplateMatchType.Ncc,
                roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath);
        }

        private static Result<TemplateInfo> CreateTemplate(
            IRenderImage image, string name, TemplateMatchType type,
            double roiRow1, double roiCol1, double roiRow2, double roiCol2,
            double angleStart, double angleEnd, string remark, string sourceImagePath)
        {
            var hImage = (image as HalconRenderImage)?.HImage;
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<TemplateInfo>.Fail("模板图像无效：请先加载本地图片或从相机采集一帧。");
            }

            var manager = new TemplateManager();
            return type == TemplateMatchType.Shape
                ? manager.CreateShapeTemplate(name, hImage,
                    roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath)
                : manager.CreateNccTemplate(name, hImage,
                    roiRow1, roiCol1, roiRow2, roiCol2, angleStart, angleEnd, remark, sourceImagePath);
        }
    }
}
