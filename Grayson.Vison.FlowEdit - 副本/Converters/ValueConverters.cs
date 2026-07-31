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

    public class BezierPathConverter : BaseMultiConverter
    {
        public override object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 4) return DependencyProperty.UnsetValue;

            double startX = System.Convert.ToDouble(values[0]);
            double startY = System.Convert.ToDouble(values[1]);
            double endX = System.Convert.ToDouble(values[2]);
            double endY = System.Convert.ToDouble(values[3]);

            Point start = new Point(startX, startY);
            Point end = new Point(endX, endY);

            double offset = Math.Abs(end.X - start.X) * 0.5;
            if (offset < 40) offset = 40;

            PathFigure figure = new PathFigure { StartPoint = start };
            figure.Segments.Add(new BezierSegment(new Point(start.X + offset, start.Y), new Point(end.X - offset, end.Y), end, true));

            PathGeometry geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            return geometry;
        }
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
            if (value is PortCategory cat && cat == PortCategory.Exec)
                return 3.5;
            return 1.8;
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
            if (value is PortCategory cat && cat == PortCategory.Exec)
                return null; // 实线
            return new DoubleCollection { 2, 2 }; // 点虚线
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }



}