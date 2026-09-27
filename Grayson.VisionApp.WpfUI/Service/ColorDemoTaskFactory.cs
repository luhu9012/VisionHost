//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ColorDemoTaskFactory.cs
// 说 明: 特征识别读取族（TaskKind.FeatureIdentification）首批内嵌演示包落地工厂
//        ——「颜色识别与分拣 · 柑橘水果」（2026-09-26）。
//
// 素材来源：本机 HALCON 24.11 自带样例图 examples\images\color\citrus_fruits_01~12.png
//   （640×480 彩色；木桌背景上摆放柑橘——橙子/柠檬，部分帧带绿叶遮挡）。
//   12 张图橙色像素占比已用 PIL 离线实测（H∈[15,40]、S>60、V>60 口径）：
//     citrus_01=40.84%  02=35.13%  03=13.20%  04= 7.39%  05=21.02%  06=35.30%
//     07=31.84%  08=19.69%  09=22.96%  10=27.28%  11=36.58%  12=36.62%
//   ⇒ 占比下限阈值 10% 时恰好 citrus_04 判 NG、其余 OK——演示"阈值判据真的在拦"。
//
// 平台等价链路（零新增算子，全部既有节点）：
//   ReadImageFile[本地文件夹批处理]
//     → ColorIdentify[HSV 颜色提取：H 15~40 / S≥60 / V≥60 → 橙色区域 + 面积占比%]
//       ≡ hdev decompose3 → trans_from_rgb(hsv) → threshold(Hue域) → area_center
//   → 引擎按模板 ConfidenceThreshold（识别读取族语义=面积占比下限%）判 OK/NG。
//
// ★ 阈值语义约定：识别读取族模板的 ConfidenceThreshold 字段复用为「面积占比下限(%)」，
//   不再表示 DL 置信度（族语义不同，见模板 OutputContractText / 引擎 ApplyIdentifyVerdict）。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 DL/测量演示包并列）。
//   ⚠ 幂等是"存在即跳过"，改判据/参数后必须 bump DemoSpecVersion（PurgeOutdatedDemo 清旧重落）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.Identification.ColorIdentify;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Repository.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>颜色识别演示包定义（图源 / HSV 参数 / 占比阈值）。</summary>
    internal class ColorDemoSpec
    {
        public string DemoKey;              // 落地目录名
        public string RecipeCode;           // RCP-ID-COLOR-001
        public string TemplateTitle;
        public string Summary;
        public string ModelNote;            // Remark 标注

        // ColorIdentify 节点 HSV 参数（PIL 离线实测橙色簇后标定，口径见文件头）
        public double HueMin, HueMax, SatMin, SatMax, ValMin, ValMax;

        // 判据：面积占比下限（%）；写入模板 ConfidenceThreshold（识别读取族语义）
        public double MinAreaRatioPercent;
    }

    /// <summary>把内嵌颜色素材落地为「图源 + ColorIdentify 配方链 + 识别读取族任务模板」（幂等，自动触发）。</summary>
    public static class ColorDemoTaskFactory
    {
        /// <summary>内嵌资产根（随构建复制到输出目录）。</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "ColorDemo");

        /// <summary>
        /// 本演示包的内容版本号。★ 判据/参数改动必须同步 bump——
        /// 工厂"存在即跳过"，不 bump 则本机已落地的旧配方/模板静默沿用旧值。
        /// </summary>
        private const string DemoSpecVersion = "2026-09-26.1";

        private static readonly List<ColorDemoSpec> Specs = new List<ColorDemoSpec>
        {
            new ColorDemoSpec
            {
                DemoKey = "柑橘水果颜色识别",
                RecipeCode = "RCP-ID-COLOR-001",
                TemplateTitle = "柑橘水果颜色识别（HSV 分拣）·Color Demo",
                Summary = "HSV 颜色空间提取橙色（柑橘）区域 → 输出面积占比%，按占比下限 10% 判 OK/NG：" +
                          "画面中有足量橙色水果 → OK（本批可分拣）；占比过低 → NG（缺料/错料）。" +
                          "12 张 HALCON 样例图中 citrus_fruits_04（7.39%）判 NG、其余 OK（纯软件独立运行）",
                ModelNote = "2026-09-26 特征识别读取族首批演示（素材=本机 HALCON 样例 citrus_fruits_01~12.png）：" +
                            "链路 = ReadImageFile[文件夹批处理] → ColorIdentify[HSV]，" +
                            "与 hdev 的 decompose3 → trans_from_rgb(hsv) → threshold → area_center 一一对应；" +
                            "HSV 参数由 PIL 离线实测橙色簇标定（H 15~40 / S≥60 / V≥60，HALCON Hue 与 PIL 同为 0~255 刻度）；" +
                            "12 帧橙色占比实测 7.39%~57.19%，阈值 10% ⇒ 仅 citrus_04 判 NG。" +
                            "★ ConfidenceThreshold 在识别读取族=面积占比下限(%)，非 DL 置信度",

                HueMin = 15, HueMax = 40,
                SatMin = 60, SatMax = 255,
                ValMin = 60, ValMax = 255,
                MinAreaRatioPercent = 10.0
            }
        };

        /// <summary>落地全部内嵌颜色演示包（幂等）。返回人类可读结果摘要。</summary>
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

        // ======================================================================
        // 单示例落地流水线（内嵌资产 → 图源 → 配方 → 模板）
        // ======================================================================

        private static string LandOne(ColorDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = EmbeddedRoot;
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\ColorDemo）";

            // ---------- 1) 演示图像落地（DemoData\{DemoKey}\Images\） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir);

            // ---------- 2) 内容版本守卫（改参数不 bump 版本 = 静默沿用旧值）----------
            PurgeOutdatedDemo(spec, step);

            // ---------- 3) 配方落地（ReadImageFile → ColorIdentify 两节点链） ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → ColorIdentify，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌 Images 目录）");
            }

            // ---------- 4) 任务模板生成（识别读取族） ----------
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
                    ApplicableMachines = "水果分拣/色块检出等颜色场景演示；换产品只需改 HSV 区间与占比阈值",
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.LocalFolder,
                        LocalFolderPath = imgDir,
                        LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                        RepeatDelayMs = 0
                    },
                    BoundRecipeId = spec.RecipeCode,
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 颜色识别链",
                    // ★ 识别读取族语义：ConfidenceThreshold = 面积占比下限（%），非 DL 置信度
                    ConfidenceThreshold = spec.MinAreaRatioPercent,
                    MeasurementSpecs = null,
                    OutputContractText = "识别读取输出 颜色区域 + 面积占比%；摘要「颜色识别 橙色区域占比=x.xx%」；" +
                        "判据=占比 ≥ ConfidenceThreshold(10%) → OK，否则 NG（缺料/错料语义）",
                    VerdictToIo = false,
                    VerdictRule = null,   // 识别读取族走占比阈值（ConfidenceThreshold），不复用关键字判据
                    // ★ 版本标记：PurgeOutdatedDemo 靠它判断"本机落地的是不是当前版本"
                    Remark = spec.ModelNote + " [spec=" + DemoSpecVersion + "]"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（占比阈值 {spec.MinAreaRatioPercent}% 已内联）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        // ======================================================================
        // 内容版本守卫（照抄 MeasurementDemoTaskFactory 的成熟做法）
        // ======================================================================

        /// <summary>
        /// 把「绑到本 RecipeCode、但 Remark 里没有当前 <see cref="DemoSpecVersion"/> 标记」的
        /// 任务模板与配方清掉，让本次以新参数重新落地（判据纪律：改参数不 bump 版本 = 静默失效）。
        /// </summary>
        private static void PurgeOutdatedDemo(ColorDemoSpec spec, List<string> step)
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

        /// <summary>把内嵌 Images 的演示图拷到 DemoData 目录（幂等：已 ≥4 张则跳过，用户增删以 DemoData 为准）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            int existing = CountImages(destDir);
            if (existing >= 4) return existing;

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
        // 配方构建（ReadImageFile → ColorIdentify 两节点链）
        // ======================================================================

        private static RecipeModel BuildRecipe(ColorDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string colorId = Guid.NewGuid().ToString("N");

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, "citrus_fruits_01.png"),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // 颜色识别节点：HSV 区间（PIL 离线实测橙色簇标定，见文件头）
            var colorParam = new ColorIdentifyParam
            {
                HueMin = spec.HueMin, HueMax = spec.HueMax,
                SatMin = spec.SatMin, SatMax = spec.SatMax,
                ValMin = spec.ValMin, ValMax = spec.ValMax
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
                    NodeId = colorId, Type = NodeType.ColorIdentify, DisplayName = "颜色识别(橙色·HSV)",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = colorParam
                }
            };
            var conns = new List<RecipeConnectionDto>
            {
                new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = readId, SourcePortId = readId + "_out", SourcePortName = "Image",
                    TargetNodeId = colorId, TargetPortId = colorId + "_in", TargetPortName = "InputImage"
                }
            };

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 颜色识别链",
                ProductCategory = "特征识别读取示例（Color Demo）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "识别读取族内嵌 Demo 自动生成（Assets\\ColorDemo，HALCON citrus_fruits 样例）：" +
                              "ReadImageFile[文件夹批处理] → ColorIdentify[HSV 橙色提取 → 区域+面积占比] → " +
                              "引擎按模板占比下限判 OK/NG",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "颜色识别链",
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
