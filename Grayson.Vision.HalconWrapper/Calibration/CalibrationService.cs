using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using HalconDotNet;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    public class CalibrationService : ICalibrationService
    {
        /// <summary>
        /// 标定显示上下文（HDevelop 式场景绘制）。
        /// 非空时，特征提取走一步画一步：每个算子执行后立即把结果提交到视图场景
        /// （ROI 搜索框 / 候选区域 / 亚像素轮廓 / 拟合圆 / 特征点十字 / 文字标注），
        /// 让操作员实时看到"特征在哪、怎么找到的"；缩放/平移/拖动后由显示层整体重放。
        /// 生命周期约定见 Submit/SubmitRegion/BeginScene 的注释。
        /// </summary>
        public ICalibrationDisplayContext DisplayContext { get; set; }

        /// <summary>
        /// 调试开关：是否把特征提取的中间过程结果（阈值分割区域 / 连通域 / 骨架 / 骨架轮廓等）
        /// 叠加绘制到视图窗口。默认 true 便于现场调试；
        /// 置 false 即可一键关闭全部过程层，最终结果层（ROI / 拟合圆 / 拟合直线 /
        /// 中心十字 / 文字标注）不受影响。
        /// </summary>
        public bool DebugDrawProcessEnabled { get; set; } = true;

        /// <summary>
        /// 特征提取算子参数（阈值/圆度/面积/搜索半径/亚像素阈值等）。
        /// 默认值与改造前硬编码参数一致；标定向导"特征配置"步骤可实时调节。
        /// </summary>
        public FeatureExtractOptions ExtractOptions { get; set; } = new FeatureExtractOptions();

        #region 场景式逐步上屏（HDevelop 语义：算子执行一步、结果上屏一步）

        /// <summary>显示上下文是否可用（已注入且窗口就绪）</summary>
        private bool DisplayReady
        {
            get
            {
                var ctx = DisplayContext;
                return ctx != null && ctx.IsReady;
            }
        }

        /// <summary>
        /// 开始新场景：清空旧场景（显示层释放其中托管对象）并登记底图（借用，不转移所有权）。
        /// 每次特征提取的第一步；此后每个算子的结果通过 Submit* 系列立即上屏。
        /// 上下文不可用时静默跳过，提取主流程不受影响。
        /// </summary>
        private void BeginScene(HObject baseImage)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.BeginScene();
                if (baseImage != null && baseImage.IsInitialized())
                {
                    ctx.AddBorrowed(baseImage);
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"场景初始化失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>
        /// 把算子产生的 HObject 提交到标定视图场景（提交即所有权转移）。
        /// 提交成功后引用置 null —— 调用方 finally 的 DisposeAndNull 自动跳过，不会双重释放；
        /// 上下文未注入 / 窗口未就绪 / 提交异常时跳过，对象仍归调用方 finally 兜底释放。
        /// </summary>
        private void Submit(ref HObject obj, string color = null, int lineWidth = 1)
        {
            var ctx = DisplayContext;
            if (obj == null || ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.Add(obj, color, lineWidth);
                obj = null; // 所有权已移交显示层场景
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"场景对象提交失败（已忽略，对象仍由 finally 释放）: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交 region 的"显示副本"：算子执行后立即上屏，原对象继续参与后续算子运算。
        /// 有偏移时用 MoveRegion 平移到全图坐标（副本即平移结果），无偏移时 CopyObj 快照。
        /// 原对象生命周期不受影响（finally 统一释放）——算子代码零所有权心智负担。
        /// </summary>
        private void SubmitRegion(HObject source, double rowOffset, double colOffset, string color, int lineWidth)
        {
            if (!DisplayReady || source == null || !source.IsInitialized())
            {
                return;
            }
            HObject display = null;
            try
            {
                if (Math.Abs(rowOffset) < 1e-9 && Math.Abs(colOffset) < 1e-9)
                {
                    HOperatorSet.CopyObj(source, out display, 1, 1);
                }
                else
                {
                    HOperatorSet.MoveRegion(source, out display, rowOffset, colOffset);
                }
                Submit(ref display, color, lineWidth);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"过程层显示副本提交失败（已忽略）: {ex.Message}");
            }
            finally
            {
                display?.Dispose(); // Submit 未接收所有权时（未就绪/异常）兜底释放副本
            }
        }

        /// <summary>
        /// 提交 XLD 轮廓的"显示副本"（ROI 局部坐标平移到全图坐标；Halcon 无 MoveXld，
        /// 有偏移时用 HomMat2dTranslate + AffineTransContourXld）。语义同 SubmitRegion。
        /// </summary>
        private void SubmitXld(HObject source, double rowOffset, double colOffset, string color, int lineWidth)
        {
            if (!DisplayReady || source == null || !source.IsInitialized())
            {
                return;
            }
            HObject display = null;
            try
            {
                if (Math.Abs(rowOffset) < 1e-9 && Math.Abs(colOffset) < 1e-9)
                {
                    HOperatorSet.CopyObj(source, out display, 1, 1);
                }
                else
                {
                    HOperatorSet.HomMat2dIdentity(out HTuple homMat2d);
                    HOperatorSet.HomMat2dTranslate(homMat2d, rowOffset, colOffset, out homMat2d);
                    HOperatorSet.AffineTransContourXld(source, out display, homMat2d);
                }
                Submit(ref display, color, lineWidth);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"XLD 显示副本提交失败（已忽略）: {ex.Message}");
            }
            finally
            {
                display?.Dispose();
            }
        }

        /// <summary>提交文本标注（image 坐标系，跟随缩放平移；失败仅记日志）</summary>
        private void SubmitText(string text, double row, double col, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddText(text, row, col, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"文本标注提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>提交十字标记（image 坐标系；失败仅记日志）</summary>
        private void SubmitCross(double row, double col, double size, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddCross(row, col, size, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"十字标记提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>提交圆标记（image 坐标系；失败仅记日志）</summary>
        private void SubmitCircle(double row, double col, double radius, string color)
        {
            var ctx = DisplayContext;
            if (ctx == null || !ctx.IsReady)
            {
                return;
            }
            try
            {
                ctx.AddCircle(row, col, radius, color);
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"圆标记提交失败（已忽略）: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交"识别结果"醒目标记（image 坐标系）：黑描边绿十字 + 双色圆环 + 阴影文字。
        /// 黑色描边保证在亮背景（中心亮）与暗背景（边缘暗）上都清晰可见；
        /// 识别错点（伪特征/反光/暗角）是标定 RMS 过大最常见的根因，
        /// 醒目标记供操作员逐点肉眼核对检测中心是否落在真实 Mark 上。
        /// </summary>
        /// <param name="row">识别中心行（全图像素坐标）</param>
        /// <param name="col">识别中心列（全图像素坐标）</param>
        /// <param name="label">随标记显示的说明文字（点号+像素坐标）</param>
        private void SubmitResultMarker(double row, double col, string label)
        {
            // 十字：黑色大十字打底 + 绿色小十字叠上，形成黑描边效果（两色对比任何底色可见）
            SubmitCross(row, col, 100, "black");
            SubmitCross(row, col, 80, "green");
            // 圆环：黑外圈 + 绿内圈，把识别中心圈出来（尺寸远大于 Mark 本体，一眼定位）
            SubmitCircle(row, col, 70, "black");
            SubmitCircle(row, col, 65, "green");
            // 文字：黑色阴影偏移 2px + 黄色前景，避免亮背景上黄字不可读
            double textRow = Math.Max(0, row - 90);
            double textCol = Math.Max(10, col + 45);
            SubmitText(label, textRow + 2, textCol + 2, "black");
            SubmitText(label, textRow, textCol, "yellow");
        }

        #endregion

        /// <summary>
        /// 安全统计对象数量（调试文本用）：null / 未初始化 / 算子异常一律返回 0，
        /// 避免异步绘制闭包中对可能缺失或已释放的中间对象调用 CountObj 抛 HALCON #4056。
        /// </summary>
        private static int SafeCount(HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return 0;
            }
            try
            {
                HOperatorSet.CountObj(obj, out HTuple count);
                return count.I;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 统一解析图像句柄：支持直接传 HObject/HImage，也支持传 IRenderImage 包装（取其 NativeHandle）。
        /// 上层 UI（如 WpfUI）受"仅 HalconWrapper/HalconWrapper.Wpf 可引用 halcondotnet"强约束，
        /// 无法直接持有 HObject，只能传 IRenderImage，这里负责解包出 HObject 供 Halcon 算子使用。
        /// </summary>
        private static HObject ResolveHObject(object imageHandle)
        {
            if (imageHandle == null)
            {
                return null;
            }
            if (imageHandle is HObject hObject)
            {
                return hObject;
            }
            if (imageHandle is IRenderImage renderImage)
            {
                return renderImage.NativeHandle as HObject;
            }
            return null;
        }

        /// <summary>
        /// 彩色图像转单通道灰度（阈值分割 / ThresholdSubPix / 骨架化等算子均要求单通道输入）。
        /// 输入已是单通道时返回 null（调用方直接用原图，零开销）；
        /// 3 通道走 Rgb1ToGray（BT.601 加权），其余非常规通道数兜底取第 1 通道。
        /// 返回的新图像归调用方所有，须在 finally 中释放。
        /// </summary>
        private static HObject ToGrayIfNeeded(HObject image)
        {
            try
            {
                HOperatorSet.CountChannels(image, out HTuple channels);
                if (channels.I == 1)
                {
                    return null;
                }
                if (channels.I == 3)
                {
                    HOperatorSet.Rgb1ToGray(image, out HObject gray);
                    return gray;
                }
                LogBus.Warn(nameof(CalibrationService),
                    $"输入图像为 {channels.I} 通道（非 1/3 通道），已兜底取第 1 通道参与算子运算。");
                HOperatorSet.AccessChannel(image, out HObject firstChannel, 1);
                return firstChannel;
            }
            catch (Exception ex)
            {
                LogBus.Warn(nameof(CalibrationService), $"通道数检查失败（按单通道继续）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 参考 Mark 半径（首个成功识别的圆 Mark 拟合半径）。
        /// 走位采样中 Mark 是同一个物理特征，半径恒定；反光点/暗角等伪特征半径往往不同，
        /// 用它过滤多候选比"离期望位置最近"更可靠——尤其期望位置不可用（全图搜索）时。
        /// </summary>
        private double _referenceMarkRadius;

        /// <summary>重置参考 Mark 半径（重新进入标定步骤 / 更换 Mark 板时调用）</summary>
        public void ResetMarkReference()
        {
            _referenceMarkRadius = 0;
        }

        /// <summary>
        /// 圆 Mark 多候选择优：先排除半径偏离参考 Mark 超过 40% 的伪特征，
        /// 再按离期望位置最近选择；无期望位置（全图搜索）时选半径最接近参考者。
        /// 无参考半径时退化为仅按期望位置择近（即 SelectClosestIndex 语义）。
        /// </summary>
        /// <param name="radiusOf">候选 i 的等效半径（XLD 拟合半径 / 区域面积等效半径）</param>
        private int SelectBestCircleIndex(HTuple row, HTuple col, Func<int, double> radiusOf, double expectedPx, double expectedPy)
        {
            int n = row.Length;
            if (n <= 1)
            {
                return 0;
            }
            bool hasExpected = expectedPx > 0 && expectedPy > 0;
            bool hasRef = _referenceMarkRadius > 1.0;
            if (!hasExpected && !hasRef)
            {
                return 0; // 两者皆无：保留旧语义（取第一个）
            }

            int bestIdx = -1;
            double bestScore = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double r = radiusOf(i);
                if (hasRef && _referenceMarkRadius > 1.0 &&
                    Math.Abs(r - _referenceMarkRadius) > _referenceMarkRadius * 0.4)
                {
                    continue; // 半径与参考 Mark 偏差>40% → 判定伪特征，直接排除
                }
                double score = hasExpected
                    ? DistanceSq(col[i].D - expectedPx, row[i].D - expectedPy)
                    : DistanceSq(r - _referenceMarkRadius, 0);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }

            // 全部候选被半径过滤（Mark 半出视野/遮挡导致半径畸变）→ 退化为按期望位置择近
            if (bestIdx < 0)
            {
                return SelectClosestIndex(row, col, expectedPx, expectedPy);
            }
            return bestIdx;
        }

        /// <summary>平方距离（避免开方）</summary>
        private static double DistanceSq(double dx, double dy)
        {
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 多候选时选离期望位置最近的索引。
        /// expectedPx/expectedPy <= 0 时（无 seed / 全图搜索）返回 0（取第一个）。
        /// 这是标定误检的核心修复：之前代码取 col[0]/row[0]（第一个候选），
        /// 当阈值分割到多个区域时，第一个很可能不是 Mark 而是伪特征。
        /// </summary>
        private static int SelectClosestIndex(HTuple row, HTuple col, double expectedPx, double expectedPy)
        {
            if (row.Length <= 1 || expectedPx <= 0 || expectedPy <= 0)
            {
                return 0;
            }
            int bestIdx = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < row.Length; i++)
            {
                double dx = col[i].D - expectedPx;
                double dy = row[i].D - expectedPy;
                double dist = dx * dx + dy * dy;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>
        /// 角度归一化到 [-180, 180]
        /// </summary>
        private static double NormalizeAngle180(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        /// <summary>点到线段的最短距离（欧氏）</summary>
        private static double PointToSegmentDistance(double px, double py, double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-9)
            {
                return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
            }
            double t = Math.Max(0, Math.Min(1, ((px - x1) * dx + (py - y1) * dy) / lenSq));
            double cx = x1 + t * dx;
            double cy = y1 + t * dy;
            return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        }

        public Result<CalibrationResult> CalcNinePointHomMat(double[] pixelXList, double[] pixelYList, double[] worldXList, double[] worldYList)
        {
            try
            {
                // ── 诊断日志：拟合前逐点打印原始数据，便于排查"哪个点像素坐标不可靠" ──
                int n = pixelXList.Length;
                LogBus.Info(nameof(CalibrationService), $"═══ 九点标定拟合开始，共 {n} 个点 ═══");
                for (int i = 0; i < n; i++)
                {
                    LogBus.Info(nameof(CalibrationService),
                        $"  点{i + 1}: Pixel({pixelXList[i]:F2}, {pixelYList[i]:F2})  World({worldXList[i]:F3}, {worldYList[i]:F3})");
                }

                // ── 数据合理性检查：像素坐标不能全相同（Mark 没动/识别始终命中同一处） ──
                bool allPxSame = true, allPySame = true;
                for (int i = 1; i < n; i++)
                {
                    if (Math.Abs(pixelXList[i] - pixelXList[0]) > 0.1) allPxSame = false;
                    if (Math.Abs(pixelYList[i] - pixelYList[0]) > 0.1) allPySame = false;
                }
                if (allPxSame && allPySame)
                {
                    LogBus.Error(nameof(CalibrationService),
                        "⚠ 所有点的像素坐标几乎相同——Mark 识别可能始终命中同一位置或未真正走位！RMS 必然异常。");
                }

                HTuple px = new HTuple(pixelXList);
                HTuple py = new HTuple(pixelYList);
                HTuple wx = new HTuple(worldXList);
                HTuple wy = new HTuple(worldYList);

                var res = Calib2DTool.CalcNinePointHomMat(px, py, wx, wy);
                if (!res.Success) return Result<CalibrationResult>.Fail(res.Message);

                // 计算 RMS 误差 + 逐点残差诊断
                double rms = 0;
                try
                {
                    HOperatorSet.AffineTransPoint2d(res.Data, px, py, out HTuple calcWx, out HTuple calcWy);
                    double sumSquareErr = 0;
                    double maxErr = 0;
                    int maxErrIdx = -1;
                    for (int i = 0; i < n; i++)
                    {
                        double errX = calcWx[i].D - worldXList[i];
                        double errY = calcWy[i].D - worldYList[i];
                        double err2D = Math.Sqrt(errX * errX + errY * errY);
                        sumSquareErr += (errX * errX + errY * errY);
                        LogBus.Info(nameof(CalibrationService),
                            $"  残差 点{i + 1}: ΔX={errX:+0.###;-0.###;0}mm  ΔY={errY:+0.###;-0.###;0}mm  |Δ|={err2D:F3}mm");
                        if (err2D > maxErr) { maxErr = err2D; maxErrIdx = i + 1; }
                    }
                    rms = Math.Sqrt(sumSquareErr / n);
                    LogBus.Info(nameof(CalibrationService),
                        $"═══ RMS={rms:F4}mm  最大残差点=#{maxErrIdx}({maxErr:F3}mm) ═══");
                }
                catch { }

                string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hommat_{Guid.NewGuid():N}.tup");
                var save = Calib2DTool.SaveHomMatToFile(res.Data, tmp);
                if (!save.Success) return Result<CalibrationResult>.Fail("矩阵保存失败: " + save.Message);

                return Result<CalibrationResult>.Ok(new CalibrationResult
                {
                    SavedFilePath = tmp,
                    RmsError = rms
                });
            }
            catch (Exception ex)
            {
                return Result<CalibrationResult>.Fail("标定计算异常: " + ex.Message, -1, ex);
            }
        }

        public Result SaveHomMatFile(string sourceFilePath, string destFilePath)
        {
            try
            {
                System.IO.File.Copy(sourceFilePath, destFilePath, true);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                return Result.Fail("保存文件失败: " + ex.Message, -1, ex);
            }
        }

        public Result<FixtureData> CreateFixture(double refRow, double refCol, double refAngle, double curRow, double curCol, double curAngle)
        {
            return FixtureTool.CreateFixture(refRow, refCol, refAngle, curRow, curCol, curAngle);
        }

        public Result<DetectionResult> DetectCalibrationPoints(string imageFilePath)
        {
            return Result<DetectionResult>.Fail("DetectCalibrationPoints 尚未实现，请在 HalconWrapper 中实现检测逻辑。");
        }

        public Result<(double WorldX, double WorldY)> MapPixelToWorld(string matrixFilePath, double px, double py)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                HOperatorSet.AffineTransPoint2d(loadRes.Data, px, py, out HTuple wx, out HTuple wy);
                return Result<(double, double)>.Ok((wx.D, wy.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标转换失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 物理坐标逆转换像素坐标 (World -> Pixel)
        /// </summary>
        public Result<(double PixelX, double PixelY)> MapWorldToPixel(string matrixFilePath, double wx, double wy)
        {
            var loadRes = Calib2DTool.LoadHomMatFromFile(matrixFilePath);
            if (!loadRes.Success) return Result<(double, double)>.Fail(loadRes.Message);

            try
            {
                // 计算矩阵逆矩阵 HomMat2dInvert
                HOperatorSet.HomMat2dInvert(loadRes.Data, out HTuple homMat2DInvert);

                // 使用逆矩阵执行坐标转换
                HOperatorSet.AffineTransPoint2d(homMat2DInvert, wx, wy, out HTuple px, out HTuple py);

                return Result<(double, double)>.Ok((px.D, py.D));
            }
            catch (Exception ex)
            {
                return Result<(double, double)>.Fail("坐标逆转换失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 高级手眼坐标转换：考虑像素、物理旋转中心与角度补正
        /// </summary>
        public Result<(double FinalWorldX, double FinalWorldY)> MapPixelToWorldWithOffset(
            string matrixFilePath,
            double px, double py,
            double rotateAngleDeg,
            double centerWx, double centerWy,
            EyeMode eyeMode,
            (double RobotX, double RobotY) currentRobotPos)
        {
            // 1. 基础仿射变换 (Px, Py -> Wx, Wy)
            var rawRes = MapPixelToWorld(matrixFilePath, px, py);
            if (!rawRes.Success) return Result<(double, double)>.Fail(rawRes.Message);

            double wx = rawRes.Data.WorldX;
            double wy = rawRes.Data.WorldY;

            if (eyeMode == EyeMode.EyeInHand)
            {
                // 眼在手上：叠加机器人当前位置
                wx += currentRobotPos.RobotX;
                wy += currentRobotPos.RobotY;
            }

            // 2. 如果存在旋转角度补正
            if (Math.Abs(rotateAngleDeg) > 0.0001)
            {
                double rad = rotateAngleDeg * Math.PI / 180.0;
                double dx = wx - centerWx;
                double dy = wy - centerWy;

                double rotatedX = dx * Math.Cos(rad) - dy * Math.Sin(rad) + centerWx;
                double rotatedY = dx * Math.Sin(rad) + dy * Math.Cos(rad) + centerWy;

                return Result<(double, double)>.Ok((rotatedX, rotatedY));
            }

            return Result<(double, double)>.Ok((wx, wy));
        }

        /// <summary>
        /// 从磁盘配置文件目录加载所有已创建的标定 Profile 方案
        /// </summary>
        /// // 标定方案 Json 配置默认存储路径
        private readonly string _configDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Calibrations");
        public CalibrationService()
        {
            if (!Directory.Exists(_configDirectory))
            {
                Directory.CreateDirectory(_configDirectory);
            }
        }
        public Result<List<CalibrationProfile>> GetAllProfiles()
        {
            try
            {
                var list = new List<CalibrationProfile>();
                var jsonFiles = Directory.GetFiles(_configDirectory, "*.json");

                foreach (var file in jsonFiles)
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonConvert.DeserializeObject<CalibrationProfile>(json);
                    if (profile != null)
                    {
                        list.Add(profile);
                    }
                }

                return Result<List<CalibrationProfile>>.Ok(list);
            }
            catch (Exception ex)
            {
                return Result<List<CalibrationProfile>>.Fail("获取标定方案失败: " + ex.Message, -1, ex);
            }
        }


        /// <summary>
        /// 【九点手眼标定】提取当前工位图像中 Mark 标记点的精准亚像素坐标。
        /// 提取过程走一步画一步（HDevelop 语义）：每个算子执行后把结果立即提交到
        /// DisplayContext 场景（ROI 搜索框 / 阈值分割 / 连通域 / 候选区域 /
        /// 亚像素轮廓 / 拟合圆 / 特征点十字 / 文字标注），供操作员实时确认识别是否正确。
        /// 坐标系说明：HALCON 的 ReduceDomain 不改变图像坐标系——对裁剪域图像做
        /// Threshold / ThresholdSubPix / AreaCenter 得到的坐标本身就是全图坐标，
        /// 本方法直接返回全图坐标，不做任何平移修正（加了反而双重偏移，Mark 会找不准）。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePoint(
            object imageHandle,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            // 1. 严格校验相机图像句柄是否有效（兼容 HObject 直传与 IRenderImage 包装）
            HObject hImage = ResolveHObject(imageHandle);
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 提取失败：当前图像缓冲区无效或相机未成功取图。");
            }

            HObject grayImage = null;
            HObject roi = null;
            HObject imageReduced = null;
            HObject thresholdRegion = null;
            HObject connectedRegions = null;
            HObject selectedRegions = null;
            HObject subPixelEdges = null;
            HObject selectedXld = null;

            double searchRadius = ExtractOptions.SearchRadius;
            double fitRadius = 0;

            try
            {
                HOperatorSet.GetImageSize(hImage, out HTuple imgWidth, out HTuple imgHeight);

                // 场景第一步：清空旧场景并登记底图（借用）。此后每个算子的结果立即上屏，
                // 走一步画一步（HDevelop 语义），缩放/平移/拖动后由显示层整体重放。
                // 底图始终登记原图（彩色图保持彩色显示，便于人工确认）。
                BeginScene(hImage);

                // 1.5 彩色图像先转单通道灰度：threshold / threshold_sub_pix 等分割算子
                // 均要求单通道输入；单通道相机图零开销直通。
                grayImage = ToGrayIfNeeded(hImage);
                HObject processImage = grayImage ?? hImage;

                // 2. 构造动态检测 ROI（有参考位置时开辟局部搜索框，否则全图搜索）。
                //    forceFullImage：降级全图重试时保留 expected 用于多候选择近，
                //    只跳过 ROI 裁剪——否则全图多候选时取第一个，极易选中伪特征。
                bool useRoi = expectedPx > 0 && expectedPy > 0 && !forceFullImage;
                if (useRoi)
                {
                    double r1 = Math.Max(0, expectedPy - searchRadius);
                    double c1 = Math.Max(0, expectedPx - searchRadius);
                    double r2 = Math.Min(imgHeight.D - 1, expectedPy + searchRadius);
                    double c2 = Math.Min(imgWidth.D - 1, expectedPx + searchRadius);

                    HOperatorSet.GenRectangle1(out roi, r1, c1, r2, c2);
                    // ROI 搜索框（绿色，全图坐标）—— 生成即上屏
                    SubmitRegion(roi, 0, 0, "green", 2);
                    // ReduceDomain 只缩小处理范围，坐标系不变：后续所有坐标均为全图坐标
                    HOperatorSet.ReduceDomain(processImage, roi, out imageReduced);
                }
                else
                {
                    // 修复 CloneObj 报错：使用 CopyImage
                    HOperatorSet.CopyImage(processImage, out imageReduced);
                }

                // 3. 图像分割与形态学筛选（每步结果立即上屏：阈值蓝 → 连通域紫 → 候选橙）
                HOperatorSet.Threshold(imageReduced, out thresholdRegion, ExtractOptions.ThresholdMin, ExtractOptions.ThresholdMax);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                    SubmitText("蓝=阈值分割 紫=连通域 绿=ROI 橙=候选 青=XLD 红=拟合圆 黄=中心",
                        0, Math.Max(10, imgWidth.D - 560), "white");
                    SubmitText($"{(grayImage != null ? "彩色已转灰度(Rgb1ToGray)  " : "")}阈值区域数:{SafeCount(thresholdRegion)}",
                        30, Math.Max(10, imgWidth.D - 560), "white");
                }

                HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                    SubmitText($"连通域数:{SafeCount(connectedRegions)}",
                        60, Math.Max(10, imgWidth.D - 560), "white");
                }

                // 筛选圆度高于 MinCircularity、面积适中的特征区域
                HOperatorSet.SelectShape(
                    connectedRegions,
                    out selectedRegions,
                    new HTuple("circularity", "area"),
                    "and",
                    new HTuple(ExtractOptions.MinCircularity, ExtractOptions.MinArea),
                    new HTuple(1.0, ExtractOptions.MaxArea));

                HOperatorSet.CountObj(selectedRegions, out HTuple matchCount);
                if (matchCount.I <= 0)
                {
                    // 3.5 光照不均兜底（中心亮边缘暗场景）：全局阈值在暗边缘处分不到 Mark →
                    // 回退局部动态阈值（MeanImage + DynThreshold，暗+亮并集）重试一次。
                    // 动态阈值只看"像素与局部均值的偏差"，对整幅亮度渐变不敏感。
                    SubmitText("全局阈值无候选 → 回退局部动态阈值(抗光照不均)...",
                        160, Math.Max(10, imgWidth.D - 560), "yellow");
                    HObject meanImage = null;
                    HObject dynDark = null;
                    HObject dynLight = null;
                    HObject dynUnion = null;
                    try
                    {
                        // 均值窗口需显著大于 Mark（约 3 倍直径），保证 Mark 整体落在"局部背景"内
                        int markDiameter = (int)Math.Ceiling(2.0 * Math.Sqrt(ExtractOptions.MaxArea / Math.PI));
                        int meanWin = Math.Max(31, markDiameter * 3 | 1);
                        HOperatorSet.MeanImage(imageReduced, out meanImage, meanWin, meanWin);
                        HOperatorSet.DynThreshold(imageReduced, meanImage, out dynDark, 8, "dark");
                        HOperatorSet.DynThreshold(imageReduced, meanImage, out dynLight, 8, "light");
                        HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn(nameof(CalibrationService), $"动态阈值兜底失败（继续走全局阈值结果）: {ex.Message}");
                    }
                    finally
                    {
                        meanImage?.Dispose();
                        dynDark?.Dispose();
                        dynLight?.Dispose();
                    }

                    if (dynUnion != null && dynUnion.IsInitialized())
                    {
                        DisposeAndNull(ref thresholdRegion);
                        thresholdRegion = dynUnion;
                        dynUnion = null;
                        if (DebugDrawProcessEnabled)
                        {
                            SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                            SubmitText($"动态阈值区域数:{SafeCount(thresholdRegion)}",
                                190, Math.Max(10, imgWidth.D - 560), "white");
                        }
                        HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                        HOperatorSet.SelectShape(
                            connectedRegions,
                            out selectedRegions,
                            new HTuple("circularity", "area"),
                            "and",
                            new HTuple(ExtractOptions.MinCircularity, ExtractOptions.MinArea),
                            new HTuple(1.0, ExtractOptions.MaxArea));
                        HOperatorSet.CountObj(selectedRegions, out matchCount);
                    }
                    // 兜底结果未被采用（null/未初始化/未转移）时释放，避免泄漏；
                    // 已转移到 thresholdRegion 的由外层 finally 统一释放
                    dynUnion?.Dispose();
                }

                // 候选区域（橙色，全图坐标）—— 筛选完成即上屏
                SubmitRegion(selectedRegions, 0, 0, "orange", 2);

                if (matchCount.I <= 0)
                {
                    // 失败路径：场景中已逐步上屏到当前步骤（底图/ROI/阈值/连通域/候选=0），
                    // 此处只补失败提示 —— 操作员能直接看清"阈值分割到了什么、候选为什么被筛光"
                    SubmitText($"几何筛选后候选数:0（圆度≥{ExtractOptions.MinCircularity:F2} 面积{ExtractOptions.MinArea}-{ExtractOptions.MaxArea}）",
                        100, Math.Max(10, imgWidth.D - 560), "white");
                    SubmitText($"#{pointIndex} 未识别到 Mark 点（检查光源/曝光/ROI）",
                        20, 20, "red");
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 视觉算子未识别到符合几何圆度特征的 Mark 点，请检查光源与曝光！");
                }

                // 4. 高精度亚像素轮廓拟合
                HOperatorSet.ThresholdSubPix(imageReduced, out subPixelEdges, ExtractOptions.SubPixThreshold);
                HOperatorSet.SelectShapeXld(subPixelEdges, out selectedXld, "circularity", "and", ExtractOptions.MinCircularity, 1.0);
                // 亚像素轮廓（青色，全图坐标）—— 拟合完成即上屏
                SubmitXld(selectedXld, 0, 0, "cyan", 1);
                if (DebugDrawProcessEnabled)
                {
                    SubmitText($"几何筛选后:{SafeCount(selectedRegions)}  亚像素轮廓:{SafeCount(selectedXld)}",
                        130, Math.Max(10, imgWidth.D - 560), "white");
                }

                double finalPixelX;
                double finalPixelY;

                HOperatorSet.CountObj(selectedXld, out HTuple xldCount);
                if (xldCount.I > 0)
                {
                    HOperatorSet.FitCircleContourXld(selectedXld, "algebraic", -1, 0, 0, 3, 2,
                        out HTuple row, out HTuple col, out HTuple radius, out _, out _, out _);

                    // 多候选择优：先按参考半径排除伪特征（Mark 半径恒定，反光点半径不同），
                    // 再按离期望位置最近选择；无期望位置（全图）时选半径最接近参考者。
                    // ReduceDomain 坐标系不变，行/列即全图坐标，直接使用
                    int best = SelectBestCircleIndex(row, col, i => radius[i].D, expectedPx, expectedPy);
                    finalPixelX = col[best].D;
                    finalPixelY = row[best].D;
                    fitRadius = radius[best].D;
                    // 记录参考半径（首个成功识别的 Mark，此后用于伪特征过滤）
                    if (_referenceMarkRadius <= 1.0)
                    {
                        _referenceMarkRadius = fitRadius;
                        LogBus.Info(nameof(CalibrationService), $"[标定点 #{pointIndex}] 记录参考 Mark 半径: {fitRadius:F1}px");
                    }
                }
                else
                {
                    HOperatorSet.AreaCenter(selectedRegions, out HTuple area, out HTuple row, out HTuple col);
                    // 区域面积换算等效半径参与伪特征过滤
                    int best = SelectBestCircleIndex(row, col, i => Math.Sqrt(area[i].D / Math.PI), expectedPx, expectedPy);
                    finalPixelX = col[best].D;
                    finalPixelY = row[best].D;
                    if (_referenceMarkRadius <= 1.0)
                    {
                        _referenceMarkRadius = Math.Sqrt(area[best].D / Math.PI);
                    }
                }

                // 5. 结果层上屏（image 坐标系，跟随缩放平移）：拟合圆（红）→ 醒目结果标记（黑描边绿十字+圆环+文字）
                if (fitRadius > 0.0001)
                {
                    SubmitCircle(finalPixelY, finalPixelX, fitRadius, "red");
                }
                SubmitResultMarker(finalPixelY, finalPixelX,
                    $"#{pointIndex}  Px:{finalPixelX:F1}  Py:{finalPixelY:F1}");

                return Result<(double, double)>.Ok((finalPixelX, finalPixelY));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CalibrationService), $"[标定点 #{pointIndex}] Halcon 算子执行异常: {ex.Message}");
                return Result<(double, double)>.Fail($"算子执行异常: {ex.Message}");
            }
            finally
            {
                // 场景式所有权规则：Submit* 提交的是"显示副本"（CopyObj/MoveRegion/仿射平移产物），
                // 场景托管副本、本地句柄归本方法 —— 无论成功/失败/异常路径统一在此释放，
                // 无需任何 overlayHandedOff 之类的移交标志。
                // grayImage 为彩色转灰度的临时对象（单通道输入时为 null），此处统一释放。
                DisposeAndNull(ref grayImage);
                DisposeAndNull(ref roi);
                DisposeAndNull(ref imageReduced);
                DisposeAndNull(ref thresholdRegion);
                DisposeAndNull(ref connectedRegions);
                DisposeAndNull(ref selectedRegions);
                DisposeAndNull(ref subPixelEdges);
                DisposeAndNull(ref selectedXld);
            }
        }
        /// <summary>
        /// 【R 轴旋转中心标定】在 R 轴步进旋转指定角度后，提取偏心 Mark 点在当前视场中的精准坐标。
        /// featureType 决定走圆 Mark（圆度筛选+亚像素圆拟合）还是十字 Mark（骨架+直线交叉）算法。
        /// </summary>
        /// <param name="imageHandle">Halcon 图像句柄</param>
        /// <param name="angleDeg">当前 R 轴旋转绝对/相对物理角度（用于日志审计与轨迹拟合验证）</param>
        /// <param name="featureType">标定方案配置的特征类型（圆 / 十字）</param>
        /// <param name="expectedPx">上一次标定或理论估算的位置，用于开辟 ROI</param>
        /// <param name="expectedPy">上一次标定或理论估算的位置，用于开辟 ROI</param>
        /// <returns>提取到的偏心 Mark 点像素物理坐标</returns>
        public Result<(double PixelX, double PixelY)> ExtractRotationFeaturePoint(
            object imageHandle,
            double angleDeg,
            CalibrationFeatureType featureType,
            double expectedPx = -1,
            double expectedPy = -1)
        {
            var extractResult = ExtractFeaturePointByType(imageHandle, featureType, 0, expectedPx, expectedPy);

            if (!extractResult.Success)
            {
                LogBus.Warn(nameof(CalibrationService), $"R 轴在 {angleDeg:F1}° 位置未识别到偏心 Mark 特征点。");
            }

            return extractResult;
        }

        /// <summary>
        /// 【已采集点标记叠加】把目前所有已成功采集的标定点，以紧凑绿十字标记阵列
        /// 追加绘制到当前显示场景（不清空场景、不换底图，直接叠加在刚完成的算子
        /// 过程结果之上）。
        /// 用途：九点标定第三步逐点采样时，每采完一个点调用一次——视图窗口中除了
        /// 当前点的算子过程呈现（ROI/阈值/连通域/XLD/拟合圆/醒目标记），还累计呈现
        /// 所有已采集点的像素位置：识别正确时 9 个绿十字构成与走位网格一致的规则
        /// 3x3 阵列；误检点（伪特征/反光/暗角）表现为十字重叠、缺失或阵列畸变，
        /// 操作员目视即可发现哪个像素点取错。
        /// </summary>
        /// <param name="pointIndices">已采集点编号（1~9，用于标记标签）</param>
        /// <param name="pixelXs">已采集点像素 X（列）</param>
        /// <param name="pixelYs">已采集点像素 Y（行）</param>
        public void AppendCapturedMarks(int[] pointIndices, double[] pixelXs, double[] pixelYs)
        {
            if (pointIndices == null || pixelXs == null || pixelYs == null)
            {
                return;
            }
            int n = Math.Min(pointIndices.Length, Math.Min(pixelXs.Length, pixelYs.Length));
            for (int i = 0; i < n; i++)
            {
                // 紧凑标记：黑描边小十字 + 点号（当前点的大号醒目标记由提取流程绘制，
                // 此处统一用小尺寸阵列，9 点同屏不互相遮挡）
                SubmitCross(pixelYs[i], pixelXs[i], 46, "black");
                SubmitCross(pixelYs[i], pixelXs[i], 36, "green");
                SubmitText($"#{pointIndices[i]}", Math.Max(0, pixelYs[i] - 40), pixelXs[i] + 20, "black");
                SubmitText($"#{pointIndices[i]}", Math.Max(0, pixelYs[i] - 42), pixelXs[i] + 18, "yellow");
            }
            if (n > 0)
            {
                SubmitText($"已采集 {n} 点（绿十字阵列应与走位网格一致，重叠/畸变=误检）", 160, 20, "yellow");
            }
        }

        /// <summary>
        /// 按标定方案配置的特征类型路由提取算法（九点标定第三步采样 / 旋转采样统一入口）：
        /// CircleMark → ExtractFeaturePoint（阈值分割 + 圆度筛选 + 亚像素圆拟合）；
        /// CrossMark → ExtractCrossMarkPoint（骨架 + 直线拟合 + 交叉点）。
        /// 此前第三步采样写死圆算法，用户在第二步选择十字 Mark 时预览正常、采样必然失败——
        /// 本方法保证采样与预览使用同一套算法。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePointByType(
            object imageHandle,
            CalibrationFeatureType featureType,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            switch (featureType)
            {
                case CalibrationFeatureType.CrossMark:
                    return ExtractCrossMarkPoint(imageHandle, pointIndex, expectedPx, expectedPy, forceFullImage);
                case CalibrationFeatureType.CircleMark:
                default:
                    return ExtractFeaturePoint(imageHandle, pointIndex, expectedPx, expectedPy, forceFullImage);
            }
        }

        /// <summary>
        /// 特征预览提取（第二步"特征配置"验证用）：按用户选择的特征类型在当前图像上提取特征中心，
        /// 并利用注入的 DisplayContext 视窗句柄，把识别过程与结果（底图 / 候选区域 /
        /// 拟合圆或拟合直线 / 中心十字 / 文字标注）叠加绘制到视图窗口。
        /// 圆形 Mark → 复用 ExtractFeaturePoint（阈值分割 + 圆度筛选 + 亚像素圆拟合）；
        /// 十字 Mark → ExtractCrossMarkPoint（骨架 + 直线拟合 + 交叉点，不依赖模板）。
        /// 注意：此方法只做"预览验证"，不写入任何标定点数据。
        /// </summary>
        public Result<(double PixelX, double PixelY)> ExtractFeaturePreview(object imageHandle, CalibrationFeatureType featureType)
        {
            switch (featureType)
            {
                case CalibrationFeatureType.CircleMark:
                    // pointIndex=0：预览模式编号；expectedPx/expectedPy=-1：无参考位置，全图搜索
                    return ExtractFeaturePoint(imageHandle, 0, -1, -1);
                case CalibrationFeatureType.CrossMark:
                    return ExtractCrossMarkPoint(imageHandle, 0, -1, -1);
                default:
                    return Result<(double, double)>.Fail($"不支持的特征类型: {featureType}");
            }
        }

        /// <summary>
        /// 【十字 Mark 特征提取】几何结构法检测十字中心：
        /// 1) 阈值分割（先暗后亮，兼容暗/亮十字；全局阈值均无候选时回退局部动态阈值抗光照不均）
        ///    → 连通域 → 按面积/宽高筛选候选区域；
        /// 2) 对候选区域骨架化（Skeleton），每条骨架段用 FitLineContourXld 拟合直线；
        /// 3) 找一对近似垂直（90°±30°）且交点靠近两线段的直线，交点即十字中心；
        /// 4) 找不到垂直对时兜底取最大候选区域中心。
        /// 支持 seed ROI（expectedPx/expectedPy>0 时 ReduceDomain 局部搜索；坐标系统一为全图坐标）。
        /// 检测过程通过 DisplayContext 叠加绘制：底图 → 候选区域(橙) → 拟合直线(青) → 中心十字+文字(黄)。
        /// 说明：当前为无模板几何法，适合配置步骤快速验证；如需高鲁棒模板匹配，
        /// 可扩展 CreateShapeModel/FindShapeModel（需要模板训练交互）。
        /// </summary>
        private Result<(double PixelX, double PixelY)> ExtractCrossMarkPoint(
            object imageHandle,
            int pointIndex,
            double expectedPx,
            double expectedPy,
            bool forceFullImage = false)
        {
            HObject hImage = ResolveHObject(imageHandle);
            if (hImage == null || !hImage.IsInitialized())
            {
                return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 十字 Mark 提取失败：当前图像缓冲区无效或相机未成功取图。");
            }

            HObject grayImage = null;
            HObject roi = null;
            HObject imageReduced = null;
            HObject thresholdRegion = null;
            HObject connectedRegions = null;
            HObject candidateRegions = null;
            HObject skeleton = null;
            HObject skelConnected = null;
            HObject skelSelected = null;
            HObject xldContours = null;

            try
            {
                HOperatorSet.GetImageSize(hImage, out HTuple imgWidth, out HTuple imgHeight);

                // 场景第一步：清空旧场景并登记底图（借用），此后走一步画一步
                BeginScene(hImage);

                // 彩色图像先转单通道灰度（threshold / skeleton 均要求单通道输入）；
                // 底图仍登记原图，显示层保持彩色。
                grayImage = ToGrayIfNeeded(hImage);
                HObject processImage = grayImage ?? hImage;

                // seed ROI：有参考位置时局部搜索（ReduceDomain 不改变坐标系，后续坐标均为全图坐标）；
                // forceFullImage：降级全图重试时保留 expected 用于候选选择，只跳过 ROI 裁剪
                bool useRoi = expectedPx > 0 && expectedPy > 0 && !forceFullImage;
                if (useRoi)
                {
                    double searchRadius = ExtractOptions.SearchRadius;
                    double r1 = Math.Max(0, expectedPy - searchRadius);
                    double c1 = Math.Max(0, expectedPx - searchRadius);
                    double r2 = Math.Min(imgHeight.D - 1, expectedPy + searchRadius);
                    double c2 = Math.Min(imgWidth.D - 1, expectedPx + searchRadius);
                    HOperatorSet.GenRectangle1(out roi, r1, c1, r2, c2);
                    SubmitRegion(roi, 0, 0, "green", 2);
                    HOperatorSet.ReduceDomain(processImage, roi, out imageReduced);
                    processImage = imageReduced;
                }

                // 1. 阈值分割：优先暗色 Mark（暗背景亮 Mark 场景自动切换）
                HOperatorSet.Threshold(processImage, out thresholdRegion, 0, ExtractOptions.CrossDarkThresholdMax);
                HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                HOperatorSet.SelectShape(
                    connectedRegions,
                    out candidateRegions,
                    new HTuple("area", "width", "height"),
                    "and",
                    new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                    new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                HOperatorSet.CountObj(candidateRegions, out HTuple candCount);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                    SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                    SubmitText("蓝=阈值分割 紫=连通域 绿=骨架 亮绿=骨架XLD 橙=候选 青=拟合直线 黄=中心",
                        10, Math.Max(10, imgWidth.D - 640), "white");
                    SubmitText($"{(grayImage != null ? "彩色已转灰度(Rgb1ToGray)  " : "")}阈值区域数:{SafeCount(thresholdRegion)}  连通域数:{SafeCount(connectedRegions)}",
                        40, Math.Max(10, imgWidth.D - 640), "white");
                }
                if (candCount.I <= 0)
                {
                    // 暗色无结果 → 亮色十字（暗背景）：重开场景，亮色路径重新逐步上屏
                    DisposeObjects(thresholdRegion, connectedRegions, candidateRegions);
                    thresholdRegion = connectedRegions = candidateRegions = null;
                    BeginScene(hImage);
                    HOperatorSet.Threshold(processImage, out thresholdRegion, ExtractOptions.CrossLightThresholdMin, 255);
                    HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                    HOperatorSet.SelectShape(
                        connectedRegions,
                        out candidateRegions,
                        new HTuple("area", "width", "height"),
                        "and",
                        new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                        new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                    HOperatorSet.CountObj(candidateRegions, out candCount);
                    if (DebugDrawProcessEnabled)
                    {
                        SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                        SubmitRegion(connectedRegions, 0, 0, "magenta", 1);
                        SubmitText("蓝=阈值分割(亮色Mark) 紫=连通域 橙=候选 青=拟合直线 黄=中心",
                            10, Math.Max(10, imgWidth.D - 640), "white");
                    }
                }

                if (candCount.I <= 0)
                {
                    // 1.5 光照不均兜底（中心亮边缘暗场景）：暗/亮全局阈值均无候选 →
                    // 局部动态阈值（MeanImage + DynThreshold 暗+亮并集）重试一次，
                    // 只看"像素与局部均值的偏差"，对整幅亮度渐变不敏感。
                    SubmitText("全局阈值(暗/亮)无候选 → 回退局部动态阈值(抗光照不均)...",
                        20, 20, "yellow");
                    HObject meanImage = null;
                    HObject dynDark = null;
                    HObject dynLight = null;
                    HObject dynUnion = null;
                    try
                    {
                        // 均值窗口需显著大于十字整体尺寸（约 3 倍），保证十字落在"局部背景"内
                        int meanWin = Math.Max(31, (int)ExtractOptions.CrossMaxSize * 3 | 1);
                        HOperatorSet.MeanImage(processImage, out meanImage, meanWin, meanWin);
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynDark, 8, "dark");
                        HOperatorSet.DynThreshold(processImage, meanImage, out dynLight, 8, "light");
                        HOperatorSet.Union2(dynDark, dynLight, out dynUnion);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn(nameof(CalibrationService), $"十字动态阈值兜底失败（继续走全局阈值结果）: {ex.Message}");
                    }
                    finally
                    {
                        meanImage?.Dispose();
                        dynDark?.Dispose();
                        dynLight?.Dispose();
                    }

                    if (dynUnion != null && dynUnion.IsInitialized())
                    {
                        DisposeAndNull(ref thresholdRegion);
                        thresholdRegion = dynUnion;
                        dynUnion = null;
                        if (DebugDrawProcessEnabled)
                        {
                            SubmitRegion(thresholdRegion, 0, 0, "blue", 1);
                            SubmitText($"动态阈值区域数:{SafeCount(thresholdRegion)}",
                                50, Math.Max(10, imgWidth.D - 640), "white");
                        }
                        HOperatorSet.Connection(thresholdRegion, out connectedRegions);
                        HOperatorSet.SelectShape(
                            connectedRegions,
                            out candidateRegions,
                            new HTuple("area", "width", "height"),
                            "and",
                            new HTuple(ExtractOptions.CrossMinArea, ExtractOptions.CrossMinSize, ExtractOptions.CrossMinSize),
                            new HTuple(ExtractOptions.CrossMaxArea, ExtractOptions.CrossMaxSize, ExtractOptions.CrossMaxSize));
                        HOperatorSet.CountObj(candidateRegions, out candCount);
                    }
                    // 兜底结果未被采用时释放；已转移到 thresholdRegion 的由外层 finally 统一释放
                    dynUnion?.Dispose();
                }

                if (candCount.I <= 0)
                {
                    // 失败路径：场景中已逐步上屏阈值/连通域结果，此处只补失败提示
                    SubmitText($"#{pointIndex} 未检测到十字 Mark（检查光源/曝光/对焦）", 20, 20, "red");
                    return Result<(double, double)>.Fail($"[标定点 #{pointIndex}] 十字 Mark 提取失败：阈值分割后无候选区域，请检查光源与曝光。");
                }

                // 候选区域（橙色）—— 筛选完成即上屏
                SubmitRegion(candidateRegions, 0, 0, "orange", 2);

                // 2. 骨架化候选区域，按骨架长度筛选（骨架绿 → 骨架轮廓亮绿，逐步上屏）
                HOperatorSet.Skeleton(candidateRegions, out skeleton);
                if (DebugDrawProcessEnabled)
                {
                    SubmitRegion(skeleton, 0, 0, "green", 1);
                }
                HOperatorSet.Connection(skeleton, out skelConnected);
                HOperatorSet.SelectShape(skelConnected, out skelSelected, "area", "and", 20, 999999);
                HOperatorSet.GenContourRegionXld(skelSelected, out xldContours, "border");
                if (DebugDrawProcessEnabled)
                {
                    SubmitXld(xldContours, 0, 0, "lime green", 1);
                    SubmitText($"阈值:{SafeCount(thresholdRegion)}  连通域:{SafeCount(connectedRegions)}  候选:{SafeCount(candidateRegions)}  骨架段:{SafeCount(xldContours)}",
                        70, Math.Max(10, imgWidth.D - 640), "white");
                }
                HOperatorSet.CountObj(xldContours, out HTuple xldCount);

                // 3. 每条骨架段拟合直线（拟合一条上屏一条，青色）
                var lines = new List<(double R1, double C1, double R2, double C2)>();
                for (int i = 1; i <= xldCount.I; i++)
                {
                    HObject one = null;
                    try
                    {
                        HOperatorSet.SelectObj(xldContours, out one, i);
                        HOperatorSet.FitLineContourXld(one, "tukey", -1, 0, 5, 2,
                            out HTuple r1, out HTuple c1, out HTuple r2, out HTuple c2, out _, out _, out _);
                        if (r1.Length > 0)
                        {
                            lines.Add((r1[0].D, c1[0].D, r2[0].D, c2[0].D));
                            // 拟合直线（青色）：算子产生结果立即上屏（提交即所有权转移，未提交时兜底释放）
                            HOperatorSet.GenContourPolygonXld(out HObject lineXld,
                                new HTuple(new double[] { r1[0].D, r2[0].D }),
                                new HTuple(new double[] { c1[0].D, c2[0].D }));
                            Submit(ref lineXld, "cyan", 2);
                            lineXld?.Dispose();
                        }
                    }
                    catch
                    {
                        // 单条骨架拟合失败忽略，继续下一条
                    }
                    finally
                    {
                        one?.Dispose();
                    }
                }

                // 4. 找近似垂直的直线对求交点（十字两臂）。
                //    多对垂直直线同时成立时，选交点离期望位置最近的一对（有 seed 时），
                //    避免交点落在远处伪特征上；无 seed（预览）保留第一对成立者。
                double centerRow = imgHeight.D / 2;
                double centerCol = imgWidth.D / 2;
                bool found = false;
                bool hasExpected = expectedPx > 0 && expectedPy > 0;
                double bestPairDistSq = double.MaxValue;
                const double AngleTolerance = 30.0;   // 与 90° 的允许偏差
                const double MaxDistToLines = 120.0;  // 交点距两线段的最大距离

                for (int i = 0; i < lines.Count; i++)
                {
                    if (found && !hasExpected)
                    {
                        break; // 无 seed 时第一对成立即可
                    }
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        var a = lines[i];
                        var b = lines[j];
                        double angA = Math.Atan2(a.R2 - a.R1, a.C2 - a.C1) * 180.0 / Math.PI;
                        double angB = Math.Atan2(b.R2 - b.R1, b.C2 - b.C1) * 180.0 / Math.PI;
                        double diff = Math.Abs(NormalizeAngle180(angA - angB));
                        if (Math.Abs(diff - 90.0) > AngleTolerance)
                        {
                            continue;
                        }

                        // 两直线解析求交
                        double d1r = a.R2 - a.R1;
                        double d1c = a.C2 - a.C1;
                        double d2r = b.R2 - b.R1;
                        double d2c = b.C2 - b.C1;
                        double det = d1r * d2c - d1c * d2r;
                        if (Math.Abs(det) < 1e-9)
                        {
                            continue;
                        }
                        double t = ((b.R1 - a.R1) * d2c - (b.C1 - a.C1) * d2r) / det;
                        double ix = a.R1 + t * d1r;
                        double iy = a.C1 + t * d1c;

                        // 交点应落在两臂延长线附近，防止远端交叉误匹配
                        if (PointToSegmentDistance(ix, iy, a.R1, a.C1, a.R2, a.C2) > MaxDistToLines ||
                            PointToSegmentDistance(ix, iy, b.R1, b.C1, b.R2, b.C2) > MaxDistToLines)
                        {
                            continue;
                        }

                        if (hasExpected)
                        {
                            // 多对成立：选交点离期望位置最近的一对
                            double dx = iy - expectedPx;
                            double dy = ix - expectedPy;
                            double distSq = dx * dx + dy * dy;
                            if (found && distSq >= bestPairDistSq)
                            {
                                continue;
                            }
                            bestPairDistSq = distSq;
                        }
                        centerRow = ix;
                        centerCol = iy;
                        found = true;
                    }
                }

                if (!found)
                {
                    // 兜底：取候选区域中心作为十字中心。
                    // 有 seed 选离期望最近的候选；无 seed 取面积最大的（最可能是十字本体）
                    HOperatorSet.AreaCenter(candidateRegions, out HTuple area, out HTuple row, out HTuple col);
                    int pickIdx;
                    if (hasExpected)
                    {
                        pickIdx = SelectClosestIndex(row, col, expectedPx, expectedPy);
                    }
                    else
                    {
                        pickIdx = 0;
                        for (int k = 1; k < area.Length; k++)
                        {
                            if (area[k].D > area[pickIdx].D)
                            {
                                pickIdx = k;
                            }
                        }
                    }
                    centerRow = row[pickIdx].D;
                    centerCol = col[pickIdx].D;
                }

                // 5. 结果层上屏（image 坐标系）：醒目结果标记（黑描边绿十字+圆环+文字）；
                //    未找到垂直直线对时文字注明"兜底"
                string centerLabel = found
                    ? $"#{pointIndex}  Px:{centerCol:F1}  Py:{centerRow:F1}"
                    : $"#{pointIndex}·兜底最大区域  Px:{centerCol:F1}  Py:{centerRow:F1}";
                SubmitResultMarker(centerRow, centerCol, centerLabel);

                return Result<(double, double)>.Ok((centerCol, centerRow));
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(CalibrationService), $"十字 Mark 提取异常: {ex.Message}");
                return Result<(double, double)>.Fail($"十字 Mark 算子执行异常: {ex.Message}");
            }
            finally
            {
                // 场景式所有权规则：Submit* 提交的是"显示副本"，场景托管副本、本地句柄归本方法，
                // 无论成功/失败/异常路径统一在此释放。
                // grayImage 为彩色转灰度的临时对象（单通道输入时为 null），此处统一释放。
                DisposeAndNull(ref grayImage);
                DisposeAndNull(ref roi);
                DisposeAndNull(ref imageReduced);
                DisposeAndNull(ref thresholdRegion);
                DisposeAndNull(ref connectedRegions);
                DisposeAndNull(ref candidateRegions);
                DisposeAndNull(ref skeleton);
                DisposeAndNull(ref skelConnected);
                DisposeAndNull(ref skelSelected);
                DisposeAndNull(ref xldContours);
            }
        }

        /// <summary>批量释放 HObject 中间对象（disp 绘制不持有引用，可直接释放）</summary>
        private static void DisposeObjects(params HObject[] objects)
        {
            foreach (var obj in objects)
            {
                if (obj != null && obj.IsInitialized())
                {
                    obj.Dispose();
                }
            }
        }

        /// <summary>
        /// 释放 HObject 并置 null（异常安全）。用于提取方法的 finally 统一清理：
        /// 已通过 Submit(ref obj) 提交场景的对象会被置 null，此处自动跳过（防双重释放）；
        /// 其余本地句柄（含 Submit* 未提交的"显示副本"兜底路径）在此统一释放。
        /// </summary>
        private static void DisposeAndNull(ref HObject obj)
        {
            if (obj != null)
            {
                try
                {
                    if (obj.IsInitialized())
                    {
                        obj.Dispose();
                    }
                }
                catch
                {
                    // 释放失败忽略，对象本身即将被 GC 回收
                }
                obj = null;
            }
        }
    }
}