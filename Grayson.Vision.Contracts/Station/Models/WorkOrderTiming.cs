//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: WorkOrderTiming.cs
// 说 明: 工单时间分解模型
//        将工单执行的各个阶段的耗时进行结构化分解
//        目的是清晰表示时间流向，便于性能分析和瓶颈识别
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工单执行全过程的时间分解模型
    /// 
    /// 时间栈结构 (从创建到完成的全链路):
    /// 
    /// 创建 (Created)
    ///   ↓
    /// [QueueWaitTime] ← 在队列中等待的时间
    ///   ↓
    /// 开始 (Started)
    ///   ↓
    /// [Hardware 硬件阶段] ← 工位设备交互
    ///   ├─ TriggerSetupTime      (触发信号交互)
    ///   ├─ CameraAcquisitionTime (图像采集)
    ///   └─ ProcessingTime        (AI 推理处理)
    ///   ↓
    /// [PostProcessTime] ← 后处理 (数据汇总、图像编码等)
    ///   ↓
    /// 完成 (Completed)
    /// 
    /// 总耗时 = QueueWaitTime + (Trigger + Acquisition + Processing) + PostProcessTime
    /// 
    /// 关键指标:
    /// - TotalQueueAndProcessTimeMs: 从创建到完成的总耗时
    /// - Hardware.Total: 硬件交互的纯耗时（不含队列等待）
    /// - 瓶颈识别: 哪个阶段耗时最长
    /// </summary>
    public class WorkOrderTiming
    {
        /// <summary>
        /// 队列等待时间 (毫秒)
        /// 
        /// 定义: 从工单创建到工位开始处理这个工单之间的时间
        /// 
        /// 影响因素:
        /// - 前面有多少个工单在排队
        /// - 工位是否在处理其他工单
        /// - 系统调度延迟
        /// 
        /// 0 表示工单创建后立即开始处理
        /// 大值表示工位繁忙或系统响应慢
        /// </summary>
        public double QueueWaitTimeMs { get; set; }

        /// <summary>
        /// 硬件交互各阶段的耗时分解
        /// 
        /// 这是工单执行的核心阶段，包含三个子阶段
        /// </summary>
        public class HardwarePhase
        {
            /// <summary>
            /// 触发信号交互耗时 (毫秒)
            /// 
            /// 包含:
            /// - 向 PLC/工位发送触发信号
            /// - 等待 PLC 确认就绪
            /// - 设置工位参数（如相机曝光、增益等）
            /// 
            /// 典型值: 5-50ms
            /// </summary>
            public double TriggerSetupTimeMs { get; set; }

            /// <summary>
            /// 图像采集耗时 (毫秒)
            /// 
            /// 包含:
            /// - 相机开始曝光
            /// - 等待曝光完成
            /// - 图像数据传输到计算机
            /// 
            /// 典型值: 30-100ms (取决于相机帧率和分辨率)
            /// </summary>
            public double CameraAcquisitionTimeMs { get; set; }

            /// <summary>
            /// AI 推理处理耗时 (毫秒)
            /// 
            /// 包含:
            /// - 图像预处理（缩放、标准化等）
            /// - 模型推理
            /// - 结果后处理（NMS、阈值过滤等）
            /// 
            /// 典型值: 50-500ms (取决于模型复杂度和硬件)
            /// 
            /// 性能优化重点: 这通常是最大的时间消耗
            /// </summary>
            public double ProcessingTimeMs { get; set; }

            /// <summary>
            /// 本硬件阶段的总耗时
            /// 
            /// 计算: TriggerSetupTimeMs + CameraAcquisitionTimeMs + ProcessingTimeMs
            /// 
            /// 此值由 Total 属性自动计算，不需手动赋值
            /// </summary>
            public double Total
            {
                get => TriggerSetupTimeMs + CameraAcquisitionTimeMs + ProcessingTimeMs;
            }

            /// <summary>
            /// 验证硬件阶段数据的一致性
            /// 
            /// 规则:
            /// - 所有时间字段应 ≥ 0
            /// - 总时间应等于三个子阶段之和
            /// </summary>
            public bool IsConsistent
            {
                get =>
                    TriggerSetupTimeMs >= 0 &&
                    CameraAcquisitionTimeMs >= 0 &&
                    ProcessingTimeMs >= 0;
            }
        }

        /// <summary>
        /// 硬件交互阶段的时间分解 (见 HardwarePhase 说明)
        /// </summary>
        public HardwarePhase Hardware { get; set; } = new HardwarePhase();

        /// <summary>
        /// 工位周期时间 (毫秒)
        /// 
        /// 定义: 从工位接收触发信号到输出最终结果的硬件执行时间
        /// 
        /// 计算: 
        /// 通常 ~= Hardware.Total
        /// 但可能因为有并行步骤或重试而不完全相等
        /// 
        /// 用途:
        /// - 评估工位产能 (每分钟可处理的工单数)
        /// - 与标准周期时间对标
        /// - 成为生产计划的约束条件
        /// </summary>
        public double StationCycleTimeMs { get; set; }

        /// <summary>
        /// 后处理耗时 (毫秒)
        /// 
        /// 包含:
        /// - 结果数据序列化
        /// - 图像编码和保存
        /// - 数据库写入
        /// - 消息队列发送
        /// 
        /// 典型值: 10-100ms
        /// 
        /// 用途: 识别 I/O 瓶颈
        /// </summary>
        public double PostProcessTimeMs { get; set; }

        /// <summary>
        /// 总耗时 (毫秒)
        /// 
        /// 定义: 从工单创建到完全处理完的端到端时间
        /// 
        /// 计算:
        /// TotalElapsedMs = QueueWaitTimeMs 
        ///                + Hardware.Total 
        ///                + PostProcessTimeMs
        /// 
        /// 此值由 Total 属性自动计算，不需手动赋值
        /// 
        /// 示例:
        /// QueueWaitTime: 50ms (队列等待)
        /// Hardware: 180ms (硬件操作)
        /// PostProcess: 20ms (数据处理)
        /// Total: 250ms (端到端)
        /// </summary>
        public double TotalElapsedMs
        {
            get => QueueWaitTimeMs + Hardware.Total + PostProcessTimeMs;
        }

        /// <summary>
        /// 验证时间数据的内部一致性
        /// 
        /// 规则:
        /// 1. 所有时间字段应 ≥ 0（非负）
        /// 2. 硬件阶段应该一致
        /// 3. 总耗时不应小于任何单个阶段的耗时
        /// 4. 工位周期时间应该合理（通常接近硬件总耗时）
        /// </summary>
        public bool IsConsistent
        {
            get
            {
                // 所有时间都应非负
                if (QueueWaitTimeMs < 0 || StationCycleTimeMs < 0 || PostProcessTimeMs < 0)
                    return false;

                // 硬件阶段应一致
                if (!Hardware.IsConsistent)
                    return false;

                // 总耗时应该满足逻辑关系
                // 总耗时应 ≥ 每个阶段的耗时
                if (TotalElapsedMs < Hardware.Total || TotalElapsedMs < PostProcessTimeMs)
                    return false;

                // 工位周期时间应该与硬件耗时接近
                // 允许 10% 的偏差（考虑并行处理）
                double maxCycleDiff = Hardware.Total * 0.1 + 10;  // 最多差 10% + 10ms
                if (Math.Abs(StationCycleTimeMs - Hardware.Total) > maxCycleDiff)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// 工厂方法: 创建一个从两个时间戳计算的工单计时
        /// </summary>
        public static WorkOrderTiming CreateFromTimeSpan(
            DateTime createdAt,
            DateTime startedAt,
            DateTime completedAt,
            double hardwareTimeMs,
            double postProcessTimeMs)
        {
            return new WorkOrderTiming
            {
                QueueWaitTimeMs = (startedAt - createdAt).TotalMilliseconds,
                Hardware = new HardwarePhase
                {
                    TriggerSetupTimeMs = hardwareTimeMs * 0.1,      // 假设 10% 用于触发
                    CameraAcquisitionTimeMs = hardwareTimeMs * 0.3, // 假设 30% 用于采集
                    ProcessingTimeMs = hardwareTimeMs * 0.6         // 假设 60% 用于处理
                },
                StationCycleTimeMs = hardwareTimeMs,
                PostProcessTimeMs = postProcessTimeMs
            };
        }

        /// <summary>
        /// 获取最慢的单个阶段（用于性能分析）
        /// </summary>
        public string GetBottleneck()
        {
            double[] times = new[]
            {
                QueueWaitTimeMs,
                Hardware.TriggerSetupTimeMs,
                Hardware.CameraAcquisitionTimeMs,
                Hardware.ProcessingTimeMs,
                PostProcessTimeMs
            };

            string[] labels = new[]
            {
                "队列等待",
                "触发信号",
                "图像采集",
                "AI处理",
                "后处理"
            };

            // 找出最大的时间消耗
            double maxTime = -1;
            string bottleneck = "未知";

            for (int i = 0; i < times.Length; i++)
            {
                if (times[i] > maxTime && times[i] > 0)
                {
                    maxTime = times[i];
                    bottleneck = $"{labels[i]} ({maxTime:F1}ms)";
                }
            }

            return bottleneck;
        }

        /// <summary>
        /// 获取时间分解的文本表示（用于日志和调试）
        /// </summary>
        public override string ToString()
        {
            return $"[时间分解] 队列:{QueueWaitTimeMs:F1}ms | " +
                   $"触发:{Hardware.TriggerSetupTimeMs:F1}ms | " +
                   $"采集:{Hardware.CameraAcquisitionTimeMs:F1}ms | " +
                   $"处理:{Hardware.ProcessingTimeMs:F1}ms | " +
                   $"后处理:{PostProcessTimeMs:F1}ms | " +
                   $"总计:{TotalElapsedMs:F1}ms";
        }
    }
}
