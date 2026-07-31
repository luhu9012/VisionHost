using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vison.FlowEdit.Converters
{
    public abstract class BaseConverter : MarkupExtension, IValueConverter
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => this;
        public abstract object Convert(object value, Type targetType, object parameter, CultureInfo culture);
        public virtual object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public abstract class BaseMultiConverter : MarkupExtension, IMultiValueConverter
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => this;
        public abstract object Convert(object[] values, Type targetType, object parameter, CultureInfo culture);
        public virtual object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class ConditionOutputVisibilityConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FlowNode ? Visibility.Visible : Visibility.Collapsed;
    }

    public class NormalOutputVisibilityConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FlowNode ? Visibility.Collapsed : Visibility.Visible;
    }

    public class NodeColorConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is NodeCategory k)) return new SolidColorBrush(Color.FromRgb(45, 45, 48));
            switch (k)
            {
                case NodeCategory.DeviceIO: return new SolidColorBrush(Color.FromRgb(0, 122, 204));
                case NodeCategory.Logic: return new SolidColorBrush(Color.FromRgb(46, 139, 87));
                case NodeCategory.Vision: return new SolidColorBrush(Color.FromRgb(218, 124, 6));
                case NodeCategory.CompositeEx: return new SolidColorBrush(Color.FromRgb(155, 89, 182));
                default: return new SolidColorBrush(Color.FromRgb(45, 45, 48));
            }
        }
    }

    public class NodeBorderConv : BaseMultiConverter
    {
        public override object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return Brushes.Transparent;
            var node = values[0] as FlowNodeBase;
            var sel = values[1] as FlowNodeBase;

            if (node != null && node.IsRunning) return Brushes.SpringGreen; // 执行动画高亮
            if (node != null && node == sel) return Brushes.Gold; // 选中高亮
            return Brushes.Transparent;
        }
    }

    public class BezierPathConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 4 || !(values[0] is double startX) || !(values[1] is double startY) ||
                !(values[2] is double endX) || !(values[3] is double endY))
                return null;

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };

            // 从上到下的布局：控制点在 Y 轴方向拉伸
            double controlOffset = Math.Max(30, Math.Abs(endY - startY) / 2);
            Point p1 = new Point(startX, startY + controlOffset); // 往下延伸
            Point p2 = new Point(endX, endY - controlOffset);   // 从上方引入
            Point endPoint = new Point(endX, endY);

            figure.Segments.Add(new BezierSegment(p1, p2, endPoint, true));
            geometry.Figures.Add(figure);

            // 🌟 绘制末端箭头
            double angle = Math.Atan2(endY - p2.Y, endX - p2.X);
            double arrowLength = 7.0;
            double arrowAngle = Math.PI / 6; // 30度角

            Point arrowP1 = new Point(endX - arrowLength * Math.Cos(angle - arrowAngle), endY - arrowLength * Math.Sin(angle - arrowAngle));
            Point arrowP2 = new Point(endX - arrowLength * Math.Cos(angle + arrowAngle), endY - arrowLength * Math.Sin(angle + arrowAngle));

            var arrowFigure = new PathFigure { StartPoint = endPoint, IsClosed = true };
            arrowFigure.Segments.Add(new LineSegment(arrowP1, true));
            arrowFigure.Segments.Add(new LineSegment(arrowP2, true));

            geometry.Figures.Add(arrowFigure);

            return geometry;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class NullToCollapsed : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value == null ? Visibility.Collapsed : Visibility.Visible;
    }

    public class NullToVisible : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 根据 PortCategory 控制端点外观样式：
    /// Data -> 方块/菱形/三角形 (CornerRadius=2)
    /// Exec -> 圆形 (CornerRadius=6)
    /// </summary>
    public class PortCategoryToCornerRadiusConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory category && category == PortCategory.Exec)
            {
                return new CornerRadius(6);     // 纯圆形 (数据流)
            }
           
            return new CornerRadius(1); // 方形/微圆角 (控制流)
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class HexToBrushConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    return new SolidColorBrush(color);
                }
                catch { }
            }
            return Brushes.DodgerBlue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 根据连线类型 (ConnectionCategory) 决定线宽：Exec 粗线(3.5)，Data 细线(1.8)
    /// </summary>
    /// <summary>
    /// 根据连线类型 (PortCategory) 决定线宽：Exec 粗线(3.5)，Data 细线(1.8)
    /// </summary>
    public class ConnectionThicknessConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return 3.5;
            //if (value is PortCategory cat && cat == PortCategory.Exec)
            //    return 3.5;
            //return 1.8;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 根据连线类型 (PortCategory) 决定虚线样式：Exec 实线(null)，Data 虚线("2 2")
    /// </summary>
    public class ConnectionDashConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null; // 实线
            //if (value is PortCategory cat && cat == PortCategory.Exec)
            //    return null; // 实线
            //return new DoubleCollection { 2, 2 }; // 点虚线
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// 将分类名称或 NodeCategory 枚举转换为对应的 Emoji 图标（用于 VisionMaster 极简侧边栏）
    /// </summary>
    public class CategoryToIconConverter : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "📌";

            string name = value.ToString();

            // 1. 处理 NodeCategory 枚举
            if (value is NodeCategory category)
            {
                switch (category)
                {
                    case NodeCategory.DeviceIO: return "⚙️";
                    case NodeCategory.Vision: return "👁️";
                    case NodeCategory.Logic: return "🧠";
                    case NodeCategory.DataProcess: return "📊";
                    case NodeCategory.SystemMES: return "🏭";
                    case NodeCategory.CompositeEx: return "🛑";
                    default: return "📦";
                }
            }

            // 2. 处理已拼接了带 Emoji 的组名 (例如 "⚙️ 设备与 IO 控制类")
            if (name.Contains("设备") || name.Contains("IO")) return "⚙️";
            if (name.Contains("Halcon") || name.Contains("视觉")) return "👁️";
            if (name.Contains("逻辑") || name.Contains("控制")) return "🧠";
            if (name.Contains("数据处理") || name.Contains("转换")) return "📊";
            if (name.Contains("生产") || name.Contains("MES")) return "🏭";
            if (name.Contains("复合") || name.Contains("异常")) return "🛑";

            return "📦";
        }
    }
    /// <summary>
    /// 根据 NodeCategory 枚举、分类名称或 FlowNodeBase 实体，统一返回匹配的主题色画刷
    /// </summary>
    public class CategoryToBrushConverter : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            NodeCategory? category = null;

            if (value is FlowNodeBase node)
            {
                category = node.Category;
            }
            else if (value is NodeCategory cat)
            {
                category = cat;
            }
            else if (value is string name)
            {
                if (name.Contains("设备") || name.Contains("IO") || name.Contains("DeviceIO")) category = NodeCategory.DeviceIO;
                else if (name.Contains("Halcon") || name.Contains("视觉") || name.Contains("Vision")) category = NodeCategory.Vision;
                else if (name.Contains("逻辑") || name.Contains("控制") || name.Contains("Logic")) category = NodeCategory.Logic;
                else if (name.Contains("数据处理") || name.Contains("转换") || name.Contains("DataProcess")) category = NodeCategory.DataProcess;
                else if (name.Contains("生产") || name.Contains("MES") || name.Contains("SystemMES")) category = NodeCategory.SystemMES;
                else if (name.Contains("复合") || name.Contains("异常") || name.Contains("CompositeEx")) category = NodeCategory.CompositeEx;
            }

            if (category.HasValue)
            {
                switch (category.Value)
                {
                    case NodeCategory.DeviceIO: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC")); // 经典蓝
                    case NodeCategory.Vision: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DA7C06"));   // 视觉橙
                    case NodeCategory.Logic: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E8B57"));    // 逻辑绿
                    case NodeCategory.DataProcess: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#16A085")); // 青绿
                    case NodeCategory.SystemMES: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D35400"));  // 棕红
                    case NodeCategory.CompositeEx: return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9B59B6"));// 复合紫
                }
            }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#434346"));
        }
    }
    /// <summary>
    /// 将分类名称或 NodeCategory 枚举提取/截取为 2~4 个字的极简短名称（用于图标下方的微型文本）
    /// </summary>
    public class CategoryToShortNameConverter : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "其他";

            string name = value.ToString();

            // 1. 处理 NodeCategory 枚举
            if (value is NodeCategory category)
            {
                switch (category)
                {
                    case NodeCategory.DeviceIO: return "设备IO";
                    case NodeCategory.Vision: return "视觉算法";
                    case NodeCategory.Logic: return "逻辑控制";
                    case NodeCategory.DataProcess: return "数据处理";
                    case NodeCategory.SystemMES: return "生产MES";
                    case NodeCategory.CompositeEx: return "复合异常";
                    default: return "其他";
                }
            }

            // 2. 处理包含完整文字的组名 (提取关键短语)
            if (name.Contains("设备") || name.Contains("IO")) return "设备IO";
            if (name.Contains("Halcon") || name.Contains("视觉")) return "视觉算法";
            if (name.Contains("逻辑")) return "逻辑控制";
            if (name.Contains("数据处理") || name.Contains("转换")) return "数据处理";
            if (name.Contains("生产") || name.Contains("MES")) return "生产MES";
            if (name.Contains("复合") || name.Contains("异常")) return "复合异常";

            // 3. 后备清洗逻辑：去除 Emoji 并截取前 4 个字符
            string cleanName = name.Replace("⚙️", "")
                                   .Replace("👁️", "")
                                   .Replace("🧠", "")
                                   .Replace("📊", "")
                                   .Replace("🏭", "")
                                   .Replace("🛑", "")
                                   .Trim();

            return cleanName.Length > 4 ? cleanName.Substring(0, 4) : cleanName;
        }
    }

    /// <summary>
    /// 布尔值转画刷转换器（常用于 Selected 选中状态的高亮边框切换）
    /// 支持在 XAML 中直接设置 SelectedBrush / NormalBrush 或使用默认值
    /// </summary>
    public class BoolToBrushConverter : BaseConverter
    {
        public Brush SelectedBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        public Brush NormalBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"));

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isTrue = false;

            if (value is bool b)
            {
                isTrue = b;
            }
            else if (value != null && bool.TryParse(value.ToString(), out bool parsed))
            {
                isTrue = parsed;
            }

            return isTrue ? SelectedBrush : NormalBrush;
        }
    }


}