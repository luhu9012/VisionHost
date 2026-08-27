//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TriggerSourceConfig.cs
// 说 明: 触发源配置模型——工位级物理接线约定（不随产品配方变化）。
//        挂在 StationConfigModel.TriggerSource 字段，由 StationManageView 配置入口编辑。
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Station.Triggers
{
    /// <summary>
    /// 触发源强类型配置。
    /// 此模型是工位物理接线的约定——PLC 点位/IO 通道接在工位上，
    /// 不随产品/配方切换而变化，变更频率低。
    /// </summary>
    [Serializable]
    public class TriggerSourceConfig
    {
        /// <summary>触发源类型（默认 Manual，与旧行为兼容）</summary>
        public TriggerSourceType SourceType { get; set; } = TriggerSourceType.Manual;

        /// <summary>边沿检测模式（PlcBit 源默认上升沿，Manual/Timer 源 None）</summary>
        public TriggerEdge Edge { get; set; } = TriggerEdge.Rising;

        /// <summary>
        /// 防抖时间（毫秒）。
        /// 信号在此窗口内的连续跳变视为一次有效触发，过滤机械抖动/噪声。
        /// 默认 0 表示不防抖。
        /// </summary>
        public int DebounceMs { get; set; } = 0;

        /// <summary>
        /// 丢帧策略——触发频率超过检测周期时的处理方式。
        /// 默认 DropOldest（保留最新信号）。
        /// </summary>
        public DropStrategy DropStrategy { get; set; } = DropStrategy.DropOldest;

        // ===== Timer 源专用 =====

        /// <summary>定时器周期（毫秒），仅 SourceType=Timer 时有效。默认 1000ms。</summary>
        public int TimerIntervalMs { get; set; } = 1000;

        // ===== PlcBit 源专用 =====

        /// <summary>PLC 设备 ID（设备池逻辑 Key），仅 SourceType=PlcBit 时有效。</summary>
        public string PlcDeviceId { get; set; }

        /// <summary>PLC 触发点位地址（如 "M0.0"、"D100.0"），仅 SourceType=PlcBit 时有效。</summary>
        public string PlcAddress { get; set; }

        /// <summary>PLC 轮询间隔（毫秒），仅 SourceType=PlcBit 时有效。默认 50ms。</summary>
        public int PollIntervalMs { get; set; } = 50;

        // ===== 统计开关 =====

        /// <summary>是否启用节拍统计（最近触发时间/平均间隔/触发次数/丢帧计数）</summary>
        public bool EnableStats { get; set; } = true;

        /// <summary>
        /// 生成人类可读的摘要文本（用于 UI 监控页展示，如"PLC 位 | PLC_001 | M0.0 | 上升沿"）。
        /// </summary>
        public string ToSummary()
        {
            switch (SourceType)
            {
                case TriggerSourceType.Manual:
                    return "手动触发";
                case TriggerSourceType.Timer:
                    return $"定时器 | {TimerIntervalMs}ms";
                case TriggerSourceType.PlcBit:
                    return $"PLC 位 | {PlcDeviceId ?? "?"} | {PlcAddress ?? "?"} | {Edge}";
                default:
                    return SourceType.ToString();
            }
        }
    }
}
