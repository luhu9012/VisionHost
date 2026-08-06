using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using Grayson.Vision.Contracts.Core;
using System.Windows;

namespace Grayson.Vision.WpfUI.Service
{
    public class DevicePoolManager
    {
        private static readonly Lazy<DevicePoolManager> _instance = new Lazy<DevicePoolManager>(() => new DevicePoolManager());
        public static DevicePoolManager Instance => _instance.Value;

        // 保存加载的所有品牌插件 key: BrandName_Category, value: IHardwarePlugin
        private readonly ConcurrentDictionary<string, IHardwarePlugin> _plugins = new ConcurrentDictionary<string, IHardwarePlugin>();

        // 设备池统一容器 key: DeviceKey (用户定义的逻辑名称，如 Cam_Top_01), value: IDevice
        private readonly ConcurrentDictionary<string, IDevice> _devicePool = new ConcurrentDictionary<string, IDevice>  ();

        public IEnumerable<IDevice> GetAllDevices() => _devicePool.Values;

        /// <summary>
        /// 扫描装载指定目录下的所有品牌插件 DLL
        /// </summary>
        public void LoadPlugins(string pluginFolder)
        {
            if (!Directory.Exists(pluginFolder)) return;

            foreach (var file in Directory.GetFiles(pluginFolder, "Plugin.*.dll"))
            {
                var assembly = Assembly.LoadFrom(file);
                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IHardwarePlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is IHardwarePlugin plugin)
                    {
                        plugin.Initialize();
                        string key = $"{plugin.BrandName}_{plugin.Category}";
                        _plugins[key] = plugin;
                    }
                }
            }
        }
        /// <summary>
        /// 自动加载当前运行目录下/引用程序集中所有实现了 IHardwarePlugin 的插件
        /// </summary>
        public void AutoLoadAllPlugins()
        {
            // 1. 获取当前运行目录下的所有 DLL 文件（包括被主程序引用的项目生成的 DLL）
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllFiles = Directory.GetFiles(baseDir, "*.dll");

            foreach (var file in dllFiles)
            {
                try
                {
                    // 过滤并加载包含 Plugin 的 DLL，或者直接全部检查
                    var assemblyName = Path.GetFileNameWithoutExtension(file);
                    if (!assemblyName.Contains("Plugin"))
                        continue;

                    var assembly = Assembly.LoadFrom(file);
                    LoadPluginsFromAssembly(assembly);
                }
                catch (Exception ex)
                {
                    // 忽略不兼容或不可加载的非 NET 程序集
                }
            }
        }

        /// <summary>
        /// 从指定的 Assembly 程序集中解析并注册 IHardwarePlugin
        /// </summary>
        public void LoadPluginsFromAssembly(Assembly assembly)
        {
            var pluginTypes = assembly.GetTypes()
                .Where(t => typeof(IHardwarePlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

            foreach (var type in pluginTypes)
            {
                if (Activator.CreateInstance(type) is IHardwarePlugin plugin)
                {
                    plugin.Initialize();
                    string key = $"{plugin.BrandName}_{plugin.Category}";
                    _plugins[key] = plugin;
                }
            }
        }
        /// <summary>
        /// 扫描所有已加载插件下的物理硬件
        /// </summary>
        public List<DeviceInfo> ScanAllPhysicalDevices()
        {
            var list = new List<DeviceInfo>();
            foreach (var plugin in _plugins.Values)
            {
                try
                {
                    Result<List<DeviceInfo>> result = plugin.EnumerateDevices();
                    if (result?.Success == true)
                    {
                        list.AddRange(result.Data);
                    }
                    else
                    {
                        //MessageBox.Show(result.Message, "扫描设备失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    // 记录某个驱动扫描失败的日志，不影响其他驱动
                }
            }
            return list;
        }

        /// <summary>
        /// 向设备池添加硬件设备
        /// </summary>
        public IDevice AddDeviceToPool(string brandName, DeviceCategory category, string deviceId, string deviceKey)
        {
            string pluginKey = $"{brandName}_{category}";
            if (!_plugins.TryGetValue(pluginKey, out var plugin))
            {
                throw new NotSupportedException($"未找到支持 [{brandName}] - [{category}] 的驱动插件!");
            }

            var device = plugin.CreateDevice(deviceId);
            device.DeviceKey = deviceKey;

            _devicePool[deviceKey] = device;
            return device;
        }

        /// <summary>
        /// 从设备池获取设备（泛型快捷方式）
        /// 业务层使用：var camera = DevicePoolManager.Instance.GetDevice<ICamera>("Cam_Top_01");
        /// </summary>
        public T GetDevice<T>(string deviceKey) where T : class, IDevice
        {
            if (_devicePool.TryGetValue(deviceKey, out var device))
            {
                return device as T;
            }
            return null;
        }

        public bool RemoveDevice(string deviceKey)
        {
            if (_devicePool.TryRemove(deviceKey, out var device))
            {
                device.Disconnect();
                device.Dispose();
                return true;
            }
            return false;
        }
        /// <summary>
        /// 获取当前已加载的所有插件实例列表
        /// </summary>
        public IEnumerable<IHardwarePlugin> GetAllPlugins()
        {
            return _plugins.Values;
        }
    }
}