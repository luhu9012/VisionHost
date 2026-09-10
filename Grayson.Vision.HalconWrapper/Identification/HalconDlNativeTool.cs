//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: HalconDlNativeTool.cs
// 说 明: HALCON 原生深度学习推理工具（stage9-2 落地，2026-09-09）。
//        把 HTH《Demo_深度学习》两个 .hdev 示例脚本翻译为 C# + halcondotnet 直调：
//          · 语义分割（药片破裂/脏污）：ReadDlModel + ReadDict → 设备查询(优先GPU) →
//            ZoomImageSize(632×300,nearest) → 样本字典 → ConvertImageType real →
//            ApplyDlModel(['segmentation_image']) → 按类别ID阈值 → 面积过滤 → 统计/Region
//          · 分类（镁片 ok/ng）：同上读模型 → ConvertImageType real → ApplyDlModel([]) →
//            classification_class_names/confidences → argmax → 摘要
//        ⚠ 铁律：不走 HDevEngine、不加载/执行 .hdev；全部算子经 HOperatorSet 直调，
//        翻译自示例脚本的算子序列；hdict 的 normalization_type=none，等价预处理=缩放+转 real。
//        生命周期：本工具内的 HObject/句柄一律 finally Dispose（对齐 DeepLearningTool 模板）；
//        .NET 包装器缺 ClearDict 算子，样本字典仅 RemoveDictKey 释放图像值引用（句柄极小）。
//===================================================================================
using HalconDotNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Grayson.Vision.HalconWrapper.Identification
{
    /// <summary>HALCON 原生 DL 推理任务类型（值域与 Contracts.InferenceTaskType / DlTaskType 对齐）。</summary>
    public enum HalconDlTaskKind
    {
        Classification = 0,
        ObjectDetection = 1,   // 预留：当前 .hdl 通道只落地分割/分类
        Segmentation = 2,
        AnomalyDetection = 3   // 预留
    }

    /// <summary>HALCON 原生 DL 推理请求（节点参数 → 本工具）。</summary>
    public class HalconNativeDlRequest
    {
        /// <summary>DLTool 导出的 .hdl 模型路径（相对运行目录或绝对路径）</summary>
        public string HdlModelPath { get; set; }

        /// <summary>DLTool 导出的预处理参数字典 .hdict（可为空——两个示例模型 normalization=none）</summary>
        public string PreprocessParamPath { get; set; }

        /// <summary>推理任务类型（仅 Classification / Segmentation 已落地）</summary>
        public HalconDlTaskKind TaskKind { get; set; } = HalconDlTaskKind.Segmentation;

        // —— 分割预处理（翻译自药片示例：先缩放再转 real）——
        public bool ResizeEnabled { get; set; } = true;
        public int ResizeWidth { get; set; } = 632;
        public int ResizeHeight { get; set; } = 300;
        public string ResizeInterpolation { get; set; } = "nearest_neighbor";

        // —— 分割后处理（翻译自药片示例：按类别 ID 阈值 + 最小面积过滤）——
        /// <summary>视为缺陷/目标的类别 ID 列表（示例：1=破裂, 2=脏污；0 一般为背景/good）</summary>
        public List<int> DefectClassIds { get; set; } = new List<int> { 1, 2 };

        /// <summary>最小缺陷面积阈值（px），小于该值的区域忽略（示例默认 100）</summary>
        public double MinDefectArea { get; set; } = 100;

        /// <summary>批大小（固定 1，翻译自示例）</summary>
        public int BatchSize { get; set; } = 1;
    }

    /// <summary>HALCON 原生 DL 推理结果（结构化文本 + HALCON Region，语义对齐 ONNX 的 DlInferenceResult）。</summary>
    public class HalconNativeDlResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public double ElapsedMs { get; set; }

        /// <summary>任务类型</summary>
        public HalconDlTaskKind TaskKind { get; set; }

        /// <summary>分类 Top-1 名称（分类任务；分割=null）</summary>
        public string TopClassName { get; set; }

        /// <summary>分类 Top-1 置信度（分类任务）</summary>
        public double TopConfidence { get; set; }

        /// <summary>检出数量（分类=Top1 类别号 1；分割=缺陷连通域总数）</summary>
        public int Count { get; set; }

        /// <summary>摘要文本（引擎判据消费：分类 "ok: 99.1%"；分割 "检出 破裂 3处…" / "未检出"）</summary>
        public string Summary { get; set; }

        /// <summary>分割缺陷 Region（已放大回原图尺寸；分类/未检出=null）</summary>
        public object ResultRegion { get; set; }

        /// <summary>ResultRegion 是否为有效 HALCON Region</summary>
        public bool HasRegion { get; set; }

        /// <summary>
        /// 任务类型校正说明：节点参数 TaskType 与 .hdl 模型真实类型不一致时，
        /// 本工具按模型类型执行并在此留痕（非空表示已自动校正），供执行器记录诊断日志。
        /// </summary>
        public string CorrectionNote { get; set; }

        /// <summary>分割各缺陷类计数明细（类ID → 连通域数）</summary>
        public Dictionary<int, int> ClassCounts { get; set; } = new Dictionary<int, int>();
    }

    /// <summary>
    /// HALCON 原生深度学习推理工具：HObject 图像 → .hdl 模型端到端（纯软件，无需相机/运控）。
    /// 线程模型：由节点执行器在后台算子线程调用，与 DeepLearningTool 同一约定。
    /// </summary>
    public static class HalconDlNativeTool
    {
        /// <summary>推理入口（每周期独立加载模型，保证多工位无共享状态；性能优化留待批量场景）</summary>
        public static HalconNativeDlResult RunInference(object image, HalconNativeDlRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.HdlModelPath))
                return Fail("推理请求为空或未配置 .hdl 模型路径");

            var hobj = image as HObject;
            if (hobj == null || !IsInitialized(hobj))
                return Fail("输入图像为空或不是有效的 HObject");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string hdlPath = ResolvePath(req.HdlModelPath);
            if (!File.Exists(hdlPath))
                return Fail("模型文件不存在: " + hdlPath);

            HTuple modelHandle = null;
            HObject work = null, extra = null, real = null, segImage = null, displayRegion = null;
            try
            {
                // ---------- 多对象容器只取第一张 ----------
                HOperatorSet.CountObj(hobj, out HTuple countT);
                if (countT.I > 1)
                {
                    HOperatorSet.SelectObj(hobj, out extra, 1);
                    work = extra;
                }
                else work = hobj;

                // ---------- 1) 加载模型 + 预处理参数 ----------
                HOperatorSet.ReadDlModel(new HTuple(hdlPath), out modelHandle);
                if (modelHandle == null || modelHandle.Length == 0)
                    return Fail("read_dl_model 失败（.hdl 文件无效或运行时异常）");

                // 设备：优先 GPU 降级 CPU（翻译自示例 query_available_dl_devices）
                HOperatorSet.QueryAvailableDlDevices(new HTuple("runtime", "runtime"), new HTuple("gpu", "cpu"),
                    out HTuple devices);
                if (devices == null || devices.Length == 0)
                    return Fail("未找到可用的计算设备（GPU/CPU 均不可用，请检查 HALCON 运行时与驱动）");
                HOperatorSet.SetDlModelParam(modelHandle, new HTuple("device"), devices[0]);
                HOperatorSet.SetDlModelParam(modelHandle, new HTuple("batch_size"), new HTuple(Math.Max(1, req.BatchSize)));

                // ---------- 1.5) 模型类型自判定（.hdl 真实类型为准，防止 TaskType 错配） ----------
                // 病根（2026-09-09 修复）：分类模型被按"分割"请求（配方/参数 TaskType=Segmentation）时，
                // apply_dl_model 会按分割请求 segmentation_image 输出层 → HALCON 7787(invalid output layer)
                // 或 2106(wrong image width)。这里以模型文件内声明的 type 为准执行，
                // 对任何"分类/分割"误标自动校正；不再盲信节点参数。
                HOperatorSet.GetDlModelParam(modelHandle, new HTuple("type"), out HTuple modelTypeT);
                string modelType = modelTypeT != null && modelTypeT.Length > 0
                    ? (modelTypeT.S ?? string.Empty).Trim().ToLowerInvariant()
                    : string.Empty;

                HalconDlTaskKind effectiveKind;
                switch (modelType)
                {
                    case "classification": effectiveKind = HalconDlTaskKind.Classification; break;
                    case "segmentation":   effectiveKind = HalconDlTaskKind.Segmentation; break;
                    default:
                        return Fail($"不支持的 .hdl 模型类型 \"{modelType}\"：当前仅支持 classification / segmentation，请核对模型文件是否选错。");
                }

                string correctionNote = null;
                if (effectiveKind != req.TaskKind)
                {
                    correctionNote = $"节点 TaskType=({(int)req.TaskKind}) 与模型实际类型 \"{modelType}\" 不一致，"
                        + $"已按模型类型以 {(effectiveKind == HalconDlTaskKind.Classification ? "分类" : "分割")} 推理执行。";
                }

                // ---------- 2) 图像预处理（等价翻译：normalization=none → 缩放 + 转 real） ----------
                HObject scaled = null;
                try
                {
                    if (effectiveKind == HalconDlTaskKind.Segmentation && req.ResizeEnabled
                        && req.ResizeWidth > 0 && req.ResizeHeight > 0)
                    {
                        HOperatorSet.ZoomImageSize(work, out scaled,
                            new HTuple(req.ResizeWidth), new HTuple(req.ResizeHeight),
                            new HTuple(req.ResizeInterpolation ?? "nearest_neighbor"));
                    }
                    HOperatorSet.ConvertImageType(scaled ?? work, out real, new HTuple("real"));
                }
                finally
                {
                    if (scaled != null && !ReferenceEquals(scaled, work)) scaled.Dispose();
                }

                // ---------- 3) 样本字典 + 推理 ----------
                HOperatorSet.CreateDict(out HTuple sample);
                HOperatorSet.SetDictObject(real, sample, new HTuple("image"));
                if (real != null && !ReferenceEquals(real, work)) real.Dispose(); // 字典持有引用，归还本地句柄
                real = null;

                HTuple resultDict = RunApply(modelHandle, sample, effectiveKind);

                switch (effectiveKind)
                {
                    case HalconDlTaskKind.Classification:
                    {
                        var clsResult = BuildClassificationResult(resultDict, sw);
                        if (correctionNote != null) clsResult.CorrectionNote = correctionNote;
                        return clsResult;
                    }
                    case HalconDlTaskKind.Segmentation:
                    {
                        var segResult = BuildSegmentationResult(resultDict, work, req, sw, ref displayRegion);
                        if (correctionNote != null) segResult.CorrectionNote = correctionNote;
                        return segResult;
                    }
                    default:
                        return Fail($"HALCON 原生 DL 暂未落地任务类型 {(int)effectiveKind}（当前支持 分类/分割）");
                }
            }
            catch (Exception ex)
            {
                return Fail("HALCON DL 推理异常: " + ex.Message);
            }
            finally
            {
                if (extra != null) extra.Dispose();
                if (real != null) real.Dispose();
                if (segImage != null) segImage.Dispose();
                if (displayRegion != null) displayRegion.Dispose();
                if (modelHandle != null)
                {
                    try { HOperatorSet.ClearDlModel(modelHandle); } catch { }
                }
            }
        }

        /// <summary>执行 apply_dl_model（按任务类型给定输出键）并解析批次结果句柄。</summary>
        private static HTuple RunApply(HTuple modelHandle, HTuple sample, HalconDlTaskKind kind)
        {
            // apply_dl_model(DLModelHandle, DLSample, Outputs, DLResultBatch)
            HOperatorSet.ApplyDlModel(modelHandle, sample,
                kind == HalconDlTaskKind.Segmentation ? new HTuple("segmentation_image") : new HTuple(),
                out HTuple batch);
            if (batch == null || batch.Length == 0)
                throw new InvalidOperationException("apply_dl_model 返回空批次");
            // 单样本输入：DLResultBatch 即 1 元素字典句柄元组；HDevelop 示例对分类直接
            // get_dict_tuple(DLResultBatch,…)、分割 get_dict_object(DLResultBatch,…)，访问器透明接受，
            // 故原样透传（不做 [0] 抽取，规避 HTuple/HTupleElements 在 C# 7.3 的类型转换问题）。
            return batch;
        }

        private static HalconNativeDlResult BuildClassificationResult(HTuple resultDict, System.Diagnostics.Stopwatch sw)
        {
            HOperatorSet.GetDictTuple(resultDict, new HTuple("classification_class_names"), out HTuple names);
            HOperatorSet.GetDictTuple(resultDict, new HTuple("classification_confidences"), out HTuple confs);

            if (names == null || names.Length == 0 || confs == null || confs.Length == 0)
                return Ok(new HalconNativeDlResult
                {
                    TaskKind = HalconDlTaskKind.Classification,
                    Summary = "无分类输出",
                    ElapsedMs = sw.ElapsedMilliseconds
                });

            int best = 0;
            double bestConf = double.MinValue;
            for (int i = 0; i < confs.Length; i++)
            {
                double v = confs[i].D;
                if (v > bestConf) { bestConf = v; best = i; }
            }
            string topName = names[best].S;
            // 摘要格式与 ONNX DlInference 节点一致："{类别}: {置信度:P1}" —— 判据可配 OkClassName/NgClassName 前缀
            string summary = string.Format(CultureInfo.InvariantCulture, "{0}: {1:P1}", topName, bestConf);
            return Ok(new HalconNativeDlResult
            {
                TaskKind = HalconDlTaskKind.Classification,
                TopClassName = topName,
                TopConfidence = bestConf,
                Count = 1,
                Summary = summary,
                Message = summary,
                ElapsedMs = sw.ElapsedMilliseconds
            });
        }

        private static HalconNativeDlResult BuildSegmentationResult(HTuple resultDict, HObject srcImage,
            HalconNativeDlRequest req, System.Diagnostics.Stopwatch sw, ref HObject displayRegion)
        {
            HOperatorSet.GetDictObject(out HObject segImage, resultDict, new HTuple("segmentation_image"));
            if (segImage == null || !IsInitialized(segImage))
                return Ok(new HalconNativeDlResult { TaskKind = HalconDlTaskKind.Segmentation, Summary = "无分割输出", ElapsedMs = sw.ElapsedMilliseconds });

            HOperatorSet.GetImageSize(segImage, out HTuple segW, out HTuple segH);
            double modelW = segW.D, modelH = segH.D;
            HOperatorSet.GetImageSize(srcImage, out HTuple srcW, out HTuple srcH);

            var classIds = (req.DefectClassIds == null || req.DefectClassIds.Count == 0)
                ? new List<int> { 1, 2 } : req.DefectClassIds;

            var counts = new Dictionary<int, int>();
            var parts = new List<string>();
            int total = 0;
            HObject merged = null;

            try
            {
                foreach (var cls in classIds.Distinct())
                {
                    HOperatorSet.Threshold(segImage, out HObject regionCls, new HTuple(cls), new HTuple(cls));
                    HObject filtered = null;
                    try
                    {
                        HOperatorSet.Connection(regionCls, out HObject conn);
                        try
                        {
                            if (req.MinDefectArea > 0)
                            {
                                HOperatorSet.SelectShape(conn, out filtered, new HTuple("area"), new HTuple("and"),
                                    new HTuple(req.MinDefectArea), new HTuple(9999999.0));
                            }
                            else filtered = conn.Clone();
                        }
                        finally { conn.Dispose(); }

                        if (filtered != null && IsInitialized(filtered))
                        {
                            HOperatorSet.CountObj(filtered, out HTuple nT);
                            int n = nT.I;
                            if (n > 0)
                            {
                                HOperatorSet.AreaCenter(filtered, out HTuple areas, out _, out _);
                                double totalArea = 0;
                                for (int i = 0; i < areas.Length; i++) totalArea += areas[i].D;
                                counts[cls] = n;
                                total += n;
                                parts.Add($"{ClassLabel(cls, req)} {n}处(面积 {(long)totalArea}px)");
                                // 缺陷 Region 并集（模型尺度 → 随后放大回原图）
                                if (merged == null) merged = filtered.Clone();
                                else
                                {
                                    HOperatorSet.ConcatObj(merged, filtered, out HObject tmp);
                                    merged.Dispose();
                                    merged = tmp;
                                }
                            }
                        }
                    }
                    finally
                    {
                        regionCls.Dispose();
                        if (filtered != null) filtered.Dispose();
                    }
                }

                string summary = total > 0 ? "检出 " + string.Join("; ", parts) : "未检出";

                // 放大回原图尺寸（同 DeepLearningTool.BuildSegmentationRegion 模式；19.11 无 ZoomRegionSize）
                if (merged != null && modelW > 0 && modelH > 0)
                {
                    double sx = srcW.D / modelW, sy = srcH.D / modelH;
                    if (Math.Abs(sx - 1) > 1e-6 || Math.Abs(sy - 1) > 1e-6)
                    {
                        HOperatorSet.ZoomRegion(merged, out HObject zoomed, new HTuple(sx), new HTuple(sy));
                        displayRegion = zoomed; // 所有权移交结果
                    }
                    else displayRegion = merged;
                    merged = null;
                }

                var result = new HalconNativeDlResult
                {
                    TaskKind = HalconDlTaskKind.Segmentation,
                    Count = total,
                    Summary = summary,
                    Message = summary,
                    ClassCounts = counts,
                    ResultRegion = displayRegion,
                    HasRegion = displayRegion != null,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
                displayRegion = null; // 🌟 所有权已移交 result，调用方 finally 不得再 Dispose
                return Ok(result);
            }
            finally
            {
                if (merged != null) merged.Dispose();
                segImage.Dispose();
            }
        }

        /// <summary>类别 ID → 可读标签（缺省 "类别{n}"；DlModel 资产类别表扩展留待模板层）</summary>
        private static string ClassLabel(int cls, HalconNativeDlRequest req)
        {
            switch (cls)
            {
                case 1: return "破裂";
                case 2: return "脏污";
                default: return "类别" + cls;
            }
        }

        private static HalconNativeDlResult Ok(HalconNativeDlResult r)
        {
            r.Success = true;
            return r;
        }

        private static HalconNativeDlResult Fail(string message)
        {
            return new HalconNativeDlResult { Success = false, Message = message, Summary = message };
        }

        /// <summary>相对路径以运行目录为基准解析（对齐 DeepLearningTool 约定）</summary>
        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            return Path.IsPathRooted(path) ? path : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
        }

        private static bool IsInitialized(HObject obj)
        {
            try
            {
                if (obj == null) return false;
                HOperatorSet.CountObj(obj, out HTuple n);
                return n.I > 0;
            }
            catch { return false; }
        }
    }
}
