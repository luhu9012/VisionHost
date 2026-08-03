using Grayson.Vision.Contracts.Business.Enums;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe.DTOs
{
    /// <summary>
    /// 配方根持久化 DTO（对应完整 RecipeModel）
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
        public DateTime LastModifiedTime { get; set; }
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

        // 🌟 新增：保存节点上的端口元数据（包含相对坐标）
        public List<NodePortDto> InputPorts { get; set; } = new List<NodePortDto>();
        public List<NodePortDto> OutputPorts { get; set; } = new List<NodePortDto>();
    }

    public class NodePortDto
    {
        public string PortId { get; set; }
        public string PortName { get; set; }
        public double RelativeX { get; set; }
        public double RelativeY { get; set; }
    }

    /// <summary>
    /// 连线持久化 DTO
    /// </summary>
    public class RecipeConnectionDto
    {
        public string ConnectionId { get; set; }
        public string SourceNodeId { get; set; }
        public string SourcePortId { get; set; }
        public string TargetNodeId { get; set; }
        public string TargetPortId { get; set; }
    }
}