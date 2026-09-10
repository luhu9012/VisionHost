using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Recipe.Traceability;

namespace Grayson.Vision.Contracts.Recipe.Models
{
    /// <summary>
    /// 全局唯一：工位级配方领域模型（数据与 UI 绑定共用）
    /// </summary>
    public class RecipeModel
    {
        #region 1. 基础元数据 (Meta Info)
        /// <summary>
        /// 配方唯一标识 GUID
        /// </summary>
        public string RecipeId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 配方编号/代号 (如: REC-20260803-001)
        /// </summary>
        public string RecipeCode { get; set; }

        /// <summary>
        /// 配方名称 (如: 瓶盖缺陷检测配方_A)
        /// </summary>
        public string RecipeName { get; set; }

        /// <summary>
        /// 产品类别/型号 (如: Product_350ml_Bottle)
        /// </summary>
        public string ProductCategory { get; set; }

        /// <summary>
        /// 配方版本号 (语义化版本，如: 1.0.2)
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// 是否作为工位当前默认/激活配方
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// 备注与版本变更说明
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// 创建人 / 修改人
        /// </summary>
        public string Author { get; set; }

        /// <summary>
        /// 创建时间（不可变，用于审计追溯）
        /// </summary>
        public DateTime CreatedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 最后修改时间
        /// </summary>
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;

        /// <summary>
        /// 审批生效时间；批准后写入，用于控制工位何时可正式启用该配方。
        /// </summary>
        public DateTime? EffectiveFrom { get; set; }

        /// <summary>
        /// 配方审批/发布状态，满足医药行业 GxP / 21 CFR Part 11 等可追溯要求。
        /// </summary>
        public Recipe.Enums.RecipeApprovalStatus ApprovalStatus { get; set; } = Recipe.Enums.RecipeApprovalStatus.Draft;

        /// <summary>
        /// 配方审批记录：提交人、审批人、提交/审批时间、审批意见。
        /// </summary>
        public RecipeApprovalInfo ApprovalInfo { get; set; } = new RecipeApprovalInfo();

        /// <summary>
        /// 变更原因 / 版本变更说明（审批与审计必填）
        /// </summary>
        public string ChangeReason { get; set; }

        /// <summary>
        /// 版本修订号（整数，自增，用于与 Version 字符串共同定位）
        /// </summary>
        public int Revision { get; set; } = 1;

        /// <summary>
        /// 配方是否已被锁定（Approved/Rejected 后应锁定，禁止再编辑核心流程）
        /// </summary>
        public bool IsLocked { get; set; }
        #endregion

        #region 业务校验属性（只读）

        /// <summary>
        /// 当前配方是否已获批且已到生效时间。
        /// </summary>
        public bool IsEffective => ApprovalStatus == Recipe.Enums.RecipeApprovalStatus.Approved &&
                                   (!EffectiveFrom.HasValue || EffectiveFrom.Value <= DateTime.Now);

        /// <summary>
        /// 当前配方核心内容是否允许编辑（草案或被驳回时可编辑；已提交/已批准/已锁定时不可）。
        /// </summary>
        public bool IsEditable => !IsLocked &&
                                  (ApprovalStatus == Recipe.Enums.RecipeApprovalStatus.Draft ||
                                   ApprovalStatus == Recipe.Enums.RecipeApprovalStatus.Rejected);

        /// <summary>
        /// 基础元数据是否满足保存/提交的最小要求。
        /// </summary>
        public bool HasMinimumMetadata =>
            !string.IsNullOrWhiteSpace(RecipeName) &&
            !string.IsNullOrWhiteSpace(RecipeCode) &&
            !string.IsNullOrWhiteSpace(ProductCategory) &&
            !string.IsNullOrWhiteSpace(Version);

        #endregion

        #region 2. 核心业务流数据 (Workflow / Execution Data)
        /// <summary>
        /// 业务流/业务蓝图：主工作流程定义（画布编排的业务逻辑）。
        /// </summary>
        public FlowProcessModel MainProcess { get; set; }

        /// <summary>
        /// 业务流/业务蓝图：子流程字典（画布编排的业务逻辑）。
        /// </summary>
        public Dictionary<string, FlowProcessModel> SubProcesses { get; set; }
            = new Dictionary<string, FlowProcessModel>();

        /// <summary>
        /// 业务流里声明的所需逻辑设备键集合。
        /// </summary>
        public List<string> RequiredDeviceKeys { get; set; } = new List<string>();
        #endregion

        #region 3. 工艺参数集
        /// <summary>
        /// 当前工艺参数集（阈值、曝光、模型、标定结果）。
        /// ⚠ 2026-09-05 决策：工位运行配置（调度器/工作模式/IO/安全联锁/语言等）已从配方整卡移除——
        ///   运行配置属工位侧能力（StationConfigModel.RuntimeParams/TriggerSource/ProcessKey），
        ///   配方只承载"随产品变化的工艺内容"。
        /// </summary>
        public ProcessParameterSet ProcessParameters { get; set; }
            = new ProcessParameterSet();
        #endregion

        /// <summary>
        /// 配方包含的逻辑设备映射。
        /// </summary>
        public List<RecipeDeviceMappingModel> LogicalDevices { get; set; } = new List<RecipeDeviceMappingModel>();

        /// <summary>
        /// 创建一个新的人口统计/追溯副本，用于记录谁、在何时创建/批准了该配方。
        /// </summary>
        public RecipeTraceabilityEntry CreateTraceabilitySnapshot()
        {
            return new RecipeTraceabilityEntry
            {
                RecipeId = this.RecipeId,
                RecipeCode = this.RecipeCode,
                RecipeName = this.RecipeName,
                Version = this.Version,
                Author = this.Author,
                ApprovalStatus = this.ApprovalStatus,
                ApprovedBy = this.ApprovalInfo?.ApprovedBy,
                ApprovedAt = this.ApprovalInfo?.ApprovedAt,
                CapturedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// 从业务流中提取所有 RequiredDeviceKeys，兼容旧逻辑设备映射。
        /// </summary>
        public List<string> GetRequiredDeviceKeys()
        {
            var keys = new HashSet<string>();
            if (RequiredDeviceKeys != null)
                foreach (var k in RequiredDeviceKeys) keys.Add(k);

            if (LogicalDevices != null)
                foreach (var d in LogicalDevices)
                    if (!string.IsNullOrEmpty(d.LogicalDeviceName))
                        keys.Add(d.LogicalDeviceName);

            return new List<string>(keys);
        }
    }
}