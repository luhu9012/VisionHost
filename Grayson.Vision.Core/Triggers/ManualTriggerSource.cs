//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ManualTriggerSource.cs
// 说 明: 手动触发源——UI 按钮点击调用 ManualTrigger()。
//        默认配置，与旧版"单次触发"按钮行为完全一致，无后台轮询。
//===================================================================================

using System;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Core.Triggers
{
    /// <summary>
    /// 手动触发源：无后台轮询，纯被动等待 UI 调用 ManualTrigger()。
    /// 适用于调试模式或无自动节拍信号的离线检测场景。
    /// </summary>
    public class ManualTriggerSource : TriggerSourceBase
    {
        public override TriggerSourceType SourceType => TriggerSourceType.Manual;

        public ManualTriggerSource(string stationId) : base(stationId)
        {
        }

        public override void Configure(TriggerSourceConfig config, IDevicePool devicePool = null)
        {
            // 手动源不需要复杂配置，仅保存引用
            SetConfig(config ?? new TriggerSourceConfig());
        }

        public override void Start()
        {
            // 手动源无后台任务，Start 只标记状态
            IsRunning = true;
        }

        public override void Stop()
        {
            IsRunning = false;
        }
    }
}
