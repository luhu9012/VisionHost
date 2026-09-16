using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Devices
{
    /// <summary>
    /// 工位级设备管理器默认实现：
    /// - 统一维护逻辑设备 -> 物理设备实例映射
    /// - 支持独占/共享只读租赁
    /// - 统一设备状态机转换与事件上报
    /// </summary>
    public class DeviceManager : IDeviceManager
    {
        private class DeviceEntry
        {
            public IDevice Device;
            public DeviceAccessMode DefaultAccessMode;
            public IDeviceLease CurrentExclusiveLease;
            public long SharedReadOnlyCount;
        }

        private readonly ConcurrentDictionary<string, DeviceEntry> _devices
            = new ConcurrentDictionary<string, DeviceEntry>();

        private readonly ConcurrentDictionary<string, DeviceLease> _activeLeases
            = new ConcurrentDictionary<string, DeviceLease>();

        private readonly SemaphoreSlim _leaseLock = new SemaphoreSlim(1, 1);

        public string StationId { get; }

        public DeviceManager(string stationId)
        {
            StationId = stationId ?? throw new ArgumentNullException(nameof(stationId));
        }

        public event EventHandler<DeviceStateChangedEventArgs> DeviceStateChanged;

        public void RegisterDevice(string logicalDeviceKey, IDevice device,
            DeviceAccessMode defaultAccessMode = DeviceAccessMode.Exclusive)
        {
            if (string.IsNullOrEmpty(logicalDeviceKey) || device == null) return;

            _devices[logicalDeviceKey] = new DeviceEntry
            {
                Device = device,
                DefaultAccessMode = defaultAccessMode
            };

            device.StateChanged += (s, state) => OnDeviceStateChanged(logicalDeviceKey, state);

            // ★ 物理身份对账：只打类型名（如 "HikCamera"）时，日志里判不出"这条链到底用了哪台相机"。
            //   设备键 = 品牌_类别_SN_用户自定义名；DeviceId 通常就是 SN。
            LogBus.Info("DeviceManager",
                $"[{StationId}] 注册逻辑设备 [{logicalDeviceKey}] -> {device.GetType().Name}" +
                $" (设备键: {device.DeviceKey ?? "-"} | SN/物理ID: {device.DeviceId ?? "-"})");
        }

        public bool UnregisterDevice(string logicalDeviceKey)
        {
            if (string.IsNullOrEmpty(logicalDeviceKey)) return false;

            if (_devices.TryRemove(logicalDeviceKey, out var entry))
            {
                entry.CurrentExclusiveLease?.Release();
                entry.Device?.Disconnect();
                return true;
            }
            return false;
        }

        public IReadOnlyDictionary<string, DeviceState> GetDeviceStates()
        {
            return _devices.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Device?.State ?? DeviceState.Uninitialized);
        }

        public async Task<Result<IDeviceLease>> AcquireAsync(string workOrderId, string logicalDeviceKey,
            DeviceAccessMode? accessMode = null, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(logicalDeviceKey))
                return Result<IDeviceLease>.Fail("逻辑设备名为空");

            if (!_devices.TryGetValue(logicalDeviceKey, out var entry))
                return Result<IDeviceLease>.Fail($"逻辑设备 [{logicalDeviceKey}] 未注册");

            var mode = accessMode ?? entry.DefaultAccessMode;
            var device = entry.Device;

            if (device?.State == DeviceState.Error || device?.State == DeviceState.Locked)
                return Result<IDeviceLease>.Fail($"设备 [{logicalDeviceKey}] 当前处于 {device.State}，无法申请");

            var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            try
            {
                await _leaseLock.WaitAsync(cts.Token).ConfigureAwait(false);

                if (mode == DeviceAccessMode.Exclusive)
                {
                    if (entry.CurrentExclusiveLease != null && entry.CurrentExclusiveLease.IsValid)
                    {
                        _leaseLock.Release();
                        return Result<IDeviceLease>.Fail($"设备 [{logicalDeviceKey}] 已被独占占用");
                    }
                    if (Interlocked.Read(ref entry.SharedReadOnlyCount) > 0)
                    {
                        _leaseLock.Release();
                        return Result<IDeviceLease>.Fail($"设备 [{logicalDeviceKey}] 存在共享读占用，无法独占");
                    }

                    var lease = new DeviceLease(null, StationId, workOrderId, logicalDeviceKey, mode, device, OnLeaseReleased);
                    entry.CurrentExclusiveLease = lease;
                    _activeLeases[lease.LeaseId] = lease;
                    SetDeviceState(device, DeviceState.Busy, $"被工单 [{workOrderId}] 独占");
                    _leaseLock.Release();
                    return Result<IDeviceLease>.Ok(lease);
                }
                else
                {
                    if (entry.CurrentExclusiveLease != null && entry.CurrentExclusiveLease.IsValid)
                    {
                        _leaseLock.Release();
                        return Result<IDeviceLease>.Fail($"设备 [{logicalDeviceKey}] 已被独占，无法共享读取");
                    }

                    Interlocked.Increment(ref entry.SharedReadOnlyCount);
                    var lease = new DeviceLease(null, StationId, workOrderId, logicalDeviceKey, mode, device, OnLeaseReleased);
                    _activeLeases[lease.LeaseId] = lease;
                    _leaseLock.Release();
                    return Result<IDeviceLease>.Ok(lease);
                }
            }
            catch (OperationCanceledException)
            {
                return Result<IDeviceLease>.Fail($"申请设备 [{logicalDeviceKey}] 超时");
            }
        }

        public Task ReleaseAsync(IDeviceLease lease)
        {
            (lease as DeviceLease)?.Release();
            return Task.CompletedTask;
        }

        public async Task<Result> OpenAllAsync()
        {
            foreach (var kv in _devices)
            {
                var device = kv.Value.Device;
                if (device == null) continue;

                if (device.State == DeviceState.Connected || device.State == DeviceState.Opened)
                    continue;

                try
                {
                    var result = await Task.Run(() => device.Connect()).ConfigureAwait(false);
                    if (result?.Success == true)
                    {
                        SetDeviceState(device, DeviceState.Opened, "OpenAll 成功");
                    }
                    else
                    {
                        SetDeviceState(device, DeviceState.Error, result?.Message ?? "连接失败");
                    }
                }
                catch (Exception ex)
                {
                    SetDeviceState(device, DeviceState.Error, $"连接异常: {ex.Message}");
                }
            }
            return Result.Ok();
        }

        public async Task CloseAllAsync()
        {
            foreach (var lease in _activeLeases.Values.ToList())
            {
                lease.Release();
            }
            _activeLeases.Clear();

            foreach (var kv in _devices)
            {
                var device = kv.Value.Device;
                if (device == null) continue;
                try
                {
                    await Task.Run(() => device.Disconnect()).ConfigureAwait(false);
                    SetDeviceState(device, DeviceState.Closed, "CloseAll");
                }
                catch (Exception ex)
                {
                    LogBus.Error("DeviceManager", $"[{StationId}] 关闭设备 [{kv.Key}] 失败: {ex.Message}", ex);
                }
            }
        }

        public async Task<Result> ReconnectAsync(string logicalDeviceKey)
        {
            if (!_devices.TryGetValue(logicalDeviceKey, out var entry) || entry.Device == null)
                return Result.Fail($"设备 [{logicalDeviceKey}] 未注册");

            var device = entry.Device;
            if (entry.CurrentExclusiveLease != null && entry.CurrentExclusiveLease.IsValid)
                return Result.Fail($"设备 [{logicalDeviceKey}] 仍被独占，无法重连");

            try
            {
                SetDeviceState(device, DeviceState.Reconnecting, "开始重连");
                await Task.Run(() => device.Disconnect()).ConfigureAwait(false);
                var result = await Task.Run(() => device.Connect()).ConfigureAwait(false);
                if (result?.Success == true)
                {
                    SetDeviceState(device, DeviceState.Opened, "重连成功");
                    return Result.Ok();
                }
                else
                {
                    SetDeviceState(device, DeviceState.Error, result?.Message ?? "重连失败");
                    return Result.Fail(result?.Message ?? "重连失败");
                }
            }
            catch (Exception ex)
            {
                SetDeviceState(device, DeviceState.Error, $"重连异常: {ex.Message}");
                return Result.Fail($"重连异常: {ex.Message}", ex: ex);
            }
        }

        public IDevice GetDeviceInstance(string logicalDeviceKey)
        {
            return _devices.TryGetValue(logicalDeviceKey, out var entry) ? entry.Device : null;
        }

        public async Task<Result> HeartbeatAllAsync()
        {
            foreach (var kv in _devices)
            {
                var device = kv.Value.Device;
                if (device == null) continue;
                if (device.State == DeviceState.Error || device.State == DeviceState.Locked) continue;

                try
                {
                    var result = await Task.Run(() => device.Heartbeat()).ConfigureAwait(false);
                    if (result?.Success != true)
                    {
                        SetDeviceState(device, DeviceState.Error, $"心跳失败: {result?.Message}");
                    }
                }
                catch (Exception ex)
                {
                    SetDeviceState(device, DeviceState.Error, $"心跳异常: {ex.Message}");
                }
            }
            return Result.Ok();
        }

        public IReadOnlyCollection<IDeviceLease> GetActiveLeases()
        {
            return new List<IDeviceLease>(_activeLeases.Values);
        }

        private void OnLeaseReleased(DeviceLease lease)
        {
            if (lease == null) return;

            _activeLeases.TryRemove(lease.LeaseId, out _);

            if (_devices.TryGetValue(lease.LogicalDeviceKey, out var entry))
            {
                if (lease.AccessMode == DeviceAccessMode.Exclusive)
                {
                    if (entry.CurrentExclusiveLease == lease)
                        entry.CurrentExclusiveLease = null;
                }
                else
                {
                    Interlocked.Decrement(ref entry.SharedReadOnlyCount);
                    if (entry.SharedReadOnlyCount < 0)
                        entry.SharedReadOnlyCount = 0;
                }

                var device = entry.Device;
                if (device != null && device.State != DeviceState.Error && device.State != DeviceState.Locked)
                {
                    SetDeviceState(device, DeviceState.Opened, $"工单 [{lease.WorkOrderId}] 释放设备");
                }
            }
        }

        private void OnDeviceStateChanged(string logicalDeviceKey, DeviceState newState)
        {
            DeviceStateChanged?.Invoke(this,
                new DeviceStateChangedEventArgs(StationId, logicalDeviceKey, DeviceState.Disconnected, newState, null));
        }

        private void SetDeviceState(IDevice device, DeviceState newState, string reason)
        {
            if (device == null) return;
            var oldState = device.State;
            if (oldState == newState) return;
            device.State = newState;
            DeviceStateChanged?.Invoke(this,
                new DeviceStateChangedEventArgs(StationId, null, oldState, newState, reason));
        }

        public void Dispose()
        {
            CloseAllAsync().Wait(TimeSpan.FromSeconds(10));
            _leaseLock.Dispose();
        }
    }
}
