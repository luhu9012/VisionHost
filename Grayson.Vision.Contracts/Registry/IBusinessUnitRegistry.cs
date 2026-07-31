using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Core;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Registry
{
    /// <summary>全局业务单元注册表，负责扫描、创建所有业务单元</summary>
    public interface IBusinessUnitRegistry
    {
        /// <summary>获取所有单元元数据，用于WPF工具箱渲染</summary>
        List<BusinessUnitMeta> GetAllMetaList();

        /// <summary>根据ModuleId反射创建单元实例</summary>
        Result<IBusinessUnit> CreateUnit(string moduleId);

        /// <summary>扫描指定目录下所有dll，加载带标记的业务单元</summary>
        void ScanAllAssembly(string pluginDir);
    }
}