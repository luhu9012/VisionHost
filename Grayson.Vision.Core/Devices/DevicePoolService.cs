using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Devices
{
    /// <summary>
    /// Core 层默认设备池服务：
    /// 负责插件加载、设备内存池管理、扫描等。持久化/数据库恢复暂时委托给 UI 的 DevicePoolManager，
    /// 后续逐步将数据库恢复逻辑下沉到 Repository/Core。
    /// </summary>
    public class DevicePoolService : IDevicePoolService
    {
        private readonly ConcurrentBag<IHardwarePlugin> _plugins = new ConcurrentBag<IHardwarePlugin>();
        private readonly ConcurrentDictionary<string, IDevice> _devicePool = new ConcurrentDictionary<string, IDevice>();
        private bool _initialized;

        public event EventHandler<DeviceStateChangedEventArgs> DeviceStateChanged;

        public Task InitializeAsync()
        {
            if (_initialized) return Task.CompletedTask;

            AutoLoadAllPlugins();
            _initialized = true;

            return Task.CompletedTask;
        }

        public void AutoLoadAllPlugins()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dllFiles = Directory.GetFiles(baseDir, "*.dll");

            foreach (var file in dllFiles)
            {
                try
                {
                    var assemblyName = Path.GetFileNameWithoutExtension(file);
                    if (!assemblyName.Contains("Plugin")) continue;

                    var assembly = Assembly.LoadFrom(file);
                    LoadPluginsFromAssembly(assembly);
                }
                catch
                {
                    // 忽略不可加载的 DLL
                }
            }
        }

        public void LoadPluginsFromAssembly(Assembly assembly)
        {
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

        public IReadOnlyCollection<IHardwarePlugin> GetAllPlugins()
        {
            return new List<IHardwarePlugin>(_plugins);
        }

        public IReadOnlyCollection<IDevice> GetAllDevices()
        {
            return new List<IDevice>(_devicePool.Values);
        }

        public IDevice GetDevice(string deviceKey)
        {
            if (string.IsNullOrEmpty(deviceKey)) return null;
            _devicePool.TryGetValue(deviceKey, out var device);
            return device;
        }

        public TDevice GetDevice<TDevice>(string deviceKey) where TDevice : class, IDevice
        {
            return GetDevice(deviceKey) as TDevice;
        }

        public Result RemoveDevice(string deviceKey)
        {
            if (string.IsNullOrEmpty(deviceKey))
                return Result.Fail("设备键不能为空");

            if (_devicePool.TryRemove(deviceKey, out var device))
            {
                try
                {
                    device?.Disconnect();
                }
                catch (Exception ex)
                {
                    LogBus.Warn("DevicePoolService", $"移除设备 [{deviceKey}] 时断开失败: {ex.Message}");
                }
                return Result.Ok();
            }

            return Result.Fail($"设备 [{deviceKey}] 不存在");
        }

        public Task<Result> ScanAndRegisterDevicesAsync()
        {
            // 默认实现：遍历所有插件枚举一次设备并加入内存池（不持久化）。
            foreach (var plugin in _plugins)
            {
                try
                {
                    var result = plugin.EnumerateDevices();
                    if (result?.Success != true || result.Data == null) continue;

                    foreach (var info in result.Data)
                    {
                        if (info == null || string.IsNullOrEmpty(info.DeviceId)) continue;

                        var device = plugin.CreateDevice(info.DeviceId);
                        if (device != null)
                        {
                            var key = device.DeviceKey ?? info.DeviceId;
                            _devicePool[key] = device;
                            device.StateChanged += (s, state) =>
                                DeviceStateChanged?.Invoke(this, new DeviceStateChangedEventArgs(null, key, DeviceState.Disconnected, state, null));
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogBus.Error("DevicePoolService", $"扫描插件 [{plugin.BrandName}] 设备失败: {ex.Message}", ex);
                }
            }

            return Task.FromResult(Result.Ok());
        }

        public void RegisterDevice(string deviceKey, IDevice device)
        {
            if (string.IsNullOrEmpty(deviceKey) || device == null) return;
            _devicePool[deviceKey] = device;
        }
    }
}
