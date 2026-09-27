//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: BlobDemoTaskFactory.cs
// 说 明: 外观测量族（TaskKind.AppearanceMeasurement）· 梯队 B 内嵌演示包落地工厂：
//        ①「散热孔计数检测」（固定阈值·Blob 连通域计数） ②「金属表面划痕检测」（动态阈值·暗极性）。
//        （2026-09-26.4 定案，参数由 .workbuddy/scratch_flask_diag 探针实测+导图验身份锁定）
//
// ★★ 2026-09-26.3/.4 全量重定案（用户实测质疑"检出结果不对"——经诊断探针证实原两组 demo
//    检出对象身份全错，count 数字成立 ≠ 检出对象正确，必须导图可视化验身份）：
//   ① 旧"药瓶计数"（flasks_01~03, Fixed128~255）检出的是【托盘金属亮面/亮隙竖条】，
//     瓶体透明且互相粘连——官方对 flasks 素材的用法是 Deep Counting（DL 计数），
//     传统阈值链对该素材先天不可行 ⇒ 素材换成 progres.png（官方 holes.hdev 同款）；
//   ② 旧"表面划痕"（scratch_calib_01~12）检出的是【标定板边框过渡带】——该序列是斜视角
//     未矫正图，垫片上的划痕对比度不足，三节点链（阈值+连通域+面积）只能检出灰度过渡带
//     边缘（圆点环/白框边/板缘，2026-09-26 两轮探针+导图证伪）；官方完整链=标定→透视矫正
//     →fast_threshold，平台缺透视矫正节点 ⇒ 该素材先天做不了划痕检测。改用课堂原配素材
//     划痕.png（D:\HTH\课堂资料\3动态阈值划痕检测.hdev 原配：拉丝金属面+暗划痕，行业高频业务代表）。
//
// 素材来源（本机 HALCON 24.11 自带样例 examples\images\）：
//   ① progres.png（仪器外壳散热孔阵）——官方 holes.hdev 链：threshold(0,150) →
//     connection → select_shape(area)。平台无 ROI 节点，官方 ROI(260,90~360,350)
//     的职能由孔物理尺寸面积窗 [21,28] 替代：全域 220 检出中恰滤掉 6 个杂质
//     （4 个标签字区 12~34px + 2 个左缘 16/17px），留 213 个纯孔。
//     另合成 progres_defect.png（7×7 亮块堵 3 孔）⇒ 计数 210 ⇒ 数量超差 NG。
//   ② 课堂素材 Image\划痕.png（转灰 metal_scratch.png）+ mean_image(18,18) 背景图
//     （= 划痕修复后的理想表面 metal_scratch_ok.png）——探针实测：原帧检出 5 域
//     （914/239/85/61/49，导图验身份=全部划痕段），OK 帧检出 0 域。
//
// 平台等价链路（三节点，全部既有节点）：
//   ReadImageFile[本地文件夹批处理]
//     → ImageThreshold（① Fixed 0~150 提暗孔 / ② Dynamic mask=18 offset=5 Dark 课堂脚本同款）
//       → BlobAnalysis[连通域拆分 → 面积筛选 → 计数]
//   → 引擎按节点参数 CountMin/CountMax 出"个数区间"判据判 OK/NG（StandaloneVisionProcess
//     的 Blob 计数分支：Count 缺值/NaN 直接 NG，不静默放行）。
//
// ★ 判据语义（写进模板 OutputContractText）——两种判据方向各演示一种：
//   ① 计数：个数 ∈ [213,213] —— 孔阵完整 OK；progres_defect 堵 3 孔 ⇒ 210 超差 NG。
//   ② 有无：个数 ∈ [0,0] —— 检出任意划痕域即 NG；OK 帧（划痕修复理想表面）检出 0 判 OK。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 DL/测量/颜色演示包并列）。
//   ⚠ 幂等是"存在即跳过"，改判据/参数后必须 bump DemoSpecVersion（PurgeOutdatedDemo 清旧重落）。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Nodes.All.ImagePreprocess.ImageThreshold;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Nodes.All.Measurement2D.BlobAnalysis;
using Grayson.Vision.Repository.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>Blob 演示包定义（图源 / 阈值参数 / Blob 面积窗与个数判据）。</summary>
    internal class BlobDemoSpec
    {
        public string DemoKey;              // 落地目录名（Config\DemoData\{DemoKey}）
        public string RecipeCode;           // RCP-ME-BLOB-001 / RCP-ME-SCRATCH-002
        /// <summary>历史版本用过的 RecipeCode——版本守卫连带清理，防旧语义模板残留（可空）。</summary>
        public string[] LegacyRecipeCodes;
        public string EmbeddedSubDir;       // 内嵌资产子目录（Assets\{EmbeddedSubDir}\Images）
        public string FirstImageFile;       // 读图节点初始 FilePath（Assets 内实际存在的文件名）
        public string TemplateTitle;
        public string Summary;
        public string ModelNote;            // Remark 标注
        public string ApplicableMachines;

        /// <summary>演示图最少张数（EnsureDemoImages 的幂等阈值）。</summary>
        public int ExpectedImageCount;

        // ImageThreshold 节点参数（探针实测+导图验身份，见文件头）
        public ThresholdMethod Method;
        public int MinGray, MaxGray;        // Fixed 模式
        public int DynMaskSize, DynOffset;  // Dynamic 模式
        public DynPolarity Polarity;

        // BlobAnalysis 节点参数：AreaMin/AreaMax 面积窗；CountMin/CountMax 由引擎消费判 OK/NG
        public double AreaMin, AreaMax;
        public int CountMin, CountMax;

        /// <summary>BlobAnalysis 节点显示名（进引擎摘要「Blob计数 {DisplayName}=N [判据]」）。</summary>
        public string BlobNodeName;
    }

    /// <summary>把内嵌素材落地为「图源 + 三节点 Blob 配方链 + 外观测量族任务模板」（幂等，自动触发）。</summary>
    public static class BlobDemoTaskFactory
    {
        /// <summary>内嵌资产根（随构建复制到输出目录）。</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");

        /// <summary>
        /// 本演示包的内容版本号。★ 判据/参数改动必须同步 bump——
        /// 工厂"存在即跳过"，不 bump 则本机已落地的旧配方/模板静默沿用旧值。
        /// </summary>
        private const string DemoSpecVersion = "2026-09-26.4";

        private static readonly List<BlobDemoSpec> Specs = new List<BlobDemoSpec>
        {
            // ---------------- ① 散热孔计数（固定阈值·官方 holes.hdev 链对齐） ----------------
            new BlobDemoSpec
            {
                DemoKey = "散热孔计数检测",
                RecipeCode = "RCP-ME-BLOB-001",
                EmbeddedSubDir = "BlobDemo",
                FirstImageFile = "progres.png",
                TemplateTitle = "散热孔计数检测（固定阈值·暗孔）·Blob Demo",
                Summary = "固定阈值 0~150 提取暗散热孔 → Blob 连通域面积窗 21~28px²（孔物理尺寸窗，" +
                          "替代官方 ROI 职责：恰滤掉 6 个标签/边缘杂质）→ 个数判据 [213~213]：" +
                          "孔阵完整 OK；内嵌 NG 帧 progres_defect（7×7 亮块堵 3 孔）计数 210 超差 NG" +
                          "（官方 holes.hdev 链对齐：threshold(0,150)→connection→select_shape；纯软件独立运行）",
                ModelNote = "2026-09-26.3 重定案（旧『药瓶计数』检出的是托盘亮隙竖条非瓶子——flasks 素材瓶体透明" +
                            "且互相粘连，官方用法是 Deep Counting(DL)，传统链先天不可行，素材已换）：" +
                            "素材=本机 HALCON 样例 progres.png（仪器外壳散热孔阵，官方 holes.hdev 同款）+ 合成 NG 帧 progres_defect.png。" +
                            "链路 = ReadImageFile[文件夹批处理] → ImageThreshold[Fixed 0~150 提暗] → BlobAnalysis[面积筛选+计数]，" +
                            "参数由 scratch_flask_diag 探针实测+导图验身份锁定：全域 [10,100] 检出 220 中恰滤 6 杂质" +
                            "（4 个标签字区 12~34px + 2 个左缘 16/17px），[21,28] 留 213 纯孔；NG 帧实测 210。" +
                            "旧 DemoData『药瓶计数检测』图目录已无用，可手工删除。" +
                            "★ 个数判据存在节点参数 CountMin/CountMax 中（引擎反射读取），非模板字段",

                Method = ThresholdMethod.Fixed,
                MinGray = 0, MaxGray = 150,
                AreaMin = 21, AreaMax = 28,
                CountMin = 213, CountMax = 213,
                BlobNodeName = "Blob计数(散热孔)",
                ExpectedImageCount = 2,
                ApplicableMachines = "规则孔阵/点阵完整性计数、缺孔堵孔检出；换产品只需改面积窗与个数区间"
            },

            // ---------------- ② 金属表面划痕（动态阈值·暗极性；课堂 3动态阈值划痕检测.hdev 链对齐） ----------------
            new BlobDemoSpec
            {
                DemoKey = "金属表面划痕检测",
                RecipeCode = "RCP-ME-SCRATCH-002",
                LegacyRecipeCodes = new[] { "RCP-ME-SCRATCH-001", "RCP-ME-DOTS-001" },
                EmbeddedSubDir = "ScratchDemo",
                FirstImageFile = "metal_scratch.png",
                TemplateTitle = "金属表面划痕检测（动态阈值·暗极性）·Scratch Demo",
                Summary = "动态阈值（掩膜 18 / 偏移 5 / 暗极性，课堂脚本 3动态阈值划痕检测.hdev 同款参数）提取" +
                          "拉丝金属面上的暗划痕 → 面积窗 40~99999px² → 个数判据 [0~0]：检出任意划痕域即 NG；" +
                          "内嵌 OK 帧（mean_image 修复后的理想表面）检出 0 域判 OK（纯软件独立运行）",
                ModelNote = "2026-09-26.4 重定案（旧『标定板圆点计数』按用户要求撤下——计数类已有散热孔代表；" +
                            "scratch_calib 素材经两轮探针+导图证实：斜视角未矫正图上三节点链检不出垫片划痕，" +
                            "检出的全是圆点环/白框边/板缘等灰度过渡带；官方完整链需标定→透视矫正→阈值，" +
                            "平台缺透视矫正节点）：素材换课堂原配 Image\\划痕.png（拉丝金属面暗划痕，行业高频业务代表）。" +
                            "探针实测：NG 帧检出 5 域（914/239/85/61/49，导图验身份=全部划痕段）；" +
                            "OK 帧=mean_image(18,18) 背景图（划痕修复理想表面）检出 0 域。" +
                            "★ 旧 DemoData『药瓶计数检测』『表面划痕检测』『标定板圆点计数检测』图目录已无用，可手工删除。" +
                            "★ 个数判据存在节点参数 CountMin/CountMax 中（引擎反射读取），非模板字段",

                Method = ThresholdMethod.Dynamic,
                DynMaskSize = 18, DynOffset = 5,
                Polarity = DynPolarity.Dark,
                MinGray = 0, MaxGray = 255,     // Dynamic 模式下不消费，仅占位
                AreaMin = 40, AreaMax = 99999,
                CountMin = 0, CountMax = 0,
                BlobNodeName = "Blob计数(划痕)",
                ExpectedImageCount = 2,
                ApplicableMachines = "金属/塑料表面划痕、压伤、脏污检出；换产品只需改面积窗与个数区间"
            }
        };

        /// <summary>落地全部内嵌 Blob/划痕演示包（幂等）。返回人类可读结果摘要。</summary>
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

        private static string LandOne(BlobDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = Path.Combine(EmbeddedRoot, spec.EmbeddedSubDir);
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\{spec.EmbeddedSubDir}）";

            // ---------- 1) 演示图像落地（DemoData\{DemoKey}\Images\） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir, spec.ExpectedImageCount);

            // ---------- 2) 内容版本守卫（改参数不 bump 版本 = 静默沿用旧值）----------
            PurgeOutdatedDemo(spec, step);

            // ---------- 3) 配方落地（ReadImageFile → ImageThreshold → BlobAnalysis 三节点链） ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → ImageThreshold → BlobAnalysis，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌 Images 目录）");
            }

            // ---------- 4) 任务模板生成（外观测量族；判据在节点参数，模板不带 MeasurementSpecs） ----------
            var library = new TaskTemplateLibraryService();
            var tpl = library.LoadAll().FirstOrDefault(t => t.Kind == TaskKind.AppearanceMeasurement
                && string.Equals(t.BoundRecipeId, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (tpl == null)
            {
                tpl = new TaskTemplateInfo
                {
                    TemplateCode = library.GenerateCode(TaskKind.AppearanceMeasurement),
                    DisplayName = spec.TemplateTitle,
                    Kind = TaskKind.AppearanceMeasurement,
                    DependencyMode = TaskDependencyMode.Standalone,
                    Status = TaskTemplateStatus.Published,
                    Version = "v1",
                    Summary = spec.Summary,
                    ApplicableMachines = spec.ApplicableMachines,
                    ImageSource = new TaskTemplateImageSource
                    {
                        Kind = TaskImageSourceKind.LocalFolder,
                        LocalFolderPath = imgDir,
                        LocalFilePattern = "*.png;*.jpg;*.bmp;*.tif",
                        RepeatDelayMs = 0
                    },
                    BoundRecipeId = spec.RecipeCode,
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " Blob 链",
                    ShapeTemplateName = null,
                    PixelPerMm = 1.0,        // 计数/划痕判"个数"，无毫米量纲；占位 1.0 不参与判定
                    MeasurementSpecs = null,
                    OutputContractText = "输出 Blob 连通域 + Count（检出个数，double）；" +
                        "摘要「Blob计数 " + spec.BlobNodeName + "=N [判据区间]」，超差标 ←超差；" +
                        "判据=个数 ∈ [CountMin~CountMax]（节点参数，引擎反射读取）→ OK，否则 NG；" +
                        "Count 缺值/NaN/未产出一律直接 NG——判据纪律：算不出 ≠ 0，不许静默放行",
                    VerdictToIo = false,
                    VerdictRule = null,   // Blob 判据走节点 CountMin/CountMax（引擎 Blob 分支），不复用 DL 关键字判据
                    // ★ 版本标记：PurgeOutdatedDemo 靠它判断"本机落地的是不是当前版本"
                    Remark = spec.ModelNote + " [spec=" + DemoSpecVersion + "]"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（个数判据 [{spec.CountMin}~{spec.CountMax}] 已内联进节点参数）");
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

        /// <summary>
        /// 把「绑到本 RecipeCode、但 Remark 里没有当前 <see cref="DemoSpecVersion"/> 标记」的
        /// 任务模板与配方清掉，让本次以新参数重新落地（判据纪律：改参数不 bump 版本 = 静默失效）。
        /// </summary>
        private static void PurgeOutdatedDemo(BlobDemoSpec spec, List<string> step)
        {
            string marker = "spec=" + DemoSpecVersion;
            try
            {
                var library = new TaskTemplateLibraryService();
                // 清理范围 = 绑到当前 RecipeCode 或任何历史 RecipeCode（LegacyRecipeCodes）的模板——
                // 改 RCP Id 后若不纳入旧 Id，旧语义模板会因"按新 Id 找不到"而永久残留。
                var codes = new List<string> { spec.RecipeCode };
                if (spec.LegacyRecipeCodes != null) codes.AddRange(spec.LegacyRecipeCodes);
                var bound = library.LoadAll()
                    .Where(t => t.Kind == TaskKind.AppearanceMeasurement
                                && codes.Any(c => string.Equals(t.BoundRecipeId, c, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (bound.Count == 0) return;
                if (bound.All(t => (t.Remark ?? string.Empty).Contains(marker))) return;

                foreach (var t in bound)
                    if (library.Delete(t.TemplateCode))
                        step.Add($"↻ 演示包版本 {DemoSpecVersion} 更新：已清理旧任务模板 {t.TemplateCode}（绑定 {t.BoundRecipeId}）");

                var storage = new RecipeStorageService();
                foreach (var code in codes)
                    if (storage.DeleteRecipe(code))
                        step.Add($"↻ 已同步清理旧配方 {code}，将按新参数重落");
            }
            catch (Exception ex)
            {
                step.Add("⚠ 演示包版本守卫执行失败（可能仍沿用旧参数）：" + ex.Message);
            }
        }

        /// <summary>把内嵌 Images 的演示图拷到 DemoData 目录（幂等：已 ≥ 期望张数则跳过，用户增删以 DemoData 为准）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir, int expectedCount)
        {
            Directory.CreateDirectory(destDir);
            int existing = CountImages(destDir);
            if (existing >= expectedCount) return existing;

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
        // 配方构建（ReadImageFile → ImageThreshold → BlobAnalysis 三节点链）
        // ======================================================================

        private static RecipeModel BuildRecipe(BlobDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string thrId = Guid.NewGuid().ToString("N");
            string blobId = Guid.NewGuid().ToString("N");

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, spec.FirstImageFile),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // 阈值分割节点（探针实测参数；Dark 极性走 2026-09-26 断裂点修复后的算子链）
            var thrParam = new ImageThresholdParam
            {
                Method = spec.Method,
                MinGray = spec.MinGray,
                MaxGray = spec.MaxGray,
                DynamicMaskSize = spec.DynMaskSize,
                DynamicOffset = spec.DynOffset,
                Polarity = spec.Polarity
            };

            // Blob 分析节点（面积窗 + 个数判据；判据由引擎反射读取）
            var blobParam = new BlobAnalysisParam
            {
                AreaMin = spec.AreaMin,
                AreaMax = spec.AreaMax,
                CountMin = spec.CountMin,
                CountMax = spec.CountMax
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
                    NodeId = thrId, Type = NodeType.ImageThreshold,
                    DisplayName = spec.Method == ThresholdMethod.Fixed
                        ? $"阈值分割(固定 {spec.MinGray}~{spec.MaxGray})"
                        : $"阈值分割(动态 mask{spec.DynMaskSize}/off{spec.DynOffset}/{spec.Polarity})",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = thrParam
                },
                new RecipeNodeDto
                {
                    NodeId = blobId, Type = NodeType.BlobAnalysis, DisplayName = spec.BlobNodeName,
                    Enable = true, PosX = 640, PosY = 120, ParameterModel = blobParam
                }
            };

            // 连线（照抄 MeasurementDemoTaskFactory 验证过的规则：PortId = NodeId + "_" + 端口注册名）
            var conns = new List<RecipeConnectionDto>();
            Action<string, string, string, string> link = (srcId, srcPort, dstId, dstPort) =>
                conns.Add(new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = srcId, SourcePortId = srcId + "_" + srcPort, SourcePortName = srcPort,
                    TargetNodeId = dstId, TargetPortId = dstId + "_" + dstPort, TargetPortName = dstPort
                });

            // 图像：读图 → 阈值分割；Region：阈值 → Blob 分析；底图：阈值 OutputImage → Blob InputImage
            // （BlobImage 端口名含 "Image" ⇒ 帧事件自动推送上屏，叠加层=绿色连通域+计数标注）
            link(readId, "Image", thrId, "InputImage");
            link(thrId, "OutputRegion", blobId, "InputRegion");
            link(thrId, "OutputImage", blobId, "InputImage");

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " Blob 链",
                ProductCategory = "外观测量示例（Blob Demo）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "外观测量族内嵌 Demo 自动生成（Assets\\" + spec.EmbeddedSubDir + "，HALCON 样例）：" +
                              "ReadImageFile[文件夹批处理] → ImageThreshold[" +
                              (spec.Method == ThresholdMethod.Fixed
                                  ? $"Fixed {spec.MinGray}~{spec.MaxGray}"
                                  : $"Dynamic mask{spec.DynMaskSize}/off{spec.DynOffset}/{spec.Polarity}") +
                              "] → BlobAnalysis[面积筛选 → 计数] → 引擎按节点个数判据 [" +
                              spec.CountMin + "~" + spec.CountMax + "] 判 OK/NG",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "Blob 链",
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
