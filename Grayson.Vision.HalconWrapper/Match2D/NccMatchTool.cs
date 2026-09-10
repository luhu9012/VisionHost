using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    public static class NccMatchTool
    {
        /// <summary>创建 NCC 灰度模板</summary>
        /// <param name="learnDomain">学习域 Region（可选，掩膜裁剪；null=全 ROI 矩形）。所有权归调用方，只借用不释放。</param>
        /// <param name="datumRow/datumCol">基准点（v2 锚点语义，创建图绝对坐标；null=不设锚点）。
        /// NCC set_ncc_model_origin 语义同 shape 版（相对默认参考点偏移），此处按 ROI 矩形中心作基准
        /// （NCC 无学习域重心语义假设，掩膜+NCC+锚点组合留待实测）。</param>
        /// <returns>模板句柄（HTuple，HHandle 元素），算子要求 handle 类型，全程不解包</returns>
        public static Result<HTuple> CreateNccModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd,
            HObject learnDomain = null, double? datumRow = null, double? datumCol = null)
        {
            if (templateImage == null || !templateImage.IsInitialized())
                return Result<HTuple>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(templateImage, learnDomain ?? roiRect, out roiImg);
                    guard.Register(roiImg);

                    // ⚠ 通道统一：create_ncc_model 要求单通道灰度图（同 ShapeMatch 的 BGR24 坑）
                    HObject roiGray;
                    HObject modelImg = TemplateMatchTool.EnsureGray(roiImg, out roiGray);
                    if (roiGray != null)
                        guard.Register(roiGray);

                    HOperatorSet.CreateNccModel(modelImg, "auto", angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "use_polarity", out modelId);

                    // ⚠ 模型原点（v2 锚点语义，2026-09-05 回归修复）：set_ncc_model_origin 参数=相对默认
                    //   参考点的偏移量（误传绝对坐标会把返回坐标推到 ≈2×中心）。基准=ROI 矩形中心；
                    //   仅在显式传入 datum 时钉锚点（默认不设，锚点=默认参考点，行为不变）。
                    if (datumRow.HasValue && datumCol.HasValue)
                    {
                        HOperatorSet.AreaCenter(roiRect, out HTuple ar, out HTuple cr, out HTuple cc);
                        double offRow = datumRow.Value - cr.D;
                        double offCol = datumCol.Value - cc.D;
                        HOperatorSet.SetNccModelOrigin(modelId, offRow, offCol);
                        LogBus.Info(nameof(NccMatchTool),
                            $"NCC 模板锚点已钉: 基准点=({datumRow.Value:F2},{datumCol.Value:F2}) ROI中心=({cr.D:F2},{cc.D:F2}) 偏移=({offRow:F2},{offCol:F2})");
                    }
                }
                // 句柄直接返回（不解包成数字）：HHandle 元素保持 handle 类型，Write/Clear/Find 才可再传回算子
                return Result<HTuple>.Ok(modelId);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "创建 NCC 模板失败", ex);
                return Result<HTuple>.Fail("NCC 模板创建异常", -1, ex);
            }
        }

        /// <summary>查找 NCC 灰度模板</summary>
        /// <param name="angleStartDeg">覆盖起始角度（°），null 表示使用模板内建范围</param>
        /// <param name="angleEndDeg">覆盖终止角度（°），null 表示使用模板内建范围</param>
        public static Result<TemplateMatchResult[]> FindNccModel(HObject searchImage, HTuple modelId, double minScore = 0.7,
            double? angleStartDeg = null, double? angleEndDeg = null)
        {
            if (searchImage == null || !searchImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            HObject gray = null;
            try
            {
                // ⚠ 通道统一：find_ncc_model 要求单通道灰度图（EnsureGray 内部自动转灰度并打日志）
                HObject findImg = TemplateMatchTool.EnsureGray(searchImage, out gray);

                // 角度参数：未指定或无效范围时使用 0/0（模板内建范围）
                double angleStartRad = 0;
                double angleExtentRad = 0;
                if (angleStartDeg.HasValue && angleEndDeg.HasValue && angleEndDeg.Value > angleStartDeg.Value)
                {
                    angleStartRad = angleStartDeg.Value / 180.0 * Math.PI;
                    angleExtentRad = (angleEndDeg.Value - angleStartDeg.Value) / 180.0 * Math.PI;
                }

                HTuple rows, cols, angles, scores;
                HOperatorSet.FindNccModel(findImg, modelId, angleStartRad, angleExtentRad, minScore, 1, 0.5, "true", 0, out rows, out cols, out angles, out scores);

                int count = rows.Length;
                if (count == 0)
                {
                    // 0 结果不等于"无从判断"：低阈值回扫给出真实最佳相似度（同 ShapeMatch 诊断）
                    DiagnoseNccMatch(findImg, modelId, minScore, angleStartRad, angleExtentRad);
                }
                TemplateMatchResult[] resultArr = new TemplateMatchResult[count];
                for (int i = 0; i < count; i++)
                {
                    resultArr[i] = new TemplateMatchResult
                    {
                        PixelRow = rows[i].D,
                        PixelCol = cols[i].D,
                        RotateDegree = angles[i].D / Math.PI * 180,
                        Score = scores[i].D
                    };
                }
                // 🌟 调试日志（同 ShapeMatchTool.FindShapeModel）：一次匹配一条，打印 MinScore/图像尺寸/候选详情
                try
                {
                    HOperatorSet.GetImageSize(findImg, out HTuple iw, out HTuple ih);
                    if (count == 0)
                    {
                        LogBus.Info(nameof(NccMatchTool),
                            $"NCC匹配: MinScore={minScore:F2} 图像{iw.I}x{ih.I} → 0 命中（低于门槛，真实最佳分见上方诊断回扫）");
                    }
                    else
                    {
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < count; i++)
                        {
                            if (i > 0) sb.Append(" | ");
                            sb.Append($"#{i} Score={scores[i].D:F3} @({rows[i].D:F1},{cols[i].D:F1}) {angles[i].D / Math.PI * 180:F2}°");
                        }
                        LogBus.Info(nameof(NccMatchTool),
                            $"NCC匹配: MinScore={minScore:F2} 图像{iw.I}x{ih.I} → 命中{count}个 {sb}");
                    }
                }
                catch { /* 日志失败不影响匹配结果 */ }
                return Result<TemplateMatchResult[]>.Ok(resultArr);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "NCC 模板匹配查找失败", ex);
                return Result<TemplateMatchResult[]>.Fail("NCC 匹配运算异常", -1, ex);
            }
            finally
            {
                gray?.Dispose();
            }
        }

        /// <summary>
        /// 0 结果诊断回扫（同 TemplateMatchTool.DiagnoseShapeMatch）：极低阈值 0.10 重跑，
        /// 报告当前搜索图与 NCC 模板的真实最佳相似度，让调参有数字依据。
        /// 仅在 0 结果时触发，诊断自身异常不影响主流程。
        /// </summary>
        private static void DiagnoseNccMatch(HObject findImg, HTuple modelId, double minScore, double angleStartRad = 0, double angleExtentRad = 0)
        {
            try
            {
                HOperatorSet.FindNccModel(findImg, modelId, angleStartRad, angleExtentRad, 0.1, 5, 0.5, "true", 0,
                    out HTuple rows, out HTuple cols, out HTuple angles, out HTuple scores);

                if (scores.Length == 0)
                {
                    LogBus.Warn(nameof(NccMatchTool),
                        "诊断回扫（MinScore=0.10）：全图无任何候选——模板与现场图像差异过大（光照/位置/尺度/极性反转），建议用当前现场图重新框选创建模板。");
                    return;
                }

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < scores.Length; i++)
                    sb.Append($"#{i + 1} Score={scores[i].D:F3} @({rows[i].D:F0},{cols[i].D:F0}) 角度{angles[i].D / Math.PI * 180:F1}°  ");

                double best = scores[0].D;
                if (best >= minScore * 0.8)
                {
                    LogBus.Warn(nameof(NccMatchTool),
                        $"诊断回扫（MinScore=0.10）：{sb}——最佳分已接近设定阈值 {minScore:F2}，把节点 MinScore 调低至 {Math.Max(0.1, best - 0.05):F2} 以下即可命中。");
                }
                else
                {
                    LogBus.Warn(nameof(NccMatchTool),
                        $"诊断回扫（MinScore=0.10）：{sb}——与阈值 {minScore:F2} 差距较大，模板/现场差异明显，建议用现场图重建模板。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(NccMatchTool), $"诊断回扫执行失败（不影响主流程）: {ex.Message}");
            }
        }

        /// <summary>
        /// 生成"贴合匹配结果"的 NCC 匹配框（region）。NCC 模型没有特征轮廓算子
        /// （对比 shape 模型的 get_shape_model_contours），用模板元数据 ROI 的外接矩形
        /// gen_rectangle2 按匹配位置 + 匹配角度旋转生成，可视化"模板贴在现场图目标上"。
        /// 参考点约定：find_ncc_model 返回的 Row/Col 即创建模板时 ROI 域的中心。
        /// </summary>
        /// <param name="roiRow1/Col1/Row2/Col2">模板创建时的 ROI（模板元数据 TemplateInfo）</param>
        /// <param name="match">匹配结果（像素坐标 + 角度制°）</param>
        /// <returns>旋转矩形 region HObject，所有权归调用方（提交预览显示或自行 Dispose）</returns>
        public static Result<HObject> CreateNccMatchRectangle(double roiRow1, double roiCol1, double roiRow2, double roiCol2, TemplateMatchResult match)
        {
            try
            {
                // gen_rectangle2(Row, Col, Phi, Length1, Length2)：
                // Length1 = Phi 方向（列）半长，Length2 = 垂直方向（行）半长
                double halfCol = Math.Abs(roiCol2 - roiCol1) / 2;
                double halfRow = Math.Abs(roiRow2 - roiRow1) / 2;
                HOperatorSet.GenRectangle2(out HObject rect, match.PixelRow, match.PixelCol,
                    match.RotateDegree / 180 * Math.PI, halfCol, halfRow);
                return Result<HObject>.Ok(rect);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "生成 NCC 匹配贴合框失败", ex);
                return Result<HObject>.Fail("NCC 匹配贴合框生成异常", -1, ex);
            }
        }

        /// <summary>释放 NCC 模板</summary>
        public static Result ClearNccModel(HTuple modelId)
        {
            try
            {
                HOperatorSet.ClearNccModel(modelId);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "销毁 NCC 模板失败", ex);
                return Result.Fail("NCC 模板释放异常", -1, ex);
            }
        }

        /// <summary>
        /// 保存 NCC 模板到文件（.ncc）。模板管理界面"创建模板"后调用，实现模板持久化。
        /// </summary>
        public static Result WriteNccModel(HTuple modelId, string filePath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                HOperatorSet.WriteNccModel(modelId, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "保存 NCC 模板文件失败", ex);
                return Result.Fail("NCC 模板文件保存异常", -1, ex);
            }
        }

        /// <summary>
        /// 从文件加载 NCC 模板（.ncc），返回内存句柄。运行时匹配前调用，
        /// 使用完毕必须 ClearNccModel 释放。
        /// </summary>
        public static Result<HTuple> ReadNccModel(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    return Result<HTuple>.Fail("NCC 模板文件不存在: " + filePath);
                HTuple modelId;
                HOperatorSet.ReadNccModel(filePath, out modelId);
                return Result<HTuple>.Ok(modelId);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(NccMatchTool), "加载 NCC 模板文件失败", ex);
                return Result<HTuple>.Fail("NCC 模板文件加载异常", -1, ex);
            }
        }
    }
}