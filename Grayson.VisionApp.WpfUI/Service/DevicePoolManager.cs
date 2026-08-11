using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 全局硬件设备池管理器（单例）
    /// 负责：硬件驱动插件装载、动态优先级匹配路由、物理设备扫描、内存设备池维护、LiteDB 数据库持久化同步
    /// </summary>
    public class DevicePoolManager
    {
        #region 单例与线程安全初始化

        private static readonly Lazy<DevicePoolManager> _instance = new Lazy<DevicePoolManager>(() => new DevicePoolManager());
        public static DevicePoolManager Instance => _instance.Value;

        private readonly object _initializeLock = new object();
        private bool _isInitialized = false;

        private DevicePoolManager()
        {
        }

        #endregion

        #region 内部容器

        /// <summary>
        /// 保存已加载的所有驱动插件实例集合（支持按 Priority 动态优先级查找）
        /// </summary>
        private readonly ConcurrentBag<IHardwarePlugin> _plugins = new ConcurrentBag<IHardwarePlugin>();

        /// <summary>
        /// 设备池统一内存容器 key: DeviceKey (逻辑名称，如 Cam_Top_01), value: IDevice 实例
        /// </summary>
        private readonly ConcurrentDictionary<string, IDevice> _devicePool = new ConcurrentDictionary<string, IDevice>();

        #endregion

        #region 系统初始化与插件加载

        /// <summary>
        /// 设备池异步初始化入口：加载插件 -> 从数据库恢复设备实例
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await Task.Run(() =>
            {
                lock (_initializeLock)
                {
                    if (_isInitialized) return;

                    // 1. 自动装载插件
                    AutoLoadAllPlugins();

                    // 2. 从数据库恢复已注册的设备实例
                    LoadConfiguredDevicesFromDb();

                    _isInitialized = true;
                }
            });
        }

        /// <summary>
        /// 自动加载当前运行目录下所有包含 Plugin 的 DLL 驱动程序集
        /// </summary>
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

                    // 防止重复加载相同类型的插件
                    if (!_plugins.Any(p => p.GetType() == plugin.GetType()))
                    {
                        _plugins.Add(plugin);
                    }
                }
            }
        }

        /// <summary>
        /// 扫描指定文件夹下的 Plugin.*.dll 插件文件
        /// </summary>
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

        #endregion

        #region 动态插件解析与路由核心

        /// <summary>
        /// 根据设备类型与品牌，按插件 Priority 优先级从高到低自动解析最佳驱动插件
        /// （优先分配 SDK 专有驱动，找不到时自动由 UniversalProtocol 兜底）
        /// </summary>
        public IHardwarePlugin ResolvePlugin(DeviceCategory category, string brand)
        {
            return _plugins
                .OrderByDescending(p => p.Priority) // 按 Priority 从高到低排序 (如 100 -> 0 -> -100)
                .FirstOrDefault(p => p.Supports(category, brand));
        }

        #endregion

        #region 数据库恢复与物理扫描

        /// <summary>
        /// 从数据库（LiteDB）加载配置的设备并在内存池中实例化（保持未连接状态）
        /// </summary>
        private void LoadConfiguredDevicesFromDb()
        {
            try
            {
                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                var configuredDevices = configRepo.GetAllEnabled();

                if (configuredDevices == null || !configuredDevices.Any()) return;

                foreach (var config in configuredDevices)
                {
                    if (_devicePool.ContainsKey(config.DeviceKey)) continue;

                    var plugin = ResolvePlugin(config.Category, config.BrandName);

                    if (plugin != null)
                    {
                        try
                        {
                            // 1. 实例化设备对象
                            var device = plugin.CreateDevice(config.DeviceId);

                            if (device == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[DevicePool] 驱动插件 [{plugin.BrandName}] 无法根据 DeviceId:[{config.DeviceId}] 创建设备实例，返回了 null。");
                                continue;
                            }

                            device.DeviceKey = config.DeviceKey;

                            if (!string.IsNullOrEmpty(config.ConnectionString))
                            {
                                device.SetParam("ConnectionString", config.ConnectionString);
                            }

                            _devicePool[config.DeviceKey] = device;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DevicePool] 复原设备 [{config.DeviceKey}] (SN:{config.DeviceId}) 失败: {ex.Message}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[DevicePool] 驱动插件缺失: [{config.BrandName}] - [{config.Category}]，无法恢复设备 [{config.DeviceKey}]");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DevicePool] 从数据库恢复设备配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 扫描所有已加载插件下的在线物理硬件信息（只读模式，不修改内存池与数据库）
        /// </summary>
        public List<DeviceInfo> ScanAllPhysicalDevices()
        {
            var list = new List<DeviceInfo>();
            foreach (var plugin in _plugins)
            {
                try
                {
                    Result<List<DeviceInfo>> result = plugin.EnumerateDevices();
                    if (result?.Success == true && result.Data != null)
                    {
                        list.AddRange(result.Data);
                    }
                }
                catch
                {
                    // 忽略单个驱动扫描失败的情况
                }
            }
            return list;
        }

        #endregion

        #region 设备管理 CRUD (内存池与 LiteDB 同步)

        /// <summary>
        /// 添加物理扫描到的设备到设备池，并异步持久化保存到数据库
        /// </summary>
        public async Task<Result<IDevice>> AddDeviceToPoolAndSaveAsync(DeviceInfo info, string userDeviceKey, string connectionString = "")
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(userDeviceKey)) return Result<IDevice>.Fail("逻辑名称不能为空!");
                if (_devicePool.ContainsKey(userDeviceKey)) return Result<IDevice>.Fail($"已存在 Key 为 [{userDeviceKey}] 的内存设备!");

                var plugin = ResolvePlugin(info.Category, info.BrandName);
                if (plugin == null)
                {
                    return Result<IDevice>.Fail($"未找到支持 [{info.BrandName}] - [{info.Category}] 的驱动插件!");
                }

                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                if (configRepo.GetByKey(userDeviceKey) != null)
                {
                    return Result<IDevice>.Fail($"数据库中已注册逻辑名称为 [{userDeviceKey}] 的设备，请更改 Key！");
                }
                if (configRepo.GetByDeviceId(info.DeviceId) != null)
                {
                    return Result<IDevice>.Fail($"数据库中已存在物理 ID 为 [{info.DeviceId}] 的设备配置，不能重复添加。");
                }

                try
                {
                    var device = plugin.CreateDevice(info.DeviceId);
                    device.DeviceKey = userDeviceKey;

                    if (!string.IsNullOrEmpty(connectionString))
                    {
                        device.SetParam("ConnectionString", connectionString);
                    }

                    var po = new DeviceConfigPo
                    {
                        DeviceKey = userDeviceKey,
                        DeviceId = info.DeviceId,
                        BrandName = info.BrandName,
                        Category = info.Category,
                        IsEnabled = true,
                        ConnectionString = connectionString
                    };

                    if (configRepo.Insert(po))
                    {
                        _devicePool[userDeviceKey] = device;
                        return Result<IDevice>.Ok(device);
                    }
                    else
                    {
                        device.Dispose();
                        return Result<IDevice>.Fail("写入数据库配置失败!");
                    }
                }
                catch (Exception ex)
                {
                    return Result<IDevice>.Fail($"添加设备并保存失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 手动创建设备（自动通过 Priority 查找 SDK 插件或通用协议驱动）
        /// </summary>
        public async Task<Result<IDevice>> CreateAndSaveManualDeviceAsync(DeviceCategory category, string brand, string deviceKey, string connectionString)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(deviceKey)) return Result<IDevice>.Fail("设备逻辑名称不能为空!");
                if (_devicePool.ContainsKey(deviceKey)) return Result<IDevice>.Fail($"内存设备池中已存在名称为 [{deviceKey}] 的设备!");

                // 核心重构优化：自动按 Priority 路由匹配最优驱动，优先使用专有 SDK 插件，没有则降级由通用协议驱动接管
                var plugin = ResolvePlugin(category, brand);
                if (plugin == null)
                {
                    return Result<IDevice>.Fail($"未找到匹配品牌 [{brand}] 与类别 [{category}] 的驱动插件，请确保相关插件已正常加载！");
                }

                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                if (configRepo.GetByKey(deviceKey) != null)
                {
                    return Result<IDevice>.Fail($"数据库中已存在逻辑名称为 [{deviceKey}] 的记录!");
                }

                try
                {
                    string manualDeviceId = $"{category}_{brand}_{DateTime.Now:yyyyMMddHHmmss}";

                    var device = plugin.CreateDevice(manualDeviceId);
                    device.DeviceKey = deviceKey;
                    if (!string.IsNullOrEmpty(connectionString))
                    {
                        device.SetParam("ConnectionString", connectionString);
                    }

                    var po = new DeviceConfigPo
                    {
                        DeviceKey = deviceKey,        // 逻辑 Key（如 "Modbus_Sensor_01"）
                        DeviceId = manualDeviceId,    // 物理全局唯一 ID
                        BrandName = brand,            // 品牌名 / 插件标识
                        Category = category,          // 用户选定的设备类别
                        IsEnabled = true,
                        ConnectionString = connectionString
                    };

                    if (configRepo.Insert(po))
                    {
                        _devicePool[deviceKey] = device;
                        return Result<IDevice>.Ok(device);
                    }
                    else
                    {
                        device.Dispose();
                        return Result<IDevice>.Fail("手动设备写入数据库失败!");
                    }
                }
                catch (Exception ex)
                {
                    return Result<IDevice>.Fail($"手动添加设备异常: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 增量批量同步在线物理设备到数据库与内存池
        /// </summary>
        public async Task<Result<int>> AddIncrementalPhysicalDevicesAsync(IEnumerable<DeviceInfo> onlineInfos)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (onlineInfos == null || !onlineInfos.Any()) return Result<int>.Ok(0);

                    var configRepo = StorageFactory.CreateDeviceConfigRepository();
                    int addedCount = 0;

                    foreach (var info in onlineInfos)
                    {
                        var existingConfig = configRepo.GetByDeviceId(info.DeviceId);
                        if (existingConfig == null)
                        {
                            string autoKey = $"{info.BrandName}_{info.Category}_{info.DeviceId}";
                            if (_devicePool.ContainsKey(autoKey)) continue;

                            var plugin = ResolvePlugin(info.Category, info.BrandName);
                            if (plugin == null) continue;

                            try
                            {
                                var device = plugin.CreateDevice(info.DeviceId);
                                device.DeviceKey = autoKey;

                                var po = new DeviceConfigPo
                                {
                                    DeviceKey = autoKey,
                                    DeviceId = info.DeviceId,
                                    BrandName = info.BrandName,
                                    Category = info.Category,
                                    IsEnabled = true,
                                    ConnectionString = ""
                                };

                                if (configRepo.Insert(po))
                                {
                                    _devicePool[autoKey] = device;
                                    addedCount++;
                                }
                                else
                                {
                                    device.Dispose();
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[DevicePool] 增量添加 SN:{info.DeviceId} 失败: {ex.Message}");
                            }
                        }
                    }
                    return Result<int>.Ok(addedCount);
                }
                catch (Exception ex)
                {
                    return Result<int>.Fail($"增量同步发生错误: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 当用户修改了设备的逻辑名称 (DeviceKey) 或连接参数后更新数据库与内存映射
        /// </summary>
        public async Task<Result> UpdateDeviceMappingAsync(IDevice device, string newConnectionString = null)
        {
            return await Task.Run(() =>
            {
                if (device == null) return Result.Fail("设备对象不能为空!");

                try
                {
                    var configRepo = StorageFactory.CreateDeviceConfigRepository();
                    var po = configRepo.GetByDeviceId(device.DeviceId);
                    if (po == null) return Result.Fail($"找不到 SN/物理ID 为 [{device.DeviceId}] 的设备配置记录，无法更新！");

                    var duplicateKey = configRepo.GetByKey(device.DeviceKey);
                    if (duplicateKey != null && duplicateKey.Id != po.Id)
                    {
                        return Result.Fail($"逻辑名称 [{device.DeviceKey}] 已被其他物理设备占用，请使用唯一的逻辑 Key！");
                    }

                    // 1. 更新数据库 PO 记录
                    po.DeviceKey = device.DeviceKey;
                    if (newConnectionString != null)
                    {
                        po.ConnectionString = newConnectionString;
                        device.SetParam("ConnectionString", newConnectionString);
                    }
                    configRepo.Update(po);

                    // 2. 迁移 ConcurrentDictionary 内存映射字典 Key
                    string oldKey = _devicePool.FirstOrDefault(x => x.Value.DeviceId == device.DeviceId).Key;
                    if (!string.IsNullOrEmpty(oldKey) && oldKey != device.DeviceKey)
                    {
                        if (_devicePool.TryRemove(oldKey, out var removedDevice))
                        {
                            _devicePool[device.DeviceKey] = removedDevice;
                        }
                    }

                    return Result.Ok();
                }
                catch (Exception ex)
                {
                    return Result.Fail($"更新逻辑名称映射失败: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 从内存池和数据库中彻底移除指定设备（并断开连接释放资源）
        /// </summary>
        public bool RemoveDevice(string deviceKey)
        {
            if (_devicePool.TryRemove(deviceKey, out var device))
            {
                try
                {
                    device.Disconnect();
                    device.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DevicePool] 释放设备 [{deviceKey}] 资源异常: {ex.Message}");
                }

                Task.Run(() =>
                {
                    try
                    {
                        var configRepo = StorageFactory.CreateDeviceConfigRepository();
                        var po = configRepo.GetByKey(deviceKey);
                        if (po != null)
                        {
                            configRepo.Delete(po.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DevicePool] 从数据库删除设备配置 [{deviceKey}] 失败: {ex.Message}");
                    }
                });

                return true;
            }
            return false;
        }

        #endregion

        #region 公共查询接口

        /// <summary>
        /// 从内存池中按 DeviceKey 获取指定强类型的设备实例
        /// 例：var camera = DevicePoolManager.Instance.GetDevice&lt;ICamera&gt;("Cam_Top_01");
        /// </summary>
        public T GetDevice<T>(string deviceKey) where T : class, IDevice
        {
            if (_devicePool.TryGetValue(deviceKey, out var device))
            {
                return device as T;
            }
            return null;
        }

        /// <summary>
        /// 获取当前内存池中所有设备列表
        /// </summary>
        public IEnumerable<IDevice> GetAllDevices() => _devicePool.Values;

        /// <summary>
        /// 获取当前已加载的所有插件实例列表
        /// </summary>
        public IEnumerable<IHardwarePlugin> GetAllPlugins() => _plugins;

        #endregion
    }
}