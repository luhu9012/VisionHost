//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 图像叠加图元（区域、文字、XLD等）的抽象描述。
//===================================================================================

using Grayson.Vision.Contracts.Flow.Nodes;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Imaging
{
    /// <summary>
    /// 叠加图元类型
    /// </summary>
    public enum OverlayKind
    {
        Region,
        Xld,
        Text
    }

    /// <summary>
    /// 图像叠加项
    /// </summary>
    public class ImageOverlay
    {
        /// <summary>
        /// 图元类型
        /// </summary>
        public OverlayKind Kind { get; set; }

        /// <summary>
        /// 颜色（如 red、green、#FF0000）
        /// </summary>
        public string Color { get; set; }

        /// <summary>
        /// 文本内容（仅 Kind=Text 时有效）
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// 文本位置 Row/Y（仅 Kind=Text 时有效）
        /// </summary>
        public double Row { get; set; }

        /// <summary>
        /// 文本位置 Column/X（仅 Kind=Text 时有效）
        /// </summary>
        public double Column { get; set; }

        /// <summary>
        /// 原生区域/XLD 句柄（如 Halcon HObject），供渲染实现层读取
        /// </summary>
        public object NativeHandle { get; set; }

        /// <summary>
        /// 轻量坐标点集合（用于非 Halcon 的简单几何描述）
        /// </summary>
        public List<Point2D> Points { get; set; } = new List<Point2D>();
    }
}
