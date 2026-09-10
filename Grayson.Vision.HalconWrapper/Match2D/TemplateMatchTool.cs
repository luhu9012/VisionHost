using System;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    /// <summary>
    /// 基于shape_model的2D刚性模板匹配
    /// 工业最常用工件定位：平移+旋转匹配，输出坐标、角度、匹配分数
    /// 统一封装模板创建、在线匹配、参数归一化
    /// </summary>
    public static class TemplateMatchTool
    {
        /// <summary>
        /// 创建形状模板（离线训练模板使用）
        /// </summary>
        /// <param name="templateImage">模板原图</param>
        /// <param name="roiRow1/Col1/Row2/Col2">模板ROI范围</param>
        /// <param name="angleStart">起始旋转角度(°)</param>
        /// <param name="angleEnd">终止旋转角度(°)</param>
        /// <param name="learnDomain">学习域 Region（可选）：由掩膜（TemplateMaskRegionBuilder.BuildLearnMaskRegion）裁剪而来。
        /// 传入时模板只学该区域内的特征（剔除干扰）；null = 全 ROI 矩形学习。所有权归调用方，本方法只借用不释放。</param>
        /// <param name="datumRow/datumCol">基准点（v2 锚点语义，创建图绝对坐标）。非空时用 set_shape_model_origin
        /// 把模型参考点钉到基准点上——find_shape_model 返回的 (Row,Col) ≡ 基准点亚像素位置，生产/标定/节点
        /// 消费同一口径。偏移量 = 基准点 − 学习域重心（掩膜场景自动正确；HALCON 该接口语义为相对默认参考点的偏移量，
        /// 2026-09-05 实证）。null=锚点留在学习域默认参考点（矩形域=域中心）。</param>
        /// <returns>模板句柄（HTuple，HHandle 元素）。HALCON 算子要求句柄参数必须是 handle 类型，
        /// 解包成 long 数字再传回会报 #1201 Wrong type of control parameter，故全程用 HTuple 承载。</returns>
        public static Result<HTuple> CreateShapeModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd,
            HObject learnDomain = null, double? datumRow = null, double? datumCol = null)
        {
            if (templateImage == null || !templateImage.IsInitialized())
                return Result<HTuple>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                // 截取ROI区域创建模板（掩膜场景：用掩膜裁剪后的"学习域"替代整矩形域）
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);

                    // ⚠ 灰度化 + 平场校正都必须在【整图】上做（先校正后 ReduceDomain）：
                    //   ① 光照场估计需要 ROI 周围大范围像素参与，只用 ROI 内像素估计会把目标本体亮度也抹掉；
                    //   ② 模板创建与在线搜索必须走完全相同的处理链（整图灰度 → 平场 → ReduceDomain / 直接 find），
                    //      两侧特征空间一致，匹配分才有意义。
                    HObject grayFull;
                    HObject fullGrayImg = EnsureGray(templateImage, out grayFull);
                    if (grayFull != null)
                        guard.Register(grayFull);
                    HObject procImg = fullGrayImg;
                    if (EnableFlatField)
                    {
                        HObject corrected;
                        procImg = ApplyFlatField(fullGrayImg, out corrected);
                        guard.Register(corrected);
                    }
                    HOperatorSet.ReduceDomain(procImg, learnDomain ?? roiRect, out roiImg);
                    guard.Register(roiImg);

                    // 创建形状模板，角度转弧度。
                    // ⚠ 参数铁律（2026-08-30，"怎么框 ROI 自测分都不超 0.6"的根因）：
                    //   - NumLevels："auto"（写死 4 会让小 ROI 在高层金字塔退化，匹配分打折）
                    //   - Contrast："auto"（写死 30 与目标实际对比度不匹配：对比度低时真实特征被筛掉，
                    //     模型残缺 → 匹配分被结构性压低；HALCON 自动估算含滞后阈值三元组）
                    //   - Optimization："auto" 自动控制模型点数
                    //   - Metric："ignore_local_polarity"（2026-08-30 实测：use_polarity 下工件旋转 >20° 分数从
                    //     0.73 崩到 0.3 以下——刻纹在定向光照下旋转后沟槽明暗倒置=局部极性翻转，
                    //     use_polarity 按梯度方向逐点比对直接判负。ignore_local_polarity 允许局部极性
                    //     不一致，是镜面/刻纹目标任意角度旋转的标准对策；代价是匹配稍慢、区分度略降）
                    HOperatorSet.CreateShapeModel(roiImg, "auto", angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "auto", "ignore_local_polarity", "auto", 10, out modelId);

                    // ⚠ 模型原点 = ROI 中心（T1 决议语义：特征点/面一律记"相对学习ROI中心"偏移；
                    //   find_shape_model 返回的 Row/Col 即"模型参考点"当前位置）。
                    // ⚠⚠ 2026-09-05 回归修复（本次"向导匹配点偏 2×ROI中心"根因）：
                    //   曾在此显式 SetShapeModelOrigin(全图ROI中心)，实测 find 返回 = 目标重心 +
                    //   所设偏移 ≈ (r1+r2,c1+c2)（ROI 中心×2），即 HALCON 该接口语义是"相对模型
                    //   重心/默认参考点的偏移量"，并非模型图像绝对坐标。矩形整域时 HALCON 默认
                    //   参考点=模型(域)重心=ROI 中心，find 已直接返回 ROI 中心——无需也不应显式设置。
                    //   掩膜（学习域非矩形）时重心偏离 ROI 中心，需钉参考点→用偏移量表达（候选
                    //   = ROI中心−掩膜重心），留待真实掩膜场景现场标定后再补，勿用全图坐标直写。
                    //   已建模板（.shm/.ncc 内固化偏移）需重新创建模板才会带正确参考点。

                    // 诊断：把 HALCON 自动确定的模板参数打出来（自测分低时对照排查）。
                    // 注意 Contrast 不在此算子的可查参数里（文档：Optimization/Contrast 不可查询）。
                    try
                    {
                        HOperatorSet.GetShapeModelParams(modelId,
                            out HTuple pLevels, out HTuple pAngleStart, out HTuple pAngleExtent, out HTuple pAngleStep,
                            out HTuple pScaleMin, out HTuple pScaleMax, out HTuple pScaleStep,
                            out HTuple pMetric, out HTuple pMinContrast);
                        LogBus.Info(nameof(TemplateMatchTool),
                            $"模板参数（HALCON 自动确定）: 金字塔层数={pLevels.I}, 最小对比度={pMinContrast.I}, " +
                            $"角度范围=[{pAngleStart.D / Math.PI * 180:F1}°, {(pAngleStart.D + pAngleExtent.D) / Math.PI * 180:F1}°], 角度步长={pAngleStep.D / Math.PI * 180:F2}°, Metric={pMetric.S}");
                    }
                    catch (Exception pex)
                    {
                        LogBus.Warn(nameof(TemplateMatchTool), $"读取模板参数失败（不影响创建）: {pex.Message}");
                    }

                    // ⚠ v2 锚点语义：把模型参考点钉到基准点（Datum）上，使 find 输出 ≡ 基准点。
                    // HALCON set_shape_model_origin 参数 = 相对【默认参考点】的偏移量（2026-09-05 实证：
                    // 误传绝对坐标会把输出推到 ≈2×中心）。默认参考点 = 学习域重心（AreaCenter），
                    // 掩膜场景（学习域非矩形）重心≠ROI 中心，用重心作基准自动消除"掩膜原点漂移"。
                    // 锚点随模板写入 .shm（ReadShapeModel 后 get_shape_model_origin 可回读验证）。
                    if (datumRow.HasValue && datumCol.HasValue)
                    {
                        HOperatorSet.AreaCenter(learnDomain ?? roiRect,
                            out HTuple domArea, out HTuple domRow, out HTuple domCol);
                        double offRow = datumRow.Value - domRow.D;
                        double offCol = datumCol.Value - domCol.D;
                        HOperatorSet.SetShapeModelOrigin(modelId, offRow, offCol);
                        LogBus.Info(nameof(TemplateMatchTool),
                            $"模板锚点已钉: 基准点=({datumRow.Value:F2},{datumCol.Value:F2}) 学习域重心=({domRow.D:F2},{domCol.D:F2}) " +
                            $"origin 偏移=({offRow:F2},{offCol:F2}) → find 输出即基准点位置");
                    }
                    else
                    {
                        HOperatorSet.AreaCenter(learnDomain ?? roiRect,
                            out HTuple domArea0, out HTuple domRow0, out HTuple domCol0);
                        LogBus.Info(nameof(TemplateMatchTool),
                            $"模板未设基准点：锚点=学习域重心({domRow0.D:F2},{domCol0.D:F2})（矩形域=ROI 中心）");
                    }
                }
                // 句柄直接返回（不解包成数字）：HHandle 元素保持 handle 类型，Write/Clear/Find 才可再传回算子
                return Result<HTuple>.Ok(modelId);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "创建Shape模板失败", ex);
                return Result<HTuple>.Fail("模板创建异常", -1, ex);
            }
        }

        /// <summary>
        /// 执行模板搜索匹配
        /// </summary>
        /// <param name="searchImage">待搜索大图</param>
        /// <param name="modelId">模板句柄（CreateShapeModel/ReadShapeModel 返回的 HTuple）</param>
        /// <param name="minScore">最低匹配分数（0~1）</param>
        /// <param name="angleStartDeg">覆盖起始角度（°），null 表示使用模板内建范围</param>
        /// <param name="angleEndDeg">覆盖终止角度（°），null 表示使用模板内建范围</param>
        /// <returns>匹配结果集合：坐标、角度、分数</returns>
        public static Result<TemplateMatchResult[]> FindShapeModel(HObject searchImage, HTuple modelId, double minScore = 0.7,
            double? angleStartDeg = null, double? angleEndDeg = null)
        {
            if (searchImage == null || !searchImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            HObject gray = null;
            HObject corrected = null;
            try
            {
                // ⚠ 通道统一：find_shape_model 要求单通道灰度图（EnsureGray 内部自动转灰度并打日志）
                HObject findImg = EnsureGray(searchImage, out gray);

                // ⚠ 平场校正：与 CreateShapeModel 同一处理链（模板建在校正后的图上，
                //   搜索也必须用校正后的图），否则两侧特征空间不一致 → 匹配分无意义。
                if (EnableFlatField)
                {
                    findImg = ApplyFlatField(findImg, out corrected);
                }
                return FindShapeModelCore(findImg, modelId, minScore, angleStartDeg, angleEndDeg);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "模板匹配查找失败", ex);
                return Result<TemplateMatchResult[]>.Fail("匹配运算异常", -1, ex);
            }
            finally
            {
                corrected?.Dispose();
                gray?.Dispose();
            }
        }

        /// <summary>
        /// 在【已处理图】上执行形状匹配（v2：卡尺精测链路用——模板匹配与卡尺测量必须用同一张
        /// 处理图（灰度+平场），本方法让调用方自行准备一次处理图、匹配与测量共享，避免双重平场）。
        /// 输入图坐标即输出坐标（ReduceDomain 不改变坐标系）。
        /// </summary>
        public static Result<TemplateMatchResult[]> FindShapeModelOnProcessed(HObject processedImage, HTuple modelId,
            double minScore = 0.7, double? angleStartDeg = null, double? angleEndDeg = null)
        {
            if (processedImage == null || !processedImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("处理图无效");
            try
            {
                return FindShapeModelCore(processedImage, modelId, minScore, angleStartDeg, angleEndDeg);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "模板匹配查找失败(处理图)", ex);
                return Result<TemplateMatchResult[]>.Fail("匹配运算异常", -1, ex);
            }
        }

        /// <summary>匹配核心：输入必须已为单通道（且按需平场）处理图</summary>
        private static Result<TemplateMatchResult[]> FindShapeModelCore(HObject findImg, HTuple modelId,
            double minScore, double? angleStartDeg, double? angleEndDeg)
        {
            // 角度参数：未指定或无效范围时使用 0/0（模板内建范围）；
            // 指定后转换为弧度，让节点可在模板训练的全角度范围内进一步收窄搜索。
            double angleStartRad = 0;
            double angleExtentRad = 0;
            if (angleStartDeg.HasValue && angleEndDeg.HasValue && angleEndDeg.Value > angleStartDeg.Value)
            {
                angleStartRad = angleStartDeg.Value / 180.0 * Math.PI;
                angleExtentRad = (angleEndDeg.Value - angleStartDeg.Value) / 180.0 * Math.PI;
            }

            HTuple rows, cols, angles, scores;
            // find_shape_model 签名：AngleStart, AngleExtent, MinScore, NumMatches, MaxOverlap, SubPixel, NumLevels, Greediness
            // - AngleStart/AngleExtent 传 0 → 使用模板创建时内建的角度范围（与 create_shape_model 的 AngleStart/AngleExtent 一致）
            // - NumMatches=1：只取最佳匹配（Executor 只用 result[0]），避免无谓的全图多匹配耗时
            // - SubPixel="least_squares"：亚像素插值，工业定位精度首选
            // ⚠ 历史坑：曾有参数错位（MinScore 写在第7位、NumMatches 传 0、SubPixel 传 0），
            //   NumMatches=0 违反 HALCON 约束（>=1）直接抛 #1309，表现为"匹配运算异常"。
            HOperatorSet.FindShapeModel(findImg, modelId, angleStartRad, angleExtentRad, minScore, 1, 0.5, "least_squares", 0, 0.5, out rows, out cols, out angles, out scores);

            int count = rows.Length;
            if (count == 0)
            {
                // 0 结果不等于"无从判断"：低阈值回扫给出真实最佳相似度，让调参有数字依据
                DiagnoseShapeMatch(findImg, modelId, minScore, angleStartRad, angleExtentRad);
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
            // 🌟 调试日志：一次匹配一条，自测/实拍验证/生产节点共用此算子。
            // 打印 MinScore、图像尺寸与全部候选的分数/位姿/角度——分数异常时凭此定位。
            try
            {
                HOperatorSet.GetImageSize(findImg, out HTuple iw, out HTuple ih);
                if (count == 0)
                {
                    LogBus.Info(nameof(TemplateMatchTool),
                        $"Shape匹配: MinScore={minScore:F2} 图像{iw.I}x{ih.I} → 0 命中（低于门槛，真实最佳分见上方诊断回扫）");
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) sb.Append(" | ");
                        sb.Append($"#{i} Score={scores[i].D:F3} @({rows[i].D:F1},{cols[i].D:F1}) {angles[i].D / Math.PI * 180:F2}°");
                    }
                    LogBus.Info(nameof(TemplateMatchTool),
                        $"Shape匹配: MinScore={minScore:F2} 图像{iw.I}x{ih.I} → 命中{count}个 {sb}");
                }
            }
            catch { /* 日志失败不影响匹配结果 */ }
            return Result<TemplateMatchResult[]>.Ok(resultArr);
        }

        /// <summary>
        /// 生成"贴合匹配结果"的模板轮廓（XLD）——把模板形状可视化画到现场图上：
        /// get_shape_model_contours 取出模型特征轮廓（模型坐标系，以参考点为原点，
        /// 参考点即创建模板时 ROI 域的中心），再 vector_angle_to_rigid 计算刚体变换
        /// （平移 Row/Col + 旋转角度），affine_trans_contour_xld 把轮廓变换到匹配位置。
        /// 目标有角度和偏移时轮廓会跟着旋转平移，肉眼直接可见"模板贴没贴合上"。
        /// </summary>
        /// <param name="modelId">模板句柄（ReadShapeModel/CreateShapeModel 返回）</param>
        /// <param name="match">匹配结果（像素坐标 + 角度制°）</param>
        /// <returns>变换后的轮廓 HObject，所有权归调用方（提交预览显示或自行 Dispose）</returns>
        public static Result<HObject> CreateShapeMatchContour(HTuple modelId, TemplateMatchResult match)
        {
            try
            {
                // level=1：最高分辨率层的模型轮廓（level 越高轮廓越粗糙，且按 2^(level-1) 缩小）
                HOperatorSet.GetShapeModelContours(out HObject contours, modelId, 1);
                if (contours == null || !contours.IsInitialized() || contours.CountObj() == 0)
                {
                    contours?.Dispose();
                    return Result<HObject>.Fail("模板特征轮廓为空");
                }

                try
                {
                    double angleRad = match.RotateDegree / 180 * Math.PI;
                    // 刚体变换：模型参考点(0,0)@0° → 匹配位置(Row,Col)@匹配角度
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, match.PixelRow, match.PixelCol, angleRad, out HTuple homMat);
                    HOperatorSet.AffineTransContourXld(contours, out HObject transformed, homMat);
                    return Result<HObject>.Ok(transformed);
                }
                finally
                {
                    contours.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "生成模板匹配贴合轮廓失败", ex);
                return Result<HObject>.Fail("模板贴合轮廓生成异常", -1, ex);
            }
        }

        /// <summary>
        /// 回读形状模板参考点偏移（诊断/验证锚点用；模型系偏移，相对默认参考点=学习域重心）。
        /// </summary>
        public static Result<(double Row, double Col)> GetShapeModelOrigin(HTuple modelId)
        {
            try
            {
                HOperatorSet.GetShapeModelOrigin(modelId, out HTuple r, out HTuple c);
                return Result<(double, double)>.Ok((r.D, c.D));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "读取模板参考点失败", ex);
                return Result<(double, double)>.Fail("模板参考点读取异常", -1, ex);
            }
        }

        /// <summary>释放模板内存，用完必须销毁</summary>
        public static Result ClearShapeModel(HTuple modelId)
        {
            try
            {
                HOperatorSet.ClearShapeModel(modelId);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "销毁模板失败", ex);
                return Result.Fail("模板释放异常", -1, ex);
            }
        }

        /// <summary>
        /// 保存形状模板到文件（.shm）。模板管理界面"创建模板"后调用，实现模板持久化。
        /// </summary>
        public static Result WriteShapeModel(HTuple modelId, string filePath)
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                HOperatorSet.WriteShapeModel(modelId, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "保存形状模板文件失败", ex);
                return Result.Fail("模板文件保存异常", -1, ex);
            }
        }

        /// <summary>
        /// 从文件加载形状模板（.shm），返回内存句柄。运行时匹配前调用，
        /// 使用完毕必须 ClearShapeModel 释放。
        /// </summary>
        public static Result<HTuple> ReadShapeModel(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                    return Result<HTuple>.Fail("形状模板文件不存在: " + filePath);
                HTuple modelId;
                HOperatorSet.ReadShapeModel(filePath, out modelId);
                return Result<HTuple>.Ok(modelId);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "加载形状模板文件失败", ex);
                return Result<HTuple>.Fail("模板文件加载异常", -1, ex);
            }
        }

        /// <summary>
        /// 0 结果诊断回扫：用极低阈值（0.10）+ 关闭亚像素重跑一次匹配，报告当前搜索图与模板的
        /// 真实最佳相似度与位置——把"0 结果"变成可量化的差距数字：
        /// - 最佳分接近设定 MinScore → 只需调低节点 MinScore 即可命中；
        /// - 最佳分很低（&lt;0.3）→ 模板与现场差异过大（光照/位置/尺度/极性反转），需重建模板。
        /// 仅在常规搜索 0 结果时触发，正常命中路径零开销；诊断自身异常不影响主流程。
        /// </summary>
        private static void DiagnoseShapeMatch(HObject findImg, HTuple modelId, double minScore, double angleStartRad = 0, double angleExtentRad = 0)
        {
            try
            {
                // Greediness 0.7（原 0.9）：贪心度高会提前剪枝，真实最佳分被低估——
                // 诊断回扫的使命就是报告"真实差距数字"，慢一点换准确（仅 0 命中时才触发）。
                HOperatorSet.FindShapeModel(findImg, modelId, angleStartRad, angleExtentRad, 0.1, 5, 0.5, "none", 0, 0.7,
                    out HTuple rows, out HTuple cols, out HTuple angles, out HTuple scores);

                if (scores.Length == 0)
                {
                    LogBus.Warn(nameof(TemplateMatchTool),
                        "诊断回扫（MinScore=0.10）：全图无任何候选——模板与现场图像差异过大（光照/位置/尺度/极性反转），建议用当前现场图重新框选创建模板。");
                    return;
                }

                // find_shape_model 结果按分数降序，取前 5 个拼明细
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < scores.Length; i++)
                    sb.Append($"#{i + 1} Score={scores[i].D:F3} @({rows[i].D:F0},{cols[i].D:F0}) 角度{angles[i].D / Math.PI * 180:F1}°  ");

                double best = scores[0].D;
                if (best >= minScore * 0.8)
                {
                    LogBus.Warn(nameof(TemplateMatchTool),
                        $"诊断回扫（MinScore=0.10）：{sb}——最佳分 {best:F2} 接近设定阈值 {minScore:F2}。" +
                        (best < 0.7
                            ? $"分数偏低（<0.7），不建议长期靠调低 MinScore 硬命中：生产节点可临时把 MinScore 降到 {Math.Max(0.1, best - 0.05):F2} 以下，但模板验证场景说明模板区分度不足，建议重建模板（紧 ROI）。"
                            : $"生产节点如需命中，把 MinScore 调低至 {Math.Max(0.1, best - 0.05):F2} 以下即可。"));
                }
                else
                {
                    LogBus.Warn(nameof(TemplateMatchTool),
                        $"诊断回扫（MinScore=0.10）：{sb}——与阈值 {minScore:F2} 差距较大，模板/现场差异明显（光照、位置偏差、极性反转或 ROI 过大含背景），建议用现场图重建模板。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(TemplateMatchTool), $"诊断回扫执行失败（不影响主流程）: {ex.Message}");
            }
        }

        /// <summary>
        /// 平场校正开关（默认开启）。开启后模板创建与搜索两侧都做光照归一化。
        /// ⚠ 切换后旧模板与新搜索图特征空间不一致，必须重建模板。
        /// </summary>
        public static bool EnableFlatField { get; set; } = true;

        /// <summary>
        /// 自适应平场校正（flat-field / shading correction）：
        /// 用"图像自身的大尺度模糊"估计低频光照场，逐像素相除归一化 —— corrected = gray / blur × 150。
        ///
        /// 适用场景（2026-08-30 实测锁定）：视野光照不均（中间亮四周暗），目标从亮区挪到暗区后
        /// 刻纹对比度整体下降、弱特征跌到噪声以下——匹配分按"特征点命中率"计算，直接崩塌
        /// （证据链：三个模板 green 全部落在创建位置 ~130px 内、挪远即 0.35；旋转自测
        ///   45/90/135° 全部平稳 → 排除角度覆盖与 ROI 大小问题，唯一变量=位置带来的光照差）。
        ///
        /// 实现：缩小(×0.02) → 高斯(gauss_filter 最大掩码 11) → 放大(bilinear)，等效 ~550px 大核平滑
        /// （毫秒级），比 mean_image 直接大核快一个量级。除法消除乘性光照场（暗区整除提亮），
        /// 目标本体的刻纹对比度相对局部亮度保留 → 挪到哪里特征都"长得一样"。
        /// </summary>
        /// <param name="grayImg">单通道灰度图</param>
        /// <param name="corrected">校正后的新图（调用方负责 Dispose）</param>
        /// <returns>校正后图像；估计失败时返回原图并打警告（绝不阻断匹配主流程）</returns>
        internal static HObject ApplyFlatField(HObject grayImg, out HObject corrected)
        {
            corrected = null;
            HObject small = null, blurSmall = null, blur = null, imgReal = null, blurReal = null, ratio = null, scaled = null;
            try
            {
                HOperatorSet.GetImageSize(grayImg, out HTuple w, out HTuple h);
                // 1) 低频光照场估计：缩小→高斯→放大。0.02 缩小 + gauss11 ≈ 全图 550px 等效核，
                //    远大于目标(~400px)，保证模糊结果只含光照不含目标细节。
                //    ⚠ gauss_filter 只支持 3/5/7/9/11 五个掩码尺寸（其余值报 #3022 Wrong size of filter，
                //    2026-08-30 实测踩坑：mask=21 每次都失败回退原图，平场从未生效）。
                HOperatorSet.ZoomImageFactor(grayImg, out small, 0.02, 0.02, "bilinear");
                HOperatorSet.GaussFilter(small, out blurSmall, 11);
                HOperatorSet.ZoomImageSize(blurSmall, out blur, w.I, h.I, "bilinear");

                // 光照场动态范围量化：min/max 直接回答"视野到底有多不均"（2:1 以上必做校正）
                try
                {
                    HOperatorSet.MinMaxGray(blurSmall, null, 0, out HTuple lmin, out HTuple lmax, out HTuple lrange);
                    double lminD = lmin.D, lmaxD = lmax.D;
                    if (lminD > 1)
                    {
                        LogBus.Info(nameof(TemplateMatchTool),
                            $"平场校正: 光照场 {lminD:F0}~{lmaxD:F0}（动态范围 {lmaxD / lminD:F2}:1，已逐像素归一化）");
                    }
                }
                catch { /* 统计失败不影响校正 */ }

                // 2) correct = gray / blur × 150（byte 截断）：除法归一化乘性光照，×150 让商
                //    (~0.8-1.3) 落进 byte 有效量程，刻纹对比度映射到 ~50-190 灰度区间。
                HOperatorSet.ConvertImageType(grayImg, out imgReal, "real");
                HOperatorSet.ConvertImageType(blur, out blurReal, "real");
                HOperatorSet.DivImage(imgReal, blurReal, out ratio, 1, 0);
                HOperatorSet.ScaleImage(ratio, out scaled, 150, 0);
                HOperatorSet.ConvertImageType(scaled, out corrected, "byte");
                return corrected;
            }
            catch (Exception ex)
            {
                // 校正是"锦上添花"而非必需：失败回退原图，匹配照常进行（只是光照敏感问题依旧）
                LogBus.Warn(nameof(TemplateMatchTool), $"平场校正失败，本次使用原始图像: {ex.Message}");
                corrected?.Dispose();
                corrected = null;
                return grayImg;
            }
            finally
            {
                small?.Dispose();
                blurSmall?.Dispose();
                blur?.Dispose();
                imgReal?.Dispose();
                blurReal?.Dispose();
                ratio?.Dispose();
                scaled?.Dispose();
            }
        }

        /// <summary>
        /// 通道统一辅助：shape/ncc 匹配算子要求单通道灰度图。
        /// 相机 BGR24 采集 → HImage 为 3 通道彩色，直接传入匹配算子会用错通道特征 → 0 分。
        /// 多通道图转灰度并返回新对象（out gray 承载，调用方负责 Dispose）；
        /// 已是单通道则原样返回，out gray = null。
        /// 转灰度动作统一在此打日志（含通道数与转换方式），供 0 分场景追溯"输入是否被自动灰度化"。
        /// </summary>
        internal static HObject EnsureGray(HObject img, out HObject gray)
        {
            gray = null;
            HOperatorSet.CountChannels(img, out HTuple channels);
            if (channels.I > 1)
            {
                HOperatorSet.Rgb1ToGray(img, out gray);
                LogBus.Info(nameof(TemplateMatchTool),
                    $"输入图像为 {channels.I} 通道彩色图，已按标准灰度化（0.299R+0.587G+0.114B）自动转单通道——匹配算子要求灰度图，模板/搜索两侧保持同一特征空间。");
                return gray;
            }
            return img;
        }
    }

    /// <summary>单条模板匹配结果实体</summary>
    public class TemplateMatchResult
    {
        /// <summary>匹配中心行像素Y</summary>
        public double PixelRow { get; set; }
        /// <summary>匹配中心列像素X</summary>
        public double PixelCol { get; set; }
        /// <summary>旋转角度 角度制°</summary>
        public double RotateDegree { get; set; }
        /// <summary>匹配相似度 0~1</summary>
        public double Score { get; set; }
    }
}