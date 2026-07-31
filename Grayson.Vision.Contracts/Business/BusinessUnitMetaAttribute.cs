using Grayson.Vision.Contracts.Business.Enums;
using System;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 业务单元标记特性
    /// 所有IBusinessUnit实现类必须加此标记，程序启动靠反射读取元数据
    /// 自动生成UI工具箱、自动创建实例、分类展示
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class BusinessUnitMetaAttribute : Attribute
    {
        /// <summary>全局唯一模块ID，序列化、创建实例依靠该ID</summary>
        public string ModuleId { get; set; }

        /// <summary>UI显示友好名称</summary>
        public string DisplayName { get; set; }

        /// <summary>单元所属大类</summary>
        public BusinessUnitType UnitType { get; set; }

        /// <summary>UI分组名称：视觉算法/PLC/运动/脚本</summary>
        public string Category { get; set; }

        /// <summary>单元功能描述，鼠标悬浮提示</summary>
        public string Description { get; set; }

        // 无参构造，支持命名赋值
        public BusinessUnitMetaAttribute()
        {

        }

        public BusinessUnitMetaAttribute(string moduleId, string displayName, BusinessUnitType unitType, string category, string desc)
        {
            ModuleId = moduleId;
            DisplayName = displayName;
            UnitType = unitType;
            Category = category;
            Description = desc;
        }
    }
}