//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: Halcon 视图窗口「直通」通道（HDevelop 风格）。
//        存在的理由：ICalibrationDisplayContext / IFlowPreviewContext 是「场景式」
//        的——想多画一种东西就得往接口上加一个方法（AddText/AddCross/AddCircle…）。
//        本接口反过来：把 Halcon 原生 HWindow 直接交给算法层，想画什么自己调算子，
//        写法与 HDevelop 里 `dev_display(Obj, WindowHandle)` 完全一致。
//
// 为什么是回调（Action<HWindow>）而不是一个 HWindow 属性：
//        HSmartWindowControlWPF 在窗口尺寸变化 / 控件重新加载时会重建 Halcon 窗口，
//        旧的 HWindow 立即失效。属性会把失效句柄缓存到算法层，回调则保证每次绘制
//        用的都是窗口当前的有效句柄。同理，绘制必须回到窗口所属的 UI 线程，
//        由实现方在回调外包一层 Dispatcher 调度，调用方无需关心。
//
// 两条通道并存，不互相取代：
//        - 场景式（BeginScene/Add/AddText/…）：生产链路用，显示层托管对象生命周期、
//          交互后自动重放，节点无需关心窗口是否存在；
//        - 直通式（本接口）：调试排查用，画得自由，但生命周期归调用方自己管。
//
// 安全约束（MC1000）：本接口的签名携带 halcondotnet 类型，**实现类不得出现在任何
//        XAML 中**（只可在代码后台 new）。否则 XAML 编译器以 ReflectionOnly 模式解析
//        该类型时会被迫加载 halcondotnet，触发 .NET 2.0 依赖解析失败。
//===================================================================================

using HalconDotNet;
using System;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// Halcon 视图窗口直通通道：把原生 <see cref="HWindow"/> 交给算法层自由绘制（HDevelop 风格）。
    /// <para>
    /// 由 <c>Grayson.Vision.HalconWrapper.Wpf</c> 的显示适配器实现，经
    /// <c>ICalibrationDisplayContext</c> / <c>IFlowPreviewContext</c> 同一个实例对外提供，
    /// 算法层用 <see cref="HalconWindowSurfaceEx.AsWindow"/> 判型取用即可。
    /// </para>
    /// <para>上下文不可用时（生产无窗口、窗口未初始化）所有调用静默跳过，主流程零影响。</para>
    /// </summary>
    public interface IHalconWindowSurface
    {
        /// <summary>窗口是否已就绪（句柄有效且未被释放）。</summary>
        bool IsReady { get; }

        /// <summary>
        /// 在窗口上直接绘制一次（HDevelop 风格）。不入场景，用户缩放/拖动窗口后内容消失。
        /// <para>典型用途：调试期的临时探针——看一眼中间结果，不需要它长期留在画面上。</para>
        /// </summary>
        /// <param name="draw">
        /// 绘制回调，参数即当前有效的 <see cref="HWindow"/>。
        /// 回调内部可直接用 <c>window.SetColor(...)</c> 等实例方法，
        /// 也可用 <c>HOperatorSet.DispObj(obj, window)</c> 等静态算子（与 HDevelop 写法一致）。
        /// 回调抛异常只记日志，不影响主流程。
        /// </param>
        void Draw(Action<HWindow> draw);

        /// <summary>
        /// 在窗口上绘制，并把回调登记进场景：用户缩放/平移/窗口尺寸变化时显示层重放该回调，
        /// 画面不丢（等效于场景式 API 的行为，但绘制内容由调用方自由书写）。
        /// <para>
        /// 注意闭包捕获的对象生命周期归调用方：重放时若对象已被 Dispose，回调内会抛异常，
        /// 显示层捕获后跳过该条目（其余条目不受影响）。
        /// </para>
        /// </summary>
        /// <param name="draw">绘制回调，语义同 <see cref="Draw"/>，但会被重复执行多次。</param>
        void DrawRecorded(Action<HWindow> draw);
    }

    /// <summary>
    /// <see cref="IHalconWindowSurface"/> 的取用扩展：从现有的显示上下文判型拿到直通通道。
    /// 不是直通实现时返回 null（生产模式无窗口、或宿主未提供），调用方判空即可。
    /// </summary>
    public static class HalconWindowSurfaceEx
    {
        /// <summary>把标定显示上下文当作窗口直通通道使用；不是则 null。</summary>
        public static IHalconWindowSurface AsWindow(this Calibration.ICalibrationDisplayContext context)
            => context as IHalconWindowSurface;

        /// <summary>把节点预览上下文当作窗口直通通道使用；不是则 null。</summary>
        public static IHalconWindowSurface AsWindow(this Contracts.Flow.Contexts.IFlowPreviewContext context)
            => context as IHalconWindowSurface;
    }
}
