// Grayson.Vision.WpfUI/Service/DlDemoTaskFactory.cs
// HTH《Demo_深度学习》两个 HALCON DL 示例的「内嵌演示包落地工厂」（2026-09-09 stage9-2）。
//
// 翻译语义（用户约定：不加载/执行 .hdev，全部翻译为 C# + halcondotnet 通道的任务）：
//   翻译成品（模型 .hdl/.hdict + 精选演示图）已一次性内嵌进本仓库
//   Grayson.VisionApp.WpfUI/Assets/DLDemo/{MDL-SEG-001|MDL-CLS-001}\ 并随构建输出复制，
//   不再依赖任何外部示例目录（D:\HTH\… 后续删除不影响）。
//   本工厂只在运行时把内嵌资产幂等落地为平台数据：
//   · 模型资产：Assets → Config\Models\{AssetCode}\（资产入库 ModelRegistry）；
//   · 演示图像：Assets → Config\DemoData\{药片缺陷分割|镁片外观分类}\Images\；
//   · 配方（AI 节点编排）：ReadImageFile[本地文件夹批处理] → HalconDlInference[HALCON 原生 DL]
//     → 落 Recipes\{RecipeCode}.json（与 FlowEdit 同一 DTO/转换器，FlowEdit 可打开再编排）；
//   · 任务模板：深度学习族模板 引用 模型资产 + 配方 + 本地图像源 + 任务级判据（VerdictRule）。
// 全程幂等：已存在（按 AssetCode/RecipeCode/ModelAssetCode）则跳过。
// 触发：模板中心每次打开时自动执行（与引导模板种子并列，静默无按钮）。
// 新增示例：把成品放入 Assets\DLDemo\{新AssetCode}\（model_opt.hdl[+preprocess.hdict]+Images\），
//   在此 Specs 增加一条并重新编译；若含新算子/自定义后处理还需扩展 HalconDlNativeTool/节点。
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Factories;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Nodes.All.Identification.HalconDlInference;
using Grayson.Vision.Repository.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>单个内嵌演示示例的定义（模型元数据 / 节点编排参数）。</summary>
    internal class DlDemoSpec
    {
        public string DemoKey;             // 落地目录名（药片缺陷分割 / 镁片外观分类）
        public string ModelAssetCode;      // MDL-SEG-001 / MDL-CLS-001（= Assets\DLDemo 子目录名）
        public DlModelKind ModelKind;
        public string RecipeCode;
        public string TemplateTitle;
        public string Summary;
        public int? ResizeWidth, ResizeHeight;
        public string DefectClassIds;      // 分割
        public double MinDefectArea;
        public string OkClassName, NgClassName; // 分类
        public string ModelNote;
    }

    /// <summary>把内嵌的 HTH DL 演示包落地为「模型资产 + 配方链 + 任务模板」（幂等，自动触发）。</summary>
    public static class DlDemoTaskFactory
    {
        /// <summary>内嵌资产根（随构建复制到输出目录，与 D:\HTH 源目录解耦）。</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "DLDemo");

        private static readonly List<DlDemoSpec> Specs = new List<DlDemoSpec>
        {
            new DlDemoSpec
            {
                DemoKey = "药片缺陷分割", ModelAssetCode = "MDL-SEG-001",
                ModelKind = DlModelKind.Segmentation,
                RecipeCode = "RCP-DL-SEG-001",
                TemplateTitle = "药片缺陷分割（破裂/脏污）·HTH Demo 翻译",
                Summary = "HALCON DLTool 分割模型：薄荷药片 破裂(ID1)/脏污(ID2) 像素级检出，面积≥100px 判 NG（632×300 预处理，纯软件独立运行）",
                ResizeWidth = 632, ResizeHeight = 300,
                DefectClassIds = "1,2", MinDefectArea = 100,
                ModelNote = "HTH《Demo_深度学习/分割/药片检测》模型_训练-260829 导出；类别：0=good,1=破裂,2=脏污"
            },
            new DlDemoSpec
            {
                DemoKey = "镁片外观分类", ModelAssetCode = "MDL-CLS-001",
                ModelKind = DlModelKind.Classification,
                RecipeCode = "RCP-DL-CLS-001",
                TemplateTitle = "镁片外观分类（ok/ng）·HTH Demo 翻译",
                Summary = "HALCON DLTool 分类模型：镁片整图分类 ok(合格)/ng(裂纹等缺陷)，Top-1 置信度判定；无归一化仅转 real，纯软件独立运行",
                OkClassName = "ok", NgClassName = "ng",
                ModelNote = "HTH《Demo_深度学习/分类/镁片》镁片检测-老师 训练-260830 导出；类别：ng / ok"
            }
        };

        /// <summary>落地全部内嵌 DL 演示包（幂等）。返回人类可读结果摘要。</summary>
        public static string EnsureDemoTasks()
        {
            var lines = new List<string>();
            int createdModels = 0, createdRecipes = 0, createdTemplates = 0;

            foreach (var spec in Specs)
            {
                lines.Add(LandOne(spec, ref createdModels, ref createdRecipes, ref createdTemplates));
            }

            return string.Join(Environment.NewLine, lines)
                   + Environment.NewLine
                   + $"合计：模型资产入库 {createdModels}、配方落地 {createdRecipes}、任务模板生成 {createdTemplates}。";
        }

        // ======================================================================
        // 单示例落地流水线（内嵌资产 → 模型 → 图像 → 配方 → 模板）
        // ======================================================================

        private static string LandOne(DlDemoSpec spec, ref int createdModels, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = Path.Combine(EmbeddedRoot, spec.ModelAssetCode);
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\DLDemo）";

            // ---------- 1) 模型资产入库（内嵌固定文件名） ----------
            var registry = new ModelRegistryService();
            string hdlSrc = Path.Combine(embedDir, "model_opt.hdl");
            string hdictSrc = Path.Combine(embedDir, "model_preprocess_params.hdict");
            if (!File.Exists(hdlSrc))
                return $"⚠ [{spec.DemoKey}] 内嵌目录无 model_opt.hdl，跳过。";

            var asset = registry.Load(spec.ModelAssetCode);
            if (asset == null)
            {
                var dir = Path.Combine(registry.ModelRootDir, spec.ModelAssetCode);
                Directory.CreateDirectory(dir);
                string hdlLib = CopyAs(hdlSrc, dir, "model_opt.hdl");
                string hdictLib = File.Exists(hdictSrc) ? CopyAs(hdictSrc, dir, "model_preprocess_params.hdict") : null;

                asset = new ModelAssetInfo
                {
                    AssetCode = spec.ModelAssetCode,
                    DisplayName = spec.DemoKey + "模型",
                    ModelKind = spec.ModelKind,
                    Framework = DlFrameworkKind.HalconDlTool,
                    ApplicableTaskKind = TaskKind.DeepLearningInference,
                    FileName = Path.GetFileName(hdlLib),
                    AuxFileNames = hdictLib != null ? new List<string> { Path.GetFileName(hdictLib) } : new List<string>(),
                    ClassNames = spec.ModelKind == DlModelKind.Segmentation
                        ? new List<string> { "good", "破裂", "脏污" }
                        : new List<string> { "ng", "ok" },
                    DefaultThreshold = null,
                    MetricsText = spec.ModelKind == DlModelKind.Segmentation ? "DLTool 分割模型（训练-260829 导出）" : "DLTool 分类模型（训练-260830 导出）",
                    Status = ModelAssetStatus.Ready,
                    Remark = spec.ModelNote
                };
                registry.Save(asset);
                createdModels++;
                step.Add($"✓ 模型资产 {spec.ModelAssetCode} 已入库（model_opt.hdl" + (hdictLib != null ? " + .hdict" : "") + "）");
            }
            else
            {
                step.Add($"· 模型资产 {spec.ModelAssetCode} 已存在，跳过复制");
            }

            // ---------- 2) 演示图像落地（扁平化；批处理节点只扫顶层） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir);

            // ---------- 3) 配方落地（ReadImageFile → HalconDlInference，AI 节点编排） ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → HalconDlInference，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                // 🌟 幂等修复（2026-09-09）：旧版生成的演示配方可能带过期参数——
                //   分类配方被误标 TaskType=Segmentation(→ 分类模型请求分割输出层 → HALCON 7787/2106)，
                //   或图像目录指向已废弃的外部 D:\HTH / 内嵌 Assets\DLDemo 路径。
                //   仅当"同一模型资产"的配方指纹与当前演示包不一致时重建覆盖（指纹比对见 NeedsRepair）。
                var existingRecipe = storage.GetAllRecipes().FirstOrDefault(r =>
                    string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
                if (existingRecipe != null && NeedsRepair(spec, imgDir))
                {
                    storage.SaveRecipe(BuildRecipe(spec, imgDir, existingRecipe));
                    createdRecipes++;
                    step.Add($"⚠ 配方 {spec.RecipeCode} 已存在但参数过期（TaskType/模型路径/图像目录与演示包不一致），已按当前演示包重建修复（保留 RecipeId/审批状态）");
                }
                else
                {
                    step.Add($"· 配方 {spec.RecipeCode} 已存在且与演示包一致，跳过");
                }
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌 Images 目录）");
            }

            // ---------- 4) 任务模板生成 ----------
            var library = new TaskTemplateLibraryService();
            var tpl = library.LoadAll().FirstOrDefault(t => t.Kind == TaskKind.DeepLearningInference
                && string.Equals(t.ModelAssetCode, spec.ModelAssetCode, StringComparison.OrdinalIgnoreCase));
            if (tpl == null)
            {
                tpl = new TaskTemplateInfo
                {
                    TemplateCode = library.GenerateCode(TaskKind.DeepLearningInference),
                    DisplayName = spec.TemplateTitle,
                    Kind = TaskKind.DeepLearningInference,
                    DependencyMode = TaskDependencyMode.Standalone,
                    Status = TaskTemplateStatus.Published,
                    Version = "v1",
                    Summary = spec.Summary,
                    ApplicableMachines = "药片/镁片示例场景；模型为 HTH Demo 训练产物，换产品需重新训练并替换模型资产",
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.LocalFolder,
                        LocalFolderPath = imgDir,
                        LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                        RepeatDelayMs = 0
                    },
                    ModelAssetCode = spec.ModelAssetCode,
                    BoundRecipeId = spec.RecipeCode,
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 推理链",
                    ConfidenceThreshold = null,
                    VerdictRule = BuildVerdict(spec),
                    OutputContractText = spec.ModelKind == DlModelKind.Segmentation
                        ? "分割输出 破裂/脏污 缺陷 Region + 连通域统计；摘要「检出 破裂 n处(面积 x px)…」/「未检出」"
                        : "分类输出 Top-1 类别+置信度；摘要「ok: 99.1%」/「ng: 87.3%」，类名前缀判定 OK/NG",
                    Remark = "HTH《Demo_深度学习》预翻译 DL 演示包（资产内嵌 Assets\\DLDemo，进模板中心自动落地）：.hdev 脚本 → C#+halcondotnet 节点链（不加载/执行 .hdev）"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（模型 + 配方 + 判据已内联）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        private static TaskVerdictRule BuildVerdict(DlDemoSpec spec)
        {
            if (spec.ModelKind == DlModelKind.Segmentation)
            {
                return new TaskVerdictRule
                {
                    OkKeyword = "未检出",
                    NgKeyword = "NG",
                    NgOnDetect = true,          // 检出即 NG（破裂/脏污缺陷语义）
                    EmptySummaryAsOk = false,
                    OkClassName = null,
                    NgClassName = null
                };
            }
            return new TaskVerdictRule
            {
                OkKeyword = null,
                NgKeyword = null,
                NgOnDetect = false,             // 分类不走"检出即 NG"（正类是 ok）
                EmptySummaryAsOk = false,
                OkClassName = spec.OkClassName, // "ok: 99.1%" → OK
                NgClassName = spec.NgClassName  // "ng: 87.3%" → NG
            };
        }

        /// <summary>从内嵌 Images 目录挑选 ≤12 张测试图复制到 Config\DemoData（已 ≥4 张则跳过，幂等）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            int existing = Directory.GetFiles(destDir, "*.png").Length
                           + Directory.GetFiles(destDir, "*.bmp").Length
                           + Directory.GetFiles(destDir, "*.jpg").Length
                           + Directory.GetFiles(destDir, "*.tif").Length;
            if (existing >= 4) return existing; // 已落地且足够，跳过（用户增删图以 DemoData 为准）

            var sourceDir = Path.Combine(embedDir, "Images");
            if (!Directory.Exists(sourceDir)) return 0;

            var exts = new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" };
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .Take(12)
                .ToList();

            int copiedCount = 0;
            foreach (var f in files)
            {
                var sub = Path.GetFileName(Path.GetDirectoryName(f));
                string name = (sub != null && !string.Equals(sub, Path.GetFileName(sourceDir), StringComparison.OrdinalIgnoreCase)
                    ? sub + "_" : "") + Path.GetFileName(f);
                var target = Path.Combine(destDir, name);
                if (File.Exists(target)) continue;
                File.Copy(f, target, true);
                copiedCount++;
            }
            return Directory.GetFiles(destDir).Length;
        }

        private static string CopyAs(string src, string dir, string name)
        {
            var dest = Path.Combine(dir, name);
            Directory.CreateDirectory(dir);
            File.Copy(src, dest, true);
            return dest;
        }

        // ======================================================================
        // 配方构建（ReadImageFile → HalconDlInference 两节点链）
        // ======================================================================

        /// <summary>
        /// 配方指纹比对：磁盘上已存在的同名配方，其 DL 节点（Type=HalconDlInference）与
        /// 读图节点是否仍与当前演示包一致。不一致 → 需要重建修复。
        /// 比对点：
        ///   1) DL 节点 TaskType 是否与 spec.ModelKind 一致（0=分类 / 2=分割；防 CLS 被误标分割 → 7787）；
        ///   2) DL 节点 HdlModelPath 是否指向 Config\Models\{AssetCode}\ 下的模型；
        ///   3) 读图节点 FolderPath 是否指向本演示包 DemoData 图像目录（排斥外部 D:\HTH / 内嵌 Assets 旧路径）。
        /// 仅在 JSON 可解析时判定；解析失败/缺节点一律按"无需修复"处理（绝不误覆盖用户自建配方）。
        /// </summary>
        private static bool NeedsRepair(DlDemoSpec spec, string imgDir)
        {
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", spec.RecipeCode + ".json");
                if (!File.Exists(file)) return false;

                var root = JObject.Parse(File.ReadAllText(file));
                var nodes = root["MainProcess"]?["Nodes"] as JArray;
                if (nodes == null) return false;

                int dlNodeType = (int)NodeType.HalconDlInference;
                int readNodeType = (int)NodeType.ReadImageFile;
                int expectedTaskKind = spec.ModelKind == DlModelKind.Segmentation ? (int)HalconDlTaskType.Segmentation : (int)HalconDlTaskType.Classification;
                string expectModelPrefix = @"Config\Models\" + spec.ModelAssetCode + @"\";

                int dlFound = 0;
                bool dlMismatch = false;
                bool readFolderMismatch = false;

                foreach (var n in nodes.OfType<JObject>())
                {
                    var pm = n["ParameterModel"] as JObject;

                    if ((int?)n["Type"] == dlNodeType)
                    {
                        dlFound++;
                        // 参数整体缺失(历史降级写入/手动损坏)→ 必须重建
                        if (pm == null) { dlMismatch = true; continue; }
                        int? taskType = pm["TaskType"]?.Value<int?>();
                        string hdlPath = pm["HdlModelPath"]?.Value<string>() ?? string.Empty;
                        if (taskType != expectedTaskKind
                            || hdlPath.IndexOf(expectModelPrefix, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            dlMismatch = true;
                        }
                    }
                    else if ((int?)n["Type"] == readNodeType)
                    {
                        string folder = pm?["FolderPath"]?.Value<string>() ?? string.Empty;
                        if (folder.IndexOf(imgDir, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            readFolderMismatch = true;
                        }
                    }
                }

                return dlFound > 0 && (dlMismatch || readFolderMismatch);
            }
            catch
            {
                return false; // 解析失败不动用户文件
            }
        }

        private static RecipeModel BuildRecipe(DlDemoSpec spec, string imgDir, RecipeModel existing = null)
        {
            string readId = Guid.NewGuid().ToString("N");
            string dlId = Guid.NewGuid().ToString("N");

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, "sample.png"),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // HALCON 原生 DL 节点：模型/预处理/后处理（与 .hdev 示例参数对齐）
            string assetDir = @"Config\Models\" + spec.ModelAssetCode;
            var dlParam = new HalconDlInferenceParam
            {
                TaskType = spec.ModelKind == DlModelKind.Segmentation ? HalconDlTaskType.Segmentation : HalconDlTaskType.Classification,
                HdlModelPath = assetDir + @"\model_opt.hdl",
                PreprocessParamPath = assetDir + @"\model_preprocess_params.hdict",
                ResizeEnabled = spec.ResizeWidth.HasValue && spec.ResizeHeight.HasValue,
                ResizeWidth = spec.ResizeWidth ?? 0,
                ResizeHeight = spec.ResizeHeight ?? 0,
                ResizeInterpolation = "nearest_neighbor",
                DefectClassIdsText = spec.DefectClassIds ?? "1,2",
                MinDefectArea = spec.MinDefectArea
            };

            var nodes = new List<RecipeNodeDto>
            {
                new RecipeNodeDto
                {
                    NodeId = readId, Type = NodeType.ReadImageFile, DisplayName = "图像读取(本地文件夹批处理)",
                    Enable = true, PosX = 120, PosY = 120, ParameterModel = readParam
                },
                new RecipeNodeDto
                {
                    NodeId = dlId, Type = NodeType.HalconDlInference, DisplayName = "HALCON 原生 DL（" + spec.DemoKey + "）",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = dlParam
                }
            };
            var conns = new List<RecipeConnectionDto>
            {
                new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = readId, SourcePortId = readId + "_out", SourcePortName = "Image",
                    TargetNodeId = dlId, TargetPortId = dlId + "_in", TargetPortName = "InputImage"
                }
            };

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 推理链",
                ProductCategory = "深度学习示例（HTH Demo 翻译）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "内嵌 DL 演示包自动生成（Assets\\DLDemo）：ReadImageFile[文件夹批处理] → HalconDlInference[HALCON 原生 .hdl]",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "推理链",
                    Nodes = nodes,
                    Connections = conns
                },
                SubProcesses = new Dictionary<string, ProcessDto>(),
                LogicalDevices = new List<RecipeDeviceMappingModel>(),
                ProcessParameters = new ProcessParameterSet()
            };

            var model = RecipeConverter.ToModel(dto);

            // 修复路径（existing != null）：保留既有元数据，避免「按 RecipeId 引用」失联 / 审批状态回退
            if (existing != null)
            {
                model.RecipeId = existing.RecipeId;
                model.CreatedTime = existing.CreatedTime;
                model.ApprovalStatus = existing.ApprovalStatus;
                model.ApprovalInfo = existing.ApprovalInfo ?? new RecipeApprovalInfo();
                model.Revision = existing.Revision;
                model.Version = existing.Version;
                model.IsActive = existing.IsActive;
            }
            return model;
        }
    }
}
