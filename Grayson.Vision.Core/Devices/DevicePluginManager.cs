using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Grayson.Vision.Core.Devices
{
    /// <summary>
    /// 硬件驱动插件管理器实现。
    /// 零 UI 依赖，可被 StationHost / WorkerHost 复用。
    /// </summary>
    public class DevicePluginManager : IDevicePluginManager
    {
        private readonly ConcurrentBag<IHardwarePlugin> _plugins = new ConcurrentBag<IHardwarePlugin>();

        public IEnumerable<IHardwarePlugin> LoadedPlugins => _plugins.ToArray();

        public void AutoLoadAllPlugins()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllFiles = Directory.GetFiles(baseDir, "*.dll");

            foreach (var file in dllFiles)
            {
                try
                {
                    var assemblyName = Path.GetFileNameWithoutExtension(file);
                    if (!assemblyName.Contains("Plugin"))
                        continue;

                    var assembly = Assembly.LoadFrom(file);
                    LoadPluginsFromAssembly(assembly);
                }
                catch
                {
                    // 忽略不可加载的非 .NET/Native 动态库
                }
            }
        }

        public void LoadPlugins(string pluginFolder)
        {
            if (!Directory.Exists(pluginFolder)) return;

            foreach (var file in Directory.GetFiles(pluginFolder, "Plugin.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    LoadPluginsFromAssembly(assembly);
                }
                catch
                {
                    // 忽略加载失败的文件
                }
            }
        }

        public void LoadPluginsFromAssembly(Assembly assembly)
        {
            if (assembly == null) return;

            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IHardwarePlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            foreach (var type in pluginTypes)
            {
                if (Activator.CreateInstance(type) is IHardwarePlugin plugin)
                {
                    plugin.Initialize();

                    if (!_plugins.Any(p => p.GetType() == plugin.GetType()))
                    {
                        _plugins.Add(plugin);
                    }
                }
            }
        }

        public IHardwarePlugin ResolvePlugin(DeviceCategory category, string brand)
        {
            return _plugins
                .OrderByDescending(p => p.Priority)
                .FirstOrDefault(p => p.Supports(category, brand));
        }

        public void ShutdownAll()
        {
            foreach (var plugin in _plugins)
            {
                try
                {
                    plugin.Shutdown();
                }
                catch
                {
                    // 忽略单个插件卸载异常
                }
            }
            while (!_plugins.IsEmpty)
            {
                _plugins.TryTake(out _);
            }
        }
    }
}
