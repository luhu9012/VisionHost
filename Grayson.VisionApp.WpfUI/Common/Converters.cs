//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: Converters.cs
// 创 建: 2026-07-18
// 说 明: 常用的 WPF 值转换器集合
//===================================================================================

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

using Grayson.Vision.Contracts.Infrastructure.Permission;

namespace Grayson.Vision.WpfUI.Common.Converters
{
    /// <summary>
    /// 布尔值转可见性
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                bool invert = parameter?.ToString() == "Invert";
                return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 配方审批状态转中文文本。
    /// </summary>
    public class RecipeApprovalStatusToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus status)
            {
                switch (status)
                {
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Draft: return "草稿";
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.PendingApproval: return "待审批";
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Approved: return "已审批";
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Frozen: return "已冻结";
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Archived: return "已归档";
                    case Grayson.Vision.Contracts.Recipe.Enums.RecipeApprovalStatus.Deprecated: return "已废弃";
                }
            }
            return "未知";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 报警等级转颜色画刷
    /// </summary>
    public class AlarmLevelToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AlarmLevel level)
            {
                switch (level)
                {
                    case AlarmLevel.Info:
                        return new SolidColorBrush(Colors.DodgerBlue);
                    case AlarmLevel.Warning:
                        return new SolidColorBrush(Colors.Orange);
                    case AlarmLevel.Error:
                        return new SolidColorBrush(Colors.OrangeRed);
                    case AlarmLevel.Critical:
                        return new SolidColorBrush(Colors.Red);
                }
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 设备状态转颜色画刷
    /// </summary>
    public class DeviceStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DeviceStatus status)
            {
                switch (status)
                {
                    case DeviceStatus.Disconnected:
                        return new SolidColorBrush(Colors.Gray);
                    case DeviceStatus.Connected:
                        return new SolidColorBrush(Colors.DodgerBlue);
                    case DeviceStatus.Running:
                        return new SolidColorBrush(Colors.LimeGreen);
                    case DeviceStatus.Paused:
                        return new SolidColorBrush(Colors.Orange);
                    case DeviceStatus.Error:
                        return new SolidColorBrush(Colors.Red);
                }
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 用户角色是否满足最低角色要求。
    /// ConverterParameter 传入目标角色名称（Operator/Engineer/Administrator），或 "Operator+" 表示 operator 及以上。
    /// </summary>
    public class RoleSatisfiesConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is UserRole currentRole)) return false;

            var target = parameter?.ToString();
            if (string.IsNullOrEmpty(target)) return false;

            switch (target)
            {
                case "Operator":
                    return currentRole >= UserRole.Operator;
                case "Engineer":
                    return currentRole >= UserRole.Engineer;
                case "Administrator":
                    return currentRole >= UserRole.Administrator;
                default:
                    if (Enum.TryParse<UserRole>(target, out var parsed))
                        return currentRole >= parsed;
                    return false;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 检测结果转颜色画刷
    /// </summary>
    public class InspectionResultToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is InspectionResult result)
            {
                switch (result)
                {
                    case InspectionResult.OK:
                        return new SolidColorBrush(Colors.LimeGreen);
                    case InspectionResult.NG:
                        return new SolidColorBrush(Colors.Red);
                    case InspectionResult.Error:
                        return new SolidColorBrush(Colors.Orange);
                }
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 空值转布尔值 (null = false, not null = true)
    /// </summary>
    public class NullToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "Invert";
            return (value != null) ^ invert;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转确认状态文本
    /// </summary>
    public class BoolToAcknowledgeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "已确认" : "未确认";
            }
            return "未知";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转确认状态颜色
    /// </summary>
    public class BoolToAcknowledgeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? new SolidColorBrush(Colors.LimeGreen) : new SolidColorBrush(Colors.Red);
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #region 新增 BoolToColor / BoolNgToColor 布尔转画刷
    /// <summary>
    /// 运行状态布尔转颜色：true绿色(运行) / false灰色(待机)
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public SolidColorBrush TrueBrush { get; set; } = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        public SolidColorBrush FalseBrush { get; set; } = new SolidColorBrush(Color.FromRgb(150, 150, 150));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? TrueBrush : FalseBrush;
            return FalseBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 检测OK/NG布尔转颜色：true红色(NG) / false绿色(OK)
    /// </summary>
    public class BoolNgToColorConverter : IValueConverter
    {
        public SolidColorBrush NgBrush { get; set; } = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        public SolidColorBrush OkBrush { get; set; } = new SolidColorBrush(Color.FromRgb(76, 175, 80));

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? NgBrush : OkBrush;
            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    #endregion
    public class BoolToStatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isConnected && isConnected)
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF00C853")); // 在线：绿色
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF757575")); // 离线：灰色
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    /// <summary>
    /// 空值转可见性 (null = Collapsed, not null = Visible)
    /// 支持 ConverterParameter="Invert" 进行反转
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNotNull = value != null;
            bool invert = parameter?.ToString() == "Invert";

            return (isNotNull ^ invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>bool → 启用 / 禁用</summary>
    public class BoolToServoTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b)
            {
                return b ? "⚡ 启用" : "⚡ 禁用";
            }
            return "⚡ 未知";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class IntToBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intVal && parameter != null && int.TryParse(parameter.ToString(), out int paramVal))
            {
                return intVal == paramVal;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolVal && boolVal && parameter != null && int.TryParse(parameter.ToString(), out int paramVal))
            {
                return paramVal;
            }
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// 布尔值取反转换器（用于 IsReadOnly = !CanEdit）。
    /// </summary>
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b) return !b;
            return value;
        }
    }


}
