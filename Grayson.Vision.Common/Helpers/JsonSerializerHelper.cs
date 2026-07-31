using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.IO;
using System.Xml;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 全局统一JSON序列化工具
    /// 配方、工位配置、流程节点全部使用此类序列化/反序列化
    /// 统一配置格式、时间、浮点、结构体处理，保证全系统JSON格式一致
    /// 兼容Pose3D结构体、枚举、DateTime
    /// </summary>
    public static class JsonSerializerHelper
    {
        /// <summary>全局固定序列化配置</summary>
        private static readonly JsonSerializerSettings _globalSettings;

        static JsonSerializerHelper()
        {
            _globalSettings = new JsonSerializerSettings
            {
                // 缩进格式化，方便人工打开JSON修改配方
                Formatting =  Newtonsoft.Json.Formatting.Indented,
                // 忽略null空字段，减小文件体积
                NullValueHandling = NullValueHandling.Ignore,
                // 枚举存字符串，可读性强，不要存数字
                Converters = { new StringEnumConverter() },
                // 日期统一格式
                DateFormatString = "yyyy-MM-dd HH:mm:ss.fff",
                // 循环引用规避（流程节点嵌套子流程必开）
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                PreserveReferencesHandling = PreserveReferencesHandling.None
            };
        }

        /// <summary>对象序列化为JSON字符串</summary>
        public static string SerializeObject(object obj)
        {
            if (obj == null)
                return string.Empty;
            try
            {
                return JsonConvert.SerializeObject(obj, _globalSettings);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("JSON序列化失败", ex, nameof(JsonSerializerHelper));
                return string.Empty;
            }
        }

        /// <summary>JSON字符串反序列化为指定实体</summary>
        public static T DeserializeObject<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;
            try
            {
                return JsonConvert.DeserializeObject<T>(json, _globalSettings);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"JSON反序列化{typeof(T).Name}失败", ex, nameof(JsonSerializerHelper));
                return default;
            }
        }

        /// <summary>将实体直接保存为本地JSON文件</summary>
        public static void SaveToFile<T>(T data, string filePath)
        {
            try
            {
                string json = SerializeObject(data);
                FileHelper.EnsureDirectoryExists(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"保存JSON文件失败:{filePath}", ex, nameof(JsonSerializerHelper));
            }
        }

        /// <summary>从本地JSON文件读取并反序列化实体</summary>
        public static T LoadFromFile<T>(string filePath)
        {
            if (!File.Exists(filePath))
            {
                GlobalLogger.Warn($"JSON配置文件不存在：{filePath}", nameof(JsonSerializerHelper));
                return default;
            }
            try
            {
                string json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                return DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"读取JSON文件失败:{filePath}", ex, nameof(JsonSerializerHelper));
                return default;
            }
        }
    }
}