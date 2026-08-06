using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Recipe.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Recipe.Services
{
    public static class RecipeDeviceExtractor
    {
        /// <summary>
        /// 从流程拓扑中自动提取所有绑定的逻辑设备
        /// </summary>
        public static List<RecipeDeviceMappingModel> ExtractLogicalDevices(FlowProcessModel process)
        {
            if (process?.Nodes == null) return new List<RecipeDeviceMappingModel>();

            var devices = new List<RecipeDeviceMappingModel>();

            // 递归扫描流程及其子流程中的节点
            ScanProcessNodes(process, devices);

            // 按 LogicalDeviceId 去重返回
            return devices
                .Where(d => !string.IsNullOrEmpty(d.LogicalDeviceId))
                .GroupBy(d => d.LogicalDeviceId)
                .Select(g => g.First())
                .ToList();
        }

        private static void ScanProcessNodes(FlowProcessModel process, List<RecipeDeviceMappingModel> devices)
        {
            foreach (var node in process.Nodes)
            {
                // 1. 处理复合/子流程节点
                if (node is CompositeFlowNode compositeNode && compositeNode.SubProcess != null)
                {
                    ScanProcessNodes(compositeNode.SubProcess, devices);
                }

                // 2. 读取节点绑定的 ParameterModel 实例
                var paramObj = node.ParameterModel;
                if (paramObj == null) continue;

                // 3. 反射获取带有 [LogicalDeviceBinding] 特性的属性
                var properties = paramObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<LogicalDeviceBindingAttribute>() != null);

                // 循环遍历当前节点参数类（如 AcquireImageParam）中所有打上了 [LogicalDeviceBinding] 特性的属性
                foreach (var prop in properties)
                {
                    // 1. 获取该属性上挂载的 [LogicalDeviceBinding] 特性实例，用于读取元数据（如设备类型、规格描述等）
                    var attr = prop.GetCustomAttribute<LogicalDeviceBindingAttribute>();

                    // 2. 通过反射从参数实例对象（paramObj）中，提取该属性当前实际填写的字符串值（例如获取到 CameraAlias 的值 "TopCam"）
                    var deviceId = prop.GetValue(paramObj)?.ToString();

                    // 3. 校验设备标识是否有效：只有当用户在参数面板配置了具体的设备 ID 时才进行提取
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        // 4. 构建设备映射模型对象，并加入到临时设备列表中
                        devices.Add(new RecipeDeviceMappingModel
                        {
                            // 逻辑设备唯一标识：优先取属性运行时配置的值（如 "TopCam"），若属性值为空则退而取特性上预设的默认 ID
                            LogicalDeviceId = deviceId,

                            // 逻辑设备显示名称：拼接“节点名称-属性名称”（如 "相机采集节点-CameraAlias"），方便 UI 识别是哪个节点引用的
                            LogicalDeviceName = attr.DeviceName ?? $"{node.DisplayName}-{prop.Name}",

                            // 设备类别：取特性中定义的大类枚举字符串（如 "Camera"、"Light" 等）
                            LogicalDeviceType = attr.DeviceType.ToString(),

                            // 设备规格要求：取特性中定义的默认硬件规格（如 "面阵工业相机"）
                            RequiredSpec = attr.RequiredSpec
                        });
                    }
                }
            }
        }
    }
}
