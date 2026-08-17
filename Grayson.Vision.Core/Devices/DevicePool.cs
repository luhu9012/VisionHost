using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Devices
{
    /// <summary>
    /// 全局设备池实现。
    /// 承载插件加载、物理扫描、设备实例化、CRUD、持久化、工位领用管理。
    /// 这是一个进程级单例，供 StationHost 持有，UI 通过 IDevicePool 契约访问。
    /// </summary>
    public class DevicePool : IDevicePool
    {
        private readonly IDevicePluginManager _pluginManager;
        private readonly ConcurrentDictionary<string, IDevice> _devicePool = new ConcurrentDictionary<string, IDevice>();
        private readonly ConcurrentDictionary<string, string> _leaseMap = new ConcurrentDictionary<string, string>(); // deviceKey -> stationId
        private readonly object _initializeLock = new object();
        private bool _isInitialized;

        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// 底层设备插件管理器。
        /// </summary>
        public IDevicePluginManager PluginManager => _pluginManager;

        public event EventHandler<DeviceLeaseEventArgs> OnDeviceLeaseChanged;

        /// <summary>
        /// 设备状态变化兼容性事件。
        /// </summary>
        public event EventHandler<DeviceStateChangedEventArgs> OnDeviceStateChanged;

        public DevicePool(IDevicePluginManager pluginManager = null)
        {
            _pluginManager = pluginManager ?? new DevicePluginManager();
        }

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await Task.Run(() =>
            {
                lock (_initializeLock)
                {
                    if (_isInitialized) return;

                    _pluginManager.AutoLoadAllPlugins();
                    LoadConfiguredDevicesFromDb();

                    _isInitialized = true;
                }
            });
        }

        public List<DeviceInfo> ScanAllPhysicalDevices()
        {
            var list = new List<DeviceInfo>();
            foreach (var plugin in _pluginManager.LoadedPlugins)
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
                    // 忽略单个驱动扫描失败
                }
            }
            return list;
        }

        public async Task<Result<IDevice>> AddDeviceToPoolAndSaveAsync(DeviceInfo info, string userDeviceKey, string connectionString = "")
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(userDeviceKey)) return Result<IDevice>.Fail("逻辑名称不能为空!");
                if (_devicePool.ContainsKey(userDeviceKey)) return Result<IDevice>.Fail($"已存在 Key 为 [{userDeviceKey}] 的内存设备!");

                var plugin = _pluginManager.ResolvePlugin(info.Category, info.BrandName);
                if (plugin == null)
                    return Result<IDevice>.Fail($"未找到支持 [{info.BrandName}] - [{info.Category}] 的驱动插件!");

                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                if (configRepo.GetByKey(userDeviceKey) != null)
                    return Result<IDevice>.Fail($"数据库中已注册逻辑名称为 [{userDeviceKey}] 的设备，请更改 Key！");
                if (configRepo.GetByDeviceId(info.DeviceId) != null)
                    return Result<IDevice>.Fail($"数据库中已存在物理 ID 为 [{info.DeviceId}] 的设备配置，不能重复添加。");

                try
                {
                    var device = plugin.CreateDevice(info.DeviceId);
                    device.DeviceKey = userDeviceKey;

                    if (!string.IsNullOrEmpty(connectionString))
                        device.SetParam("ConnectionString", connectionString);

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

        public async Task<Result<IDevice>> CreateAndSaveManualDeviceAsync(DeviceCategory category, string brand, string deviceKey, string connectionString)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(deviceKey)) return Result<IDevice>.Fail("设备逻辑名称不能为空!");
                if (_devicePool.ContainsKey(deviceKey)) return Result<IDevice>.Fail($"内存设备池中已存在名称为 [{deviceKey}] 的设备!");

                var plugin = _pluginManager.ResolvePlugin(category, brand);
                if (plugin == null)
                    return Result<IDevice>.Fail($"未找到匹配品牌 [{brand}] 与类别 [{category}] 的驱动插件，请确保相关插件已正常加载！");

                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                if (configRepo.GetByKey(deviceKey) != null)
                    return Result<IDevice>.Fail($"数据库中已存在逻辑名称为 [{deviceKey}] 的记录!");

                try
                {
                    string manualDeviceId = $"{category}_{brand}_{DateTime.Now:yyyyMMddHHmmss}";

                    var device = plugin.CreateDevice(manualDeviceId);
                    device.DeviceKey = deviceKey;
                    if (!string.IsNullOrEmpty(connectionString))
                        device.SetParam("ConnectionString", connectionString);

                    var po = new DeviceConfigPo
                    {
                        DeviceKey = deviceKey,
                        DeviceId = manualDeviceId,
                        BrandName = brand,
                        Category = category,
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

                            var plugin = _pluginManager.ResolvePlugin(info.Category, info.BrandName);
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
                        return Result.Fail($"逻辑名称 [{device.DeviceKey}] 已被其他物理设备占用，请使用唯一的逻辑 Key！");

                    po.DeviceKey = device.DeviceKey;
                    if (newConnectionString != null)
                    {
                        po.ConnectionString = newConnectionString;
                        device.SetParam("ConnectionString", newConnectionString);
                    }
                    configRepo.Update(po);

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

        public bool RemoveDevice(string deviceKey)
        {
            if (_leaseMap.ContainsKey(deviceKey))
                return false; // 已被工位领用，不能删除

            if (_devicePool.TryRemove(deviceKey, out var device))
            {
                try
                {
                    device.Disconnect();
                    device.Dispose();
                }
                catch
                {
                    // 忽略释放异常
                }

                var configRepo = StorageFactory.CreateDeviceConfigRepository();
                var po = configRepo.GetByKey(deviceKey);
                if (po != null)
                    configRepo.Delete(po.Id);

                return true;
            }
            return false;
        }

        public IEnumerable<IDevice> GetAllDevices() => _devicePool.Values.ToArray();

        public IDevice GetDevice(string deviceKey)
        {
            _devicePool.TryGetValue(deviceKey, out var device);
            return device;
        }

        public bool ContainsDevice(string deviceKey) => _devicePool.ContainsKey(deviceKey);

        public bool LeaseDeviceToStation(string deviceKey, string stationId)
        {
            if (!_devicePool.ContainsKey(deviceKey)) return false;
            if (_leaseMap.ContainsKey(deviceKey)) return false; // 已被其他工位领用

            if (_leaseMap.TryAdd(deviceKey, stationId))
            {
                OnDeviceLeaseChanged?.Invoke(this, new DeviceLeaseEventArgs(deviceKey, stationId, true));
                return true;
            }
            return false;
        }

        public bool ReturnDeviceFromStation(string deviceKey, string stationId)
        {
            if (_leaseMap.TryGetValue(deviceKey, out var leaseStationId) && leaseStationId == stationId)
            {
                if (_leaseMap.TryRemove(deviceKey, out _))
                {
                    OnDeviceLeaseChanged?.Invoke(this, new DeviceLeaseEventArgs(deviceKey, stationId, false));
                    return true;
                }
            }
            return false;
        }

        public IEnumerable<string> GetLeasedDeviceKeys(string stationId)
        {
            return _leaseMap.Where(x => x.Value == stationId).Select(x => x.Key).ToArray();
        }

        public Result ConnectDevice(string deviceKey)
        {
            var device = GetDevice(deviceKey);
            if (device == null) return Result.Fail($"池中不存在设备 [{deviceKey}]");
            return device.Connect();
        }

        public Result DisconnectDevice(string deviceKey)
        {
            var device = GetDevice(deviceKey);
            if (device == null) return Result.Fail($"池中不存在设备 [{deviceKey}]");
            return device.Disconnect();
        }

        public void Dispose()
        {
            foreach (var device in _devicePool.Values)
            {
                try
                {
                    device.Disconnect();
                    device.Dispose();
                }
                catch
                {
                    // 忽略释放异常
                }
            }
            _devicePool.Clear();
            _leaseMap.Clear();
            _pluginManager.ShutdownAll();
        }

        #region 私有方法

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

                    var plugin = _pluginManager.ResolvePlugin(config.Category, config.BrandName);
                    if (plugin == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DevicePool] 驱动插件缺失: [{config.BrandName}] - [{config.Category}]，无法恢复设备 [{config.DeviceKey}]");
                        continue;
                    }

                    try
                    {
                        var device = plugin.CreateDevice(config.DeviceId);
                        if (device == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DevicePool] 驱动插件 [{plugin.BrandName}] 无法根据 DeviceId:[{config.DeviceId}] 创建设备实例，返回了 null。");
                            continue;
                        }

                        device.DeviceKey = config.DeviceKey;

                        if (!string.IsNullOrEmpty(config.ConnectionString))
                            device.SetParam("ConnectionString", config.ConnectionString);

                        device.StateChanged += (s, state) =>
                            OnDeviceStateChanged?.Invoke(this, new DeviceStateChangedEventArgs(null, config.DeviceKey, DeviceState.Disconnected, state, null));

                        _devicePool[config.DeviceKey] = device;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DevicePool] 复原设备 [{config.DeviceKey}] (SN:{config.DeviceId}) 失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DevicePool] 从数据库恢复设备配置失败: {ex.Message}");
            }
        }

        #endregion
    }
}
