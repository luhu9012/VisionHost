using System;
using System.ComponentModel;
using System.Reflection;

namespace Grayson.Vision.Contracts.Business.Helpers
{
    public static class EnumExtensions
    {
        /// <summary>
        /// 获取枚举字段上标记的 DescriptionAttribute 文本
        /// </summary>
        public static string GetDescription(this Enum enumValue)
        {
            if (enumValue == null) return string.Empty;

            var fieldInfo = enumValue.GetType().GetField(enumValue.ToString());
            if (fieldInfo == null) return enumValue.ToString();

            var attribute = fieldInfo.GetCustomAttribute<DescriptionAttribute>();
            return attribute != null ? attribute.Description : enumValue.ToString();
        }

        /// <summary>
        /// 获取枚举字段上的自定义特性 (如 [NodeUIInfo("相机采集", "SegoeMDL2:Camera", "触发硬件采图")] )
        /// </summary>
        public static T GetAttribute<T>(this Enum enumValue) where T : Attribute
        {
            if (enumValue == null) return null;
            var fieldInfo = enumValue.GetType().GetField(enumValue.ToString());
            return fieldInfo?.GetCustomAttribute<T>();
        }
    }
}