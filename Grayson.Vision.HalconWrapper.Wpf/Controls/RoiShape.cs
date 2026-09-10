//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: ROI 交互模型 —— HalconImageDisplayHost 内置 ROI 编辑器使用的通用区域描述。
//        所有坐标为图像像素坐标，与窗口缩放/平移无关。
//        纯 WPF 类型（不涉及 halcondotnet），供控件公共 API 与上层宿主消费。
//        行 = row（向下为正），列 = col（向右为正）。
//===================================================================================
using System;
using System.Windows;

namespace Grayson.Vision.HalconWrapper.Wpf.Controls
{
    /// <summary>ROI 几何类型（HALCON 常用集合：矩形/圆/椭圆/任意区域/线段）</summary>
    public enum RoiShapeKind
    {
        /// <summary>平行矩形（axis-parallel）：由两对角点确定</summary>
        Rectangle1,
        /// <summary>旋转矩形（中心式）：中心 + 角度 + 半长/半宽</summary>
        Rectangle2,
        /// <summary>圆形：中心 + 半径</summary>
        Circle,
        /// <summary>椭圆（中心式）：中心 + 角度 + 主轴/副轴半长</summary>
        Ellipse,
        /// <summary>任意多边形区域（顶点闭合），也可表示自由绘制区域</summary>
        Polygon,
        /// <summary>线段（两个端点）</summary>
        Line,
        /// <summary>涂抹画笔轨迹（掩膜画笔，2026-09-09 P1）：Polygon 槽存按住拖动采集的
        /// 连续轨迹点（X=col，Y=row，开放路径不闭合），BrushRadius 为画笔半径(px)。
        /// 语义=沿轨迹以半径膨胀出的圆盘带区域（引擎侧膨胀，见 TemplateMaskRegionBuilder）</summary>
        Brush
    }

    /// <summary>
    /// 一条由内置 ROI 编辑器维护的区域/标注（图像像素坐标系）。
    /// 具体使用哪些槽位由 Kind 决定：
    ///   Rectangle1 / Line —— Row,Col 为端点 A，Row2,Col2 为端点 B；
    ///   Circle / Ellipse / Rectangle2 —— Row,Col 为中心；
    ///   Polygon —— Polygon 顶点数组（X=col，Y=row），隐式闭合。
    /// 角度约定：Phi=0 时主轴沿列方向（水平），正方向向 +row 倾斜（HALCON 像素系）。
    /// </summary>
    public sealed class RoiShape
    {
        public RoiShapeKind Kind { get; set; }

        // ---------- 通用坐标槽（按 Kind 解释，见类注释） ----------
        public double Row;
        public double Col;
        public double Row2;
        public double Col2;

        /// <summary>主轴角度（弧度）</summary>
        public double Phi;

        /// <summary>Circle：半径；Ellipse：主轴半长（沿 Phi 方向）</summary>
        public double Radius1;

        /// <summary>Ellipse：副轴半长（垂直 Phi）；Rectangle2：半宽（垂直 Phi）</summary>
        public double Radius2;

        /// <summary>Rectangle2：半长（沿 Phi 方向）</summary>
        public double Length1;

        /// <summary>Rectangle2：半宽（垂直 Phi 方向）</summary>
        public double Length2;

        /// <summary>Polygon 顶点（X=col，Y=row），隐式闭合；至少 3 点</summary>
        public Point[] Polygon;

        /// <summary>Brush（涂抹画笔）：画笔半径（图像像素）。其余 Kind 忽略。</summary>
        public double BrushRadius;

        /// <summary>HALCON 风格颜色名（yellow/cyan/orange/red/green/magenta/blue/white）</summary>
        public string ColorName = "yellow";

        /// <summary>
        /// 区域化显示（fill）：true = 内部以 FillColorName 实心填充 + ColorName 描边；
        /// false = 仅描边轮廓（margin，默认，仿 HDevelop ROI）。
        /// 仅闭合形状（矩形/圆/椭圆/多边形）有意义，线段恒为轮廓。
        /// </summary>
        public bool Filled;

        /// <summary>填充色（HALCON 风格名）；为 null/空时沿用 ColorName</summary>
        public string FillColorName;

        /// <summary>ROI 创建/编辑时所处图像尺寸（图像坐标系边界用）</summary>
        public double ImageWidth;
        public double ImageHeight;

        public RoiShape(RoiShapeKind kind)
        {
            Kind = kind;
        }

        /// <summary>整条移动（编辑/拖动）：所有几何量平移 dr 行、dc 列</summary>
        public void Translate(double dr, double dc)
        {
            Row += dr; Col += dc; Row2 += dr; Col2 += dc;
            if (Polygon != null)
            {
                for (int i = 0; i < Polygon.Length; i++)
                {
                    Polygon[i] = new Point(Polygon[i].X + dc, Polygon[i].Y + dr);
                }
            }
        }
    }

    /// <summary>ROI 集合变化事件参数（RoiCommitted 新增 / RoiEdited 修改 / RoiRemoved 移除）</summary>
    public sealed class RoiShapeEventArgs : EventArgs
    {
        public RoiShape Shape { get; }
        public RoiShapeEventArgs(RoiShape shape)
        {
            Shape = shape;
        }
    }

    /// <summary>视图工具切换事件参数（用户点选工具条工具 / 程序激活工具时触发）。
    /// Tool = ViewTool 枚举名："Pointer"/"Hand"/"ZoomRect"/"Rect1"/"Rect2"/"Circle"/"Ellipse"/"Polygon"/"Freehand"/"Brush"/"Line"。</summary>
    public sealed class ViewToolChangedEventArgs : EventArgs
    {
        public string Tool { get; }
        public ViewToolChangedEventArgs(string tool)
        {
            Tool = tool;
        }
    }

    /// <summary>
    /// 🖌 涂抹画笔草绘状态广播（2026-09-09 掩膜版图化）：按住涂抹期间每个新采样点、以及
    /// 收笔/右键取消/Esc 取消等状态转折时触发，供页面把"当前未收笔轨迹"实时并入学习域
    /// （∪ 版图实时扩大 / ∖ 实时抠洞，所见即所得）。
    /// Points = 当前轨迹点快照（图像坐标，X=col，Y=row，开放路径；收笔/取消后为空数组）。
    /// Down = true 表示按住涂抹中（轨迹在持续增长）；false = 一笔结束/放弃（Points 为空）。
    /// Radius = 该笔的画笔半径（图像像素）。
    /// </summary>
    public sealed class BrushSketchEventArgs : EventArgs
    {
        public Point[] Points { get; }
        public double Radius { get; }
        public bool Down { get; }
        public BrushSketchEventArgs(Point[] points, double radius, bool down)
        {
            Points = points ?? Array.Empty<Point>();
            Radius = radius;
            Down = down;
        }
    }
}
