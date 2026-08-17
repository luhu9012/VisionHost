using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Recipe.Models;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.Core.Recipe
{
    /// <summary>
    /// 从业务蓝图中提取所需逻辑设备键。
    /// 支持从节点属性、逻辑设备映射、以及后续属性标记扩展。
    /// </summary>
    public static class RecipeDeviceExtractor
    {
        /// <summary>
        /// 从 RecipeModel 提取所有 RequiredDeviceKeys。
        /// </summary>
        public static List<string> ExtractRequiredDeviceKeys(RecipeModel recipe)
        {
            if (recipe == null) return new List<string>();

            var keys = new HashSet<string>(recipe.GetRequiredDeviceKeys());

            // 遍历业务流主流程与子流程节点，拾取节点参数中的逻辑设备名
            var allProcesses = new List<FlowProcessModel>();
            if (recipe.MainProcess != null) allProcesses.Add(recipe.MainProcess);
            if (recipe.SubProcesses != null)
                allProcesses.AddRange(recipe.SubProcesses.Values);

            foreach (var process in allProcesses)
            {
                foreach (var node in process?.Nodes ?? Enumerable.Empty<FlowNodeBase>())
                {
                    ExtractFromNode(node, keys);
                }
            }

            return keys.ToList();
        }

        private static void ExtractFromNode(FlowNodeBase node, HashSet<string> keys)
        {
            if (node?.ParameterModel == null) return;

            var paramType = node.ParameterModel.GetType();
            var properties = paramType.GetProperties();

            foreach (var prop in properties)
            {
                var value = prop.GetValue(node.ParameterModel);
                if (value is string str && !string.IsNullOrWhiteSpace(str))
                {
                    // 简单启发式：属性名包含 DeviceKey / CameraKey / RobotKey / PlcKey 等
                    var name = prop.Name.ToLowerInvariant();
                    if (name.Contains("devicekey") || name.Contains("camerakey") ||
                        name.Contains("robotkey") || name.Contains("plckey") ||
                        name.Contains("motionkey") || name.Contains("iokey"))
                    {
                        keys.Add(str.Trim());
                    }
                }
            }
        }
    }
}
