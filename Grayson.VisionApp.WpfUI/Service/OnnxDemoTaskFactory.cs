// Grayson.Vision.WpfUI/Service/OnnxDemoTaskFactory.cs
// ONNX 演示包「落地工厂」——与 DlDemoTaskFactory（HALCON DL 原生 .hdl 通道）并列的第二条通道。
//
// 演示内容：本工程自训练的 YOLOv8n-cls 镁片 ok/ng 分类模型（ONNX），端到端演示
//   「模型资产 → 演示图 → 配方 → 任务模板 → 工位执行」的完整落地路径。
//
// 与 DlDemoTaskFactory 的差异（= 两条通道的真正区别）：
//   · 模型资产 Framework = OnnxRuntime（单 .onnx 文件），非 HalconDlTool（.hdl + .hdict）；
//   · 配方用 DlInference 节点（走 Plugins.Inference.OnnxRuntime 插件），非 HalconDlInference；
//   · ★ 归一化必须与训练侧对齐：节点参数 MeanText="0,0,0" / StdText="255,255,255"。
//     YOLOv8-cls 训练侧像素归一到 0~1；若沿用分类任务的插件默认值（ImageNet 123.675/58.395），
//     输出去相关——症状是「全图恒判同一类、置信度恒定」（实测 10 张全判 ok@73.1%，准确率 40%）。
//   · 额外落地一个「演示工位」（ProcessKey=StandaloneVision，绑定本配方 + 本模板），
//     使其能被工位执行引擎直接跑：程序启动时 StationRuntimeBootstrap 会把已启用工位装配进
//     运行时，工位监视页【▶ 启动】即离线连续检测（无相机/运控/PLC 依赖）。
//
// 全程幂等：已存在则跳过；参数过期（缺归一化 / 模型路径不符 / 图像目录不符）则按当前包重建。
// 触发：模板中心每次打开时自动执行（与其它演示包工厂并列，静默无按钮）。
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Triggers;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.Identification.DlInference;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Services;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>把内嵌的 ONNX 演示包落地为「模型资产 + 配方链 + 任务模板 + 演示工位」（幂等，自动触发）。</summary>
    public static class OnnxDemoTaskFactory
    {
        /// <summary>模型资产编码（对应 Assets\OnnxDemo 子目录名）</summary>
        public const string ModelAssetCode = "MDL-CLS-ONNX-001";

        /// <summary>配方编码</summary>
        public const string RecipeCode = "RCP-DL-ONNX-CLS-001";

        /// <summary>演示落地目录名（Config\DemoData 下）</summary>
        public const string DemoKey = "镁片外观分类ONNX";

        /// <summary>演示工位编码（工位执行引擎的运行时键）</summary>
        public const string DemoStationCode = "ST_ONNX_CLS_01";

        /// <summary>演示工位所属产线（仅作分组，避免与真实产线混排）</summary>
        private const string DemoLineId = "LINE_DEMO";
        private const string DemoLineName = "演示产线";

        /// <summary>独立视觉任务引擎键（Core 注册：StandaloneVisionProcess.ProcessKeyValue）</summary>
        private const string StandaloneProcessKey = "StandaloneVision";

        /// <summary>
        /// 独立视觉引擎参数补丁（→ StandaloneVisionConfig，字段级覆盖）。
        /// 分类任务口径：不走「检出即 NG」，按 Top-1 类名前缀判 ok/ng。
        /// </summary>
        private const string ProcessConfigJson =
            "{\"LogTag\":\"ONNX镁片分类\",\"OkKeyword\":\"未检出\",\"NgKeyword\":\"NG\"," +
            "\"NgOnDetect\":false,\"EmptySummaryAsOk\":false,\"OkClassName\":\"ok\",\"NgClassName\":\"ng\"," +
            "\"EnableCsv\":true,\"OutputCsvPath\":\"Config\\\\StandaloneOutput\\\\{StationId}.csv\"}";

        /// <summary>内嵌资产根（随构建复制到输出目录）</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "OnnxDemo", ModelAssetCode);

        /// <summary>演示图落地目录</summary>
        private static string DemoImageDir
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", DemoKey, "Images");

        // ======================================================================
        // 入口
        // ======================================================================

        /// <summary>落地全部 ONNX 演示数据（幂等）。返回人类可读结果摘要。</summary>
        public static string EnsureDemoTasks()
        {
            var lines = new List<string>();
            int createdModels = 0, createdRecipes = 0, createdTemplates = 0, createdStations = 0;

            try
            {
                lines.Add(LandModel(ref createdModels));
                int imgCount = LandImages(out string imagesLine);
                lines.Add(imagesLine);
                lines.Add(LandRecipe(imgCount, ref createdRecipes));

                string tplCode = LandTemplate(ref createdTemplates, out string tplLine);
                lines.Add(tplLine);
                lines.Add(LandStation(tplCode, ref createdStations));
            }
            catch (Exception ex)
            {
                lines.Add("⚠ ONNX 演示包落地异常：" + ex.Message);
            }

            return string.Join(Environment.NewLine, lines)
                   + Environment.NewLine
                   + $"合计：模型资产入库 {createdModels}、配方落地 {createdRecipes}、" +
                     $"任务模板生成 {createdTemplates}、演示工位新增 {createdStations}。";
        }

        // ======================================================================
        // 1) 模型资产入库（Assets\OnnxDemo\MDL-CLS-ONNX-001\magnesium_cls.onnx → Config\Models\...）
        // ======================================================================

        private static string LandModel(ref int created)
        {
            string onnxSrc = Path.Combine(EmbeddedRoot, "magnesium_cls.onnx");
            if (!File.Exists(onnxSrc))
                return $"⚠ [{ModelAssetCode}] 内嵌模型不存在，跳过：{onnxSrc}（应随构建输出到 bin\\Assets\\OnnxDemo\\…）";

            var registry = new ModelRegistryService();
            if (registry.Load(ModelAssetCode) != null)
                return $"· 模型资产 {ModelAssetCode} 已存在，跳过";

            string dir = Path.Combine(registry.ModelRootDir, ModelAssetCode);
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "magnesium_cls.onnx");
            File.Copy(onnxSrc, dest, true);
            long kb = new FileInfo(dest).Length / 1024;

            var asset = new ModelAssetInfo
            {
                AssetCode = ModelAssetCode,
                DisplayName = "镁片外观分类 (YOLOv8n-cls / ONNX)",
                ModelKind = DlModelKind.Classification,
                Framework = DlFrameworkKind.OnnxRuntime,
                ApplicableTaskKind = TaskKind.DeepLearningInference,
                FileName = "magnesium_cls.onnx",
                AuxFileNames = new List<string>(),
                InputSpecText = "输入 1×3×224×224 float32（0~1 归一）；输出 1×2 概率（0=ng, 1=ok）",
                ClassNames = new List<string> { "ng", "ok" },
                MetricsText = "YOLOv8n-cls 迁移学习 30 epochs（CPU 3.8min），验证集 top1_acc = 100%",
                SizeKb = kb,
                Status = ModelAssetStatus.Ready,
                Remark = "本工程自训练产物（D:\\YoloTrain，ultralytics 8.4，GitHub v8.4.0 预训练权重迁移学习）；" +
                         "归一化 = YOLO 口径（mean 0 / std 255），非 ImageNet —— " +
                         "配方节点 MeanText/StdText 必须与之保持一致，否则输出与输入无关"
            };
            registry.Save(asset);
            created++;
            return $"✓ 模型资产 {ModelAssetCode} 已入库（magnesium_cls.onnx, {kb} KB）";
        }

        // ======================================================================
        // 2) 演示图落地（平铺；批处理节点只扫顶层目录）
        // ======================================================================

        private static int LandImages(out string line)
        {
            string imgDir = DemoImageDir;
            Directory.CreateDirectory(imgDir);

            if (CountImages(imgDir) >= 4)
            {
                line = $"· 演示图已存在 {CountImages(imgDir)} 张，跳过复制";
                return CountImages(imgDir);
            }

            string srcDir = Path.Combine(EmbeddedRoot, "Images");
            if (!Directory.Exists(srcDir))
            {
                line = $"⚠ 内嵌演示图目录不存在：{srcDir}";
                return 0;
            }

            foreach (var f in Directory.GetFiles(srcDir, "*.*", SearchOption.TopDirectoryOnly).OrderBy(x => x))
            {
                string target = Path.Combine(imgDir, Path.GetFileName(f));
                if (File.Exists(target)) continue;
                File.Copy(f, target, true);
            }

            int total = CountImages(imgDir);
            int okN = Directory.GetFiles(imgDir, "ok_*").Length;
            int ngN = Directory.GetFiles(imgDir, "ng_*").Length;
            line = $"✓ 演示图落地 {total} 张（ok {okN} / ng {ngN}，模型独立测试集）→ {imgDir}";
            return total;
        }

        private static int CountImages(string dir)
        {
            if (!Directory.Exists(dir)) return 0;
            var exts = new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" };
            return Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Count(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }

        // ======================================================================
        // 3) 配方落地（ReadImageFile[文件夹批处理] → DlInference[ONNX Runtime]）
        // ======================================================================

        private static string LandRecipe(int imgCount, ref int created)
        {
            var storage = new RecipeStorageService();
            var existing = storage.GetAllRecipes().FirstOrDefault(r =>
                string.Equals(r.RecipeCode, RecipeCode, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                if (imgCount <= 0)
                    return "⚠ 无演示图，未生成配方（请检查内嵌 Images 目录）";
                storage.SaveRecipe(BuildRecipe());
                created++;
                return $"✓ 配方 {RecipeCode} 已落地（ReadImageFile → DlInference[ONNX]，含 {imgCount} 张演示图）";
            }

            if (NeedsRepair())
            {
                // 保留既有元数据（RecipeId/创建时间/审批状态），避免按 RecipeId 引用失联
                storage.SaveRecipe(BuildRecipe(existing));
                created++;
                return $"⚠ 配方 {RecipeCode} 参数过期（缺归一化/模型路径不符/图像目录不符），已按当前演示包重建（保留 RecipeId 与审批状态）";
            }
            return $"· 配方 {RecipeCode} 已存在且与演示包一致，跳过";
        }

        /// <summary>
        /// 配方指纹比对：磁盘上同名配方的 DlInference 节点是否仍与当前演示包一致。
        /// 比对点：① 存在 DlInference 节点且 TaskType=Classification；
        ///         ② ModelPath 指向 Config\Models\{ModelAssetCode}\；
        ///         ③ ★MeanText/StdText 已配（缺 = 本次新增能力之前落地的旧配方，必须重建，
        ///           否则会沿用插件 ImageNet 默认归一 → 输出与输入无关）；
        ///         ④ 读图节点 FolderPath 指向本演示包 DemoData 目录。
        /// 仅在 JSON 可解析时判定；解析失败一律按「无需修复」（绝不误覆盖用户自建配方）。
        /// </summary>
        private static bool NeedsRepair()
        {
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", RecipeCode + ".json");
                if (!File.Exists(file)) return false;

                var root = JObject.Parse(File.ReadAllText(file));
                if (!(root["MainProcess"]?["Nodes"] is JArray nodes)) return false;

                int dlNodeType = (int)NodeType.DlInference;
                int readNodeType = (int)NodeType.ReadImageFile;
                string expectModelPrefix = @"Config\Models\" + ModelAssetCode + @"\";
                string imgDir = DemoImageDir;

                int dlFound = 0;
                bool mismatch = false;

                foreach (var n in nodes.OfType<JObject>())
                {
                    var pm = n["ParameterModel"] as JObject;
                    if ((int?)n["Type"] == dlNodeType)
                    {
                        dlFound++;
                        if (pm == null) { mismatch = true; continue; }
                        string modelPath = pm["ModelPath"]?.Value<string>() ?? string.Empty;
                        string meanText = pm["MeanText"]?.Value<string>() ?? string.Empty;
                        string stdText = pm["StdText"]?.Value<string>() ?? string.Empty;
                        int? taskType = pm["TaskType"]?.Value<int?>();

                        if (taskType != (int)DlTaskType.Classification
                            || modelPath.IndexOf(expectModelPrefix, StringComparison.OrdinalIgnoreCase) != 0
                            || string.IsNullOrWhiteSpace(meanText)
                            || string.IsNullOrWhiteSpace(stdText))
                        {
                            mismatch = true;
                        }
                    }
                    else if ((int?)n["Type"] == readNodeType)
                    {
                        string folder = pm?["FolderPath"]?.Value<string>() ?? string.Empty;
                        if (folder.IndexOf(imgDir, StringComparison.OrdinalIgnoreCase) != 0) mismatch = true;
                    }
                }
                return dlFound > 0 && mismatch;
            }
            catch
            {
                return false; // 解析失败不动用户文件
            }
        }

        private static RecipeModel BuildRecipe(RecipeModel existing = null)
        {
            string readId = Guid.NewGuid().ToString("N");
            string dlId = Guid.NewGuid().ToString("N");
            string imgDir = DemoImageDir;

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, "ok_good_021.png"),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // ONNX 推理节点：模型 + 类别 + ★归一化（与训练侧一致）
            string assetDir = @"Config\Models\" + ModelAssetCode;
            var dlParam = new DlInferenceParam
            {
                TaskType = DlTaskType.Classification,
                ModelPath = assetDir + @"\magnesium_cls.onnx",
                // ★类别顺序必须与训练导出一致：ultralytics 导出 names = {0:'ng', 1:'ok'}
                LabelsText = "ng,ok",
                ConfidenceThreshold = 0.5,
                LayoutHint = "auto",
                MeanText = "0,0,0",        // ← 与 YOLO 训练侧 0~1 归一对齐（关键）
                StdText = "255,255,255"
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
                    NodeId = dlId, Type = NodeType.DlInference, DisplayName = "AI 推理(ONNX Runtime · 镁片分类)",
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
                RecipeCode = RecipeCode,
                RecipeName = RecipeCode + " · 镁片外观分类（ONNX）推理链",
                ProductCategory = "深度学习示例（自训练 ONNX 通道）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "ONNX 演示包自动生成（Assets\\OnnxDemo）：" +
                              "ReadImageFile[文件夹批处理] → DlInference[Plugins.Inference.OnnxRuntime]；" +
                              "归一化 mean=0/std=255 与 YOLOv8-cls 训练侧对齐",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = "镁片外观分类ONNX推理链",
                    Nodes = nodes,
                    Connections = conns
                },
                SubProcesses = new Dictionary<string, ProcessDto>(),
                LogicalDevices = new List<RecipeDeviceMappingModel>(),
                ProcessParameters = new ProcessParameterSet()
            };

            var model = RecipeConverter.ToModel(dto);

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

        // ======================================================================
        // 4) 任务模板（深度学习族 · 独立运行 · 判据 ok/ng 类名前缀）
        // ======================================================================

        private static string LandTemplate(ref int created, out string line)
        {
            var library = new TaskTemplateLibraryService();
            var tpl = library.LoadAll().FirstOrDefault(t => t.Kind == TaskKind.DeepLearningInference
                && string.Equals(t.ModelAssetCode, ModelAssetCode, StringComparison.OrdinalIgnoreCase));

            if (tpl != null)
            {
                line = $"· 任务模板 {tpl.TemplateCode} 已存在，跳过";
                return tpl.TemplateCode;
            }

            tpl = new TaskTemplateInfo
            {
                TemplateCode = library.GenerateCode(TaskKind.DeepLearningInference),
                DisplayName = "镁片外观分类（ok/ng）· ONNX 自训练通道",
                Kind = TaskKind.DeepLearningInference,
                DependencyMode = TaskDependencyMode.Standalone,
                Status = TaskTemplateStatus.Published,
                Version = "v1",
                Summary = "自训练 YOLOv8n-cls ONNX 分类模型：镁片整图判 ok(合格)/ng(裂纹等缺陷)，" +
                          "Top-1 类名前缀判定；归一化 mean=0/std=255 与训练侧对齐，纯软件独立运行",
                ApplicableMachines = "镁片/药片等整图有无缺陷分类场景；换产品需重新训练并替换模型资产（MDL-CLS-ONNX-001）",
                ImageSource = new TaskTemplateImageSource
                {
                    Kind = TaskImageSourceKind.LocalFolder,
                    LocalFolderPath = DemoImageDir,
                    LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                    RepeatDelayMs = 0
                },
                ModelAssetCode = ModelAssetCode,
                BoundRecipeId = RecipeCode,
                BoundRecipeName = RecipeCode + " · 镁片外观分类（ONNX）推理链",
                ConfidenceThreshold = null,
                VerdictRule = new TaskVerdictRule
                {
                    OkKeyword = null,
                    NgKeyword = null,
                    NgOnDetect = false,          // 分类不走「检出即 NG」（正类是 ok）
                    EmptySummaryAsOk = false,
                    OkClassName = "ok",          // 摘要 "ok: 99.1%" → OK
                    NgClassName = "ng",          // 摘要 "ng: 87.3%" → NG
                    Note = "分类判据：DlInference 摘要形如「{Top-1类名}: {置信度}」，按类名前缀判 OK/NG"
                },
                OutputContractText = "分类输出 Top-1 类别 + 置信度；摘要「ok: 99.1%」/「ng: 87.3%」，类名前缀判定 OK/NG；" +
                                     "下游可接 MathLogic/条件节点做更复杂路由",
                VerdictToIo = false,
                Remark = "ONNX 通道对照样本（vs HALCON DL 通道模板）：同一功能、同一数据集，" +
                         "差异在模型格式（.onnx vs .hdl）与预处理归一化（YOLO 0~1 vs HALCON 无归一化仅转 real）"
            };

            library.Save(tpl);
            created++;
            line = $"✓ 任务模板 {tpl.TemplateCode} 已生成（{tpl.DisplayName}）";
            return tpl.TemplateCode;
        }

        // ======================================================================
        // 5) 演示工位（独立视觉任务引擎 · 绑定本配方 + 本模板）
        // ======================================================================

        private static string LandStation(string templateCode, ref int created)
        {
            var repo = StorageFactory.CreateStationRepository();
            var all = repo.GetAllLines();
            bool exists = all
                .SelectMany(l => l.Stations ?? new List<StationConfigModel>())
                .Any(s => s != null && string.Equals(s.StationCode, DemoStationCode, StringComparison.OrdinalIgnoreCase));

            if (exists)
                return $"· 演示工位 {DemoStationCode} 已存在，跳过";

            var station = new StationConfigModel
            {
                StationId = DemoStationCode,
                StationCode = DemoStationCode,
                StationName = "ONNX 镁片外观分类（离线自检）",
                LineId = DemoLineId,
                LineName = DemoLineName,
                IsEnabled = true,
                TimeoutMs = 10000,
                BoundRecipeId = RecipeCode,
                BoundRecipeName = RecipeCode + " · 镁片外观分类（ONNX）推理链",
                ProcessKey = StandaloneProcessKey,
                ProcessConfigJson = ProcessConfigJson,
                TaskTemplateCode = templateCode,
                TaskTemplateName = "镁片外观分类（ok/ng）· ONNX 自训练通道",
                TaskTemplateKindText = "深度学习推理",
                TriggerSource = new TriggerSourceConfig
                {
                    SourceType = TriggerSourceType.Manual,   // 手动触发：监视页【▶ 启动】/【执行一次】
                    Edge = TriggerEdge.Rising,
                    TimerIntervalMs = 1000
                }
            };

            if (!repo.SaveStation(station))
                return $"⚠ 演示工位 {DemoStationCode} 写入失败（工位库不可写？）";

            created++;
            return $"✓ 演示工位 {DemoStationCode} 已创建（{StandaloneProcessKey} + 绑定配方/模板；" +
                   "程序启动时自动装配，工位监视页可直接启动）";
        }
    }
}
