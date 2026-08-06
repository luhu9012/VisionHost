// Core/BaseEntity.cs
using System;

namespace Grayson.Vision.Repository.Core
{
    /// <summary>
    /// 持久化实体基类，LiteDB 默认识别名为 Id 或带有 [BsonId] 的字段为主键
    /// </summary>
    public abstract class BaseEntity
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime UpdatedTime { get; set; } = DateTime.Now;
    }
}