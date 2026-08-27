//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 节点实时预览便捷工具（Nodes 层调用，object 弱类型入参，内部判型）。
//        存在意义：Nodes 源码保持零 using HalconDotnet（图像端口一律 object 弱类型），
//        预览所需的 Halcon 侧操作（显示副本、判型）全部收敛到这里，节点侧一行调用。
// 使用约定：
//   1) CopyForDisplay 返回显示副本（所有权归显示层），原对象留在数据管线继续被下游消费；
//   2) 非 Halcon 图形对象返回 null（上层弱类型 Add 对 null 静默忽略）；
//   3) 所有方法 null 安全。
//===================================================================================

using HalconDotNet;

namespace Grayson.Vision.HalconWrapper
{
    /// <summary>
    /// 节点实时预览（IFlowPreviewContext）的 Halcon 侧便捷封装。
    /// </summary>
    public static class NodePreviewHelper
    {
        /// <summary>
        /// 为预览显示创建对象副本（CopyObj）。
        /// 原因：IFlowPreviewContext.Add 为"提交即所有权转移"，显示层会在场景切换时
        /// 释放托管对象；而节点输出对象同时作为端口输出继续被下游消费，
        /// 直接提交会导致双重释放。提交副本后两边生命周期互不干扰。
        /// </summary>
        /// <param name="obj">端口/算子输出的图形对象（HObject）</param>
        /// <returns>独立副本；非 Halcon 图形对象或 null 返回 null</returns>
        public static object CopyForDisplay(object obj)
        {
            if (obj is HObject hObj)
            {
                try
                {
                    return hObj.CopyObj(1, -1);
                }
                catch
                {
                    // HALCON 句柄已失效等场景：预览失败不影响主流程
                    return null;
                }
            }
            return null;
        }

        /// <summary>
        /// 创建矩形区域（供 ROI 裁剪框、搜索区域框等预览标注）。
        /// 返回的 HObject 所有权归调用方——应通过 IFlowPreviewContext.Add 提交后由显示层释放。
        /// </summary>
        public static object CreateRectangle(double row1, double col1, double row2, double col2)
        {
            try
            {
                HObject rect;
                HOperatorSet.GenRectangle1(out rect, row1, col1, row2, col2);
                return rect;
            }
            catch
            {
                return null;
            }
        }
    }
}
