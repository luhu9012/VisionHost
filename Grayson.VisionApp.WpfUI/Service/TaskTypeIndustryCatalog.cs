// Grayson.Vision.WpfUI/Service/TaskTypeIndustryCatalog.cs
// 任务类型·行业全量目录（2026-09-09 stage9-2 迭代）。
//
// 背景：机器视觉行业任务类型远不止三类；任务模板中心的"新建任务"向导出全量目录，
// 但**只落地三大任务族**（引导定位 / 深度学习推理 / 外观测量），其余条目标"规划中"禁用——
// 目录数据源独立于 TaskKind 枚举，后续族扩充分支时只需追加目录行 + 实现对应引擎。
using Grayson.Vision.Contracts.TaskLibrary.Models;
using System.Collections.Generic;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>行业任务类型目录条目（展示用，非模板模型）。</summary>
    public class IndustryTaskType
    {
        /// <summary>目录条目 ID（如 GD-01 / DL-02 / PLAN-OCR）</summary>
        public string Id { get; set; }

        public string Icon { get; set; }
        public string Name { get; set; }

        /// <summary>所属已实现任务族（规划中条目为 null）</summary>
        public TaskKind? FamilyKind { get; set; }

        /// <summary>族中文（引导定位/深度学习推理/外观测量/—）</summary>
        public string FamilyText { get; set; }

        /// <summary>是否已实现（true=可一键生成任务模板）</summary>
        public bool IsImplemented { get; set; }

        /// <summary>一句话说明（副标题）</summary>
        public string Description { get; set; }

        /// <summary>新建模板时的默认名称（可为空，编辑器内可改）</summary>
        public string DefaultTemplateName { get; set; }

        /// <summary>规划条目：提示未来族/能力（如 OCR/读码→识别读取族）</summary>
        public string RoadmapHint { get; set; }
    }

    /// <summary>行业全量任务类型目录（静态，纯展示数据源）。</summary>
    public static class TaskTypeIndustryCatalog
    {
        public static readonly IReadOnlyList<IndustryTaskType> All = new List<IndustryTaskType>
        {
            // ============ 已实现：引导定位族（TaskKind.PositioningGuidance） ============
            new IndustryTaskType
            {
                Id = "GD-01", Icon = "🧭", Name = "视觉引导定位",
                FamilyKind = TaskKind.PositioningGuidance, FamilyText = "引导定位",
                IsImplemented = true,
                Description = "相机定位目标 → 标定换算 → 引导机器人/运动轴取放、对位、纠偏（需相机+执行方案引擎）",
                DefaultTemplateName = "视觉引导定位模板"
            },
            new IndustryTaskType
            {
                Id = "GD-02", Icon = "🤖", Name = "引导取放 / 摆盘",
                FamilyKind = TaskKind.PositioningGuidance, FamilyText = "引导定位",
                IsImplemented = true,
                Description = "视觉引导吸嘴取料 → 摆盘放料（U 轴归正/偏心补偿等节拍由执行方案承载）",
                DefaultTemplateName = "视觉引导取放模板"
            },

            // ============ 已实现：深度学习推理族（TaskKind.DeepLearningInference） ============
            new IndustryTaskType
            {
                Id = "DL-01", Icon = "🎯", Name = "目标检测",
                FamilyKind = TaskKind.DeepLearningInference, FamilyText = "深度学习推理",
                IsImplemented = true,
                Description = "框出目标/缺陷（YOLO 等检测模型；本地图源+模型即可，无硬件依赖）",
                DefaultTemplateName = "目标检测任务模板"
            },
            new IndustryTaskType
            {
                Id = "DL-02", Icon = "🧩", Name = "缺陷分割（像素级）",
                FamilyKind = TaskKind.DeepLearningInference, FamilyText = "深度学习推理",
                IsImplemented = true,
                Description = "语义分割标出缺陷像素（破裂/脏污等；DLTool .hdl 端到端或 ONNX 分割模型）",
                DefaultTemplateName = "缺陷分割任务模板"
            },
            new IndustryTaskType
            {
                Id = "DL-03", Icon = "🏷️", Name = "图像分类",
                FamilyKind = TaskKind.DeepLearningInference, FamilyText = "深度学习推理",
                IsImplemented = true,
                Description = "整图类别判定（ok/ng、型号/品种；输出 Top-1 类别+置信度）",
                DefaultTemplateName = "图像分类任务模板"
            },
            new IndustryTaskType
            {
                Id = "DL-04", Icon = "🌡️", Name = "异常检测（无监督）",
                FamilyKind = TaskKind.DeepLearningInference, FamilyText = "深度学习推理",
                IsImplemented = true,
                Description = "仅 OK 样本训练，输出异常分数/热区（PatchCore 等）",
                DefaultTemplateName = "异常检测任务模板"
            },

            // ============ 已实现：外观测量族（TaskKind.AppearanceMeasurement） ============
            new IndustryTaskType
            {
                Id = "AM-01", Icon = "📏", Name = "尺寸 / 几何测量",
                FamilyKind = TaskKind.AppearanceMeasurement, FamilyText = "外观测量",
                IsImplemented = true,
                Description = "卡尺/找线/找圆等算子量取宽度、圆径、孔距并判公差（纯软件）",
                DefaultTemplateName = "尺寸测量任务模板"
            },
            new IndustryTaskType
            {
                Id = "AM-02", Icon = "⚪", Name = "Blob 面积 / 灰度统计",
                FamilyKind = TaskKind.AppearanceMeasurement, FamilyText = "外观测量",
                IsImplemented = true,
                Description = "连通域面积/数量、灰度均值等外观量（缺陷量化、色差辅助判定）",
                DefaultTemplateName = "外观量化测量模板"
            },

            // ============ 规划中：识别读取族等（行业全量，暂不落地） ============
            new IndustryTaskType
            {
                Id = "PLAN-OCR", Icon = "🔤", Name = "OCR 字符识别",
                FamilyKind = null, FamilyText = "规划中", IsImplemented = false,
                Description = "打印字符/批号读取（工业 OCR）",
                RoadmapHint = "后续归入「识别读取族」落地（K 层已具备 OCRTool）"
            },
            new IndustryTaskType
            {
                Id = "PLAN-BARCODE", Icon = "〰️", Name = "条码 / 二维码读取",
                FamilyKind = null, FamilyText = "规划中", IsImplemented = false,
                Description = "1D/2D 码读取与校验",
                RoadmapHint = "后续归入「识别读取族」（节点 ReadBarcode 已存在，族入口待接）"
            },
            new IndustryTaskType
            {
                Id = "PLAN-COLOR", Icon = "🎨", Name = "颜色识别",
                FamilyKind = null, FamilyText = "规划中", IsImplemented = false,
                Description = "颜色分类/色差判定",
                RoadmapHint = "后续归入「识别读取族」（ColorIdentify/ColorTool 已存在）"
            },
            new IndustryTaskType
            {
                Id = "PLAN-3D", Icon = "🧊", Name = "3D 定位 / 3D 测量",
                FamilyKind = null, FamilyText = "规划中", IsImplemented = false,
                Description = "点云定位、高度/平面度/体积测量",
                RoadmapHint = "需引入 3D 相机与点云处理链路"
            },
            new IndustryTaskType
            {
                Id = "PLAN-TEXTURE", Icon = "〽️", Name = "纹理 / 表面粗糙度",
                FamilyKind = null, FamilyText = "规划中", IsImplemented = false,
                Description = "纹理缺陷、粗糙度等级判定",
                RoadmapHint = "传统滤波+统计或专用模型，族归属待定"
            }
        };

        /// <summary>新建任务向导：已实现条目（可直接生成模板）</summary>
        public static IEnumerable<IndustryTaskType> Implemented => Filter(true);
        /// <summary>新建任务向导：规划中条目（展示禁用）</summary>
        public static IEnumerable<IndustryTaskType> Planned => Filter(false);

        private static IEnumerable<IndustryTaskType> Filter(bool implemented)
        {
            foreach (var item in All)
                if (item.IsImplemented == implemented)
                    yield return item;
        }
    }
}
