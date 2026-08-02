using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vison.FlowEdit.Helpers;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Grayson.Vison.FlowEdit.Converters
{
    #region 基础抽象 Converter

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

    #endregion

    #region 核心：万能分类与节点元数据 Converter

    /// <summary>
    /// 万能分类与节点 UI 元数据转换器
    /// 通过 Parameter (icon, shortname, desc, brush, hex) 提取对应属性
    /// </summary>
    public class CategoryMetaConverter : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return GetDefaultValue(parameter);

            NodeMetaInfo info = null;

            if (value is FlowNodeBase node)
            {
                info = NodeMetaRegistry.Get(node.Type);
                if (info == null || info.ShortName == "通用")
                {
                    info = NodeMetaRegistry.Get(node.Category);
                }
            }
            else if (value is NodeCategory category)
            {
                info = NodeMetaRegistry.Get(category);
            }
            else if (value is NodeType nodeType)
            {
                info = NodeMetaRegistry.Get(nodeType);
            }
            else
            {
                info = NodeMetaRegistry.Resolve(value);
            }

            if (info == null) return GetDefaultValue(parameter);

            string param = parameter?.ToString()?.ToLower();

            switch (param)
            {
                case "emoji":
                case "icon":
                    return info.Emoji;

                case "shortname":
                    return info.ShortName;

                case "desc":
                case "description":
                    return info.Description;

                case "hex":
                case "colorhex":
                    return info.ColorHex;

                case "brush":
                default:
                    return info.Brush;
            }
        }

        private object GetDefaultValue(object parameter)
        {
            string param = parameter?.ToString()?.ToLower();
            if (param == "emoji" || param == "icon") return "⚙️";
            if (param == "shortname" || param == "desc" || param == "description") return "通用";
            return NodeMetaRegistry.Get(NodeCategory.DeviceIO).Brush;
        }
    }

    #endregion

    #region 视图布局与控件转换器

    public class ConditionOutputVisibilityConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FlowNode ? Visibility.Visible : Visibility.Collapsed;
    }

    public class NormalOutputVisibilityConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FlowNode ? Visibility.Collapsed : Visibility.Visible;
    }

    public class NodeBorderConv : BaseMultiConverter
    {
        public override object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return Brushes.Transparent;
            var node = values[0] as FlowNodeBase;
            var sel = values[1] as FlowNodeBase;

            if (node != null && node.IsRunning) return Brushes.SpringGreen;
            if (node != null && node == sel) return Brushes.Gold;
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

            double controlOffset = Math.Max(30, Math.Abs(endY - startY) / 2);
            Point p1 = new Point(startX, startY + controlOffset);
            Point p2 = new Point(endX, endY - controlOffset);
            Point endPoint = new Point(endX, endY);

            figure.Segments.Add(new BezierSegment(p1, p2, endPoint, true));
            geometry.Figures.Add(figure);

            double angle = Math.Atan2(endY - p2.Y, endX - p2.X);
            double arrowLength = 7.0;
            double arrowAngle = Math.PI / 6;

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

    public class PortCategoryToCornerRadiusConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory category && category != PortCategory.Data)
            {
                return new CornerRadius(6);
            }
            return new CornerRadius(1);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class HexToBrushConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
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
    }

    public class ConnectionThicknessConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => 3.5;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class ConnectionDashConv : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => null;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class BoolToBrushConverter : BaseConverter
    {
        public Brush SelectedBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#007ACC"));
        public Brush NormalBrush { get; set; } = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555"));

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isTrue = false;
            if (value is bool b) isTrue = b;
            else if (value != null && bool.TryParse(value.ToString(), out bool parsed)) isTrue = parsed;

            return isTrue ? SelectedBrush : NormalBrush;
        }
    }
    public class BoolToTextConverter:BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isTrue = false;
            if (value is bool b) isTrue = b;
            else if (value != null && bool.TryParse(value.ToString(), out bool parsed)) isTrue = parsed;

            return isTrue ? "远程模式" : "嵌入式模式";
        }
    }

    #endregion

    #region 从 NodeControl 合并的控件级特效与端点转换器

    /// <summary>
    /// 当节点执行时，生成发光/阴影特效
    /// </summary>
    public class RunningGlowEffectConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isRunning && isRunning)
            {
                return new DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString("#00FF66"),
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.95
                };
            }
            return null;
        }
    }

    /// <summary>
    /// 端口中心点 Y 轴坐标偏移转换器
    /// </summary>
    public class OffsetYConverter : BaseConverter
    {
        public double Offset { get; set; }

        public OffsetYConverter() { }
        public OffsetYConverter(double offset) { Offset = offset; }

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double offsetToUse = Offset;
            if (parameter != null && double.TryParse(parameter.ToString(), out double pOffset))
            {
                offsetToUse = pOffset;
            }

            if (value is double y) return y + offsetToUse;
            if (value is int iy) return iy + offsetToUse;
            return offsetToUse;
        }
    }

    /// <summary>
    /// 控制端点用纯圆 (CornerRadius=5)，数据端点用小方块 (CornerRadius=1)
    /// </summary>
    public class PortShapeConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat != PortCategory.Data)
            {
                return new CornerRadius(5); // 纯圆形
            }
            return new CornerRadius(1);     // 小方块
        }
    }

    /// <summary>
    /// 控制端点 10px，数据端点更小一点 (8px)
    /// </summary>
    public class PortSizeConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat != PortCategory.Data)
            {
                return 10.0; // 控制端点尺寸大一点
            }
            return 8.0;  // 数据端点尺寸小一点
        }
    }

    /// <summary>
    /// 根据端口类别 (Category) 与全局开关 (ShowDataPorts) 决定端口端点是否显示
    /// </summary>
    public class PortVisibilityConv : BaseMultiConverter
    {
        public override object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is PortCategory category && values[1] is bool showDataPorts)
            {
                if (category != PortCategory.Data)
                {
                    return Visibility.Visible;
                }

                return showDataPorts ? Visibility.Visible : Visibility.Collapsed;
            }

            return Visibility.Visible;
        }
    }

    /// <summary>
    /// 控制 Exec 端口隐藏/过滤
    /// </summary>
    public class ExecPortHiddenConv : BaseConverter
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is PortCategory cat && cat != PortCategory.Data)
            {
                return Visibility.Collapsed; // 完全隐藏 Exec 端口
            }
            return Visibility.Visible;
        }
    }



    #endregion



}