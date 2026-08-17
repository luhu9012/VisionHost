//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationStatistics.cs
// 说 明: 工位统计数据模型
//        仅包含统计聚合数据（总产数、良率、缺陷统计等）
//        与配置和运行状态分离，确保单一职责
//        通常由后台任务周期性计算和更新
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工位统计数据模型
    /// 
    /// 此模型表示工位的统计信息，特点：
    /// - 低频变化（分钟级或批量更新）
    /// - 通常由异步任务计算（不是实时的）
    /// - 适合存储在主库中用于报表和分析
    /// - 不包含配置或实时状态
    /// 
    /// 职责分离:
    /// - StationConfigModel: 工位配置（生命周期：创建-修改-删除）
    /// - StationRuntimeStatus: 工位运行状态（变化频繁：秒级）
    /// - StationStatistics: 工位统计数据（变化周期：分钟级）
    /// 
    /// 数据来源:
    /// - 从工单完成记录（WorkOrder）中聚合而来
    /// - 通常在工单完成或定时计算时更新
    /// - 可与 Datamart 层提供的聚合结果进行对账
    /// </summary>
    public class StationStatistics
    {
        /// <summary>
        /// 工位唯一标识 (外键关联到 StationConfigModel)
        /// </summary>
        public string StationId { get; set; }

        /// <summary>
        /// 统计数据的统计周期
        /// 
        /// 统计周期类型:
        /// - "Daily"    = 当日统计（每天凌晨重置）
        /// - "Shift"    = 班次统计（按班次时间周期）
        /// - "Monthly"  = 月度统计
        /// - "Total"    = 累计统计（从工位投入使用起的全累计）
        /// 
        /// 默认为 "Daily" (当日统计)
        /// </summary>
        public string Period { get; set; } = "Daily";

        /// <summary>
        /// 本统计周期的起始时间
        /// 
        /// 示例：如果是日统计，则为当天 00:00:00
        ///       如果是班次统计，则为该班次的开始时间
        /// </summary>
        public DateTime PeriodStartTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 本统计周期的结束时间
        /// 
        /// null 表示统计周期仍在进行中（未结束）
        /// 当周期结束时（如日期变更），记录结束时间并生成归档记录
        /// </summary>
        public DateTime? PeriodEndTime { get; set; }

        /// <summary>
        /// 本统计周期处理的总工单数
        /// 
        /// 包括所有完成的工单（OK、NG、异常停止等）
        /// 不包括当前正在处理的工单
        /// </summary>
        public int TotalProcessed { get; set; }

        /// <summary>
        /// 本统计周期的良品（OK）工单数
        /// 
        /// 计数标准: WorkOrderResultData.IsOk == true
        /// </summary>
        public int OkCount { get; set; }

        /// <summary>
        /// 本统计周期的不良品（NG）工单数
        /// 
        /// 计数标准: WorkOrderResultData.IsOk == false
        /// NG 可能由于：
        /// - 质量检测缺陷
        /// - 尺寸超差
        /// - 使用寿命检测失败
        /// 等原因
        /// </summary>
        public int NgCount { get; set; }

        /// <summary>
        /// 本统计周期的异常停止（Error）工单数
        /// 
        /// 计数标准: WorkOrder.Status 为异常终止状态
        /// Error 与 NG 区别：
        /// - NG: 产品本身不合格（质量问题）
        /// - Error: 工位执行过程中发生异常导致无结果
        /// </summary>
        public int ErrorCount { get; set; }

        /// <summary>
        /// 本统计周期的良率百分比 (0-100)
        /// 
        /// 计算公式:
        /// YieldRate = (OkCount / TotalProcessed) * 100
        /// 
        /// 特殊情况:
        /// - 如果 TotalProcessed == 0，则值为 0（避免除零）
        /// - 保留到小数点后两位（如：99.50%）
        /// </summary>
        public double YieldRate 
        { 
            get
            {
                if (TotalProcessed == 0)
                    return 0;
                return Math.Round((double)OkCount / TotalProcessed * 100, 2);
            }
        }

        /// <summary>
        /// 本统计周期的平均周期时间（毫秒）
        /// 
        /// 计算: 所有完成工单的总耗时 / 工单数
        /// 
        /// 用途:
        /// - 评估工位产能
        /// - 发现性能瓶颈
        /// - 与目标周期时间对标
        /// 
        /// 0 表示还未计算或无数据
        /// </summary>
        public double AverageCycleTimeMs { get; set; }

        /// <summary>
        /// 本统计周期实际运行的时间（秒）
        /// 
        /// 计算: 从第一个工单开始到最后一个工单完成的累计运行时间
        /// （不包括空闲、暂停、故障等非生产时间）
        /// 
        /// 用途:
        /// - 计算实际产能
        /// - 与名义产能对标
        /// - 评估设备利用率
        /// </summary>
        public double ActualRunningTimeSeconds { get; set; }

        /// <summary>
        /// 本统计周期内发生的故障次数
        /// </summary>
        public int FaultCount { get; set; }

        /// <summary>
        /// 本统计周期的首次缺陷发生时间
        /// 
        /// 用于:
        /// - 快速定位问题起点
        /// - 分析根本原因
        /// 
        /// null 表示本周期内无缺陷
        /// </summary>
        public DateTime? FirstDefectTime { get; set; }

        /// <summary>
        /// 本统计周期最后一条缺陷描述
        /// 
        /// 示例: "尺寸超差 5mm", "表面划伤", "装配错误"
        /// </summary>
        public string LastDefectDescription { get; set; }

        /// <summary>
        /// 统计数据的最后更新时间
        /// 
        /// 更新时机:
        /// - 每完成一个工单后，立即更新
        /// - 或定时批量更新（如每分钟汇总一次）
        /// 
        /// 用途:
        /// - 追踪统计数据的鲜度
        /// - 在分布式环境下识别过期缓存
        /// </summary>
        public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 验证统计数据的内部一致性
        /// 
        /// 规则:
        /// - OkCount + NgCount + ErrorCount ≤ TotalProcessed
        /// - YieldRate 应在 [0, 100] 范围内
        /// - AverageCycleTimeMs ≥ 0
        /// - PeriodEndTime 应 ≥ PeriodStartTime
        /// </summary>
        public bool IsConsistent
        {
            get
            {
                // 各类工单数不应超过总数
                if (OkCount + NgCount + ErrorCount > TotalProcessed)
                    return false;

                // 良率应在正常范围
                if (YieldRate < 0 || YieldRate > 100)
                    return false;

                // 周期时间应为正数
                if (AverageCycleTimeMs < 0)
                    return false;

                // 周期时间应大于 0（除非总数为 0）
                if (TotalProcessed > 0 && AverageCycleTimeMs == 0)
                    return false;

                // 周期结束时间不应早于开始时间
                if (PeriodEndTime.HasValue && PeriodEndTime < PeriodStartTime)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// 工厂方法: 创建一个新的当日统计对象
        /// </summary>
        public static StationStatistics CreateDailyStatistics(string stationId)
        {
            var now = DateTime.UtcNow;
            return new StationStatistics
            {
                StationId = stationId,
                Period = "Daily",
                PeriodStartTime = now.Date,  // 当日 00:00:00
                PeriodEndTime = null,         // 统计进行中
                TotalProcessed = 0,
                OkCount = 0,
                NgCount = 0,
                ErrorCount = 0,
                AverageCycleTimeMs = 0,
                LastUpdatedAt = now
            };
        }

        /// <summary>
        /// 累加一个工单的统计数据
        /// </summary>
        public void AccumulateWorkOrder(
            bool isOk, 
            double cycleTimeMs, 
            bool isFaultTerminated = false,
            string defectDescription = null)
        {
            // 总数加 1
            TotalProcessed++;

            // 根据结果分类计数
            if (isFaultTerminated)
            {
                ErrorCount++;
            }
            else if (isOk)
            {
                OkCount++;
            }
            else
            {
                NgCount++;
                if (!FirstDefectTime.HasValue)
                    FirstDefectTime = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(defectDescription))
                    LastDefectDescription = defectDescription;
            }

            // 更新平均周期时间（增量平均）
            if (TotalProcessed == 1)
            {
                AverageCycleTimeMs = cycleTimeMs;
            }
            else
            {
                AverageCycleTimeMs = 
                    (AverageCycleTimeMs * (TotalProcessed - 1) + cycleTimeMs) 
                    / TotalProcessed;
            }

            // 更新时间戳
            LastUpdatedAt = DateTime.UtcNow;
        }
    }
}
