//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: WorkOrderResult.cs
// 说 明: 工单检验结果统一模型
//        合并原来的 WorkOrderResultData 和 WorkOrderQualityData
//        提供完整的、结构化的工单执行结果数据
//        包括检验结果、测量数据、时间分解、质量统计等
//===================================================================================

using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工单执行结果的完整统一模型
    /// 
    /// 此模型包含工单执行的所有结果信息，结构化组织为几个逻辑板块：
    /// 
    /// 1️⃣ 基本结果 (是否OK/NG)
    /// 2️⃣ 检验与测量数据
    /// 3️⃣ 时间分解 (详见 WorkOrderTiming)
    /// 4️⃣ 证据材料 (图像、日志)
    /// 
    /// 职责分离:
    /// - WorkOrder: 工单的生命周期管理（状态、创建/完成时间）
    /// - WorkOrderResult: 工单的执行结果数据（OK/NG、测量、图像）
    /// - WorkOrderTiming: 工单的时间分解（各阶段耗时）
    /// 
    /// 使用场景:
    /// - UI 显示: 直接读取 IsOk、DefectCodes、Measurements、ImagePaths
    /// - 数据库存储: 整个 WorkOrderResult 作为 JSON/二进制存储
    /// - Datamart 聚合: 统计 OkCount、NgCount、ConfidenceScore 等
    /// - 追溯系统: 记录 ImagePaths 和完整的检验过程
    /// </summary>
    public class WorkOrderResult
    {
        /// <summary>
        /// 检验结果核心信息
        /// </summary>
        public class BasicResult
        {
            /// <summary>
            /// 工单总体检验结果
            /// 
            /// true  = 产品合格 (OK)
            /// false = 产品不合格 (NG)
            /// 
            /// 注意: 此值由工位最后的判定逻辑生成，是多个检验项的综合结果
            /// </summary>
            public bool IsOk { get; set; }

            /// <summary>
            /// 产品的缺陷分类代码列表
            /// 
            /// 仅当 IsOk = false 时有意义
            /// 
            /// 示例:
            /// - "SCRATCH"        → 表面划伤
            /// - "MISSING_PART"   → 零件缺失
            /// - "DIMENSION_OOT"  → 尺寸超差 (Out of Tolerance)
            /// - "ASSEMBLY_ERROR" → 装配错误
            /// - "DISCOLORATION"  → 色泽异常
            /// 
            /// 一个工单可能有多个缺陷，通过列表方式记录
            /// </summary>
            public List<string> DefectCodes { get; set; } = new List<string>();

            /// <summary>
            /// 产品的缺陷等级
            /// 
            /// 用于分类处理:
            /// - "Minor"   → 轻微缺陷，可能进入维修流程
            /// - "Major"   → 严重缺陷，必须报废
            /// - "None"    → 无缺陷（ IsOk = true 时）
            /// </summary>
            public string SeverityLevel { get; set; } = "None";
        }

        /// <summary>
        /// 检验与测量的数据板块
        /// </summary>
        public class MeasurementData
        {
            /// <summary>
            /// 测量项的结果集合
            /// 
            /// Key: 测量项名称 (如 "Length", "Width", "Height", "Diameter")
            /// Value: 测量值 (浮点数，通常单位为毫米)
            /// 
            /// 示例:
            /// {
            ///     "Length": 123.45,
            ///     "Width": 67.89,
            ///     "Depth": 45.12,
            ///     "Weight": 234.5
            /// }
            /// 
            /// 每个值可与规格文件中的公差进行对标，判断是否超差
            /// </summary>
            public Dictionary<string, double> Values { get; set; } = new Dictionary<string, double>();

            /// <summary>
            /// AI 推理模型的置信度/评分
            /// 
            /// 范围: [0.0, 1.0]
            /// - 1.0  = 完全确定 (模型 100% 确信判定结果)
            /// - 0.5  = 中等确定 (模型 50% 确信)
            /// - 0.0  = 完全不确定 (模型无法做出判定)
            /// 
            /// 用途:
            /// - 发现模型不确定的边界情况
            /// - 二次人工审核的优先级排序
            /// - 模型性能评估 (平均信心度)
            /// </summary>
            public double ConfidenceScore { get; set; }

            /// <summary>
            /// 本次检验的检验区域/ROI (Region of Interest) 数量
            /// 
            /// 示例:
            /// - 如果检验 3 个不同区域，则值为 3
            /// - 如果做了 2 次扫描，1 次高分辨率扫描，则值为 3
            /// 
            /// 用途:
            /// - 评估检验覆盖度
            /// - 后续质量分析（检测到缺陷的概率 ∝ ROI 数）
            /// </summary>
            public int InspectionRegionCount { get; set; }

            /// <summary>
            /// 验证测量数据的一致性
            /// </summary>
            public bool IsConsistent
            {
                get =>
                    ConfidenceScore >= 0 && ConfidenceScore <= 1.0 &&
                    InspectionRegionCount >= 0;
            }
        }

        /// <summary>
        /// 基本检验结果 (OK/NG、缺陷代码)
        /// </summary>
        public BasicResult Result { get; set; } = new BasicResult();

        /// <summary>
        /// 检验与测量数据
        /// </summary>
        public MeasurementData Measurements { get; set; } = new MeasurementData();

        /// <summary>
        /// 工单执行时间分解 (详见 WorkOrderTiming 类)
        /// </summary>
        public WorkOrderTiming Timing { get; set; } = new WorkOrderTiming();

        /// <summary>
        /// 证据材料：检验过程中产生的图像文件路径列表
        /// 
        /// 示例:
        /// [
        ///     "c:\\inspect_results\\20260114\\order_001_original.png",
        ///     "c:\\inspect_results\\20260114\\order_001_annotated_defects.png",
        ///     "c:\\inspect_results\\20260114\\order_001_heatmap.png"
        /// ]
        /// 
        /// 用途:
        /// - 质量追溯: 后续发现问题时查看当时的图像证据
        /// - 模型持续改进: 收集更多的工单样本优化模型
        /// - 纠纷解决: 提供客户或监管部门的检验证据
        /// 
        /// 路径策略:
        /// - 相对路径: 便于备份和迁移
        /// - 绝对路径: 便于快速定位
        /// - 云存储 URL: 适合分布式环境
        /// </summary>
        public List<string> ImagePaths { get; set; } = new List<string>();

        /// <summary>
        /// 错误/异常信息
        /// 
        /// 可能出现在以下场景:
        /// 1. 硬件异常导致无结果
        ///    示例: "相机超时", "PLC 通讯中断"
        /// 2. 处理异常
        ///    示例: "图像解析失败", "模型推理异常"
        /// 3. 工位异常退出
        ///    示例: "触发超时，工单中止"
        /// 
        /// null 或空字符串 = 无异常，检验正常完成
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 操作员备注或补充说明
        /// 
        /// 可能记录:
        /// - 手工复核的意见
        /// - 特殊处理情况变更记录
        /// - 样品来源备注
        /// 
        /// 例如: "依配方V2.1检验，左上角有擦伤但未超规格"
        /// </summary>
        public string ResultComment { get; set; }

        /// <summary>
        /// 结果生成/记录的时间戳
        /// </summary>
        public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 检验员或工位的标识码
        /// 
        /// 可用于审计和追溯（谁在什么时候执行了检验）
        /// 通常填入:
        /// - 人工检验: 操作员工号
        /// - 自动工位: 工位 ID
        /// - AI 系统: 模型版本号
        /// </summary>
        public string Inspector { get; set; }

        /// <summary>
        /// 验证结果数据的内部一致性
        /// 
        /// 规则:
        /// 1. 如果 IsOk = true，DefectCodes 应为空
        /// 2. 如果 IsOk = false，DefectCodes 不应为空
        /// 3. ConfidenceScore 应在 [0, 1] 范围内
        /// 4. Timing 应内部一致
        /// 5. 如果 ErrorMessage 非空，应表示异常（IsOk = false）
        /// </summary>
        public bool IsConsistent
        {
            get
            {
                // OK 结果不应有缺陷代码
                if (Result.IsOk && Result.DefectCodes.Count > 0)
                    return false;

                // NG 结果应有缺陷代码（除非是异常）
                if (!Result.IsOk && Result.DefectCodes.Count == 0 && string.IsNullOrEmpty(ErrorMessage))
                    return false;

                // 测量数据应一致
                if (!Measurements.IsConsistent)
                    return false;

                // 时间数据应一致
                if (!Timing.IsConsistent)
                    return false;

                // 如果有错误，不应该有有效的结果
                if (!string.IsNullOrEmpty(ErrorMessage) && Result.IsOk)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// 工厂方法: 创建一个 OK 的检验结果
        /// </summary>
        public static WorkOrderResult CreateOk(
            double confidenceScore,
            Dictionary<string, double> measurements = null,
            List<string> imagePaths = null)
        {
            return new WorkOrderResult
            {
                Result = new BasicResult
                {
                    IsOk = true,
                    DefectCodes = new List<string>(),
                    SeverityLevel = "None"
                },
                Measurements = new MeasurementData
                {
                    Values = measurements ?? new Dictionary<string, double>(),
                    ConfidenceScore = confidenceScore
                },
                ImagePaths = imagePaths ?? new List<string>(),
                RecordedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 工厂方法: 创建一个 NG 的检验结果
        /// </summary>
        public static WorkOrderResult CreateNg(
            List<string> defectCodes,
            string severityLevel = "Major",
            double confidenceScore = 0.95,
            string comment = null)
        {
            return new WorkOrderResult
            {
                Result = new BasicResult
                {
                    IsOk = false,
                    DefectCodes = defectCodes ?? new List<string> { "UNKNOWN_NG" },
                    SeverityLevel = severityLevel
                },
                Measurements = new MeasurementData
                {
                    ConfidenceScore = confidenceScore
                },
                ResultComment = comment,
                RecordedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 工厂方法: 创建一个异常/错误的检验结果
        /// </summary>
        public static WorkOrderResult CreateError(string errorMessage, string remark = null)
        {
            return new WorkOrderResult
            {
                Result = new BasicResult
                {
                    IsOk = false,
                    SeverityLevel = "Error"
                },
                ErrorMessage = errorMessage,
                ResultComment = remark,
                RecordedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 获取结果的文本摘要（用于日志和显示）
        /// </summary>
        public override string ToString()
        {
            string status = Result.IsOk ? "✓ OK" : "✗ NG";
            string defects = Result.DefectCodes.Count > 0 ? 
                $" ({string.Join(",", Result.DefectCodes)})" : "";
            string confidence = $" | 信心度:{Measurements.ConfidenceScore:P}";
            string timing = $" | 耗时:{Timing.TotalElapsedMs:F0}ms";

            return $"{status}{defects}{confidence}{timing}";
        }
    }
}
