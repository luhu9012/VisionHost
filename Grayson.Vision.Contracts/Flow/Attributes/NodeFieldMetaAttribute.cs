using System;

namespace Grayson.Vision.Contracts.Flow.Attributes
{
    /// <summary>
    /// 节点的 UI 视觉元数据特性（图标、背景色 Hex、微型短名称）
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class NodeFieldMetaAttribute : Attribute
    {
        public string Emoji { get; }
        public string ColorHex { get; }
        public string ShortName { get; }

        public string Description { get; }

        public NodeFieldMetaAttribute(string emoji, string colorHex, string shortName = null, string description = null  )
        {
            Emoji = emoji;
            ColorHex = colorHex;
            ShortName = shortName;
            Description = description;
        }
    }
}