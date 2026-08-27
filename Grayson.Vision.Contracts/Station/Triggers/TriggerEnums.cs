//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: TriggerEnums.cs
// 说 明: 触发源相关枚举定义
//        触发源是工位"信号从哪来"的抽象，与调度器"执行模式"正交不冲突。
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Station.Triggers
{
    /// <summary>
    /// 触发源类型——决定信号从何处来。
    /// 新增触发方式时在此扩展，对应实现 ITriggerSource。
    /// </summary>
    public enum TriggerSourceType
    {
        /// <summary>手动触发（UI 按钮点击）——默认值，与当前行为完全一致</summary>
        Manual = 0,

        /// <summary>定时器触发（固定周期自动执行）——可用于无 PLC 的离线检测场景</summary>
        Timer = 1,

        /// <summary>PLC 通讯位触发（轮询指定 PLC 地址的 bool 位，边沿检测后触发）</summary>
        PlcBit = 2,

        // TODO: 以下类型预留，待插件能力补全后实现
        // Tcp = 3,     // TCP/UDP 指令触发（接收指定报文后触发）
        // OpcUa = 4,   // OPC UA 订阅节点变化触发
        // IoCard = 5,  // IO 采集卡硬件输入点触发（需要 IIoDevice 轮询）
    }

    /// <summary>
    /// 信号边沿检测模式——决定什么电平变化才算一次有效触发。
    /// 工业场景中 PLC 触发位通常用脉冲（上升沿）而非持续高电平，避免重复触发。
    /// </summary>
    public enum TriggerEdge
    {
        /// <summary>不检测边沿——信号为 true 时持续触发（慎用，仅特殊场景）</summary>
        None = 0,

        /// <summary>上升沿触发——信号 false→true 变化时触发一次（最常用）</summary>
        Rising = 1,

        /// <summary>下降沿触发——信号 true→false 变化时触发一次</summary>
        Falling = 2,

        /// <summary>双沿触发——上升沿和下降沿都触发（如计数场景）</summary>
        Both = 3,
    }

    /// <summary>
    /// 触发频率超过检测周期时的丢帧策略——决定丢弃旧请求还是新请求。
    /// 例如检测一次需 200ms，PLC 触发周期 50ms，则会有 3 次信号在执行中被忽略。
    /// </summary>
    public enum DropStrategy
    {
        /// <summary>丢弃最旧的——保留最新信号，执行完成后响应当前最新信号（默认，适合实时性要求高的场景）</summary>
        DropOldest = 0,

        /// <summary>丢弃最新的——执行中收到的新信号被丢弃，等当前执行完成后再响应（适合不可丢拍的场景）</summary>
        DropNewest = 1,

        /// <summary>只排一帧——执行中只缓存 1 个待执行信号，多余丢弃（与 DropNewest 类似但语义更明确）</summary>
        QueueOne = 2,
    }
}
