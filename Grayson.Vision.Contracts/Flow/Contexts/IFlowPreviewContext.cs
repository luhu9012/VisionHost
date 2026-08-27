//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 节点实时预览显示上下文（编辑器属性面板专用）。
//        语义与 HalconWrapper.ICalibrationDisplayContext 完全一致的弱类型版本：
//        场景 = 按添加顺序累积的显示条目，每条 Add 立即上屏；用户缩放/平移后
//        显示层整体重放，画面不丢。
// 使用约定（与标定显示上下文一致）：
//   1) 提交即所有权转移：Add(obj) 后对象的释放归显示层，调用方不得再 Dispose；
//   2) 底图走 AddBorrowed（借用，不转移所有权，生命周期归数据管线）；
//   3) 上下文未注入（生产运行 / IPC 模式）时调用方应判空跳过，节点行为零变化；
//   4) object 参数由实现方（Wpf 层适配器）负责判型转换；非 Halcon 图形对象静默忽略；
//   5) 所有方法线程安全：实现方内部自动调度到窗口所属（UI）线程。
// 弱类型原因：Contracts 层禁止引用 halcondotnet；注入点（NodeExecutionContext.Preview）
// 定义在本层，只能以 object 传递图形对象。强类型便捷封装见 HalconWrapper.NodePreviewHelper。
//===================================================================================

namespace Grayson.Vision.Contracts.Flow.Contexts
{
    /// <summary>
    /// 节点实时预览显示上下文：编辑器双击节点弹出的属性面板内嵌视图窗口的场景式绘制接口。
    /// 节点 Executor 在执行过程中"走一步画一步"，显示层负责其余一切。
    /// </summary>
    public interface IFlowPreviewContext
    {
        /// <summary>窗口是否已就绪（句柄有效且未被释放）。</summary>
        bool IsReady { get; }

        /// <summary>
        /// 开始新画面：清空当前场景（释放其中托管对象）并清空窗口。
        /// 每次预览执行的第一步（通常紧跟 AddBorrowed(底图)）。
        /// </summary>
        void BeginScene();

        /// <summary>
        /// 提交显示对象（region / XLD / 图像），立即上屏并纳入场景供交互后重放。
        /// ⚠️ 提交即所有权转移：对象由显示层统一释放，调用方不得再 Dispose。
        /// 建议提交显示副本（HalconWrapper.NodePreviewHelper.CopyForDisplay），
        /// 原对象留在数据管线继续被下游消费。
        /// </summary>
        /// <param name="obj">Halcon 图形对象（HObject）</param>
        /// <param name="color">颜色名（"blue"/"magenta"/...）；null 表示按图像对象绘制（底图等）</param>
        /// <param name="lineWidth">线宽（像素）</param>
        void Add(object obj, string color = null, int lineWidth = 1);

        /// <summary>
        /// 提交借用对象（典型：输入底图）。立即上屏并纳入场景，但显示层不负责释放
        /// ——生命周期归数据管线（端口值缓存）管理。
        /// </summary>
        void AddBorrowed(object obj);

        /// <summary>提交文本标注（image 坐标系：row/col 为图像坐标，跟随缩放平移）。</summary>
        void AddText(string text, double row, double col, string color);

        /// <summary>提交十字标记（特征点中心），image 坐标系。</summary>
        void AddCross(double row, double col, double size, string color);

        /// <summary>提交圆标记（拟合圆等），image 坐标系。</summary>
        void AddCircle(double row, double col, double radius, string color);
    }
}
