using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Media;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Enums;

namespace Grayson.Vison.FlowEdit.Helpers
{
    /// <summary>
    /// 节点/分类 UI 视觉元数据实体
    /// </summary>
    public class NodeMetaInfo
    {
        public string Emoji { get; set; } = "⚙️";
        public string ColorHex { get; set; } = "#434346";
        public string ShortName { get; set; } = "通用";
        public string Description { get; set; } = "未知类型";
        public SolidColorBrush Brush { get; set; }
    }

    /// <summary>
    /// 全局节点与分类元数据注册表（静态初始化缓存 + 画刷冻结性能优化）
    /// </summary>
    public static class NodeMetaRegistry
    {
        private static readonly ConcurrentDictionary<NodeCategory, NodeMetaInfo> CategoryCache
            = new ConcurrentDictionary<NodeCategory, NodeMetaInfo>();

        private static readonly ConcurrentDictionary<NodeType, NodeMetaInfo> NodeCache
            = new ConcurrentDictionary<NodeType, NodeMetaInfo>();

        private static readonly NodeMetaInfo DefaultMeta = new NodeMetaInfo
        {
            Emoji = "⚙️",
            ColorHex = "#434346",
            ShortName = "通用",
            Description = "未知类型",
            Brush = CreateFrozenBrush("#434346")
        };

        static NodeMetaRegistry()
        {
            // 1. 静态初始化加载 NodeCategory 的元数据
            foreach (NodeCategory cat in Enum.GetValues(typeof(NodeCategory)))
            {
                CategoryCache.TryAdd(cat, ParseEnumMeta(cat));
            }

            // 2. 静态初始化加载 NodeType 的元数据
            foreach (NodeType nodeType in Enum.GetValues(typeof(NodeType)))
            {
                NodeCache.TryAdd(nodeType, ParseEnumMeta(nodeType));
            }
        }

        private static NodeMetaInfo ParseEnumMeta<TEnum>(TEnum enumValue) where TEnum : Enum
        {
            var field = enumValue.GetType().GetField(enumValue.ToString());
            if (field == null) return DefaultMeta;

            var metaAttr = field.GetCustomAttribute<NodeFieldMetaAttribute>();
            if (metaAttr == null) return DefaultMeta;

            string colorHex = string.IsNullOrWhiteSpace(metaAttr.ColorHex) ? "#434346" : metaAttr.ColorHex;

            return new NodeMetaInfo
            {
                Emoji = metaAttr.Emoji ?? "⚙️",
                ColorHex = colorHex,
                ShortName = metaAttr.ShortName ?? enumValue.ToString(),
                Description = metaAttr.Description ?? enumValue.ToString(),
                Brush = CreateFrozenBrush(colorHex)
            };
        }

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze(); // 冻结画刷：提升 WPF 渲染效率，且支持跨线程安全调用
                return brush;
            }
            catch
            {
                var fallback = new SolidColorBrush(Colors.Gray);
                fallback.Freeze();
                return fallback;
            }
        }

        /// <summary>
        /// 获取分类 (NodeCategory) 元数据
        /// </summary>
        public static NodeMetaInfo Get(NodeCategory category)
        {
            return CategoryCache.TryGetValue(category, out var info) ? info : DefaultMeta;
        }

        /// <summary>
        /// 获取节点类型 (NodeType) 元数据
        /// </summary>
        public static NodeMetaInfo Get(NodeType nodeType)
        {
            return NodeCache.TryGetValue(nodeType, out var info) ? info : DefaultMeta;
        }

        /// <summary>
        /// 兼容动态解析对象（如 NodeCategory、NodeType 或字符串）
        /// </summary>
        public static NodeMetaInfo Resolve(object target)
        {
            if (target is NodeCategory category) return Get(category);
            if (target is NodeType nodeType) return Get(nodeType);

            if (target is string str && !string.IsNullOrWhiteSpace(str))
            {
                // 先尝试匹配 Category
                if (Enum.TryParse<NodeCategory>(str, true, out var cat))
                    return Get(cat);

                // 再尝试匹配 NodeType
                if (Enum.TryParse<NodeType>(str, true, out var nt))
                    return Get(nt);
            }

            return DefaultMeta;
        }
    }
}