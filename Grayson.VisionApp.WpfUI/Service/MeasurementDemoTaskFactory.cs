//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: MeasurementDemoTaskFactory.cs
// 说 明: 外观测量族内嵌演示包落地工厂（stage9-3，2026-09-10）。
//
// 目的：把"外观测量族"做成能开箱即跑的 TPL-AM seed，闭环验证三族全到执行层。
//   复用 DlDemoTaskFactory 的范式：内嵌课堂素材 → 运行时幂等落 Config\DemoData → 配方链
//   ReadImageFile[本地文件夹] → FitCircle（环形卡尺取样+亚像素拟合圆）→ 判据按模板
//   MeasurementSpecs 第一项的 NominalMm±ToleranceMm 判定 OK/NG。
//
// 落地资产（一次性嵌入 Assets\MeasureDemo\）：
//   · rings_and_nuts.png —— HALCON 课堂素材（含多个齿轮与六角螺母的 640×480 灰度图）；
//     选图中左下六角螺母的外圆做 FitCircle 测量对象（质心≈(337,108)、半径≈53px、环形卡尺取样）。
//   · 配方 RCP-AM-001：ReadImageFile[本地文件夹批处理] → FitCircle[种子硬编码到螺母外圆]。
//   · 任务模板 TPL-AM-001：AppearanceMeasurement 族，独立纯软件运行；PixelPerMm=0.1（演示用假设当量，
//     真机产线应通过标定件实测；Remark 标注），MeasurementSpecs 一项「外圆直径 NominalMm=10.6±0.5」，
//     摘要「圆 直径=10.60 mm 圆心=(...) R=53.00px」按区间判定 OK/NG。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 EnsureDemoTasks 并列）。
// 新增示例：把图拷进 Assets\MeasureDemo\，本工厂 Specs 增加一条并重新编译即可。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Nodes.All.Measurement2D.FitCircle;
using Grayson.Vision.Repository.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>单个测量演示示例的定义（图源 / 节点种子 / 测量项 / 当量）。</summary>
    internal class MeasureDemoSpec
    {
        public string DemoKey;           // 落地目录名（齿轮+螺母直径）
        public string SourceFileName;    // 内嵌 Assets\MeasureDemo\{SourceFileName}
        public string RecipeCode;        // RCP-AM-001
        public string TemplateTitle;
        public string Summary;
        public string ApplicableMachines;
        // FitCircle 种子（像素，针对 rings_and_nuts.png 640×480 图）
        public double SeedRow, SeedCol, SeedRadius;
        public double AnnulusHalf;
        public double Sigma, Threshold;
        public int MinEdgePoints;
        // 测量判据
        public double PixelPerMm;          // 演示用假设当量（真机标定件实测）
        public string MeasureName;         // 测量项名称
        public double NominalMm;           // 标称值（mm，已含当量换算）
        public double ToleranceMm;
        public string ModelNote;           // Remark 标注
    }

    /// <summary>把内嵌的测量素材落地为「图源 + 配方链 + 任务模板」（幂等，自动触发）。</summary>
    public static class MeasurementDemoTaskFactory
    {
        /// <summary>内嵌资产根（随构建复制到输出目录）。</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "MeasureDemo");

        private static readonly List<MeasureDemoSpec> Specs = new List<MeasureDemoSpec>
        {
            new MeasureDemoSpec
            {
                DemoKey = "六角螺母外圆直径",
                SourceFileName = "rings_and_nuts.png",
                RecipeCode = "RCP-AM-001",
                TemplateTitle = "六角螺母外圆直径（环形卡尺·亚像素拟合）·Measurement Demo",
                Summary = "FitCircle 环形卡尺在种子圆心周围取样，亚像素拟合圆心+半径；模板 PixelPerMm=0.1 演示当量换算成 mm，按 MeasurementSpecs 第一项 NominalMm±ToleranceMm 判 OK/NG（纯软件独立运行）",
                ApplicableMachines = "课堂图/演示态；真机产线应通过标定件实测 PixelPerMm 后填入",
                SeedRow = 337.0, SeedCol = 108.0, SeedRadius = 53.0,
                AnnulusHalf = 8.0,
                Sigma = 1.0, Threshold = 30.0,
                MinEdgePoints = 8,
                PixelPerMm = 0.1,
                MeasureName = "外圆直径",
                NominalMm = 10.6,    // 53px*2*0.1 = 10.6 mm
                ToleranceMm = 0.5,
                ModelNote = "stage9-3 测量族闭环 Demo（2026-09-10）：HALCON 课堂素材 rings_and_nuts.png 选左下六角螺母外圆做 FitCircle；PixelPerMm=0.1 为演示假设当量，真机需标定件实测；seed 为目测估算（质心+R），annulus 半宽 8px 容错种子偏差 5~10px；如种子偏离过大可手动调参"
            }
        };

        /// <summary>落地全部内嵌测量演示包（幂等）。返回人类可读结果摘要。</summary>
        public static string EnsureMeasurementDemoTasks()
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

        private static string LandOne(MeasureDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = EmbeddedRoot;
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\MeasureDemo）";

            var embedFile = Path.Combine(embedDir, spec.SourceFileName);
            if (!File.Exists(embedFile))
                return $"⚠ [{spec.DemoKey}] 内嵌素材缺失 {spec.SourceFileName}，跳过。";

            // ---------- 1) 演示图像落地（DemoData\{DemoKey}\Images\） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir, spec.SourceFileName);

            // ---------- 2) 配方落地（ReadImageFile → FitCircle） ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（ReadImageFile → FitCircle[seed=({spec.SeedRow},{spec.SeedCol}) R={spec.SeedRadius} px]，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌素材）");
            }

            // ---------- 3) 任务模板生成 ----------
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
                    BoundRecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 测量链",
                    PixelPerMm = spec.PixelPerMm,
                    MeasurementSpecs = new List<MeasurementSpecItem>
                    {
                        new MeasurementSpecItem
                        {
                            Name = spec.MeasureName,
                            ToolKind = "FitCircle 环形卡尺",
                            RegionRef = "左下六角螺母外圆（seed 硬编码）",
                            NominalMm = spec.NominalMm,
                            ToleranceMm = spec.ToleranceMm,
                            Enabled = true,
                            Note = "stage9-3 演示测量项；真机应按产品规格书填 NominalMm/ToleranceMm"
                        }
                    },
                    OutputContractText = "测量摘要：「圆 直径=10.60 mm 圆心=(R,C) R=53.00px」；按 MeasurementSpecs 标称±公差判 OK/NG（区间 [10.10, 11.10] mm）",
                    VerdictToIo = false,
                    VerdictRule = null,   // 测量族走 MeasurementSpecs 判据，不复用 DL 关键字判据
                    Remark = spec.ModelNote
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（MeasurementSpecs+PixelPerMm={spec.PixelPerMm} 已内联）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        /// <summary>把内嵌素材中的指定源文件拷到 DemoData 图像目录（幂等：已存在则跳过）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir, string sourceFileName)
        {
            Directory.CreateDirectory(destDir);
            int existing = Directory.GetFiles(destDir, "*.png").Length
                           + Directory.GetFiles(destDir, "*.bmp").Length
                           + Directory.GetFiles(destDir, "*.jpg").Length;
            if (existing >= 1) return existing; // 至少已有 1 张，跳过

            var src = Path.Combine(embedDir, sourceFileName);
            if (!File.Exists(src)) return 0;
            var dst = Path.Combine(destDir, sourceFileName);
            File.Copy(src, dst, true);
            return Directory.GetFiles(destDir).Length;
        }

        // ======================================================================
        // 配方构建（ReadImageFile → FitCircle 两节点链）
        // ======================================================================

        private static RecipeModel BuildRecipe(MeasureDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string fitId = Guid.NewGuid().ToString("N");

            // 读图节点：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, spec.SourceFileName),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // FitCircle 节点：环形卡尺取样 + 亚像素拟合圆（seed 硬编码到本图螺母外圆）
            var fitParam = new FitCircleParam
            {
                SeedRow = spec.SeedRow,
                SeedCol = spec.SeedCol,
                Radius = spec.SeedRadius,
                AnnulusHalf = spec.AnnulusHalf,
                ArcStartDeg = 0.0,
                ArcExtentDeg = 360.0,
                Sigma = spec.Sigma,
                Threshold = spec.Threshold,
                Transition = CaliperTransition.All,
                Select = CaliperEdgeSelect.All,
                MinEdgePoints = spec.MinEdgePoints
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
                    NodeId = fitId, Type = NodeType.FitCircle, DisplayName = "FitCircle 环形卡尺拟合(" + spec.DemoKey + ")",
                    Enable = true, PosX = 380, PosY = 120, ParameterModel = fitParam
                }
            };
            var conns = new List<RecipeConnectionDto>
            {
                new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = readId, SourcePortId = readId + "_out", SourcePortName = "Image",
                    TargetNodeId = fitId, TargetPortId = fitId + "_in", TargetPortName = "InputImage"
                }
            };

            var dto = new VisionRecipeDto
            {
                RecipeId = Guid.NewGuid().ToString("N"),
                RecipeCode = spec.RecipeCode,
                RecipeName = spec.RecipeCode + " · " + spec.DemoKey + " 测量链",
                ProductCategory = "外观测量示例（Measurement Demo）",
                Version = "1.0.0",
                Author = "admin",
                CreatedTime = DateTime.Now,
                LastModifiedTime = DateTime.Now,
                ApprovalStatus = RecipeApprovalStatus.Draft,
                Description = "stage9-3 测量族内嵌 Demo 自动生成（Assets\\MeasureDemo）：ReadImageFile[本地文件夹批处理] → FitCircle[seed 硬编码 → 环形卡尺拟合圆 → 引擎按 MeasurementSpecs 判 OK/NG]",
                MainProcess = new ProcessDto
                {
                    ProcessId = Guid.NewGuid().ToString("N"),
                    ProcessName = spec.DemoKey + "测量链",
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