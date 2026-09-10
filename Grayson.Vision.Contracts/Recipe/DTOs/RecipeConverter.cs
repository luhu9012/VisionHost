using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Factories;
using Grayson.Vision.Contracts.Flow.Helpers;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;
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
                CreatedTime = recipe.CreatedTime,
                LastModifiedTime = recipe.LastModifiedTime,

                // 审批域（2026-09-05 补齐：审批状态/审批记录/生效/修订/锁定 不再保存即丢）
                ApprovalStatus = recipe.ApprovalStatus,
                EffectiveFrom = recipe.EffectiveFrom,
                ChangeReason = recipe.ChangeReason,
                Revision = recipe.Revision,
                IsLocked = recipe.IsLocked,
                ApprovalInfo = CloneApprovalInfo(recipe.ApprovalInfo),
                RequiredDeviceKeys = recipe.RequiredDeviceKeys != null
                    ? new List<string>(recipe.RequiredDeviceKeys)
                    : new List<string>(),

                // 2. 主流程转换
                MainProcess = ProcessToDto(recipe.MainProcess),

                // 3. 逻辑设备映射转换
                LogicalDevices = recipe.LogicalDevices?.Select(device => new RecipeDeviceMappingModel
                {
                    LogicalDeviceId = device.LogicalDeviceId,
                    LogicalDeviceName = device.LogicalDeviceName,
                    LogicalDeviceType = device.LogicalDeviceType,
                    RequiredSpec = device.RequiredSpec,
                    Role = device.Role,
                    MappedDeviceId = device.MappedDeviceId
                }).ToList() ?? new List<RecipeDeviceMappingModel>(),

                // 4. 工艺参数（2026-09-05 补齐：不再随保存丢弃；工位运行配置已整卡移除，见 RecipeModel 注）
                ProcessParameters = recipe.ProcessParameters
            };

            // 4. 子流程字典转换
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
                    DisplayName = ResolveNodeDisplayName(node.Type, node.DisplayName),
                    Enable = node.Enable,
                    PosX = node.PosX,
                    PosY = node.PosY,
                    ParameterModel = node.ParameterModel
                }).ToList() ?? new List<RecipeNodeDto>(),

                Connections = process.Connections?.Select(conn =>
                new RecipeConnectionDto
                {
                    ConnectionId = conn.ConnectionId,
                    SourceNodeId = conn.SourceNode?.NodeId,
                    SourcePortId = conn.SourcePortId,
                    // 🌟 优先实时解析端口名；端口对象被动态端口机制替换后实时解析可能失败，回退到创建时缓存的端口名
                    SourcePortName = conn.SourcePort?.PortName ?? conn.SourcePortName,
                    TargetNodeId = conn.TargetNode?.NodeId,
                    TargetPortId = conn.TargetPortId,
                    TargetPortName = conn.TargetPort?.PortName ?? conn.TargetPortName
                }).ToList() ?? new List<RecipeConnectionDto>()
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
                CreatedTime = dto.CreatedTime != default(DateTime) ? dto.CreatedTime : dto.LastModifiedTime,
                LastModifiedTime = dto.LastModifiedTime,

                // 审批域（2026-09-05 补齐：旧 JSON 无这些字段 → 保持 RecipeModel 默认值，无损兼容）
                ApprovalStatus = dto.ApprovalStatus,
                EffectiveFrom = dto.EffectiveFrom,
                ChangeReason = dto.ChangeReason,
                Revision = dto.Revision > 0 ? dto.Revision : 1,
                IsLocked = dto.IsLocked,
                ApprovalInfo = CloneApprovalInfo(dto.ApprovalInfo),
                RequiredDeviceKeys = dto.RequiredDeviceKeys != null
                    ? new List<string>(dto.RequiredDeviceKeys)
                    : new List<string>(),

                // 恢复主流程 VM
                MainProcess = ProcessToModel(dto.MainProcess),

                // 恢复逻辑设备映射
                LogicalDevices = dto.LogicalDevices?.Select(device => new RecipeDeviceMappingModel
                {
                    LogicalDeviceId = device.LogicalDeviceId,
                    LogicalDeviceName = device.LogicalDeviceName,
                    LogicalDeviceType = device.LogicalDeviceType,
                    RequiredSpec = device.RequiredSpec,
                    Role = device.Role,
                    MappedDeviceId = device.MappedDeviceId
                }).ToList() ?? new List<RecipeDeviceMappingModel>(),

                // 工艺参数（旧 JSON 缺失 → 保留模型默认实例）
                ProcessParameters = dto.ProcessParameters ?? new ProcessParameterSet()
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
                    string displayName = ResolveNodeDisplayName(nodeDto.Type, nodeDto.DisplayName);
                    FlowNodeBase node = NodeFactory.CreateNodeInstance(nodeDto.Type, position, displayName);
                    if (node == null) continue;

                    node.NodeId = nodeDto.NodeId;
                    node.Enable = nodeDto.Enable;

                    // 🌟 从 NodeFactory 注册表拿到该节点对应的真实 ParamType
                    Type targetParamType = NodeFactory.GetParameterType(nodeDto.Type);

                    if (targetParamType != null && nodeDto.ParameterModel != null)
                    {
                        // 实例化干净的真实参数 Model 实例[cite: 18]
                        var paramInstance = Activator.CreateInstance(targetParamType);
                        var rawModel = nodeDto.ParameterModel;

                        // 🌟 情况 1：rawModel 是 Newtonsoft.Json.Linq.JObject (JObject 实现了 IEnumerable)
                        if (rawModel is System.Collections.IEnumerable enumerable && !(rawModel is string))
                        {
                            foreach (var item in enumerable)
                            {
                                // 反射读取 JProperty 的 Name 与 Value
                                var itemType = item.GetType();
                                var nameProp = itemType.GetProperty("Name");
                                var valProp = itemType.GetProperty("Value");

                                if (nameProp != null && valProp != null)
                                {
                                    string propName = nameProp.GetValue(item)?.ToString();
                                    object jTokenVal = valProp.GetValue(item);

                                    if (!string.IsNullOrEmpty(propName) && jTokenVal != null)
                                    {
                                        var targetProp = targetParamType.GetProperty(propName);
                                        if (targetProp != null && targetProp.CanWrite)
                                        {
                                            TryAssignPropertyValue(targetProp, paramInstance, jTokenVal);
                                        }
                                    }
                                }
                            }
                            node.ParameterModel = paramInstance;
                        }
                        // 🌟 情况 2：rawModel 已经是 IDictionary
                        else if (rawModel is System.Collections.IDictionary dict)
                        {
                            foreach (var prop in targetParamType.GetProperties())
                            {
                                if (prop.CanWrite && dict.Contains(prop.Name))
                                {
                                    TryAssignPropertyValue(prop, paramInstance, dict[prop.Name]);
                                }
                            }
                            node.ParameterModel = paramInstance;
                        }
                        // 🌟 情况 3：类型刚好一致
                        else if (rawModel.GetType() == targetParamType)
                        {
                            node.ParameterModel = rawModel;
                        }
                    }

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

                    // 1. 按 NodeId 精确查找
                    nodeDict.TryGetValue(connDto.SourceNodeId, out var sourceNode);
                    nodeDict.TryGetValue(connDto.TargetNodeId, out var targetNode);

                    // 🌟【Bug修复】移除旧的"兜底重挂到 nodes[0]/nodes[1]"容错逻辑：
                    //    该逻辑会在 NodeId 对不上时把连线静默错挂到前两个节点上，
                    //    造成"相机采集→形状匹配"重复堆积、其它连线凭空消失的脏数据。
                    //    现在改为跳过并记录警告，保证不产生错误拓扑。
                    if (sourceNode == null || targetNode == null)
                    {
                        LogBus.Warn("RecipeConverter",
                            $"连线还原失败：SourceNodeId={connDto.SourceNodeId} / TargetNodeId={connDto.TargetNodeId} 未匹配到节点，已跳过该连线。");
                        continue;
                    }

                    // 2. 匹配端口（优先按端口名，其次按 PortId，最后兜底首端口）
                    var sourcePort = sourceNode.OutputPorts.FirstOrDefault(p => p.PortName == connDto.SourcePortName)
                                     ?? sourceNode.OutputPorts.FirstOrDefault(p => p.PortId == connDto.SourcePortId)
                                     ?? sourceNode.OutputPorts.FirstOrDefault();

                    var targetPort = targetNode.InputPorts.FirstOrDefault(p => p.PortName == connDto.TargetPortName)
                                     ?? targetNode.InputPorts.FirstOrDefault(p => p.PortId == connDto.TargetPortId)
                                     ?? targetNode.InputPorts.FirstOrDefault();

                    if (sourcePort != null && targetPort != null)
                    {
                        // 🌟【Bug修复】去重守卫：同一对 (源节点+源端口 → 目标节点+目标端口) 只保留一条连线，
                        //    自动修复历史保存产生的重复连线脏数据。
                        bool isDuplicate = process.Connections.Any(c =>
                            c.SourceNode == sourceNode && c.TargetNode == targetNode &&
                            c.SourcePortId == sourcePort.PortId && c.TargetPortId == targetPort.PortId);

                        if (isDuplicate)
                        {
                            LogBus.Warn("RecipeConverter",
                                $"检测到重复连线 [{sourceNode.DisplayName}.{sourcePort.PortName} -> {targetNode.DisplayName}.{targetPort.PortName}]，已自动去重。");
                            continue;
                        }

                        // 3. 将连线加入流程
                        var connection = CreateAndBindConnection(sourceNode, sourcePort, targetNode, targetPort, connDto.ConnectionId);
                        process.Connections.Add(connection);
                    }
                    else
                    {
                        LogBus.Warn("RecipeConverter",
                            $"连线还原失败：端口匹配为空 (源端口 {connDto.SourcePortName} / 目标端口 {connDto.TargetPortName})，已跳过该连线。");
                    }
                }
            }

            return process;
        }
        /// <summary>
        /// 安全的属性反射赋值（支持 JToken / 基础类型安全转换，兼容 .NET Framework 4.7.2）
        /// </summary>
        private static void TryAssignPropertyValue(System.Reflection.PropertyInfo prop, object targetObj, object rawValue)
        {
            if (prop == null || targetObj == null || rawValue == null) return;

            try
            {
                // 如果 rawValue 是 JToken/JValue，通过反射调用 JToken.ToObject 或 Value
                object actualValue = rawValue;
                var rawType = rawValue.GetType();

                if (rawType.Namespace != null && rawType.Namespace.StartsWith("Newtonsoft.Json"))
                {
                    // 通过反射调用 JToken.ToObject(prop.PropertyType)
                    var toObjectMethod = rawType.GetMethods()
                        .FirstOrDefault(m => m.Name == "ToObject" && m.IsGenericMethod && m.GetParameters().Length == 0);

                    if (toObjectMethod != null)
                    {
                        var genericMethod = toObjectMethod.MakeGenericMethod(prop.PropertyType);
                        actualValue = genericMethod.Invoke(rawValue, null);
                    }
                    else
                    {
                        // 兜底：获取 JValue.Value
                        var valProp = rawType.GetProperty("Value");
                        if (valProp != null)
                        {
                            actualValue = valProp.GetValue(rawValue);
                        }
                    }
                }

                if (actualValue != null)
                {
                    var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                    // 枚举类型转换
                    if (targetType.IsEnum)
                    {
                        var enumVal = Enum.Parse(targetType, actualValue.ToString());
                        prop.SetValue(targetObj, enumVal);
                    }
                    else
                    {
                        var convertedVal = Convert.ChangeType(actualValue, targetType);
                        prop.SetValue(targetObj, convertedVal);
                    }
                }
            }
            catch
            {
                // 忽略复杂集合转换异常或无法转换的值
            }
        }
        #endregion

        #region 3. 辅助方法

        /// <summary>
        /// 深拷贝审批记录（含 History 流水）。null 输入返回全新空实例，
        /// 保证 RecipeModel 缺省 ApprovalInfo 非 null（UI 双向绑定安全）。
        /// </summary>
        private static RecipeApprovalInfo CloneApprovalInfo(RecipeApprovalInfo src)
        {
            if (src == null) return new RecipeApprovalInfo();
            return new RecipeApprovalInfo
            {
                SubmittedBy = src.SubmittedBy,
                SubmittedAt = src.SubmittedAt,
                ApprovedBy = src.ApprovedBy,
                ApprovedAt = src.ApprovedAt,
                LastActionAt = src.LastActionAt,
                ApprovalComment = src.ApprovalComment,
                RejectionReason = src.RejectionReason,
                ElectronicSignature = src.ElectronicSignature,
                History = src.History?.Select(h => new RecipeApprovalHistoryEntry
                {
                    ActionAt = h.ActionAt,
                    ActionBy = h.ActionBy,
                    Action = h.Action,
                    Comment = h.Comment,
                    ElectronicSignature = h.ElectronicSignature
                }).ToList() ?? new List<RecipeApprovalHistoryEntry>()
            };
        }

        private static string ResolveNodeDisplayName(NodeType type, string persistedDisplayName)
        {
            if (!string.IsNullOrWhiteSpace(persistedDisplayName))
                return persistedDisplayName;

            var metaAttr = type.GetAttribute<NodeFieldMetaAttribute>();
            if (!string.IsNullOrWhiteSpace(metaAttr?.ShortName))
                return metaAttr.ShortName;

            var description = type.GetDescription();
            return !string.IsNullOrWhiteSpace(description) ? description : type.ToString();
        }

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
            // （节点位置监听已在 ConnectionModel 构造函数中挂载，这里不再重复订阅，避免双重回调）
            UpdateConnectionCoordinates(connection);

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