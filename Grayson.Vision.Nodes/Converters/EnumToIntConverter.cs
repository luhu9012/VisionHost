using System;
using System.Globalization;
using System.Windows.Data;

namespace Grayson.Vision.Nodes.Converters
{
    public class EnumToIntConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Enum)
            {
                return (int)value;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intVal && targetType.IsEnum)
            {
                return Enum.ToObject(targetType, intVal);
            }
            return value;
        }
    }
}