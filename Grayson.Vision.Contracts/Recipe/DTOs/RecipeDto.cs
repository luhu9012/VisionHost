using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.DTOs
{
    /// <summary>
    /// 配方根持久化 DTO（对应完整 RecipeModel）。
    /// ⚠ 2026-09-05 修复：历史版本只持久化"元数据+流程"，导致审批状态/审批记录/生效时间/
    ///   运行配置/工艺参数/锁定标记 保存即丢、重启回 Draft 的数据丢失 bug。现补齐全字段，
    ///   旧 JSON 缺字段时走默认值，无损兼容加载。
    /// </summary>
    public class VisionRecipeDto
    {
        #region 1. 配方元数据 (Meta Info)
        public string RecipeId { get; set; }
        public string RecipeCode { get; set; }
        public string RecipeName { get; set; }
        public string ProductCategory { get; set; }
        public string Version { get; set; } = "1.0.0";
        public bool IsActive { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }

        /// <summary>创建时间（不可变，审计用）——历史 DTO 未存，缺省回退到 LastModifiedTime</summary>
        public DateTime CreatedTime { get; set; }

        public DateTime LastModifiedTime { get; set; }

        /// <summary>审批/发布状态（历史版本丢失，加载时缺省 Draft）</summary>
        public RecipeApprovalStatus ApprovalStatus { get; set; } = RecipeApprovalStatus.Draft;

        /// <summary>审批记录（提交/审批人/时间/意见/电子签名/历史流水）</summary>
        public RecipeApprovalInfo ApprovalInfo { get; set; }

        /// <summary>审批生效时间（Approved 后写入）</summary>
        public DateTime? EffectiveFrom { get; set; }

        /// <summary>变更原因 / 版本变更说明</summary>
        public string ChangeReason { get; set; }

        /// <summary>版本修订号</summary>
        public int Revision { get; set; } = 1;

        /// <summary>是否锁定（禁止编辑核心内容）</summary>
        public bool IsLocked { get; set; }

        /// <summary>业务流声明的所需逻辑设备键集合</summary>
        public List<string> RequiredDeviceKeys { get; set; } = new List<string>();
        #endregion

        #region 2. 流程拓扑数据 (Main & Sub Processes)
        /// <summary>
        /// 主工作流程定义 DTO
        /// </summary>
        public ProcessDto MainProcess { get; set; }

        /// <summary>
        /// 子流程/并行流程字典 DTO (Key: ProcessId 或 ProcessName)
        /// </summary>
        public Dictionary<string, ProcessDto> SubProcesses { get; set; } = new Dictionary<string, ProcessDto>();

        /// <summary>
        /// 配方中声明的逻辑设备映射。
        /// </summary>
        public List<RecipeDeviceMappingModel> LogicalDevices { get; set; } = new List<RecipeDeviceMappingModel>();
        #endregion

        #region 3. 工艺参数（2026-09-05 补齐持久化）
        /// <summary>当前工艺参数集（阈值、曝光、AI 模型、标定数据路径）</summary>
        public ProcessParameterSet ProcessParameters { get; set; }
        #endregion
    }

    /// <summary>
    /// 单个流程拓扑 DTO（对应 FlowProcessModel）
    /// </summary>
    public class ProcessDto
    {
        public string ProcessId { get; set; }
        public string ProcessName { get; set; }
        public List<RecipeNodeDto> Nodes { get; set; } = new List<RecipeNodeDto>();
        public List<RecipeConnectionDto> Connections { get; set; } = new List<RecipeConnectionDto>();
    }

    /// <summary>
    /// 节点持久化 DTO
    /// </summary>
    public class RecipeNodeDto
    {
        public string NodeId { get; set; }
        public NodeType Type { get; set; }
        public string DisplayName { get; set; }
        public bool Enable { get; set; }
        public double PosX { get; set; }
        public double PosY { get; set; }
        public object ParameterModel { get; set; }

        /// <summary>
        /// CompositeFlow（Group 子流程）节点的内部子流程。
        /// ⚠ 2026-09-11 修复：此前保存配方时未序列化 CompositeFlowNode.SubProcess，
        ///   拖进画布的子流程保存后变空壳（内部节点/连线丢失）。现在递归持久化，
        ///   仅 CompositeFlow 节点非空，其余节点为 null（JSON 忽略空值）。
        /// </summary>
        public ProcessDto SubProcess { get; set; }
    }

    /// <summary>
    /// 连线持久化 DTO
    /// </summary>
    public class RecipeConnectionDto
    {
        public string ConnectionId { get; set; }
        public string SourceNodeId { get; set; }
        public string SourcePortId { get; set; }
        public string SourcePortName { get; set; }
        public string TargetNodeId { get; set; }
        public string TargetPortId { get; set; }
        public string TargetPortName { get; set; }
    }
}