//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 与图像库无关的渲染上下文，供 FlowEdit 等 UI 项目持有。
//===================================================================================

using System.Collections.Generic;


namespace Grayson.Vision.Contracts.Imaging
{
    /// <summary>
    /// 图像渲染上下文
    /// </summary>
    public class ImageRenderContext
    {
        /// <summary>
        /// 节点Id
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// 节点名称
        /// </summary>
        public string NodeName { get; set; }

        /// <summary>
        /// 渲染图像句柄（内部可能是 HImage 等原生对象）
        /// </summary>
        public IRenderImage Image { get; set; }

        /// <summary>
        /// 叠加区域信息（与具体库无关的轻量描述）
        /// </summary>
        public List<ImageOverlay> Overlays { get; set; } = new List<ImageOverlay>();

        /// <summary>
        /// 当前是否被选中
        /// </summary>
        public bool IsSelected { get; set; }
    }
}
