//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TriggerOptionCatalog.cs
// 说 明: 触发信号配置的下拉选项目录——ComboBox 显示中文名 + 悬停 ToolTip 说明。
//        通过 {x:Static} 直接绑定静态字段，无需 ViewModel 额外暴露属性。
//        每个选项：Value=枚举实例（SelectedValuePath 用）、DisplayName=中文显示、
//        ToolTip=悬停说明。
//===================================================================================

using System.Collections.Generic;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.WpfUI.Common
{
    /// <summary>触发配置下拉的单个选项</summary>
    public sealed class TriggerOption
    {
        /// <summary>选项对应的枚举值（用于 SelectedValue 双向绑定）</summary>
        public object Value { get; set; }

        /// <summary>下拉显示的中文名</summary>
        public string DisplayName { get; set; }

        /// <summary>鼠标悬停时的说明文字</summary>
        public string ToolTip { get; set; }
    }

    /// <summary>触发信号配置的选项目录（触发源类型 / 边沿检测 / 丢帧策略）</summary>
    public static class TriggerOptionCatalog
    {
        /// <summary>触发源类型选项</summary>
        public static readonly IReadOnlyList<TriggerOption> SourceTypeOptions = new List<TriggerOption>
        {
            new TriggerOption
            {
                Value = TriggerSourceType.Manual,
                DisplayName = "手动触发",
                ToolTip = "由操作员在监控页点击按钮触发。适用于调试、试机、首件确认。",
            },
            new TriggerOption
            {
                Value = TriggerSourceType.Timer,
                DisplayName = "定时器",
                ToolTip = "按固定周期自动触发，无需外部信号。适用于流水线匀速运动、无 PLC 同步信号的离线检测场景。",
            },
            new TriggerOption
            {
                Value = TriggerSourceType.PlcBit,
                DisplayName = "PLC 位",
                ToolTip = "按轮询间隔读取 PLC 指定地址的 bool 位，边沿跳变时触发一次。需 PLC 插件已实现 ReadBit。",
            },
        };

        /// <summary>边沿检测模式选项</summary>
        public static readonly IReadOnlyList<TriggerOption> EdgeOptions = new List<TriggerOption>
        {
            new TriggerOption
            {
                Value = TriggerEdge.None,
                DisplayName = "不检测",
                ToolTip = "不做边沿检测，信号为 true 时持续触发。慎用——定时器源强制为此值。",
            },
            new TriggerOption
            {
                Value = TriggerEdge.Rising,
                DisplayName = "上升沿",
                ToolTip = "信号由 0→1 跳变时触发一次，是最常用的\"一个物料一次检测\"语义。",
            },
            new TriggerOption
            {
                Value = TriggerEdge.Falling,
                DisplayName = "下降沿",
                ToolTip = "信号由 1→0 跳变时触发一次，用于下落、离开类场景。",
            },
            new TriggerOption
            {
                Value = TriggerEdge.Both,
                DisplayName = "双边沿",
                ToolTip = "上升沿和下降沿都触发，适用于需要双向计数的场景。",
            },
        };

        /// <summary>丢帧策略选项（触发频率超过检测周期时）</summary>
        public static readonly IReadOnlyList<TriggerOption> DropStrategyOptions = new List<TriggerOption>
        {
            new TriggerOption
            {
                Value = DropStrategy.DropOldest,
                DisplayName = "丢弃旧信号",
                ToolTip = "执行中收到新信号时，丢弃已积压的旧信号，完成后响应最新信号。适合实时性要求高的场景（默认）。",
            },
            new TriggerOption
            {
                Value = DropStrategy.DropNewest,
                DisplayName = "丢弃新信号",
                ToolTip = "执行中收到的新信号直接丢弃并计数，完成后不再补发。适合不可丢拍的场景。",
            },
            new TriggerOption
            {
                Value = DropStrategy.QueueOne,
                DisplayName = "排队 1 帧",
                ToolTip = "执行中只缓存 1 个待执行信号，完成后自动补一次，再来的信号丢弃。",
            },
        };
    }
}
