using System;

namespace VisualCalibTool.Abstractions
{
    /// <summary>
    /// ★ 叠加绘制目标（ARC 反投影 / 残差矢量 / 热力图 / 十字光标全靠它）。
    ///
    /// 为什么要有这层抽象：
    ///   ① HALCON 的 <c>HWindow</c> 类型一旦进入公共 API，XAML 编译器（ReflectionOnly）
    ///      会去加载 halcondotnet → 触发 <c>MC1000</c>。所以显示控件的公共面必须<b>只含普通类型</b>。
    ///   ② 视图模型/绘制器因此完全不知道 HALCON 存在，业务逻辑可离线单测。
    ///
    /// ★ 坐标系约定（很容易搞反，这里写死）：
    ///   所有几何参数都是<b>图像坐标</b>，且以 <b>(row, col)</b> 顺序出现 —— 与 HALCON 一致。
    ///   row = 行 = 图像 Y；col = 列 = 图像 X。需要 (X, Y) 语义时用 <c>Pixel</c> 辅助类型显式表达。
    /// </summary>
    public interface ICalibOverlayTarget
    {
        /// <summary>窗口是否已就绪（句柄有效、还没被释放）。</summary>
        bool IsReady { get; }

        /// <summary>清空全部叠加（不影响底图）。</summary>
        void ResetOverlay();

        /// <summary>设定后续绘制的颜色（HALCON 颜色名，如 "red" / "#00FF00"）。</summary>
        void SetColor(string color);

        /// <summary>设定后续绘制的线宽。</summary>
        void SetLineWidth(int width);

        /// <summary>十字（row, col 为图像坐标；size 为臂长，像素）。</summary>
        void DrawCross(double row, double col, double size, double angleDeg = 0.0);

        /// <summary>圆（row, col 为圆心图像坐标；radius 为半径，像素）。</summary>
        void DrawCircle(double row, double col, double radius);

        /// <summary>直线（图像坐标）。</summary>
        void DrawLine(double row1, double col1, double row2, double col2);

        /// <summary>带箭头的线（残差矢量图用它）。</summary>
        void DrawArrow(double row1, double col1, double row2, double col2, double size = 8.0);

        /// <summary>轴对齐矩形（由两角点给出，图像坐标）。</summary>
        void DrawRectangle(double row1, double col1, double row2, double col2);

        /// <summary>文本（锚点为图像坐标）。</summary>
        void DrawText(double row, double col, string text);

        /// <summary>折线 / 多边形（图像坐标数组，row/col 等长）。</summary>
        void DrawPolyline(double[] rows, double[] cols, bool closed = false);

        /// <summary>触发重放（缩放 / 平移 / 窗口尺寸变化后由控件自己调用；外部也可主动要求重画）。</summary>
        void Redraw();

        /// <summary>光标在<b>图像坐标</b>上移动（row, col）。"指哪打哪"依赖它。</summary>
        event Action<double, double> CursorPixelMoved;
    }

    /// <summary>
    /// ★ 框选（ROI 示教）接缝：显示控件支持"按住拖一个矩形"的交互模式时实现它。
    ///
    /// 为什么单独一个接口、不并进 <see cref="ICalibImageSurface"/>：
    ///   框选是<b>交互能力</b>，不是显示义务 —— 宿主若换成不可交互的显示面（只读回放窗），
    ///   不实现这个接口就行，显示照常工作。接口越小，"不实现"的成本越低。
    ///
    /// ★ 坐标约定与 <see cref="ICalibOverlayTarget"/> 一致：<b>图像坐标 (row, col)</b>。
    /// </summary>
    public interface IRoiPickSurface
    {
        /// <summary>进入 / 退出框选模式。进入后左键拖拽 = 画框（平移让位），光标变十字。</summary>
        void SetRoiPickMode(bool active);

        /// <summary>
        /// 用户拖完一个框（松开左键）时触发一次。参数为归一化后的两角点（图像坐标，r1&lt;r2、c1&lt;c2）。
        /// ★ 太小的框（小于 3×3 像素 = 误点）不触发。
        /// </summary>
        event Action<double, double, double, double> RoiPicked;
    }

    /// <summary>
    /// 图像显示面（视图模型与显示控件之间的唯一接缝）。
    /// ★ 同样<b>只含普通类型</b>：视图模型因此完全不知道 HALCON 存在，可离线单测。
    /// </summary>
    public interface ICalibImageSurface
    {
        bool IsReady { get; }

        bool HasImage { get; }

        /// <summary>叠加绘制入口。</summary>
        ICalibOverlayTarget Overlay { get; }

        /// <summary>显示一帧 8 位灰度裸帧（<paramref name="fit"/> = 是否顺带适配整图）。</summary>
        void ShowFrame(byte[] rawGray, int width, int height, bool fit);

        /// <summary>清空图像。</summary>
        void ClearImage();

        /// <summary>适配整图。</summary>
        void FitImage();

        /// <summary>把当前帧编码成 PNG（留档用）。可能返回 null。</summary>
        byte[] TryEncodeCurrentFramePng();
    }
}
