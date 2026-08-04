using Grayson.Vision.Contracts.Flow.Factories;
using Grayson.Vision.Contracts.Flow.Helpers;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Recipe.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Grayson.Vision.Contracts.Recipe.DTOs
{
    public static class RecipeConverter
    {
        #region 1. RecipeModel -> VisionRecipeDto (保存)

        /// <summary>
        /// 完整将 RecipeModel 转换为极设持久化 DTO
        /// </summary>
        public static VisionRecipeDto ToDto(RecipeModel recipe)
        {
            if (recipe == null) return null;

            var dto = new VisionRecipeDto
            {
                // 1. 基础元数据
                RecipeId = recipe.RecipeId,
                RecipeCode = recipe.RecipeCode,
                RecipeName = recipe.RecipeName,
                ProductCategory = recipe.ProductCategory,
                Version = recipe.Version,
                IsActive = recipe.IsActive,
                Description = recipe.Description,
                Author = recipe.Author,
                LastModifiedTime = recipe.LastModifiedTime,

                // 2. 主流程转换
                MainProcess = ProcessToDto(recipe.MainProcess)
            };

            // 3. 子流程字典转换
            if (recipe.SubProcesses != null)
            {
                foreach (var kvp in recipe.SubProcesses)
                {
                    dto.SubProcesses[kvp.Key] = ProcessToDto(kvp.Value);
                }
            }

            return dto;
        }

        /// <summary>
        /// FlowProcessModel -> ProcessDto
        /// </summary>
        public static ProcessDto ProcessToDto(FlowProcessModel process)
        {
            if (process == null) return null;

            return new ProcessDto
            {
                ProcessId = process.ProcessId,
                ProcessName = process.ProcessName,
                Nodes = process.Nodes?.Select(node => new RecipeNodeDto
                {
                    NodeId = node.NodeId,
                    Type = node.Type,
                    DisplayName = node.DisplayName,
                    Enable = node.Enable,
                    PosX = node.PosX,
                    PosY = node.PosY,
                    ParameterModel = node.ParameterModel,

                    // 🌟【新增1】映射保存输入与输出端口（包含 PortId、PortName 和 RelativeX/Y 坐标）
                    InputPorts = node.InputPorts?.Select(p => ToPortDto(p)).ToList() ?? new List<NodePortDto>(),
                    OutputPorts = node.OutputPorts?.Select(p => ToPortDto(p)).ToList() ?? new List<NodePortDto>()
                }).ToList() ?? new List<RecipeNodeDto>(),

                Connections = process.Connections?.Select(conn =>
                new RecipeConnectionDto
                {
                    ConnectionId = conn.ConnectionId,
                    SourceNodeId = conn.SourceNode?.NodeId,
                    SourcePortId = conn.SourcePortId,
                    TargetNodeId = conn.TargetNode?.NodeId,
                    TargetPortId = conn.TargetPortId
                }).ToList() ?? new List<RecipeConnectionDto>()
            };
        }

        /// <summary>
        /// 单个 NodePort 转换为 NodePortDto
        /// </summary>
        private static NodePortDto ToPortDto(NodePort port)
        {
            if (port == null) return null;

            return new NodePortDto
            {
                PortId = port.PortId,
                PortName = port.PortName,
                RelativeX = port.RelativeX,
                RelativeY = port.RelativeY
            };
        }

        #endregion

        #region 2. VisionRecipeDto -> RecipeModel (加载还原)

        /// <summary>
        /// 将 VisionRecipeDto 完整恢复为带 VM 的 RecipeModel 实体
        /// </summary>
        public static RecipeModel ToModel(VisionRecipeDto dto)
        {
            if (dto == null) return null;

            var recipe = new RecipeModel
            {
                RecipeId = dto.RecipeId,
                RecipeCode = dto.RecipeCode,
                RecipeName = dto.RecipeName,
                ProductCategory = dto.ProductCategory,
                Version = dto.Version,
                IsActive = dto.IsActive,
                Description = dto.Description,
                Author = dto.Author,
                LastModifiedTime = dto.LastModifiedTime,

                // 恢复主流程 VM
                MainProcess = ProcessToModel(dto.MainProcess)
            };

            // 恢复子流程字典 VM
            if (dto.SubProcesses != null)
            {
                foreach (var kvp in dto.SubProcesses)
                {
                    recipe.SubProcesses[kvp.Key] = ProcessToModel(kvp.Value);
                }
            }

            return recipe;
        }

        /// <summary>
        /// ProcessDto -> FlowProcessModel
        /// </summary>
        public static FlowProcessModel ProcessToModel(ProcessDto dto)
        {
            if (dto == null) return null;

            var process = new FlowProcessModel
            {
                ProcessId = dto.ProcessId,
                ProcessName = dto.ProcessName
            };

            var nodeDict = new Dictionary<string, FlowNodeBase>();

            // 1. 还原节点 ViewModel 及其属性与端口
            if (dto.Nodes != null)
            {
                foreach (var nodeDto in dto.Nodes)
                {
                    var position = new Point2D(nodeDto.PosX, nodeDto.PosY);
                    FlowNode node = NodeFactory.CreateNodeInstance(nodeDto.Type, position, nodeDto.DisplayName);
                    if (node == null) continue;

                    node.NodeId = nodeDto.NodeId;
                    node.Enable = nodeDto.Enable;
                    if (nodeDto.ParameterModel != null)
                    {
                        node.ParameterModel = nodeDto.ParameterModel;
                    }

                    // 🌟【新增2】用 DTO 保存的端口数据恢复/覆写工厂创建的端口（匹配 PortId 并回填 RelativeX/Y）
                    RestoreNodePorts(node.InputPorts, nodeDto.InputPorts);
                    RestoreNodePorts(node.OutputPorts, nodeDto.OutputPorts);

                    process.Nodes.Add(node);
                    nodeDict[node.NodeId] = node;
                }
            }

            // 2. 还原连线及坐标绑定
            // RecipeConverter.cs -> ProcessToModel 中的连线还原部分

            if (dto.Connections != null)
            {
                foreach (var connDto in dto.Connections)
                {
                    if (string.IsNullOrEmpty(connDto.SourceNodeId) || string.IsNullOrEmpty(connDto.TargetNodeId))
                        continue;

                    // 1. 尝试按 NodeId 精确查找
                    nodeDict.TryGetValue(connDto.SourceNodeId, out var sourceNode);
                    nodeDict.TryGetValue(connDto.TargetNodeId, out var targetNode);

                    // 🌟 容错兜底：如果 JSON 里的 NodeId 错乱对不上，但节点数量正好符合拓扑关系
                    if (sourceNode == null && process.Nodes.Count >= 1) sourceNode = process.Nodes[0];
                    if (targetNode == null && process.Nodes.Count >= 2) targetNode = process.Nodes[1];

                    if (sourceNode != null && targetNode != null)
                    {
                        // 2. 匹配端口（找不到 PortId 就拿第一个默认端口）
                        var sourcePort = sourceNode.OutputPorts.FirstOrDefault(p => p.PortId == connDto.SourcePortId)
                                         ?? sourceNode.OutputPorts.FirstOrDefault();

                        var targetPort = targetNode.InputPorts.FirstOrDefault(p => p.PortId == connDto.TargetPortId)
                                         ?? targetNode.InputPorts.FirstOrDefault();

                        if (sourcePort != null && targetPort != null)
                        {
                            // 3. 将连线加入流程
                            var connection = CreateAndBindConnection(sourceNode, sourcePort, targetNode, targetPort, connDto.ConnectionId);
                            process.Connections.Add(connection);
                        }
                    }
                }
            }

            return process;
        }

        /// <summary>
        /// 将 DTO 保存的端口信息同步/还原给工厂创建的 NodePort 列表
        /// </summary>
        private static void RestoreNodePorts(ObservableCollection<NodePort> instancePorts, List<NodePortDto> portDtos)
        {
            if (instancePorts == null || portDtos == null || !portDtos.Any()) return;

            for (int i = 0; i < instancePorts.Count; i++)
            {
                var instancePort = instancePorts[i];

                // 优先按 PortName 或索引匹配 DTO 中的端口数据
                var portDto = portDtos.FirstOrDefault(p => p.PortName == instancePort.PortName)
                              ?? (i < portDtos.Count ? portDtos[i] : null);

                if (portDto != null)
                {
                    // 🌟 核心：将固化的 PortId 和相对坐标回填给实例端口
                    instancePort.PortId = portDto.PortId;
                    instancePort.RelativeX = portDto.RelativeX;
                    instancePort.RelativeY = portDto.RelativeY;
                }
            }
        }

        #endregion

        #region 3. 辅助方法

        public static ConnectionModel CreateAndBindConnection(
            FlowNodeBase sourceNode, NodePort sourcePort,
            FlowNodeBase targetNode, NodePort targetPort,
            string connectionId = null)
        {
            var connection = new ConnectionModel(sourceNode, sourcePort, targetNode, targetPort)
            {
                ConnectionId = connectionId ?? Guid.NewGuid().ToString("N")
            };

            // 使用最新的 RelativeX/Y 刷新两端起点与终点坐标
            UpdateConnectionCoordinates(connection);

            // 挂载节点位置改变事件监听
            EventHandler updatePosHandler = (s, e) => UpdateConnectionCoordinates(connection);
            sourceNode.OnPositionChanged += updatePosHandler;
            targetNode.OnPositionChanged += updatePosHandler;

            return connection;
        }

        public static void UpdateConnectionCoordinates(ConnectionModel conn)
        {
            if (conn == null) return;

            if (conn.SourceNode != null && conn.SourcePort != null)
            {
                conn.StartX = conn.SourceNode.PosX + conn.SourcePort.RelativeX;
                conn.StartY = conn.SourceNode.PosY + conn.SourcePort.RelativeY;
            }

            if (conn.TargetNode != null && conn.TargetPort != null)
            {
                conn.EndX = conn.TargetNode.PosX + conn.TargetPort.RelativeX;
                conn.EndY = conn.TargetNode.PosY + conn.TargetPort.RelativeY;
            }
        }

        #endregion

        #region 4. 单流程 ProcessDto 独立转换 (用于子流程/复合模板)

        /// <summary>
        /// 【保存子流程模板】：单独将 FlowProcessModel 转换为 ProcessDto
        /// </summary>
        public static ProcessDto ToProcessDto(FlowProcessModel process)
        {
            return ProcessToDto(process);
        }

        /// <summary>
        /// 【加载子流程模板】：将 ProcessDto 还原为独立的 FlowProcessModel
        /// </summary>
        public static FlowProcessModel ToProcessModel(ProcessDto dto)
        {
            return ProcessToModel(dto);
        }

        #endregion
    }
}