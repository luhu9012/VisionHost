//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TriggerSourceFactory.cs
// 说 明: 触发源工厂——根据配置创建对应 ITriggerSource 实例。
//===================================================================================

using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Core.Triggers
{
    /// <summary>
    /// 触发源工厂：根据 TriggerSourceConfig.SourceType 创建对应的 ITriggerSource 实例。
    /// 新增触发方式时在此扩展 case 即可。
    /// </summary>
    public static class TriggerSourceFactory
    {
        /// <summary>
        /// 创建触发源实例并完成配置。
        /// </summary>
        /// <param name="stationId">工位 ID</param>
        /// <param name="config">触发源配置（null 时默认 Manual）</param>
        /// <param name="devicePool">设备池（PlcBit 源用）</param>
        /// <returns>已配置的 ITriggerSource 实例</returns>
        public static ITriggerSource Create(
            string stationId,
            TriggerSourceConfig config,
            Contracts.Devices.Services.IDevicePool devicePool = null)
        {
            if (string.IsNullOrEmpty(stationId))
                throw new ArgumentNullException(nameof(stationId));

            config = config ?? new TriggerSourceConfig();

            ITriggerSource source;
            switch (config.SourceType)
            {
                case TriggerSourceType.Manual:
                    source = new ManualTriggerSource(stationId);
                    break;
                case TriggerSourceType.Timer:
                    source = new TimerTriggerSource(stationId);
                    break;
                case TriggerSourceType.PlcBit:
                    source = new PlcBitTriggerSource(stationId);
                    break;
                default:
                    LogBus.Warn("TriggerSource",
                        $"[{stationId}] 未知触发源类型 {config.SourceType}，回退为 Manual");
                    source = new ManualTriggerSource(stationId);
                    break;
            }

            source.Configure(config, devicePool);
            LogBus.Info("TriggerSource",
                $"[{stationId}] 创建触发源: {source.SourceType} | {config.ToSummary()}");

            return source;
        }
    }
}
