using System;
using System.Collections.Generic;

namespace Grayson.Vision.Common.Extensions
{
    /// <summary>
    /// 全项目通用对象扩展方法，简化判空、类型转换代码
    /// </summary>
    public static class ObjectExtension
    {
        /// <summary>判断引用类型为空</summary>
        public static bool IsNull(this object obj)
        {
            return obj == null;
        }

        /// <summary>判断不为空</summary>
        public static bool NotNull(this object obj)
        {
            return obj != null;
        }

        /// <summary>安全字典取值，无Key返回默认值，不抛KeyNotFound</summary>
        public static T SafeGet<T>(this Dictionary<string, object> dict, string key, T defaultValue = default)
        {
            if (dict.IsNull() || !dict.ContainsKey(key))
                return defaultValue;

            object val = dict[key];
            try
            {
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}