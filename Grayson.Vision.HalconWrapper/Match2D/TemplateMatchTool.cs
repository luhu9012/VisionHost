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
        /// <returns>模板句柄（HTuple，HHandle 元素）。HALCON 算子要求句柄参数必须是 handle 类型，
        /// 解包成 long 数字再传回会报 #1201 Wrong type of control parameter，故全程用 HTuple 承载。</returns>
        public static Result<HTuple> CreateShapeModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd)
        {
            if (templateImage == null || !templateImage.IsInitialized())
                return Result<HTuple>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                // 截取ROI区域创建模板
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(templateImage, roiRect, out roiImg);
                    guard.Register(roiImg);

                    // ⚠ 通道统一：create_shape_model 要求单通道灰度图。相机 BGR24 彩色图直接传入
                    //   会报错或用错通道，模板特征与现场搜索不在同一空间 → 匹配 0 分。
                    HObject roiGray;
                    HObject modelImg = EnsureGray(roiImg, out roiGray);
                    if (roiGray != null)
                        guard.Register(roiGray);

                    // 创建形状模板，角度转弧度
                    HOperatorSet.CreateShapeModel(modelImg, 4, angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "auto", "use_polarity", 30, 0, out modelId);
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
        /// <returns>匹配结果集合：坐标、角度、分数</returns>
        public static Result<TemplateMatchResult[]> FindShapeModel(HObject searchImage, HTuple modelId, double minScore = 0.7)
        {
            if (searchImage == null || !searchImage.IsInitialized())
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            HObject gray = null;
            try
            {
                // ⚠ 通道统一：find_shape_model 要求单通道灰度图（EnsureGray 内部自动转灰度并打日志）
                HObject findImg = EnsureGray(searchImage, out gray);

                HTuple rows, cols, angles, scores;
                // find_shape_model 签名：AngleStart, AngleExtent, MinScore, NumMatches, MaxOverlap, SubPixel, NumLevels, Greediness
                // - AngleStart/AngleExtent 传 0 → 使用模板创建时内建的角度范围（与 create_shape_model 的 AngleStart/AngleExtent 一致）
                // - NumMatches=1：只取最佳匹配（Executor 只用 result[0]），避免无谓的全图多匹配耗时
                // - SubPixel="least_squares"：亚像素插值，工业定位精度首选
                // ⚠ 历史坑：曾有参数错位（MinScore 写在第7位、NumMatches 传 0、SubPixel 传 0），
                //   NumMatches=0 违反 HALCON 约束（>=1）直接抛 #1309，表现为"匹配运算异常"。
                HOperatorSet.FindShapeModel(findImg, modelId, 0, 0, minScore, 1, 0.5, "least_squares", 0, 0.5, out rows, out cols, out angles, out scores);

                int count = rows.Length;
                if (count == 0)
                {
                    // 0 结果不等于"无从判断"：低阈值回扫给出真实最佳相似度，让调参有数字依据
                    DiagnoseShapeMatch(findImg, modelId, minScore);
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
                return Result<TemplateMatchResult[]>.Ok(resultArr);
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(TemplateMatchTool), "模板匹配查找失败", ex);
                return Result<TemplateMatchResult[]>.Fail("匹配运算异常", -1, ex);
            }
            finally
            {
                gray?.Dispose();
            }
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
        private static void DiagnoseShapeMatch(HObject findImg, HTuple modelId, double minScore)
        {
            try
            {
                HOperatorSet.FindShapeModel(findImg, modelId, 0, 0, 0.1, 5, 0.5, "none", 0, 0.9,
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
                        $"诊断回扫（MinScore=0.10）：{sb}——最佳分已接近设定阈值 {minScore:F2}，把节点 MinScore 调低至 {Math.Max(0.1, best - 0.05):F2} 以下即可命中。");
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