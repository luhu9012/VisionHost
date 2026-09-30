//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: BarcodeDemoTaskFactory.cs
// 说 明: 特征识别读取族（TaskKind.FeatureIdentification）条码/二维码读取演示包落地工厂
//        ——「一维码 + 二维码识别」（2026-09-28）。
//
// 素材来源：D:\HTH\课堂资料（翰庭汇视觉课堂）+ 本机 HALCON 样例图
//   · 一维码：同课程 6一维码识别.hdev / 6一维码识别2.hdev（Code39/Code128/EAN-13/UPC/mixup 混排）
//   · 二维码：同课程 6二维码识别.hdev（qr1.png）+ HALCON barcode2d 样例
//     （Data Matrix ECC200 黑/白、Aztec 黑/白、MicroQR、PDF417）
//
// 平台等价链路（沿用既有节点，引擎已扩族）：
//   ReadImageFile[本地文件夹批处理]
//     → ReadBarcode[find_bar_code / find_data_code_2d 真 HALCON 实现]
//   → 引擎按模板 VerdictRule.OkKeyword（读码族语义=期望文本）判 OK/NG。
//
// ★ 本次一并补齐的两处"未实现完整"（原状）：
//   ① HalconWrapper/Identification/BarcodeTool.cs 原为**桩实现**——
//      image!=null 时恒定返回 Success=true + "SAMPLE_BARCODE_12345"，门禁一处不会红；
//      现已换为真实算子（majority_voting 多线投票 + element_size 放宽 + get_bar_code_result 回读码制）。
//   ② StandaloneVisionProcess 的识别族分支原先**只认 ColorIdentify**：
//      条码/OCR 节点即便跑出结果，引擎也完全不消费（跑完等于没跑）；
//      现已新增 isReadTask 分支，按端口 BarcodeText / OcrText 消费并判据。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 DL/测量/颜色/Blob 演示包并列）。
//   ⚠ 幂等是"存在即跳过"，改判据/参数后必须 bump DemoSpecVersion（PurgeOutdatedDemo 清旧重落）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.Identification.ReadBarcode;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Repository.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>条码/二维码读取演示包定义（图源 / 码制 / 期望串）。</summary>
    internal class BarcodeDemoSpec
    {
        public string DemoKey;              // 落地目录名
        public string RecipeCode;           // RCP-ID-BARCODE-001
        public string TemplateTitle;
        public string Summary;
        public string ModelNote;            // Remark 标注

        public BarcodeType CodeType;
        public BarcodePolarity Polarity;
        public int MaxCount = 1;
        public double ElementSizeMin = 0;

        /// <summary>期望文本（读码族语义：写进模板 VerdictRule.OkKeyword，多个用 " | " 分隔）</summary>
        public string ExpectText;

        /// <summary>该演示包的内嵌素材子目录（Assets\BarcodeDemo 或 Assets\QrDemo）</summary>
        public string AssetSubDir;
    }

    /// <summary>把内嵌条码素材落地为「图源 + ReadBarcode 配方链 + 特征识别读取族任务模板」（幂等，自动触发）。</summary>
    public static class BarcodeDemoTaskFactory
    {
        /// <summary>
        /// 本演示包的内容版本号。★ 判据/参数改动必须同步 bump——
        /// 工厂"存在即跳过"，不 bump 则本机已落地的旧配方/模板静默沿用旧值。
        /// </summary>
        private const string DemoSpecVersion = "2026-09-28.1";

        private static string AssetRoot(string sub)
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", sub);

        private static readonly List<BarcodeDemoSpec> Specs = new List<BarcodeDemoSpec>
        {
            // ---------- 一维码 ----------
            new BarcodeDemoSpec
            {
                DemoKey = "一维码识别",
                RecipeCode = "RCP-ID-BARCODE-001",
                TemplateTitle = "一维码读取（Code39/Code128/EAN13/UPC）·Barcode Demo",
                AssetSubDir = "BarcodeDemo",
                Summary = "对一幅条码图执行 find_bar_code 一维码识别，输出码值文本与符号区域；" +
                          "按模板期望串判 OK/NG：读出内容命中期望 → OK，未读出或内容不符 → NG。" +
                          "素材含 Code39 / Code128 / EAN-13 / UPC 及混排图（纯软件独立运行）",
                ModelNote = "2026-09-28 特征识别读取族·条码演示（素材=HTH 课堂 6一维码识别 案例图 + Code128/EAN13/UPC 样例）：" +
                            "链路 = ReadImageFile[文件夹批处理] → ReadBarcode[find_bar_code]，" +
                            "与 hdev 的 create_bar_code_model → set_bar_code_param(majority_voting) → find_bar_code 一一对应；" +
                            "★ BarcodeTool 由桩实现改为真 HALCON（原实现恒定返回 SAMPLE_BARCODE_12345）",
                CodeType = BarcodeType.Auto,      // 混排图用 auto（课堂案例原话：多码制通用但更慢）
                Polarity = BarcodePolarity.DarkOnLight,
                MaxCount = 1,
                ElementSizeMin = 0,
                ExpectText = null                 // 一维码图码值各异，不写死期望（读到即通过）
            },
            // ---------- 二维码 ----------
            new BarcodeDemoSpec
            {
                DemoKey = "二维码识别",
                RecipeCode = "RCP-ID-BARCODE-002",
                TemplateTitle = "二维码读取（QR/DataMatrix/Aztec）·QR Demo",
                AssetSubDir = "QrDemo",
                Summary = "对一幅二维码图执行 find_data_code_2d 识别，输出码值文本与符号 XLD 轮廓；" +
                          "按模板期望串判 OK/NG。" +
                          "素材含 QR Code / Data Matrix ECC200 / Aztec / MicroQR / PDF417（纯软件独立运行）",
                ModelNote = "2026-09-28 特征识别读取族·二维码演示（素材=HTH 课堂 6二维码识别 案例 qr1.png + HALCON barcode2d 样例）：" +
                            "链路 = ReadImageFile[文件夹批处理] → ReadBarcode[find_data_code_2d]，" +
                            "与 hdev 的 create_data_code_2d_model → set_data_code_2d_param(polarity) → find_data_code_2d 一一对应；" +
                            "★ 极性参数已暴露（课程 qr1.png 为白码黑底 light_on_dark，默认 any 可自动兼容）",
                CodeType = BarcodeType.QRCode,
                Polarity = BarcodePolarity.Any,   // 素材含黑码白底与白码黑底两类，用 any
                MaxCount = 1,
                ElementSizeMin = 0,
                ExpectText = null
            }
        };

        /// <summary>落地全部内嵌条码演示包（幂等）。返回人类可读结果摘要。</summary>
        public static string EnsureDemoTasks()
        {
            var lines = new List<string>();
            int createdRecipes = 0, createdTemplates = 0;
            foreach (var spec in Specs)
            {
                lines.Add(LandOne(spec, ref createdRecipes, ref createdTemplates));
            }
            return string.Join(Environment.NewLine, lines)
                   + Environment.NewLine
                   + $"合计：配方落地 {createdRecipes}、任务模板生成 {createdTemplates}。";
        }

        private static string LandOne(BarcodeDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = AssetRoot(spec.AssetSubDir);
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\{spec.AssetSubDir}）";

            // ---------- 1) 演示图像落地（Config\DemoData\{DemoKey}\Images\） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir);

            // ---------- 2) 内容版本守卫 ----------
            PurgeOutdatedDemo(spec, step);

            // ---------- 3) 配方落地 ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → ReadBarcode[{spec.CodeType}]，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌 Images 目录）");
            }

            // ---------- 4) 任务模板生成（特征识别读取族） ----------
            var library = new TaskTemplateLibraryService();
            var tpl = library.LoadAll().FirstOrDefault(t => t.Kind == TaskKind.FeatureIdentification
                && string.Equals(t.BoundRecipeId, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (tpl == null)
            {
                tpl = new TaskTemplateInfo
                {
                    TemplateCode = library.GenerateCode(TaskKind.FeatureIdentification),
                    DisplayName = spec.TemplateTitle,
                    Kind = TaskKind.FeatureIdentification,
                    DependencyMode = TaskDependencyMode.Standalone,
                    Status = TaskTemplateStatus.Published,
                    Version = "v1",
                    Summary = spec.Summary,
                    ApplicableMachines = "产品追溯/上料校验等读码场景；换码制只需改节点 CodeType，换产品改期望串",
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.LocalFolder,
                        LocalFolderPath = imgDir,
                        LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                        RepeatDelayMs = 0
                    },
                    BoundRecipeId = spec.RecipeCode,
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 读码链",
                    // ★ 读码族语义：OkKeyword 复用为"期望文本"（多个用 " | " 分隔）；未配=读到即通过
                    VerdictRule = new TaskVerdictRule
                    {
                        OkKeyword = spec.ExpectText,
                        Note = string.IsNullOrWhiteSpace(spec.ExpectText)
                            ? "读码族：未配期望串 ⇒ 只要识别出非空码值即判 OK（演示'能不能读到'）"
                            : "读码族：码值需包含该期望串才判 OK"
                    },
                    OutputContractText = "读码输出 码值文本 + 符号区域(XLD)；摘要「条码/二维码读取 …」；" +
                        "判据=读出的码值包含 VerdictRule.OkKeyword(期望串) → OK，未读出/不符 → NG",
                    VerdictToIo = false,
                    // ★ 版本标记：PurgeOutdatedDemo 靠它判断"本机落地的是不是当前版本"
                    Remark = spec.ModelNote + " [spec=" + DemoSpecVersion + "]"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（码制 {spec.CodeType}）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        // ======================================================================
        // 内容版本守卫（照抄 ColorDemoTaskFactory 的成熟做法）
        // ======================================================================
        private static void PurgeOutdatedDemo(BarcodeDemoSpec spec, List<string> step)
        {
            string marker = "spec=" + DemoSpecVersion;
            try
            {
                var library = new TaskTemplateLibraryService();
                var bound = library.LoadAll()
                    .Where(t => t.Kind == TaskKind.FeatureIdentification
                                && string.Equals(t.BoundRecipeId, spec.RecipeCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (bound.Count == 0) return;
                if (bound.All(t => (t.Remark ?? string.Empty).Contains(marker))) return;

                foreach (var t in bound)
                    if (library.Delete(t.TemplateCode))
                        step.Add($"↻ 演示包版本 {DemoSpecVersion} 更新：已清理旧任务模板 {t.TemplateCode}");

                if (new RecipeStorageService().DeleteRecipe(spec.RecipeCode))
                    step.Add($"↻ 已同步清理旧配方 {spec.RecipeCode}，将按新参数重落");
                else
                    step.Add($"⚠ 旧配方 {spec.RecipeCode} 删除失败：请手工删除它，否则本次仍会沿用旧参数");
            }
            catch (Exception ex)
            {
                step.Add("⚠ 演示包版本守卫执行失败（可能仍沿用旧参数）：" + ex.Message);
            }
        }

        /// <summary>把内嵌 Images 的演示图拷到 DemoData 目录（幂等：已 ≥1 张则跳过，用户增删以 DemoData 为准）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            int existing = CountImages(destDir);
            if (existing >= 1) return existing;

            var sourceDir = Path.Combine(embedDir, "Images");
            if (!Directory.Exists(sourceDir)) return 0;

            var exts = new[] { ".png", ".bmp", ".jpg", ".jpeg", ".tif", ".tiff" };
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f)
                .ToList();

            foreach (var f in files)
            {
                var target = Path.Combine(destDir, Path.GetFileName(f));
                if (File.Exists(target)) continue;
                try { File.Copy(f, target, true); } catch { /* 单张失败不阻断其余 */ }
            }
            return CountImages(destDir);
        }

        private static int CountImages(string dir)
            => Directory.GetFiles(dir, "*.png").Length
               + Directory.GetFiles(dir, "*.bmp").Length
               + Directory.GetFiles(dir, "*.jpg").Length
               + Directory.GetFiles(dir, "*.tif").Length;

        // ======================================================================
        // 配方构建（ReadImageFile → ReadBarcode 两节点链）
        // ======================================================================
        private static RecipeModel BuildRecipe(BarcodeDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string bcId = Guid.NewGuid().ToString("N");

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = null,
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            var bcParam = new ReadBarcodeParam
            {
                CodeType = spec.CodeType,
                Polarity = spec.Polarity,
                MaxCount = spec.MaxCount,
                ElementSizeMin = spec.ElementSizeMin,
                TimeoutMs = 1000
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
                    NodeId = bcId, Type = NodeType.ReadBarcode,
                    DisplayName = $"读码({spec.CodeType}·{spec.Polarity})",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = bcParam
                }
            };
            var conns = new List<RecipeConnectionDto>
            {
                new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = readId, SourcePortId = readId + "_Image", SourcePortName = "Image",
                    TargetNodeId = bcId, TargetPortId = bcId + "_InputImage", TargetPortName = "InputImage"
                }
            };

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 读码链",
                ProductCategory = "特征识别读取示例（Barcode Demo）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "识别读取族内嵌 Demo 自动生成（Assets\\" + spec.AssetSubDir + "，HTH 课堂读码案例素材）：" +
                              "ReadImageFile[文件夹批处理] → ReadBarcode[真 HALCON find_bar_code / find_data_code_2d] → " +
                              "引擎按模板期望串判 OK/NG",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "读码链",
                    Nodes = nodes,
                    Connections = conns
                },
                SubProcesses = new Dictionary<string, ProcessDto>(),
                LogicalDevices = new List<RecipeDeviceMappingModel>(),
                ProcessParameters = new ProcessParameterSet()
            };

            return RecipeConverter.ToModel(dto);
        }
    }
}
