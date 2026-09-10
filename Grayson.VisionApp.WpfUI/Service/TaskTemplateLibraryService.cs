// Grayson.Vision.WpfUI/Service/TaskTemplateLibraryService.cs
// 任务模板库存储服务（T 层 UI 落地，2026-09-09）。
//   · TaskKindCatalog：任务族展示元数据（名称/图标/代码前缀/默认运行载体），UI 列表与编辑器共用，避免文案漂移；
//   · TaskTemplateLibraryService：Config\TaskLibrary\{TemplateCode}.json 增删改查（与 StationProfile/Recipe 的
//     "Config 目录 + JSON 单文件" 惯例一致；JSON 序列化 Newtonsoft，纯配置无运行时依赖）。
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>任务族展示目录（图标/文案/默认值集中定义，UI 消费统一走这里）。</summary>
    public static class TaskKindCatalog
    {
        public static string KindDisplay(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.PositioningGuidance: return "引导定位";
                case TaskKind.DeepLearningInference: return "深度学习推理";
                case TaskKind.AppearanceMeasurement: return "外观测量";
                default: return "未分类";
            }
        }

        public static string KindIcon(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.PositioningGuidance: return "🧭";
                case TaskKind.DeepLearningInference: return "🧠";
                case TaskKind.AppearanceMeasurement: return "📏";
                default: return "🗂️";
            }
        }

        /// <summary>模板代码前缀（TPL-{prefix}-###）</summary>
        public static string KindCodePrefix(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.PositioningGuidance: return "GD";
                case TaskKind.DeepLearningInference: return "DL";
                case TaskKind.AppearanceMeasurement: return "AM";
                default: return "XX";
            }
        }

        /// <summary>该族新建模板的默认运行载体（引导定位=工位联动；DL/测量=独立纯软件，可改）</summary>
        public static TaskDependencyMode DefaultDependency(TaskKind kind)
        {
            return kind == TaskKind.PositioningGuidance ? TaskDependencyMode.StationBound : TaskDependencyMode.Standalone;
        }

        /// <summary>族的一句话能力说明（新建向导副标题）</summary>
        public static string KindHint(TaskKind kind)
        {
            switch (kind)
            {
                case TaskKind.PositioningGuidance:
                    return "相机 + 标定 + 执行方案引擎 → 输出机器人引导坐标（需工位硬件，工位监视【启动】驱动）";
                case TaskKind.DeepLearningInference:
                    return "本地图片源 + 训练模型（ONNX/DLTool）→ 检测框/掩膜/类别/异常（纯软件独立运行，无需相机/运控/PLC）";
                case TaskKind.AppearanceMeasurement:
                    return "本地图片源 + 卡尺/找线/找圆等测量算子 → 尺寸数值 + OK/NG 判定（纯软件独立运行）";
                default: return "";
            }
        }

        public static string DependencyDisplay(TaskDependencyMode mode)
        {
            return mode == TaskDependencyMode.StationBound ? "工位联动" : "独立纯软件";
        }

        public static string StatusDisplay(TaskTemplateStatus s)
        {
            switch (s)
            {
                case TaskTemplateStatus.Draft: return "草稿";
                case TaskTemplateStatus.Published: return "已发布";
                case TaskTemplateStatus.Retired: return "已停用";
                default: return "未知";
            }
        }

        public static string ImageSourceDisplay(TaskImageSourceKind k)
        {
            switch (k)
            {
                case TaskImageSourceKind.CameraSource: return "相机源";
                case TaskImageSourceKind.LocalFolder: return "本地文件夹";
                case TaskImageSourceKind.LocalFile: return "本地单图";
                default: return "未配置";
            }
        }

        public static string ModelKindDisplay(DlModelKind k)
        {
            switch (k)
            {
                case DlModelKind.Detection: return "目标检测";
                case DlModelKind.Segmentation: return "语义分割";
                case DlModelKind.Classification: return "分类";
                case DlModelKind.AnomalyDetection: return "异常检测";
                default: return "其他";
            }
        }

        public static string FrameworkDisplay(DlFrameworkKind k)
        {
            switch (k)
            {
                case DlFrameworkKind.OnnxRuntime: return "ONNX Runtime";
                case DlFrameworkKind.HalconDlTool: return "HALCON DLTool";
                default: return "其他";
            }
        }
    }

    /// <summary>任务模板库存储：Config\TaskLibrary\{TemplateCode}.json。</summary>
    public class TaskTemplateLibraryService
    {
        public string RootDir { get; }

        public TaskTemplateLibraryService(string baseDir = null)
        {
            baseDir = string.IsNullOrWhiteSpace(baseDir)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDir;
            RootDir = Path.Combine(baseDir, "Config", "TaskLibrary");
        }

        private void EnsureDir() { Directory.CreateDirectory(RootDir); }

        public List<TaskTemplateInfo> LoadAll()
        {
            EnsureDir();
            var result = new List<TaskTemplateInfo>();
            foreach (var file in Directory.GetFiles(RootDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var item = JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file));
                    if (item == null || string.IsNullOrWhiteSpace(item.TemplateCode)) continue;
                    result.Add(item);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TaskLibrary] 读取失败 {file}: {ex.Message}");
                }
            }
            return result.OrderBy(t => t.TemplateCode, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public TaskTemplateInfo Load(string templateCode)
        {
            if (string.IsNullOrWhiteSpace(templateCode)) return null;
            var file = Path.Combine(RootDir, templateCode + ".json");
            return File.Exists(file) ? JsonConvert.DeserializeObject<TaskTemplateInfo>(File.ReadAllText(file)) : null;
        }

        public bool Exists(string templateCode)
        {
            return !string.IsNullOrWhiteSpace(templateCode)
                && File.Exists(Path.Combine(RootDir, templateCode + ".json"));
        }

        /// <summary>保存（新模板须先有 TemplateCode；库内主键=文件名）</summary>
        public bool Save(TaskTemplateInfo template)
        {
            if (template == null || string.IsNullOrWhiteSpace(template.TemplateCode)) return false;
            EnsureDir();
            template.UpdatedTime = DateTime.Now;
            File.WriteAllText(Path.Combine(RootDir, template.FileName),
                JsonConvert.SerializeObject(template, Formatting.Indented));
            return true;
        }

        public bool Delete(string templateCode)
        {
            if (string.IsNullOrWhiteSpace(templateCode)) return false;
            var file = Path.Combine(RootDir, templateCode + ".json");
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }

        /// <summary>按族生成下一个可用代码：TPL-{GD|DL|AM}-{3位流水}。</summary>
        public string GenerateCode(TaskKind kind)
        {
            var prefix = TaskKindCatalog.KindCodePrefix(kind);
            int max = 0;
            foreach (var t in LoadAll())
            {
                if (t.TemplateCode == null || !t.TemplateCode.StartsWith("TPL-" + prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
                var tail = t.TemplateCode.Substring(("TPL-" + prefix + "-").Length);
                if (int.TryParse(tail, out int n) && n > max) max = n;
            }
            return string.Format("TPL-{0}-{1:D3}", prefix, max + 1);
        }
    }
}
