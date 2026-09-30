//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: OcrDemoTaskFactory.cs
// 说 明: 特征识别读取族（TaskKind.FeatureIdentification）OCR 字符识别演示包落地工厂
//        ——「字符识别（打印体/批号）」（2026-09-28）。
//
// 素材来源：D:\HTH\课堂资料（翰庭汇视觉课堂）+ 本机 HALCON 样例图
//   · 6OCR num.hdev —— letters.png（字母数字打印体）
//   · 6OCR 训练识别.hdev —— hantinghui.png / hantinghui2.png / hantinghui3.png（中文，需自训分类器）
//   · 6OCR环形识别-作业.hdev —— cricle.png（环形字符，先极坐标展开）
//   · audi2.png —— 课程 OCR 案例用的车身铭牌/条码图
//
// 平台等价链路（沿用既有节点，引擎已扩族）：
//   ReadImageFile[本地文件夹批处理]
//     → ReadOCR[threshold → connection → select_shape → dilation_circle → read_ocr_class_mlp
//              → do_ocr_multi_class_mlp 真 HALCON 实现]
//   → 引擎按模板 VerdictRule.OkKeyword（读码/OCR 族语义=期望文本）判 OK/NG。
//
// ★ 分类器路线定案（回答 TaskTypeIndustryCatalog.PLAN-OCR 的 RoadmapHint "另需先定案路线"）：
//   选【分割 + read_ocr_class_mlp 分类器】，不选 text_finder 一体路线。理由：
//     ① HTH 课堂 4 个 OCR 案例全部走这条（算子全既有，零新增原生依赖）；
//     ② 工业读码场景 ROI 明确、字符排布规律 ⇒ 分割路线精度与可控性更好；
//     ③ 训练扩展性：课程"训练识别"案例可 append_ocr_trainf + trainf_ocr_class_mlp
//        自训中文/专用字体；一体路线做同样的事要换一整套 API。
//
// ★ 本次一并补齐的两处"未实现完整"（原状）：
//   ① HalconWrapper/Identification/OCRTool.cs 原为**桩实现**——
//      恒定返回 Success=true + "A1234"，门禁一处不会红；现已换为真实算子链。
//   ② StandaloneVisionProcess 的识别族分支原先只认 ColorIdentify，
//      OCR 节点跑出结果引擎也不消费；现已新增 isReadTask 分支消费 OcrText 端口。
//
// ★ 置信度门（课程反复强调的教训，已内置进引擎与节点）：
//   do_ocr_multi_class_mlp 对**未训练**字符不拒绝，会把它判成"最后一个训练字符"且照样给高置信度
//   ⇒ "识别成功"≠"结果对"。节点参数 MinConfidence 与模型选用（NoRej vs Rej）是唯一安全网。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 DL/测量/颜色/Blob/条码 演示包并列）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.Identification.ReadOCR;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Repository.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>OCR 演示包定义（图源 / 分类器 / 二值化与筛选参数 / 期望串）。</summary>
    internal class OcrDemoSpec
    {
        public string DemoKey;
        public string RecipeCode;           // RCP-ID-OCR-001
        public string TemplateTitle;
        public string Summary;
        public string ModelNote;

        // ReadOCR 节点参数
        public string FontFileName = "Industrial_0-9A-Z_NoRej.omc";
        public double MinStrokeWidth = 2.0;
        public double ThresholdMax = 71;
        public double AreaMin = 30;
        public double AreaMax = 70000;
        public OcrPolarity Polarity = OcrPolarity.Dark;
        public bool SortByRow = false;
        public string ExpressionFilter = "*";
        public double MinConfidence = 0.0;

        /// <summary>期望文本（OCR 族语义：写进模板 VerdictRule.OkKeyword，多个用 " | " 分隔）</summary>
        public string ExpectText;
    }

    /// <summary>把内嵌 OCR 素材落地为「图源 + ReadOCR 配方链 + 特征识别读取族任务模板」（幂等，自动触发）。</summary>
    public static class OcrDemoTaskFactory
    {
        /// <summary>
        /// 本演示包的内容版本号。★ 判据/参数改动必须同步 bump——
        /// 工厂"存在即跳过"，不 bump 则本机已落地的旧配方/模板静默沿用旧值。
        /// </summary>
        private const string DemoSpecVersion = "2026-09-28.1";

        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "OcrDemo");

        private static readonly List<OcrDemoSpec> Specs = new List<OcrDemoSpec>
        {
            new OcrDemoSpec
            {
                DemoKey = "OCR字符识别",
                RecipeCode = "RCP-ID-OCR-001",
                TemplateTitle = "OCR 字符识别（打印体字母数字）·OCR Demo",
                Summary = "对一幅字符图执行阈值分割 + HALCON MLP 分类器识别，输出字符串与字符区域；" +
                          "按模板期望串判 OK/NG：读出的字符串命中期望 → OK，读不出或内容不符 → NG。" +
                          "素材含字母数字打印体、中文标注、环形字符与铭牌图（纯软件独立运行）",
                ModelNote = "2026-09-28 特征识别读取族·OCR 演示（素材=HTH 课堂 6OCR num / 训练识别 / 环形识别 案例图）：" +
                            "链路 = ReadImageFile[文件夹批处理] → ReadOCR[threshold→connection→select_shape→" +
                            "dilation_circle→read_ocr_class_mlp→do_ocr_multi_class_mlp]，与 hdev 案例一一对应；" +
                            "★ OCRTool 由桩实现改为真 HALCON（原实现恒定返回 A1234）；" +
                            "★ 分类器 Industrial_0-9A-Z_NoRej.omc 走 Config\\Ocr → Assets\\Ocr → HALCON ocr\\ 三级回退查找；" +
                            "★ 未配期望串 ⇒ 读到非空文本即 OK（演示'能不能读'）",

                // 二值化上限 71 与课程 6OCR num.hdev 一致（白底黑字取暗字符）
                FontFileName = "Industrial_0-9A-Z_NoRej.omc",
                MinStrokeWidth = 2.0,
                ThresholdMax = 71,
                AreaMin = 30,
                AreaMax = 70000,
                Polarity = OcrPolarity.Dark,
                SortByRow = false,
                ExpressionFilter = "*",
                MinConfidence = 0.0,
                ExpectText = null
            }
        };

        /// <summary>落地全部内嵌 OCR 演示包（幂等）。返回人类可读结果摘要。</summary>
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

        private static string LandOne(OcrDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = EmbeddedRoot;
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\OcrDemo）";

            // ---------- 1) 演示图像落地 ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir);

            // ---------- 2) 分类器落地（.omc 兜到 Config\Ocr，现场可见可换） ----------
            EnsureClassifiers(step);
            // OCR 演示跑不动的头号原因就是分类器找不到 ⇒ 提前把搜索路径与结果印出来
            var resolved = ResolveClassifierProbe(spec.FontFileName);
            step.Add(resolved != null
                ? $"✓ 分类器可用: {resolved}"
                : $"⚠ 分类器 {spec.FontFileName} 未找到（Config\\Ocr / Assets\\Ocr / HALCON ocr\\ 均无）——" +
                  "请从 HALCON 安装目录 ocr\\ 拷贝 .omc 到 Config\\Ocr，否则本模板运行必失败");

            // ---------- 3) 内容版本守卫 ----------
            PurgeOutdatedDemo(spec, step);

            // ---------- 4) 配方落地 ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → ReadOCR，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌 Images 目录）");
            }

            // ---------- 5) 任务模板生成 ----------
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
                    ApplicableMachines = "批号/日期/铭牌字符读取等追溯场景；换字体只需换 .omc 分类器，换产品改期望串",
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.LocalFolder,
                        LocalFolderPath = imgDir,
                        LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                        RepeatDelayMs = 0
                    },
                    BoundRecipeId = spec.RecipeCode,
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 字符识别链",
                    // ★ 读码/OCR 族语义：OkKeyword 复用为"期望文本"（多个用 " | " 分隔）；未配=读到即通过
                    VerdictRule = new TaskVerdictRule
                    {
                        OkKeyword = spec.ExpectText,
                        Note = string.IsNullOrWhiteSpace(spec.ExpectText)
                            ? "OCR 族：未配期望串 ⇒ 只要识别出非空文本即判 OK（演示'能不能读'）"
                            : "OCR 族：识别文本需包含该期望串才判 OK"
                    },
                    OutputContractText = "OCR 输出 字符串 + 字符区域；摘要「OCR 字符识别 …」；" +
                        "判据=识别文本包含 VerdictRule.OkKeyword(期望串) → OK，未读出/不符 → NG。" +
                        "★ 另有节点级 MinConfidence 置信度门（未训练字符会被误识别且给高置信度）",
                    VerdictToIo = false,
                    Remark = spec.ModelNote + " [spec=" + DemoSpecVersion + "]"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（分类器 {spec.FontFileName}）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        // ======================================================================
        // 分类器落地与探测
        // ======================================================================

        /// <summary>把内嵌 Assets\OcrDemo\Classifiers 下的 .omc 拷到 Config\Ocr（幂等）。</summary>
        private static void EnsureClassifiers(List<string> step)
        {
            try
            {
                var src = Path.Combine(EmbeddedRoot, "Classifiers");
                if (!Directory.Exists(src)) return;
                var dst = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Ocr");
                Directory.CreateDirectory(dst);
                int n = 0;
                foreach (var f in Directory.GetFiles(src, "*.omc", SearchOption.TopDirectoryOnly))
                {
                    var target = Path.Combine(dst, Path.GetFileName(f));
                    if (File.Exists(target)) continue;
                    try { File.Copy(f, target, false); n++; } catch { }
                }
                if (n > 0) step.Add($"✓ 已落地 {n} 个内嵌 OCR 分类器到 Config\\Ocr");
            }
            catch (Exception ex)
            {
                step.Add("⚠ 内嵌分类器落地失败：" + ex.Message);
            }
        }

        /// <summary>用 OCRTool 的解析逻辑探一下分类器到底能不能找到（演示跑不动时第一个要看的东西）。</summary>
        private static string ResolveClassifierProbe(string font)
        {
            try
            {
                return Grayson.Vision.HalconWrapper.Identification.OCRTool.ResolveClassifierPath(font);
            }
            catch { return null; }
        }

        // ======================================================================
        // 内容版本守卫（照抄 ColorDemoTaskFactory 的成熟做法）
        // ======================================================================
        private static void PurgeOutdatedDemo(OcrDemoSpec spec, List<string> step)
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
                try { File.Copy(f, target, true); } catch { }
            }
            return CountImages(destDir);
        }

        private static int CountImages(string dir)
            => Directory.GetFiles(dir, "*.png").Length
               + Directory.GetFiles(dir, "*.bmp").Length
               + Directory.GetFiles(dir, "*.jpg").Length
               + Directory.GetFiles(dir, "*.tif").Length;

        // ======================================================================
        // 配方构建（ReadImageFile → ReadOCR 两节点链）
        // ======================================================================
        private static RecipeModel BuildRecipe(OcrDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string ocrId = Guid.NewGuid().ToString("N");

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

            var ocrParam = new ReadOCRParam
            {
                FontFileName = spec.FontFileName,
                MinStrokeWidth = spec.MinStrokeWidth,
                ExpressionFilter = spec.ExpressionFilter,
                ThresholdMax = spec.ThresholdMax,
                AreaMin = spec.AreaMin,
                AreaMax = spec.AreaMax,
                Polarity = spec.Polarity,
                SortByRow = spec.SortByRow,
                MinConfidence = spec.MinConfidence
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
                    NodeId = ocrId, Type = NodeType.ReadOCR,
                    DisplayName = "字符识别(OCR·分割+MLP)",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = ocrParam
                }
            };
            var conns = new List<RecipeConnectionDto>
            {
                new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = readId, SourcePortId = readId + "_Image", SourcePortName = "Image",
                    TargetNodeId = ocrId, TargetPortId = ocrId + "_InputImage", TargetPortName = "InputImage"
                }
            };

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 字符识别链",
                ProductCategory = "特征识别读取示例（OCR Demo）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "识别读取族内嵌 Demo 自动生成（Assets\\OcrDemo，HTH 课堂 OCR 案例素材）：" +
                              "ReadImageFile[文件夹批处理] → ReadOCR[分割 + HALCON MLP 分类器] → " +
                              "引擎按模板期望串判 OK/NG",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "字符识别链",
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
