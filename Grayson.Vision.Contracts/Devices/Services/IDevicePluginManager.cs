using System.Collections.Generic;
using System.Reflection;
using Grayson.Vision.Contracts.Devices.Enums;

namespace Grayson.Vision.Contracts.Devices.Services
{
    /// <summary>
    /// 硬件驱动插件管理器契约。
    /// 负责插件加载、卸载与动态优先级路由。
    /// </summary>
    public interface IDevicePluginManager
    {
        /// <summary>
        /// 已加载的所有插件集合。
        /// </summary>
        IEnumerable<IHardwarePlugin> LoadedPlugins { get; }

        /// <summary>
        /// 自动加载运行目录下所有包含 "Plugin" 标识的 DLL 程序集。
        /// </summary>
        void AutoLoadAllPlugins();

        /// <summary>
        /// 从指定文件夹扫描 Plugin.*.dll 并加载。
        /// </summary>
        void LoadPlugins(string pluginFolder);

        /// <summary>
        /// 从指定程序集中解析并注册 IHardwarePlugin。
        /// </summary>
        void LoadPluginsFromAssembly(Assembly assembly);

        /// <summary>
        /// 根据设备类型与品牌，按插件 Priority 优先级从高到低解析最佳驱动插件。
        /// </summary>
        IHardwarePlugin ResolvePlugin(DeviceCategory category, string brand);

        /// <summary>
        /// 卸载所有插件并释放底层资源。
        /// </summary>
        void ShutdownAll();
    }
}
