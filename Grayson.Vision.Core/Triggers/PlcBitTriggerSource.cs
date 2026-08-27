//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: PlcBitTriggerSource.cs
// 说 明: PLC 通讯位触发源——轮询指定 PLC 地址的 bool 位，边沿检测后触发。
//        产线最常用的触发方式：PLC 发脉冲信号 → 上位机检测到 → 执行检测。
//===================================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Core.Triggers
{
    /// <summary>
    /// PLC 通讯位触发源：按配置周期轮询 PLC 指定地址的 bool 位，
    /// 经过边沿检测（默认上升沿）+ 防抖 + 丢帧策略后产生有效触发。
    /// <para>依赖 IPlc.ReadBit(addr) 契约，需 PLC 插件补全实际读取能力。</para>
    /// </summary>
    public class PlcBitTriggerSource : TriggerSourceBase
    {
        public override TriggerSourceType SourceType => TriggerSourceType.PlcBit;

        private IPlc _plc;
        private string _plcAddress;
        private Timer _pollTimer;
        private IDevicePool _devicePool;
        private int _consecutiveErrors;
        private const int MaxConsecutiveErrors = 5;

        public PlcBitTriggerSource(string stationId) : base(stationId)
        {
        }

        public override void Configure(TriggerSourceConfig config, IDevicePool devicePool = null)
        {
            SetConfig(config ?? new TriggerSourceConfig());
            _devicePool = devicePool;
            _plcAddress = config?.PlcAddress;
            _consecutiveErrors = 0;

            if (string.IsNullOrEmpty(_plcAddress))
            {
                LogBus.Warn("TriggerSource", $"[{StationId}] PlcBit 触发源配置地址为空，将无法轮询");
            }

            if (config != null && !string.IsNullOrEmpty(config.PlcDeviceId) && _devicePool != null)
            {
                var device = _devicePool.GetDevice(config.PlcDeviceId);
                _plc = device as IPlc;

                if (_plc == null && device != null)
                {
                    LogBus.Warn("TriggerSource",
                        $"[{StationId}] 设备 [{config.PlcDeviceId}] 不是 IPlc，PlcBit 触发源将无法轮询");
                }
                else if (_plc == null)
                {
                    LogBus.Warn("TriggerSource",
                        $"[{StationId}] 设备池未找到 PLC 设备 [{config.PlcDeviceId}]，PlcBit 触发源待设备连接后可重新配置");
                }
            }
        }

        public override void Start()
        {
            if (IsRunning) return;
            if (_plc == null)
            {
                LogBus.Warn("TriggerSource",
                    $"[{StationId}] PlcBit 触发源未就绪（PLC 设备未连接），Start 将被忽略");
                RaiseError("PlcBit.Start", new InvalidOperationException("PLC 设备未就绪"), recoverable: true);
                return;
            }

            IsRunning = true;
            var interval = Config.PollIntervalMs > 0 ? Config.PollIntervalMs : 50;
            _pollTimer = new Timer(OnPollTick, null, interval, interval);

            LogBus.Info("TriggerSource",
                $"[{StationId}] PlcBit 触发源已启动：设备={Config.PlcDeviceId}，地址={_plcAddress}，" +
                $"轮询={interval}ms，边沿={Config.Edge}，防抖={Config.DebounceMs}ms");
        }

        public override void Stop()
        {
            IsRunning = false;
            var timer = _pollTimer;
            _pollTimer = null;
            if (timer != null)
            {
                // .NET Framework 4.7.2 的 Timer.Dispose 无 TimeSpan 重载
                timer.Change(Timeout.Infinite, Timeout.Infinite);
                timer.Dispose();
            }
            LogBus.Info("TriggerSource", $"[{StationId}] PlcBit 触发源已停止");
        }

        private void OnPollTick(object state)
        {
            if (!IsRunning || _plc == null) return;

            try
            {
                // 同步读取 PLC 位（IPlc.ReadBit 是同步方法，在 ThreadPool 线程执行）
                var result = _plc.ReadBit(_plcAddress);

                if (result == null || !result.Success)
                {
                    _consecutiveErrors++;
                    if (_consecutiveErrors == MaxConsecutiveErrors)
                    {
                        RaiseError("PlcBit.ReadBit",
                            new Exception($"连续 {MaxConsecutiveErrors} 次读取失败: {result?.Message ?? "null"}"),
                            recoverable: true);
                    }
                    return;
                }

                // 读取成功——重置错误计数
                _consecutiveErrors = 0;

                // 传入基类做边沿检测+防抖+丢帧策略
                RaiseSignal(result.Data);
            }
            catch (Exception ex)
            {
                _consecutiveErrors++;
                if (_consecutiveErrors >= MaxConsecutiveErrors)
                {
                    RaiseError("PlcBit.Poll", ex, recoverable: true);
                }
            }
        }

        public override void Dispose()
        {
            Stop();
            base.Dispose();
        }
    }
}
