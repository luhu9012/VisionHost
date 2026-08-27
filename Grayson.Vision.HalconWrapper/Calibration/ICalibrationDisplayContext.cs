//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 标定特征识别显示上下文（HDevelop 式"场景"绘制模型）。
//        CalibrationService 走一步绘制一步：每个 Halcon 算子执行后立即把产生的
//        对象提交到场景（Add/AddText/AddCross/AddCircle），当场上屏、当场可见；
//        显示层负责其余一切——缩放/平移/拖动/窗口尺寸变化后整体重放场景、
//        托管对象生命周期（下一帧 BeginScene 或控件销毁时统一释放）。
// 使用约定（健壮性核心，算子代码只管调用，其余一概不管）：
//   1) 提交即所有权转移：Add(obj) 后对象的释放归显示层，调用方不得再 Dispose；
//   2) 底图走 AddBorrowed（借用，不转移所有权，生命周期归图像流管理）；
//   3) 窗口未就绪/上下文未注入时所有方法静默跳过（记 Debug 日志），提取主流程不受影响；
//   4) 单条绘制失败只记日志，不中断后续条目，也不影响提取结果；
//   5) 所有方法线程安全：内部自动调度到窗口所属（UI）线程。
// 实现方: Wpf 层 HalconDisplayContextAdapter（持有 HalconImageDisplayHost）。
//===================================================================================

using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 标定显示上下文：HDevelop 式场景绘制。
    /// 场景 = 按添加顺序累积的显示条目（对象/文本/十字/圆），
    /// 每条 Add 立即上屏；用户缩放/平移/拖动后显示层整体重放，画面不丢。
    /// </summary>
    public interface ICalibrationDisplayContext
    {
        /// <summary>
        /// 窗口是否已就绪（句柄有效且未被释放）。
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// 开始新画面：清空当前场景（释放其中托管对象）并清空窗口。
        /// 每帧提取的第一步（通常紧跟 AddBorrowed(底图)）。
        /// </summary>
        void BeginScene();

        /// <summary>
        /// 提交显示对象（region / XLD / 图像），立即上屏并纳入场景供交互后重放。
        /// ⚠️ 提交即所有权转移：对象由显示层统一释放，调用方不得再 Dispose。
        /// </summary>
        /// <param name="obj">Halcon 图形对象</param>
        /// <param name="color">颜色名（"blue"/"magenta"/...）；null 表示按图像对象绘制（底图等）</param>
        /// <param name="lineWidth">线宽（像素）</param>
        void Add(HObject obj, string color = null, int lineWidth = 1);

        /// <summary>
        /// 提交借用对象（典型：底图）。立即上屏并纳入场景，但显示层不负责释放
        /// ——生命周期归图像流/渲染上下文管理（避免与新帧渲染的所有权冲突）。
        /// </summary>
        void AddBorrowed(HObject obj);

        /// <summary>
        /// 提交文本标注（image 坐标系：row/col 为图像坐标，跟随缩放平移）。
        /// </summary>
        void AddText(string text, double row, double col, string color);

        /// <summary>
        /// 提交十字标记（特征点中心），image 坐标系。
        /// </summary>
        void AddCross(double row, double col, double size, string color);

        /// <summary>
        /// 提交圆标记（拟合圆等），image 坐标系。
        /// </summary>
        void AddCircle(double row, double col, double radius, string color);
    }
}
