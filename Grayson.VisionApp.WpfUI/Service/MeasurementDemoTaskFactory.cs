//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: MeasurementDemoTaskFactory.cs
// 说 明: 外观测量族内嵌演示包落地工厂（stage9-3 2026-09-10 首版；
//        2026-09-25 换成「模板匹配 + 几何变换 + 测量」课堂案例）。
//
// 案例来源：D:\HTH\课堂资料\5模板匹配+几何变换+测量.hdev（HALCON 课堂）
//   素材：lk_01~04.bmp（640×480 8bit 灰度）—— 同一块带圆孔工件在视野里平移 + 旋转摆放。
//         实测匹配角 Δθ（相对 lk_01 基准）：0° / +20.94° / +3.96° / −43.92°
//         —— 取自离线探针 .workbuddy/measure_chain_probe/run.py，不用"目测约 xx°"。
//   hdev 链路：edges_sub_pix → create_shape_model_xld → find_shape_model
//              → vector_angle_to_rigid → affine_trans_point_2d（圆 + 两条边）
//              → create_metrology_model / add_metrology_object_generic → apply_metrology_model
//              → distance_pl（圆心到线 2 的距离）
//
// 平台等价链路（全部用既有节点，无新增算法）：
//   ReadImageFile[本地文件夹批处理]
//     → ShapeMatch[形状模板匹配 → 基准点/角度]              ≡ find_shape_model
//     → CreateFixture[基准位姿 → 当前位姿 的刚性矩阵]        ≡ vector_angle_to_rigid
//     → ApplyFixture ×3[圆心 / 线1中点 / 线2中点 跟随]        ≡ affine_trans_point_2d
//     → FitCircle[圆孔] + FitLine ×2[两侧边]                 ≡ metrology 圆/线
//   → 引擎按 MeasurementSpecs 出「圆孔直径 / 圆心到线距离 / 两线间距」并判 OK/NG
//                                                            ≡ distance_pl（派生量应用）
//
// 教学点：工件随便摆（平移 + 旋转），测出来的尺寸恒定不变 —— 因为测量要素是「跟着工件走」的
//         （种子由模板位姿→当前位姿的刚性矩阵变换而来），而不是钉死在图像某一处。
//
// ★★ 已知限制（2026-09-25 离线探针实测定案，细节见 .workbuddy/measure_chain_probe 与 ModelNote）：
//   · 圆孔直径：四个姿态全稳（实测 mean=7.130mm、极差 0.0592mm）—— 教学点由它承载。
//   · 两条竖边：lk_01/lk_02 正常；lk_03 因【匹配分只有 0.640、位姿把线要素摆偏 ~10px】而量错
//     （★卡尺找到的是"真边"不是邻边——该 ±20px 窗口内只有一处边；错在种子摆错，不在卡尺）；
//     lk_04 因【measure_pos 在该处静默取不到边（剖面上边就在种子上，任何口径都只给 amp≈1）】而丢边。
//     ⇒ 这两帧会判 NG，且原因不是公差超差，而是上述两条链路缺陷（前者匹配、后者算子环境）。
//   · 开关：本工厂 EnableLineMeasure 置 false 即切成"只测圆孔、四帧全 OK"的绿色演示。
//
// 全程幂等：模板中心每次 Refresh() 自动执行（与 EnsureDemoTasks 并列）。
//   ⚠ 幂等是"存在即跳过"，所以【改判据/参数后必须 bump DemoSpecVersion】，
//     否则本机已落地的旧配方/任务模板会静默沿用旧值（PurgeOutdatedDemo 负责按版本清理重落）。
// 新增示例：把图拷进 Assets\MeasureDemo\，本工厂 Specs 增加一条并重新编译即可。
//===================================================================================
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Recipe.DTOs;
using Grayson.Vision.Contracts.Recipe.Enums;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.Nodes.All.CalibrationLocation.ApplyFixture;
using Grayson.Vision.Nodes.All.CalibrationLocation.CreateFixture;
using Grayson.Vision.Nodes.All.CalibrationLocation.ShapeMatch;
using Grayson.Vision.Nodes.All.ImageInput.ReadImageFile;
using Grayson.Vision.Nodes.All.Measurement2D.FitCircle;
using Grayson.Vision.Nodes.All.Measurement2D.FitLine;
using Grayson.Vision.Repository.Services;
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>单个测量演示示例的定义（图源 / 模板 / 测量要素 / 测量项 / 当量）。</summary>
    internal class MeasureDemoSpec
    {
        public string DemoKey;              // 落地目录名
        public string RecipeCode;           // RCP-AM-002
        public string TemplateTitle;
        public string Summary;
        public string ApplicableMachines;
        public string ModelNote;            // Remark 标注

        // ── 形状模板（学习用基准帧 = lk_01）──
        public string BaseImageFileName;    // lk_01.bmp
        public string TemplateName;         // 模板管理里的唯一名
        public double TemplateRoiRow1, TemplateRoiCol1, TemplateRoiRow2, TemplateRoiCol2;
        public double SearchRoiRow1, SearchRoiCol1, SearchRoiRow2, SearchRoiCol2;
        public double MinScore;

        // ── 模板基准位姿（= 默认基准点 = 学习框中心；CreateFixture 的 ref 端）──
        public double BaselineRow, BaselineCol, BaselineAngle;

        // ── 测量要素（基准帧图像坐标，随位姿刚性变换后交给卡尺）──
        // 圆孔：圆心 + 期望半径（半径不随刚体变换改变）
        public double CircleRow, CircleCol, CircleRadius;
        // 两侧竖直边：边中点 + 边走向（90°=竖直边）
        public double Line1MidRow, Line1MidCol, Line1PhiDeg;
        public double Line2MidRow, Line2MidCol, Line2PhiDeg;
        // 卡尺共用参数
        public double AnnulusHalf;
        public double HalfSpanAlongEdge, ScanHalf;
        public double ProbeAvgHalf;          // 沿边平均半宽（= measure 的 Length2）
        public int NumPoints;                // 直线卡尺探针数
        public double Sigma, Threshold;

        // ── 测量判据 ──
        public double PixelPerMm;           // 演示用假设当量（真机标定件实测）
        public List<MeasurementSpecItem> Specs;
    }

    /// <summary>把内嵌的测量素材落地为「图源 + 形状模板 + 配方链 + 任务模板」（幂等，自动触发）。</summary>
    public static class MeasurementDemoTaskFactory
    {
        /// <summary>内嵌资产根（随构建复制到输出目录）。</summary>
        private static string EmbeddedRoot
            => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "MeasureDemo");

        /// <summary>演示图像文件名前缀（同目录内全部 <c>lk_*.bmp</c> 都会拷进落地目录）。</summary>
        private const string DemoImagePattern = "lk_*.bmp";

        // 旧版（2026-09-10 六角螺母外圆直径）落的资产 —— 本次"换为课堂案例"时一并清理。
        private const string LegacyDemoKey = "六角螺母外圆直径";
        private const string LegacyRecipeCode = "RCP-AM-001";
        private const string LegacyTemplateKeyword = "六角螺母外圆直径";

        /// <summary>
        /// 本演示包的内容版本号。★ 必须随【判据/参数/链路结构】的每次实质改动一起 bump：
        /// 工厂是"存在即跳过"的幂等逻辑，若只改参数不 bump，本机已落地的旧配方/任务模板会一直沿用
        /// 旧标称值（典型的"改了代码但现场没变"）。bump 后 LandOne 会识别出旧版本并清掉重落。
        /// </summary>
        private const string DemoSpecVersion = "2026-09-25.3";

        /// <summary>
        /// 两条竖直边的直线卡尺开关（教学演示用）。
        /// true  = 完整多量设计（教学点：多量并列 + 派生几何量），但 lk_03/lk_04 两帧会 NG（原因见 ModelNote）；
        /// false = 只测圆孔直径，四帧全 OK，属于"绿色演示"。
        /// 注：不能只把 spec 设 Enabled=false —— 引擎对【任何 FitLine 失败】是直接 NG（AnyFitFailed），
        ///     与 spec 无关；必须把 FitLine 节点本身停用（Node.Enable=false），FindNodes 才会跳过它。
        /// </summary>
        private const bool EnableLineMeasure = true;

        private static readonly List<MeasureDemoSpec> Specs = new List<MeasureDemoSpec>
        {
            new MeasureDemoSpec
            {
                DemoKey = "lk件_模板匹配几何变换测量",
                RecipeCode = "RCP-AM-002",
                TemplateTitle = "lk 工件外形测量（模板匹配·几何变换跟随·圆孔与边距）·Measurement Demo",
                Summary = "形状模板匹配定位工件位姿 → 刚性变换把圆孔与两条边随工件跟随 → 环形/直线卡尺亚像素精测；" +
                          "输出「圆孔直径 / 圆心到两侧边距离 / 两线间距」，按 MeasurementSpecs 逐项判 OK/NG（纯软件独立运行）。" +
                          "⚠ 已知限制：lk_03（匹配分 0.640，位姿偏 ~10px）与 lk_04（measure_pos 在该处静默取不到边）" +
                          "两帧的直线要素出空值或错值（lk_03 量错、lk_04 丢边）⇒ 这两帧会判 NG；圆孔直径四帧皆稳。详见 ModelNote",
                ApplicableMachines = "课堂图/演示态；真机产线应通过标定件实测 PixelPerMm 后填入",
                ModelNote = "2026-09-25 换成 HALCON 课堂案例「5模板匹配+几何变换+测量」（素材 lk_01~04.bmp）：" +
                            "链路 = ReadImageFile → ShapeMatch → CreateFixture → ApplyFixture×3 → FitCircle + FitLine×2，" +
                            "与 hdev 的 find_shape_model → vector_angle_to_rigid → affine_trans_point_2d → metrology 一一对应；" +
                            "PixelPerMm=0.1 为演示假设当量（真机需标定件实测）；测量要素种子取自 hdev 静态参数，工件刚性⇒各尺寸不随摆放变化。" +
                            "【实测标定值（探针 .workbuddy/measure_chain_probe，四姿态复算）】圆孔直径 7.130mm（极差 0.0592）；" +
                            "圆心到边2距离 20.172/20.289（仅 lk_01/lk_02）；边1到边2间距 27.630/27.803（仅 lk_01/lk_02）。" +
                            "【已知限制·两个不同根因】① lk_03：匹配分仅 0.640（其余 0.96~0.999），灰度剖面证明真边不在种子上" +
                            "而是偏出约 10px，故线要素量错（★该窗口内只有一处边，卡尺找到的是真边，错在种子位姿，不在卡尺）；" +
                            "且偏量不成比例（圆要素偏 1.19px / 线要素偏 ~10px，距离比仅 4.86）⇒ 既非纯平移亦非简单绕基准旋转；" +
                            "唯一可归因的上游信号是 0.640 的匹配分——属该帧图像/模板问题，非卡尺参数；" +
                            "② lk_04：剖面上边就在种子上（t=0→108,t=+4→0），但 measure_pos 任何口径（L1=20/40、L2=1/5、" +
                            "sigma 0.6/1.0、thr 1/5/10/30、正负过渡）都取不到，最好只有 thr=1 时 n=7 且 amp≈1 —— 与 " +
                            "gen_measure_arc 恒 0 同族的静默故障，参数无解。故本 Demo 保留完整多量设计（教学：多量并列+派生量），" +
                            "但请知悉这两帧的 NG 是上述原因、不是公差超差。" +
                            "【改成绿色演示】把本工厂 EnableLineMeasure 置 false（两个 FitLine 节点与其两项 spec 一并停用），" +
                            "圆孔直径四帧皆 OK；改动后本文件改 DemoSpecVersion 即会自动重落配方/任务模板。" +
                            "【不要把 FitLine 节点删掉】FitLine 一旦失败，引擎 AnyFitFailed 直接 NG（响亮失败），与 spec 是否 Enabled 无关。",

                BaseImageFileName = "lk_01.bmp",
                TemplateName = "lk工件外形(课堂案例)",
                // hdev: gen_rectangle1(ROI_0, 187.929, 140.83, 278.194, 322.076) —— 学"带圆孔的工件段"
                TemplateRoiRow1 = 187.929, TemplateRoiCol1 = 140.83,
                TemplateRoiRow2 = 278.194, TemplateRoiCol2 = 322.076,
                // hdev: gen_rectangle1(ROI_0, 38.9859, 18.6137, 444.95, 600.656) —— 在这里找
                SearchRoiRow1 = 38.9859, SearchRoiCol1 = 18.6137,
                SearchRoiRow2 = 444.95, SearchRoiCol2 = 600.656,
                MinScore = 0.5,     // hdev: find_shape_model(..., 0.5, 1, 0.5, ...)

                // 默认基准点 = 学习框（ROI）中心；基准帧即 lk_01，模板朝向即基准朝向 ⇒ 0°
                BaselineRow = (187.929 + 278.194) / 2.0,
                BaselineCol = (140.83 + 322.076) / 2.0,
                BaselineAngle = 0.0,

                // hdev: circleParam = [244.061, 239.304, 37.4471]
                // ⚠ 37.4471 是 hdev 的【初始近似半径】（metrology 种子的期望半径），不是工件真值：
                //   离线探针实测拟合半径 ≈35.70px（直径 71.4px×0.1=7.14mm）。这里保留 hdev 值不动 ——
                //   环带 = 37.4471±8 ⇒ 覆盖 29.45~45.45，含 35.70，四个姿态实测都拟合成功。
                CircleRow = 244.061, CircleCol = 239.304, CircleRadius = 37.4471,
                // hdev: line1Param = [284.41,166.572 ~ 173.162,165.465]（左竖直边）⇒ 中点 + 90°
                Line1MidRow = (284.41 + 173.162) / 2.0, Line1MidCol = (166.572 + 165.465) / 2.0, Line1PhiDeg = 90.0,
                // hdev: line2Param = [322.155,441.709 ~ 214.88,442.263]（右竖直边）⇒ 中点 + 90°
                Line2MidRow = (322.155 + 214.88) / 2.0, Line2MidCol = (441.709 + 442.263) / 2.0, Line2PhiDeg = 90.0,
                // hdev: set_metrology_model_param('scale', 0.02) + 卡尺 sigma=1 / threshold=30 / 环带 5px
                AnnulusHalf = 8.0,
                // 直线卡尺：对齐 hdev add_metrology_object_generic(..., 20, 5, 1, 30)
                //   跨边半长 20 / 沿边平均半宽 5 / sigma 1 / threshold 30。
                //   ★ 口径已用合成短竖边独立验证：Length1 就是【剖面扫描半长】
                //     （L1=20/L2=5 能找到偏 10px 的边；L1=5/L2=20 漏；L1=1/L2=20 抛 #3022）。
                HalfSpanAlongEdge = 45.0, ScanHalf = 20.0,
                ProbeAvgHalf = 5.0, NumPoints = 41,
                Sigma = 1.0, Threshold = 30.0,

                PixelPerMm = 0.1,
                Specs = new List<MeasurementSpecItem>
                {
                    new MeasurementSpecItem
                    {
                        Name = "圆孔直径",
                        ToolKind = "Diameter",
                        RegionRef = "圆孔（圆心随工件位姿跟随）",
                        // ★★ 必须是【实测量】不是手算：hdev 的 R=37.4471 只是初始近似，
                        //    真值是拟合出来的 35.70px ⇒ 直径 71.4px×0.1 = 7.14mm。
                        //    离线探针四姿态实测：mean=7.130 min=7.094 max=7.153 极差=0.0592mm。
                        //    （旧值 7.49 取自 2×37.4471×0.1，是近似半径换算 ⇒ 四张图全判 NG。）
                        NominalMm = 7.13,
                        ToleranceMm = 0.30,     // ≥2×实测极差(0.059)，留亚像素+卡尺噪声余量
                        Enabled = true,
                        Note = "实测量回填：四姿态拟合直径 mean=7.130mm 极差=0.0592mm（探针 measure_chain_probe）；" +
                               "真机应按产品规格书填 NominalMm/ToleranceMm"
                    },
                    new MeasurementSpecItem
                    {
                        // ★ Name 必须逐字等于引擎产出的量名（StandaloneVisionProcess.BuildMeasurementOutcome）：
                        //   "圆孔直径" / "线{j}长度" / "圆心到边{j}距离" / "边{j}到边{j+1}间距"。
                        //   写成别的名字会掉进 ToolKind 兜底：PointToLine→"距离"只取【第一个名字含"距离"的量】
                        //   = "圆心到边1距离"（≈7.4mm），而本项标称 20.23mm 属于【边2】——会拿错量判公差。
                        Name = "圆心到边2距离",
                        ToolKind = "PointToLine",
                        RegionRef = "圆孔圆心 ↔ 线2（右竖直边）＝ hdev distance_pl 的派生量",
                        // 探针实测：lk_01=20.172、lk_02=20.289 ⇒ 取中 20.23
                        NominalMm = 20.23,
                        ToleranceMm = 0.30,
                        Enabled = EnableLineMeasure,
                        Note = "测量结果的进一步应用：把「圆」与「线」两个拟合结果组合成一个功能尺寸。" +
                               "⚠ 依赖线2拟合成功：本案例 lk_03/lk_04 两帧线卡尺失效（原因见 Remark），该两帧会判 NG"
                    },
                    new MeasurementSpecItem
                    {
                        // 引擎产出的量名是 "边{j+1}到边{j+2}间距"（两条线时 = "边1到边2间距"）。
                        // 旧名"两侧边间距"只能靠 ToolKind=LineSpacing 兜底命中，属于脆弱写法，改回逐字量名。
                        Name = "边1到边2间距",
                        ToolKind = "LineSpacing",
                        RegionRef = "线1 ↔ 线2（两竖直边中点距）",
                        // 探针实测：lk_01=27.630、lk_02=27.803 ⇒ 取中 27.72
                        NominalMm = 27.72,
                        ToleranceMm = 0.30,
                        Enabled = EnableLineMeasure,
                        Note = "刚性变换下间距恒定 ⇒ 可作「摆放无关」的稳定性判据。" +
                               "⚠ 同「圆心到边2距离」：lk_03/lk_04 两帧无值 ⇒ 会判 NG"
                    }
                }
            }
        };

        /// <summary>落地全部内嵌测量演示包（幂等）。返回人类可读结果摘要。</summary>
        public static string EnsureMeasurementDemoTasks()
        {
            var lines = new List<string>();

            // 0) 先清理上一版（六角螺母）落的资产 —— 用户要的是「换为」，不是并存
            PurgeLegacyDemo(lines);

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
        // 0) 旧版演示包清理（幂等：找不到就跳过）
        // ======================================================================

        /// <summary>
        /// 清理 2026-09-10 版「六角螺母外圆直径」演示资产（任务模板 / 配方 / 演示图像目录）。
        /// 只删能被**确认为本工厂旧版产物**的对象：模板必须同时命中 AM 族 + 旧配方码或旧标题关键字。
        /// </summary>
        private static void PurgeLegacyDemo(List<string> lines)
        {
            try
            {
                var lib = new TaskTemplateLibraryService();
                foreach (var t in lib.LoadAll().ToList())
                {
                    if (t.Kind != TaskKind.AppearanceMeasurement) continue;
                    bool hit = string.Equals(t.BoundRecipeId, LegacyRecipeCode, StringComparison.OrdinalIgnoreCase)
                               || (t.DisplayName ?? string.Empty).Contains(LegacyTemplateKeyword);
                    if (!hit) continue;
                    if (lib.Delete(t.TemplateCode))
                        lines.Add($"✓ 已清理旧演示任务模板 {t.TemplateCode}（{t.DisplayName}）");
                }
            }
            catch (Exception ex) { lines.Add("⚠ 清理旧任务模板失败：" + ex.Message); }

            try
            {
                if (new RecipeStorageService().DeleteRecipe(LegacyRecipeCode))
                    lines.Add($"✓ 已清理旧演示配方 {LegacyRecipeCode}（六角螺母测量链）");
            }
            catch (Exception ex) { lines.Add("⚠ 清理旧配方失败：" + ex.Message); }

            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", LegacyDemoKey);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    lines.Add($"✓ 已清理旧演示图像目录 Config\\DemoData\\{LegacyDemoKey}\\");
                }
            }
            catch (Exception ex) { lines.Add("⚠ 清理旧演示图像目录失败：" + ex.Message); }
        }

        // ======================================================================
        // 0b) 内容版本守卫
        // ======================================================================

        /// <summary>
        /// 把「绑到本 RecipeCode、但 Remark 里没有当前 <see cref="DemoSpecVersion"/> 标记」的任务模板与配方
        /// 清掉，让本次以新参数重新落地。
        ///
        /// 为什么必须有：工厂对配方与任务模板都是"存在即跳过"，只改代码里的标称值/卡尺参数而不 bump
        /// DemoSpecVersion，则本机已落地的旧对象会一直沿用旧值——现场表现是"改了代码，跑起来没变"，
        /// 且完全不报错（静默失效）。历史遗留（Remark 里没有版本标记）一律视为过期。
        /// </summary>
        private static void PurgeOutdatedDemo(MeasureDemoSpec spec, List<string> step)
        {
            string marker = "spec=" + DemoSpecVersion;
            try
            {
                var library = new TaskTemplateLibraryService();
                var bound = library.LoadAll()
                    .Where(t => t.Kind == TaskKind.AppearanceMeasurement
                                && string.Equals(t.BoundRecipeId, spec.RecipeCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (bound.Count == 0) return;                                  // 还没落过，无需守卫
                if (bound.All(t => (t.Remark ?? string.Empty).Contains(marker))) return;   // 已是当前版本

                foreach (var t in bound)
                    if (library.Delete(t.TemplateCode))
                        step.Add($"↻ 演示包版本 {DemoSpecVersion} 更新：已清理旧任务模板 {t.TemplateCode}");

                if (new RecipeStorageService().DeleteRecipe(spec.RecipeCode))
                    step.Add($"↻ 已同步清理旧配方 {spec.RecipeCode}，将按新参数重落");
                else
                    step.Add($"⚠ 旧配方 {spec.RecipeCode} 删除失败：请手工删除它，否则本次仍会沿用旧标称值");
            }
            catch (Exception ex)
            {
                step.Add("⚠ 演示包版本守卫执行失败（可能仍沿用旧参数）：" + ex.Message);
            }
        }

        // ======================================================================
        // 单示例落地流水线（内嵌资产 → 形状模板 → 图源 → 配方 → 模板）
        // ======================================================================

        private static string LandOne(MeasureDemoSpec spec, ref int createdRecipes, ref int createdTemplates)
        {
            var step = new List<string>();
            var embedDir = EmbeddedRoot;
            if (!Directory.Exists(embedDir))
                return $"⚠ [{spec.DemoKey}] 内嵌资产目录不存在，跳过：{embedDir}（应随构建输出到 bin\\Assets\\MeasureDemo）";

            var baseImage = Path.Combine(embedDir, spec.BaseImageFileName);
            if (!File.Exists(baseImage))
                return $"⚠ [{spec.DemoKey}] 内嵌基准图缺失 {spec.BaseImageFileName}，跳过。";

            // ---------- 1) 形状模板落地（模板匹配的识别器；已存在则复用） ----------
            step.Add(EnsureShapeTemplate(spec, baseImage));

            // ---------- 2) 演示图像落地（DemoData\{DemoKey}\Images\） ----------
            var imgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "DemoData", spec.DemoKey, "Images");
            var copied = EnsureDemoImages(embedDir, imgDir);

            // ---------- 2b) 内容版本守卫（★ 缺了它，"改参数不生效"是静默的）----------
            PurgeOutdatedDemo(spec, step);

            // ---------- 3) 配方落地（ReadImageFile → ShapeMatch → CreateFixture → ApplyFixture×3 → 测量） ----------
            var storage = new RecipeStorageService();
            bool recipeExists = storage.GetAllRecipes().Any(r =>
                string.Equals(r.RecipeCode, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (!recipeExists && copied > 0)
            {
                storage.SaveRecipe(BuildRecipe(spec, imgDir));
                createdRecipes++;
                step.Add($"✓ 配方 {spec.RecipeCode} 已落地（9 节点链：读图→匹配→几何变换→跟随×3→圆+两线，含 {copied} 张演示图）");
            }
            else if (recipeExists)
            {
                step.Add($"· 配方 {spec.RecipeCode} 已存在，跳过");
            }
            else
            {
                step.Add($"⚠ [{spec.DemoKey}] 演示图像复制为 0 张，未生成配方（请检查内嵌素材 {DemoImagePattern}）");
            }

            // ---------- 4) 任务模板生成 ----------
            var library = new TaskTemplateLibraryService();
            var tpl = library.LoadAll().FirstOrDefault(t => t.Kind == TaskKind.AppearanceMeasurement
                && string.Equals(t.BoundRecipeId, spec.RecipeCode, StringComparison.OrdinalIgnoreCase));
            if (tpl == null)
            {
                var first = spec.Specs.FirstOrDefault(s => s.Enabled) ?? spec.Specs.FirstOrDefault();
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
                    ShapeTemplateName = spec.TemplateName,
                    PixelPerMm = spec.PixelPerMm,
                    MeasurementSpecs = spec.Specs,
                    OutputContractText = "测量摘要（多量并列）：" +
                        "「圆孔 直径=x.xx mm 圆心=(R,C) R=xx.xxpx | 线1/线2 长度=… | 圆心到线1/线2=x.xx mm | 线间距=x.xx mm」；" +
                        "按 MeasurementSpecs 逐项 Enabled 项在 NominalMm±ToleranceMm 内判 OK/NG" +
                        (first != null ? $"（首项 {first.Name}）" : string.Empty) +
                        "。⚠ 直线卡尺失效的帧（本案例 lk_03/lk_04）拿不到「线*长度 / 圆心到边*距离 / 边间距」，" +
                        "且引擎对任何 FitLine 失败一律直接 NG（AnyFitFailed），不进公差判定",
                    VerdictToIo = false,
                    VerdictRule = null,   // 测量族走 MeasurementSpecs 判据，不复用 DL 关键字判据
                    // ★ 版本标记：PurgeOutdatedDemo 靠它判断"本机落地的是不是当前版本"。
                    //   改判据/参数后必须 bump DemoSpecVersion，否则本机旧对象会被"存在即跳过"永久沿用。
                    Remark = spec.ModelNote + " [spec=" + DemoSpecVersion + "]"
                };
                library.Save(tpl);
                createdTemplates++;
                step.Add($"✓ 任务模板 {tpl.TemplateCode} 已生成（MeasurementSpecs×{spec.Specs.Count} + PixelPerMm={spec.PixelPerMm} 已内联）");
            }
            else
            {
                step.Add($"· 任务模板 {tpl.TemplateCode} 已存在，跳过");
            }

            return "[" + spec.DemoKey + "] " + string.Join("；", step);
        }

        /// <summary>
        /// 落地/复用形状模板：把课堂素材当基准帧，按 hdev 的模板 ROI 学习（角度 -180~180 全角）。
        /// 已存在同名模板时直接复用（幂等）。
        /// </summary>
        private static string EnsureShapeTemplate(MeasureDemoSpec spec, string baseImagePath)
        {
            try
            {
                var mgr = new TemplateManager();
                if (mgr.GetByName(spec.TemplateName).Success)
                    return $"· 形状模板 [{spec.TemplateName}] 已存在，复用";

                HOperatorSet.ReadImage(out HObject img, baseImagePath);
                try
                {
                    var res = mgr.CreateShapeTemplate(spec.TemplateName, img,
                        spec.TemplateRoiRow1, spec.TemplateRoiCol1, spec.TemplateRoiRow2, spec.TemplateRoiCol2,
                        -180.0, 180.0,
                        "课堂案例「模板匹配+几何变换+测量」：lk 工件外形（带圆孔）", baseImagePath);
                    if (!res.Success)
                        return $"⚠ 形状模板 [{spec.TemplateName}] 创建失败：{res.Message}（配方仍会落地，但运行时 ShapeMatch 匹配不到）";

                    double q = res.Data?.QualityScore ?? -1;
                    return $"✓ 形状模板 [{spec.TemplateName}] 已创建（ROI {spec.TemplateRoiRow1:F0},{spec.TemplateRoiCol1:F0}-" +
                           $"{spec.TemplateRoiRow2:F0},{spec.TemplateRoiCol2:F0}，角度 -180~180°，自测分 {q:F3}）";
                }
                finally { img?.Dispose(); }
            }
            catch (Exception ex)
            {
                return $"⚠ 形状模板 [{spec.TemplateName}] 创建异常：{ex.Message}";
            }
        }

        /// <summary>把内嵌素材里的演示图（lk_*.bmp）拷到 DemoData 图像目录（幂等：已有则跳过）。</summary>
        private static int EnsureDemoImages(string embedDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            int existing = CountImages(destDir);
            if (existing >= 1) return existing; // 至少已有 1 张，跳过

            foreach (var src in Directory.GetFiles(embedDir, DemoImagePattern))
            {
                try { File.Copy(src, Path.Combine(destDir, Path.GetFileName(src)), true); }
                catch { /* 单张失败不阻断其余 */ }
            }
            return CountImages(destDir);
        }

        private static int CountImages(string dir)
            => Directory.GetFiles(dir, "*.png").Length
               + Directory.GetFiles(dir, "*.bmp").Length
               + Directory.GetFiles(dir, "*.jpg").Length;

        // ======================================================================
        // 配方构建（9 节点链：读图 → 匹配 → 变换 → 跟随×3 → 圆 + 两线）
        // ======================================================================

        private static RecipeModel BuildRecipe(MeasureDemoSpec spec, string imgDir)
        {
            string readId = Guid.NewGuid().ToString("N");
            string matchId = Guid.NewGuid().ToString("N");
            string fixId = Guid.NewGuid().ToString("N");
            string afCircleId = Guid.NewGuid().ToString("N");
            string afLine1Id = Guid.NewGuid().ToString("N");
            string afLine2Id = Guid.NewGuid().ToString("N");
            string circleId = Guid.NewGuid().ToString("N");
            string line1Id = Guid.NewGuid().ToString("N");
            string line2Id = Guid.NewGuid().ToString("N");

            // ---- 1) 图像读取：本地文件夹批处理（每次链执行自动推进索引，LoopFolder 循环）----
            var readParam = new ReadImageFileParam
            {
                IsBatchFolder = true,
                FolderPath = imgDir,
                LoopFolder = true,
                FilePath = Path.Combine(imgDir, spec.BaseImageFileName),
                SelectedFilePath = null,
                CurrentImageIndex = 0
            };
            readParam.FileItems.Clear();

            // ---- 2) 形状模板匹配：定位工件位姿（≡ find_shape_model）----
            var matchParam = new ShapeMatchParam
            {
                TemplateName = spec.TemplateName,
                MinScore = spec.MinScore,
                AngleStart = -180.0,
                AngleEnd = 180.0,
                SearchRoiEnabled = true,
                SearchRow1 = spec.SearchRoiRow1,
                SearchCol1 = spec.SearchRoiCol1,
                SearchRow2 = spec.SearchRoiRow2,
                SearchCol2 = spec.SearchRoiCol2
            };

            // ---- 3) 几何变换：基准位姿 → 当前位姿 的刚性矩阵（≡ vector_angle_to_rigid）----
            var fixParam = new CreateFixtureParam
            {
                BaselineRow = spec.BaselineRow,
                BaselineCol = spec.BaselineCol,
                BaselineAngle = spec.BaselineAngle
            };

            // ---- 4) 要素跟随 ×3（≡ affine_trans_point_2d）----
            // 线要素还要跟随朝向：BaseAngle=90°（竖直边）+ Δθ ⇒ 接 FitLine.SeedPhi
            var afCircle = new ApplyFixtureParam
            {
                TargetType = FollowType.Point,
                BaseRow = spec.CircleRow,
                BaseCol = spec.CircleCol
            };
            var afLine1 = new ApplyFixtureParam
            {
                TargetType = FollowType.Point,
                BaseRow = spec.Line1MidRow,
                BaseCol = spec.Line1MidCol,
                BaseAngle = spec.Line1PhiDeg
            };
            var afLine2 = new ApplyFixtureParam
            {
                TargetType = FollowType.Point,
                BaseRow = spec.Line2MidRow,
                BaseCol = spec.Line2MidCol,
                BaseAngle = spec.Line2PhiDeg
            };

            // ---- 5) 测量：环形卡尺拟合圆 + 直线卡尺拟合两侧边（≡ metrology 圆/线）----
            var circleParam = new FitCircleParam
            {
                SeedRow = spec.CircleRow,
                SeedCol = spec.CircleCol,
                Radius = spec.CircleRadius,
                AnnulusHalf = spec.AnnulusHalf,
                ArcStartDeg = 0.0,
                ArcExtentDeg = 360.0,
                Sigma = spec.Sigma,
                Threshold = spec.Threshold,
                Transition = CaliperTransition.All,
                Select = CaliperEdgeSelect.All,
                MinEdgePoints = 8
            };
            var line1Param = new FitLineParam
            {
                SeedMidRow = spec.Line1MidRow,
                SeedMidCol = spec.Line1MidCol,
                EdgePhiDeg = spec.Line1PhiDeg,
                HalfSpanAlongEdge = spec.HalfSpanAlongEdge,
                ScanHalf = spec.ScanHalf,
                ProbeAvgHalf = spec.ProbeAvgHalf,
                NumPoints = spec.NumPoints,
                Sigma = spec.Sigma,
                Threshold = spec.Threshold,
                Transition = CaliperTransition.All,
                Select = CaliperEdgeSelect.All,
                MinEdgePoints = 3
            };
            var line2Param = new FitLineParam
            {
                SeedMidRow = spec.Line2MidRow,
                SeedMidCol = spec.Line2MidCol,
                EdgePhiDeg = spec.Line2PhiDeg,
                HalfSpanAlongEdge = spec.HalfSpanAlongEdge,
                ScanHalf = spec.ScanHalf,
                ProbeAvgHalf = spec.ProbeAvgHalf,
                NumPoints = spec.NumPoints,
                Sigma = spec.Sigma,
                Threshold = spec.Threshold,
                Transition = CaliperTransition.All,
                Select = CaliperEdgeSelect.All,
                MinEdgePoints = 3
            };

            // 节点顺序即执行顺序：读图 → 匹配 → 变换 → 跟随 → 测量（与 hdev 逐行对应）
            var nodes = new List<RecipeNodeDto>
            {
                new RecipeNodeDto { NodeId = readId, Type = NodeType.ReadImageFile, DisplayName = "图像读取(本地文件夹批处理)", Enable = true, PosX = 60, PosY = 320, ParameterModel = readParam },
                new RecipeNodeDto { NodeId = matchId, Type = NodeType.ShapeMatch, DisplayName = "形状匹配(定位工件位姿)", Enable = true, PosX = 280, PosY = 320, ParameterModel = matchParam },
                new RecipeNodeDto { NodeId = fixId, Type = NodeType.CreateFixture, DisplayName = "几何变换(基准→当前 刚性矩阵)", Enable = true, PosX = 500, PosY = 200, ParameterModel = fixParam },
                new RecipeNodeDto { NodeId = afCircleId, Type = NodeType.ApplyFixture, DisplayName = "要素跟随-圆孔中心", Enable = true, PosX = 720, PosY = 60, ParameterModel = afCircle },
                new RecipeNodeDto { NodeId = afLine1Id, Type = NodeType.ApplyFixture, DisplayName = "要素跟随-线1(左竖直边)", Enable = true, PosX = 720, PosY = 260, ParameterModel = afLine1 },
                new RecipeNodeDto { NodeId = afLine2Id, Type = NodeType.ApplyFixture, DisplayName = "要素跟随-线2(右竖直边)", Enable = true, PosX = 720, PosY = 460, ParameterModel = afLine2 },
                new RecipeNodeDto { NodeId = circleId, Type = NodeType.FitCircle, DisplayName = "FitCircle 圆孔(环形卡尺)", Enable = true, PosX = 960, PosY = 60, ParameterModel = circleParam },
                // ★ 两条线的开关走 EnableLineMeasure：只把 spec 设 Enabled=false 没用 ——
                //   引擎对任何 FitLine 失败是直接 NG（AnyFitFailed），与 spec 无关；
                //   必须停用节点本身，FindNodes（只收 Enable=true）才会跳过它。
                new RecipeNodeDto { NodeId = line1Id, Type = NodeType.FitLine, DisplayName = "FitLine 线1(左竖直边)", Enable = EnableLineMeasure, PosX = 960, PosY = 260, ParameterModel = line1Param },
                new RecipeNodeDto { NodeId = line2Id, Type = NodeType.FitLine, DisplayName = "FitLine 线2(右竖直边)", Enable = EnableLineMeasure, PosX = 960, PosY = 460, ParameterModel = line2Param }
            };

            var conns = new List<RecipeConnectionDto>();
            Action<string, string, string, string> link = (srcId, srcPort, dstId, dstPort) =>
                conns.Add(new RecipeConnectionDto
                {
                    ConnectionId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = srcId, SourcePortId = srcId + "_" + srcPort, SourcePortName = srcPort,
                    TargetNodeId = dstId, TargetPortId = dstId + "_" + dstPort, TargetPortName = dstPort
                });

            // 图像：读图 → 匹配（MatchImage 借用输入帧同实例，继续向下游供图，保证执行顺序）
            link(readId, "Image", matchId, "InputImage");
            link(matchId, "MatchImage", circleId, "InputImage");
            link(matchId, "MatchImage", line1Id, "InputImage");
            link(matchId, "MatchImage", line2Id, "InputImage");

            // 位姿：匹配基准点/角度 → 几何变换
            link(matchId, "MatchRow", fixId, "CurrentRow");
            link(matchId, "MatchCol", fixId, "CurrentCol");
            link(matchId, "MatchAngle", fixId, "CurrentAngle");

            // 变换矩阵 + Δθ：几何变换 → 三个跟随节点
            foreach (var afId in new[] { afCircleId, afLine1Id, afLine2Id })
            {
                link(fixId, "HomMat2D", afId, "HomMat2D");
                link(fixId, "DeltaAngle", afId, "DeltaAngle");
            }

            // 跟随结果 → 测量种子（位置 + 朝向）
            link(afCircleId, "OutputRow", circleId, "SeedRow");
            link(afCircleId, "OutputCol", circleId, "SeedCol");
            link(afLine1Id, "OutputRow", line1Id, "SeedMidRow");
            link(afLine1Id, "OutputCol", line1Id, "SeedMidCol");
            link(afLine1Id, "OutputAngle", line1Id, "SeedPhi");
            link(afLine2Id, "OutputRow", line2Id, "SeedMidRow");
            link(afLine2Id, "OutputCol", line2Id, "SeedMidCol");
            link(afLine2Id, "OutputAngle", line2Id, "SeedPhi");

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
                Description = "外观测量内嵌 Demo 自动生成（Assets\\MeasureDemo，课堂案例 5模板匹配+几何变换+测量）：" +
                              "ReadImageFile[本地文件夹批处理] → ShapeMatch[形状模板] → CreateFixture[基准→当前刚性矩阵] → " +
                              "ApplyFixture×3[圆孔中心 / 两侧边中点 位置+朝向 跟随] → FitCircle + FitLine×2 → " +
                              "引擎按 MeasurementSpecs 出多量并按标称±公差判 OK/NG",
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
