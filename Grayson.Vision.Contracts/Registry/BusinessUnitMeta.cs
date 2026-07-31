using Grayson.Vision.Contracts.Business.Enums;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Registry
{
    /// <summary>单元反射元数据实体，注册表扫描后缓存该信息</summary>
    public class BusinessUnitMeta
    {
        public string ModuleId { get; set; }
        public string DisplayName { get; set; }
        public BusinessUnitType UnitType { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        /// <summary>单元实现完整类型，反射实例化使用</summary>
        public Type ImplementType { get; set; }
    }
}