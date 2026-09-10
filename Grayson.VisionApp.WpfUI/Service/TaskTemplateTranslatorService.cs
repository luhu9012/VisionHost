// Grayson.Vision.WpfUI/Service/TaskTemplateTranslatorService.cs
// 执行方案(Engine/Process) → 任务模板 翻译服务（2026-09-09 stage9-2 迭代）。
//
// 迭代语义（不再兼容"工位直接装配 ProcessKey"的旧 UI 入口）：
//   · 引导定位族的 3 个内置执行方案（MahjongPick / MahjongDualNozzle / VisionPickPlace）
//     一次性翻译为「引导定位任务模板」落 TaskLibrary——模板中心是这些任务唯一的编辑/绑定入口，
//     EngineKey（=原 ProcessKey）只作为模板内部字段由绑定层回写工位；
//   · 独立视觉引擎（StandaloneVision）不是引导任务，不参与翻译，由深度学习/外观测量模板挂载。
//
// 服务内聚约定：任何"模板 → 工位应挂的 ProcessKey"决策都走 ResolveProcessKeyForTemplate，
// 保证引导族/独立族装载规则只有一份实现。
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Core.Processes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    public static class TaskTemplateTranslatorService
    {
        /// <summary>
        /// 模板绑定工位时应挂载的运行引擎键：
        ///   引导定位 → 模板 EngineKey（须为已注册引擎，否则 null=不挂引擎）；
        ///   深度学习/外观测量 → StandaloneVision（独立纯软件）。
        /// </summary>
        public static string ResolveProcessKeyForTemplate(TaskTemplateInfo tpl)
        {
            if (tpl == null) return null;
            if (tpl.Kind != TaskKind.PositioningGuidance)
                return StandaloneVisionProcess.ProcessKeyValue;

            var engineKey = tpl.EngineKey;
            if (string.IsNullOrWhiteSpace(engineKey)) return null;
            try
            {
                var host = App.StationHostRuntime as Grayson.Vision.Contracts.Station.Services.IStationHostRuntime
                           ?? Grayson.Vision.Core.Station.StationHostRuntime.GlobalInstance;
                var supported = host?.GetSupportedProcessKeys();
                if (supported != null && !supported.Contains(engineKey.Trim(), StringComparer.OrdinalIgnoreCase))
                    return null; // 目录外键：不给挂载（引擎未注册）
            }
            catch { /* 运行时不就绪：按目录键集合校验 */ }

            return StationProcessCatalog.Find(engineKey) != null ? engineKey.Trim() : null;
        }

        /// <summary>
        /// 把「内置引导定位执行方案」翻译为任务模板（幂等）：
        /// 已存在 EngineKey 相同的引导模板则跳过；返回本次新建的模板列表。
        /// 旧工位已不再需要迁移兼容（无历史数据），本方法只负责静态翻译落库。
        /// </summary>
        public static List<TaskTemplateInfo> EnsureTranslatedGuidanceTemplates()
        {
            var library = new TaskTemplateLibraryService();
            var existing = library.LoadAll();

            var created = new List<TaskTemplateInfo>();
            foreach (var engine in StationProcessCatalog.GetOptions())
            {
                // StandaloneVision 是独立视觉引擎（非引导），不翻译为引导模板
                if (string.Equals(engine.Key, StandaloneVisionProcess.ProcessKeyValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool already = existing.Any(t =>
                    t.Kind == TaskKind.PositioningGuidance &&
                    string.Equals(t.EngineKey, engine.Key, StringComparison.OrdinalIgnoreCase));
                if (already) continue;

                var tpl = new TaskTemplateInfo
                {
                    TemplateCode = library.GenerateCode(TaskKind.PositioningGuidance),
                    DisplayName = engine.Name,
                    Kind = TaskKind.PositioningGuidance,
                    DependencyMode = TaskDependencyMode.StationBound,
                    Status = TaskTemplateStatus.Published,
                    Version = "v1",
                    Summary = engine.Summary,
                    ApplicableMachines = engine.ApplicableTo,
                    EngineKey = engine.Key,
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.CameraSource,
                        CameraSlotKey = "TopCam"
                    },
                    VerdictToIo = true,
                    OutputContractText = "引导定位任务：视觉段输出目标坐标 → 执行方案引擎按节拍完成抓/放（坐标随配方链输出，产品差异由配方+示教承载）。",
                    TeachNote = engine.HasTeachPanel ? "该执行方案带参数/示教面板：绑定工位后在「工位工作台 → ④ 任务模板」就地示教。" : null,
                    Remark = $"由执行方案 {engine.Key} 翻译生成（stage9-2 迭代：执行方案=任务模板的运行时引擎，模板中心为唯一入口）"
                };
                library.Save(tpl);
                created.Add(tpl);
                existing.Add(tpl);
            }
            return created;
        }

        /// <summary>取库内某引导模板的引擎显示文本（列表/部署用）</summary>
        public static string EngineDisplayName(string engineKey)
        {
            return string.IsNullOrWhiteSpace(engineKey) ? "未选执行方案"
                : (StationProcessCatalog.Find(engineKey)?.Name ?? engineKey);
        }
    }
}
