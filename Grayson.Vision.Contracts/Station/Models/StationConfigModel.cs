//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationConfigModel.cs
// 说 明: 工位静态配置模型
//        包含工位基础信息、绑定配方、设备映射等配置数据
//        仅包含配置信息，与运行时状态分离
//===================================================================================

using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Triggers;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工位对外配置模型：包含配方、硬件映射、IO/运动参数。
    /// 
    /// 此模型表示工位的静态配置信息，特点：
    /// - 生命周期长（工位投入使用到报废）
    /// - 变更频率低（通常只在工位配置或维护时变更）
    /// - 由 UI 层配置保存后下发给 StationRuntimeManager
    /// 
    /// 不包含:
    /// - 运行时状态 (IsConnected, State 等) → 参考 StationRuntimeStatus
    /// - 统计数据 (TotalCount, OkCount 等) → 参考 StationStatistics
    /// 
    /// 用于:
    /// - UI 层配置界面
    /// - 工位初始化和参数加载
    /// - 配置持久化和版本管理
    /// </summary>
    public class StationConfigModel
    {
        /// <summary>
        /// 工位唯一标识
        /// 格式: "STA-001", "LINE_A_01" 等
        /// </summary>
        public string StationId { get; set; }

        /// <summary>
        /// 工位代码（可读性强的简码）
        /// 用途：日志、告警、快速辨识
        /// </summary>
        public string StationCode { get; set; }

        /// <summary>
        /// 工位名称（中文或外文描述）
        /// 例如："焊接工位A", "CCD检测工位", "分拣工位"
        /// </summary>
        public string StationName { get; set; }

        /// <summary>
        /// 工位所属产线 ID
        /// 外键关联到 LineConfigModel.LineId
        /// 用于产线级拓扑和管理
        /// </summary>
        public string LineId { get; set; }

        /// <summary>
        /// 工位所属产线名称（冗余字段，便于 UI 显示）
        /// </summary>
        public string LineName { get; set; }

        /// <summary>
        /// 工位启用状态
        /// true  = 工位可用，可以接收工单
        /// false = 工位禁用，不接收新工单（可用于维护或报废的工位）
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 工位通讯超时时间（毫秒）
        /// 
        /// 定义：从发送 PLC 命令到预期响应的最大等待时间
        /// 典型值：3000ms (3秒)
        /// 
        /// 用途：
        /// - PLC 心跳检测超时判定
        /// - 硬件故障快速发现
        /// 
        /// 注意：不同硬件可能需要不同的超时设置
        /// </summary>
        public int TimeoutMs { get; set; } = 3000;

        /// <summary>
        /// 当前绑定配方的唯一标识（RecipeId）。
        /// 完整 RecipeModel 由 <see cref="IRecipeStorageService.LoadRecipe(string)"/> 按需加载，
        /// 避免在工位配置中冗余嵌入整个配方导致文档嵌套过深。
        /// </summary>
        public string BoundRecipeId { get; set; }

        /// <summary>
        /// 当前绑定配方的显示名称冗余字段，便于 UI 列表/概览直接展示。
        /// </summary>
        public string BoundRecipeName { get; set; }

        /// <summary>
        /// 配方中的逻辑设备 → 物理设备的映射列表
        /// 
        /// 新版本统一使用 RecipeDeviceMappingModel (来自 Contracts.Recipe.Models)
        /// 
        /// RecipeDeviceMappingModel 的优点：
        /// - 与配方绑定，便于配方版本管理
        /// - 支持更完整的设备信息（规格、参数等）
        /// - 便于配方级的设备兼容性检查
        /// 
        /// 映射关系示例:
        /// - 逻辑设备 "相机_主" → 物理设备 "Camera_001_USB3"
        /// - 逻辑设备 "PLC_控制" → 物理设备 "Siemens_S7_Net_192.168.1.100"
        /// </summary>
        public List<Recipe.Models.RecipeDeviceMappingModel> DeviceMappings { get; set; } 
            = new List<Recipe.Models.RecipeDeviceMappingModel>();

        /// <summary>
        /// 工位从设备池领用的物理硬件设备 ID 列表
        /// 
        /// 注意：这与 DeviceMappings 是独立的概念：
        /// - LeaseDeviceIds：工位领用了哪些物理设备（比如：领用3个摄像头）
        /// - DeviceMappings：配方中的逻辑设备映射到工位设备的关系（比如：配方只需要1个摄像头）
        /// 
        /// 用途：
        /// - UI 层展示工位已领用的硬件列表
        /// - 工位配置持久化：保存工位与设备的领用关系
        /// - 初始化时恢复 HardwareDevices 列表
        /// 
        /// 说明：设备 ID 来自全局设备池（IDevicePool.GetAllDevices()）
        /// </summary>
        public List<string> LeaseDeviceIds { get; set; } 
            = new List<string>();

        /// <summary>
        /// 运行期参数（可扩展：如触发模式、调试/生产模式等）
        /// 
        /// 典型参数键值:
        /// - "TriggerMode"       = "PLC" | "MANUAL" | "SCHEDULER"
        /// - "DebugMode"         = "true" | "false"
        /// - "SimulationMode"    = "true" | "false"
        /// - "CameraExposure"    = "50" (单位毫秒)
        /// - "MaxRetry"          = "2"
        /// 
        /// 用途:
        /// - 工位初始化时装载参数
        /// - UI 参数编辑时存储
        /// - 工单执行时读取（如触发模式决定如何等待工单）
        /// </summary>
        public Dictionary<string, string> RuntimeParams { get; set; } 
            = new Dictionary<string, string>();

        /// <summary>
        /// 工位触发源配置——工位级物理接线约定（PLC 点位/IO 通道等），不随配方变化。
        /// 由 StationManageView 触发信号配置 Tab 编辑，工位初始化时由 StationHostRuntime 创建对应 ITriggerSource。
        /// </summary>
        public TriggerSourceConfig TriggerSource { get; set; } = new TriggerSourceConfig();

        /// <summary>
        /// 配置创建时间戳
        /// 用于审计和版本追踪
        /// </summary>
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 配置最后修改时间戳
        /// 用于检测配置变更
        /// </summary>
        public DateTime UpdatedTime { get; set; } = DateTime.Now;
    }

    //=== 注意：DeviceMappingModel 已在 Phase A 删除 ===
    // 新版本统一使用 RecipeDeviceMappingModel (来自 Contracts.Recipe.Models)
    // 见下面的 StationConfigModel.DeviceMappings 字段说明

    /// 
    /// 此模型表示一条产线的逻辑视图，包含该产线下的所有工位
    /// 
    /// 用途:
    /// - UI 产线拓扑树的顶级节点
    /// - 产线级操作（如启动/停止整条产线）
    /// - 产线级数据聚合（汇总所有工位的统计）
    /// </summary>
    public class LineConfigModel
    {
        /// <summary>
        /// 产线唯一标识
        /// 格式: "LINE_A", "LINE_B_01" 等
        /// </summary>
        public string LineId { get; set; }

        /// <summary>
        /// 产线名称
        /// 例如："喷漆产线", "装配产线A"
        /// </summary>
        public string LineName { get; set; }

        /// <summary>
        /// 该产线下的所有工位列表
        /// 
        /// 注意：此列表使用 List&lt;StationConfigModel&gt;
        /// 而非 ObservableCollection，原因如下：
        /// - 配置模型是静态的，很少动态修改
        /// - ObservableCollection 用于 UI 绑定（WPF），不适合纯业务层
        /// 
        /// UI 层应该做转换:
        /// var uiLineConfig = new UI_LineModel
        /// {
        ///     LineId = contractsLine.LineId,
        ///     Stations = new ObservableCollection&lt;UI_StationModel&gt;(
        ///         contractsLine.Stations.Select(s => ConvertToUI(s))
        ///     )
        /// };
        /// </summary>
        public List<StationConfigModel> Stations { get; set; } 
            = new List<StationConfigModel>();
    }

    /// <summary>
    /// [已过时] 工位实时信息模型
    /// 
    /// 此类已弃用，使用下列新模型替代：
    /// - StationRuntimeStatus: 工位运行时动态状态（连接、运行状态等）
    /// - StationStatistics: 工位统计数据（良率、产数等）
    /// 
    /// 保留此类用于向后兼容，但新代码不应使用
    /// 
    /// 迁移建议:
    /// 将现有 StationRuntimeInfo 的字段分离到：
    /// - IsConnected, State, LastUpdateTime → StationRuntimeStatus
    /// - TotalCount, OkCount, NgCount, YieldRate → StationStatistics
    /// - CurrentRecipeName → 直接从 StationConfigModel.BoundRecipe 读取
    /// - LastResult, LastMessage → 直接查询最新工单的 Result 信息
    /// </summary>
    [Obsolete(
        "StationRuntimeInfo 已弃用，请使用 StationRuntimeStatus （运行状态）和 StationStatistics （统计数据）替代。" +
        "迁移详情见类注解。", 
        error: false)]  // error=false 允许编译通过但产生警告
    public class StationRuntimeInfo
    {
        /// <summary>
        /// 工位唯一标识
        /// </summary>
        public string StationId { get; set; }

        /// <summary>
        /// [已迁移到 StationRuntimeStatus.IsConnected]
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// [已迁移到 StationRuntimeStatus.State]
        /// </summary>
        public string State { get; set; } = "Stopped";

        /// <summary>
        /// [已迁移到 StationConfigModel.BoundRecipe.RecipeName]
        /// </summary>
        public string CurrentRecipeName { get; set; }

        /// <summary>
        /// [已迁移到 StationStatistics.TotalProcessed]
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// [已迁移到 StationStatistics.OkCount]
        /// </summary>
        public int OkCount { get; set; }

        /// <summary>
        /// [已迁移到 StationStatistics.NgCount]
        /// </summary>
        public int NgCount { get; set; }

        /// <summary>
        /// [已迁移到 StationStatistics.YieldRate]
        /// </summary>
        public double YieldRate { get; set; }

        /// <summary>
        /// [已迁移到 WorkOrderResult（最新工单）]
        /// </summary>
        public string LastResult { get; set; }

        /// <summary>
        /// [已迁移到 StationRuntimeStatus.LastErrorMessage]
        /// </summary>
        public string LastMessage { get; set; }

        /// <summary>
        /// [已迁移到 StationRuntimeStatus.LastUpdateTime]
        /// </summary>
        public DateTime LastUpdateTime { get; set; } = DateTime.Now;
    }
}
