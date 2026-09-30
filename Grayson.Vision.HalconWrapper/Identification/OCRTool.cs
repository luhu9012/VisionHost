//===================================================================================
// 文件名: OCRTool.cs
// 说 明: 字符识别（OCR）工具 —— 真实 HALCON 实现（分割 + MLP 分类器路线）。
//
// ⚠ 2026-09-28：由 mock 改为真实实现。原实现恒定返回 Success=true + TextResult="A1234"
//   （连 TODO 都没有），属最危险的静默 mock（链路"跑通给结果"，门禁一处不红）。
//
// ★ 路线定案（回答 TaskTypeIndustryCatalog 里那条"另需先定案路线"的 RoadmapHint）：
//   选【分割 + read_ocr_class_mlp 分类器】路线，而不是 text_finder 一体路线。
//   理由：
//     ① HTH 课堂 4 个 OCR 案例（6OCR num / 6OCR 训练识别 / 6OCR环形识别）全部走这条：
//        threshold → connection → select_shape →（可选 dilation_circle 填笔画孔洞）
//        → read_ocr_class_mlp → do_ocr_multi_class_mlp；
//     ② 本仓已有算子完全覆盖，零新增原生依赖；
//     ③ 工业读码场景（批号/日期/刻印）字符排布规律、ROI 明确，分割路线精度与可控性更好；
//     ④ 训练扩展性：课程"训练识别"案例可用 append_ocr_trainf + trainf_ocr_class_mlp
//        自训中文/专用字体，一体路线做同样的事要换一整套 API。
//
// 真实链路（与 6OCR num.hdev 一一对应）：
//   rgb1_to_gray（若彩色）
//     → threshold(image, 0, ThresholdMax) 二值化（默认白底黑字：取暗字符）
//     → connection 连通分割
//     → select_shape 'area' [AreaMin, AreaMax] 滤掉噪点/大块
//     →（可选）dilation_circle(MinStrokeWidth) 膨胀填笔画孔洞，提高分类器命中率
//     →（可选）sort_region 'character' 按 行/列 排序（保证拼出的字符串顺序正确）
//     → read_ocr_class_mlp(FontFileName) → do_ocr_multi_class_mlp(regions, image, handle, Class, Confidence)
//
// ★ 置信度门（课程反复强调的那条）：do_ocr_multi_class_mlp **不接受**未训练字符的拒绝——
//   遇到没训过的字，它会把结果判成"最后一个训练字符"，且**照样给高置信度**。
//   所以"识别成功"不等于"结果对"：必须显式传 MinConfidence 做门限，并把置信度回给调用方。
//
// ★ 分类器路径解析（三级）：
//   ① 绝对路径且文件存在 → 直接用；
//   ② 纯文件名（如 Industrial_0-9A-Z_NoRej.omc）→ 依次在
//      Config\Ocr、Assets\Ocr、HALCON 安装 ocr 目录（%HALCONROOT%\ocr）下找；
//   ③ 都找不到 → 返回失败并写明"去哪找/怎么配"（不静默退化成假成功）。
//===================================================================================
using System;
using System.IO;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Identification
{
    /// <summary>OCR 结果：TextResult=拼接后的字符串，CharRegions=字符区域，Confidence=最低单字置信度。</summary>
    public class OcrReadResult
    {
        public bool Success { get; set; }
        public string TextResult { get; set; } = string.Empty;
        public object CharRegions { get; set; }
        public string Message { get; set; } = string.Empty;
        /// <summary>识别到的字符个数</summary>
        public int Count { get; set; }
        /// <summary>最低单字置信度（0~1）。★ 判据应主要看它，不是看"识别成功与否"</summary>
        public double MinConfidence { get; set; }
        /// <summary>平均置信度（0~1）</summary>
        public double MeanConfidence { get; set; }
        /// <summary>实际使用的分类器全路径（便于现场核对到底加载了哪个 .omc）</summary>
        public string ResolvedFontPath { get; set; } = string.Empty;
    }

    public static class OCRTool
    {
        /// <summary>
        /// 字符识别（分割 + MLP 分类器）。
        /// </summary>
        /// <param name="image">输入图像（HObject，运行时必须是 HALCON 图像对象）</param>
        /// <param name="region">搜索区域（HObject 区域；null=全图）</param>
        /// <param name="fontFileName">分类器：绝对路径 或 .omc 文件名（会在 Config\Ocr / Assets\Ocr / HALCON ocr 目录下解析）</param>
        /// <param name="minStrokeWidth">膨胀半径（像素）：填笔画孔洞提高识别率；&lt;=0 表示不膨胀</param>
        /// <param name="expressionFilter">结果字符过滤（正则/白名单，如 "0-9" 或 "*" 不过滤）</param>
        /// <param name="thresholdMax">二值化上限（默认 71，与课堂案例一致：白底黑字取暗字符）</param>
        /// <param name="areaMin">字符面积下限（滤噪点）</param>
        /// <param name="areaMax">字符面积上限（滤大块背景）</param>
        /// <param name="polarity">前景极性：'dark'（暗字符亮底，默认）/ 'light'（亮字符暗底，需先 invert）</param>
        /// <param name="sortByRow">true=先按行再按列排序（多行文本）；false=只按列（单行，默认）</param>
        /// <param name="minConfidence">置信度门限：低于它判失败（0=不设门）</param>
        public static OcrReadResult RecognizeText(object image, object region, string fontFileName,
            double minStrokeWidth, string expressionFilter,
            double thresholdMax = 71, double areaMin = 30, double areaMax = 70000,
            string polarity = "dark", bool sortByRow = false, double minConfidence = 0)
        {
            var hImg = image as HObject;
            if (hImg == null || !hImg.IsInitialized())
                return Fail("输入图像为空或未初始化");

            var roi = region as HObject;
            bool hasRoi = roi != null && roi.IsInitialized();

            HObject gray = null, work = null, reduced = null, binary = null, target = null;
            HObject connected = null, selected = null, dilated = null, sorted = null;
            HTuple ocrHandle = null;
            bool ownGray = false, ownWork = false;
            try
            {
                // ---- 0) 转灰度 ----
                HOperatorSet.CountChannels(hImg, out HTuple channels);
                if (channels.Length > 0 && channels[0].I > 1)
                {
                    HOperatorSet.Rgb1ToGray(hImg, out gray);
                    ownGray = true;
                }
                else
                {
                    gray = hImg;
                }

                // ---- 1) ROI 裁剪 ----
                if (hasRoi)
                {
                    HOperatorSet.ReduceDomain(gray, roi, out work);
                    ownWork = true;
                }
                else
                {
                    work = gray;
                }

                // ---- 2) 极性处理：亮字符暗底需反相（分类器按"白底黑字"训练）----
                target = work;
                if (string.Equals(polarity, "light", StringComparison.OrdinalIgnoreCase))
                {
                    HOperatorSet.InvertImage(work, out reduced);
                    target = reduced;
                }

                // ---- 3) 二值化 + 连通分割 + 面积筛选 ----
                HOperatorSet.Threshold(target, out binary, 0, thresholdMax);
                HOperatorSet.Connection(binary, out connected);
                HOperatorSet.SelectShape(connected, out selected, "area", "and", areaMin, areaMax);

                HOperatorSet.CountObj(selected, out HTuple selCount);
                if (selCount.Length == 0 || selCount[0].I == 0)
                    return Fail($"未分割到字符区域（area∈[{areaMin:0},{areaMax:0}] 无连通域）。" +
                                "请调 二值化阈值 / 面积上下限，或检查 极性 polarity 是否反了");

                // ---- 4) 膨胀填笔画孔洞（课程：字体中间有孔洞，膨胀填满提高识别度）----
                HObject charRegions;
                if (minStrokeWidth > 0)
                {
                    HOperatorSet.DilationCircle(selected, out dilated, minStrokeWidth);
                    charRegions = dilated;
                }
                else
                {
                    charRegions = selected;
                }

                // ---- 5) 排序：保证拼出的字符串顺序正确（单行按列；多行先行后列）----
                if (sortByRow)
                    HOperatorSet.SortRegion(charRegions, out sorted, "character", "true", "row");
                else
                    HOperatorSet.SortRegion(charRegions, out sorted, "character", "true", "column");

                // ---- 6) 解析分类器路径 + 读取分类器 ----
                string fontPath = ResolveClassifierPath(fontFileName);
                if (fontPath == null)
                {
                    return Fail($"OCR 分类器未找到: '{fontFileName}'。" +
                                "请把 .omc 放到 <程序目录>\\Config\\Ocr\\ 或 Assets\\Ocr\\ 下，" +
                                "或填 HALCON 安装目录 ocr\\ 里的文件名（如 Industrial_0-9A-Z_NoRej.omc）");
                }
                HOperatorSet.ReadOcrClassMlp(fontPath, out ocrHandle);

                // ---- 7) 识别 ----
                HOperatorSet.DoOcrMultiClassMlp(sorted, target, ocrHandle,
                    out HTuple classes, out HTuple confidences);

                if (classes == null || classes.Length == 0)
                    return Fail("分类器未返回任何字符（区域与图像不匹配？）");

                // ---- 8) 拼接文本 + 计算置信度统计 ----
                var sb = new System.Text.StringBuilder();
                double minConf = double.PositiveInfinity, sumConf = 0;
                for (int i = 0; i < classes.Length; i++)
                {
                    string ch = classes[i].S ?? string.Empty;
                    sb.Append(ch);
                    double c = i < confidences.Length ? confidences[i].D : 0;
                    sumConf += c;
                    if (c < minConf) minConf = c;
                }
                string text = sb.ToString();
                if (double.IsInfinity(minConf)) minConf = 0;
                double meanConf = classes.Length > 0 ? sumConf / classes.Length : 0;

                // ---- 9) 表达式过滤（白名单语义：过滤后为空视为未识别）----
                string filtered = ApplyFilter(text, expressionFilter);

                // ---- 10) 置信度门（课程核心教训：识别"成功"不等于结果对）----
                var warnings = new System.Collections.Generic.List<string>();
                if (minConfidence > 0 && minConf < minConfidence)
                    warnings.Add($"最低置信度 {minConf:F3} < 门限 {minConfidence:F3}（可能命中未训练字符，结果不可信）");
                if (filtered.Length == 0 && !string.IsNullOrWhiteSpace(expressionFilter)
                    && expressionFilter != "*")
                    warnings.Add($"过滤 '{expressionFilter}' 后无有效字符");

                bool ok = warnings.Count == 0;
                return new OcrReadResult
                {
                    Success = ok,
                    TextResult = ok ? filtered : filtered,
                    CharRegions = sorted,
                    Count = classes.Length,
                    MinConfidence = minConf,
                    MeanConfidence = meanConf,
                    ResolvedFontPath = fontPath,
                    Message = ok
                        ? $"识别成功: {filtered}（{classes.Length} 字，最低置信度 {minConf:F3}）"
                        : $"识别结果可疑: {filtered} —— " + string.Join("；", warnings)
                };
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(OCRTool), "OCR 识别失败", ex);
                return Fail("OCR 识别异常: " + ex.Message);
            }
            finally
            {
                if (ocrHandle != null) { try { HOperatorSet.ClearOcrClassMlp(ocrHandle); } catch { } }
                // 只释放本方法自建对象；返回的 sorted 归调用方
                if (target != work) reduced?.Dispose();
                binary?.Dispose();
                connected?.Dispose();
                selected?.Dispose();
                dilated?.Dispose();
                if (ownWork) work?.Dispose();
                if (ownGray) gray?.Dispose();
            }
        }

        // ==================================================================
        // 分类器路径解析（三级回退，找不到就响亮失败）
        // ==================================================================

        /// <summary>查找顺序：绝对路径 → Config\Ocr → Assets\Ocr → HALCON ocr 目录。找不到返回 null。</summary>
        public static string ResolveClassifierPath(string fontFileName)
        {
            if (string.IsNullOrWhiteSpace(fontFileName)) return null;
            string name = fontFileName.Trim();

            // ① 绝对路径
            try
            {
                if (Path.IsPathRooted(name) && File.Exists(name)) return name;
            }
            catch { /* 非法路径字符 → 继续按文件名找 */ }

            string fileName = Path.GetFileName(name);
            var probes = new System.Collections.Generic.List<string>
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Ocr", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Ocr", fileName)
            };

            // ③ HALCON 安装目录：%HALCONROOT%\ocr
            foreach (var rootVar in new[] { "HALCONROOT", "HALCONROOT24", "HALCONROOT_SA" })
            {
                var root = Environment.GetEnvironmentVariable(rootVar);
                if (!string.IsNullOrWhiteSpace(root))
                    probes.Add(Path.Combine(root, "ocr", fileName));
            }
            // 本机实证的 HALCON 24.11 安装路径（环境变量常缺失，兜一个已知位置）
            probes.Add(Path.Combine(@"C:\Program Files\MVTec\HALCON-24.11-Progress-Steady", "ocr", fileName));

            foreach (var p in probes)
            {
                try { if (File.Exists(p)) return p; } catch { }
            }
            return null;
        }

        // ==================================================================
        // 表达式过滤（轻量白名单/正则，避免引 Regex 全功能带来误用）
        // ==================================================================

        /// <summary>
        /// 过滤识别结果：
        ///   null/空/"*" → 不过滤；
        ///   "0-9" / "0-9A-Z" 这类区间白名单 → 只保留落在区间的字符；
        ///   其它 → 按正则匹配保留（失败则原样返回，不静默清空）。
        /// </summary>
        private static string ApplyFilter(string text, string filter)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (string.IsNullOrWhiteSpace(filter) || filter.Trim() == "*") return text;

            string f = filter.Trim();

            // 区间白名单：把 "0-9A-Z" 展开成允许字符集
            var allowed = new System.Collections.Generic.HashSet<char>();
            bool rangeMode = true;
            for (int i = 0; i < f.Length; i++)
            {
                char c = f[i];
                if (i + 2 < f.Length && f[i + 1] == '-' &&
                    (char.IsLetterOrDigit(c) && char.IsLetterOrDigit(f[i + 2])))
                {
                    char from = c, to = f[i + 2];
                    for (char x = from; x <= to; x++) allowed.Add(x);
                    i += 2;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    allowed.Add(c);
                }
                else
                {
                    rangeMode = false;   // 含正则元字符 → 交给正则
                    break;
                }
            }

            if (rangeMode && allowed.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (char c in text)
                    if (allowed.Contains(c)) sb.Append(c);
                return sb.ToString();
            }

            try
            {
                var m = System.Text.RegularExpressions.Regex.Matches(text, f);
                var sb = new System.Text.StringBuilder();
                foreach (System.Text.RegularExpressions.Match x in m) sb.Append(x.Value);
                return sb.Length > 0 ? sb.ToString() : text;
            }
            catch
            {
                // 表达式非法：不过滤，原样返回（不静默清空成"识别成功但没结果"）
                return text;
            }
        }

        private static OcrReadResult Fail(string msg)
            => new OcrReadResult { Success = false, Message = msg };
    }
}
