using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    /// <summary>
    /// 上下机映射（跨相机映射，采集路径 CrossCameraPairWalk）：
    /// 同一 Mark 先后被【上相机】与【下相机】观测 → 每观测一次记一个像素 → 两个像素配成一对 →
    /// 用 3~9 对拟合"上相机像素 ↔ 下相机像素"的 2×3 仿射跨相机映射。
    /// 生产算式：δ = H_down(CrossCameraMap(u_up)) − H_down(R_cdown)（其后与⑥下相机相对纠偏完全相同）。
    ///
    /// 采集动作为什么长这样（TriggerSample = 逐次采集 + 自动配对，配对状态活在本策略实例里）：
    ///   第 1 次点【采集】= 记【上相机】像素 u_up；
    ///   第 2 次点【采集】= 记【下相机】像素 u_down，与上一次的 u_up 配成一对入库；
    ///   如此交替。两次之间必须：同一 Mark 仍吸在吸嘴上（不许松开/换件），并把当前相机切到下相机。
    ///
    /// ★本轮（2026-09-17）**明确没做**的两件，都不许装作做了：
    ///   ① 自动走位采集（AutoRunAll）：要让机械手在『上相机拍照位』与『下相机基准位』之间往返、
    ///      两处各触发一次拍照 —— 需要在生产配方与运动控制里加走位节点，用户明确暂缓
    ///      ⇒ 本方法**拒绝并说清原因**，不返回假成功。
    ///   ② 写档（tup / 入档）：跨相机映射的落盘未接入 ⇒ ExecuteCalibration 只给结果与判据，
    ///      并**明确告知未写入档案**（否则现场会以为已经标定完了）。
    /// </summary>
    public class CrossCameraPairCalibrationStrategy : ICalibrationStepStrategy
    {
        /// <summary>已记下、尚未配对的上相机像素（状态活在本策略实例；会话结束即释放，不落 VM）</summary>
        private double? _pendingUpX;
        private double? _pendingUpY;

        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1:
                    return "绑定两台相机（上相机 + 下固定相机）与运动控制器。";
                case 2:
                    return "选特征：同一个 Mark 必须能被两台相机分别稳定识别（推荐吸嘴上的延伸杆圆点/十字）。";
                case 3:
                    return "成对采集：JOG 到上相机拍照位 → 点【采集】（记 u_up）；"
                         + "再把当前相机切到【下相机】、JOG 到下相机基准位（Mark 全程不许松开）→ 再点【采集】（记 u_down）。"
                         + "重复到至少 3 对。";
                case 4:
                    return "点【拟合跨相机映射】：看残差 RMS 与形状判据。两个拍照位姿必须与作业时一致 ——"
                         + "尤其 Z（物距变 ⇒ 当量变 ⇒跨相机映射变成乘性偏差、会过纠）。";
                default:
                    return "同一 Mark 先后被上、下相机观测 → 成对采样 → 拟合跨相机映射（像素→像素）。";
            }
        }

        /// <summary>
        ///跨相机映射的样本是"成对像素"，不是九点那样的独立点表 ⇒ 不种默认点。
        /// （采样动作见 TriggerSample：逐次采集 → 自动配对。）
        /// </summary>
        public void InitializePoints(CalibrationWizardViewModel context)
        {
        }

        /// <summary>采集一次：奇数次记上相机像素，偶数次记下相机像素并与上一次配成对。</summary>
        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context == null) return;

            double px, py;
            if (!context.CaptureFeaturePixel("上下机映射·成对采样", out px, out py))
            {
                context.AppendLog("[跨相机映射] 本次未取到特征像素 —— 请确认：① 当前相机画面里有 Mark；"
                                + "② 特征类型/参数在第二步已调好（可先点【抓图】确认识别成功），然后再点【采集】。");
                return;
            }

            if (!_pendingUpX.HasValue)
            {
                _pendingUpX = px;
                _pendingUpY = py;
                context.AppendLog(string.Format(
                    "[跨相机映射] 已记【上相机】像素 u_up=({0:F2}, {1:F2})。下一步：把当前相机切到【下相机】，"
                  + "JOG 到下相机基准位（同一 Mark 仍在吸嘴上，不许松开/换件），再点一次【采集】。", px, py));
                return;
            }

            double upX = _pendingUpX.Value;
            double upY = _pendingUpY.Value;
            _pendingUpX = null;
            _pendingUpY = null;
            context.AddCrossCameraPair(upX, upY, px, py);
            context.AppendLog(string.Format(
                "[跨相机映射] 已入库第 {0} 对：u_up=({1:F2}, {2:F2}) ↔ u_down=({3:F2}, {4:F2})。"
              + "继续请先回到上相机拍照位再点采集；够 3 对即可点【拟合跨相机映射】。",
                context.CrossCameraPairPoints.Count, upX, upY, px, py));
        }

        /// <summary>自动走位采集未接入 ⇒ 拒绝并说清原因（不静默什么都不做）。</summary>
        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            if (context == null) return;
            MessageBox.Show(
                "上下机映射的【自动走位采集】尚未接入。\n\n"
              + "它需要机械手自动在『上相机拍照位』与『下相机基准位』之间往返、并在两处各触发一次拍照 —— "
              + "这要在生产配方与运动控制里加走位节点，还没做。\n\n"
              + "现在可用的是手动方式：JOG 到上相机拍照位点一次【采集】→ 切到下相机、JOG 到基准位再点一次"
              + "【采集】，如此交替，最少 3 对，然后点【拟合跨相机映射】。\n\n"
              + "（本策略不会「假装」自动采集：静默什么都不做比报错更危险。）",
                "自动采集未接入", MessageBoxButton.OK, MessageBoxImage.Information);
            context.AppendLog("[跨相机映射] 【自动走位采集】未接入 —— 请手动 JOG 逐次采集（见上方弹窗说明）。");
        }

        /// <summary>
        /// 拟合跨相机映射：2×3 仿射（最小二乘）+ 残差 + 形状判据。
        /// ★只给结果与判据，**不写档案**（写档未接入）—— 结论里会明说，避免"以为标定完了"。
        /// </summary>
        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            if (context == null) return;
            var pts = context.CrossCameraPairPoints;
            int n = pts == null ? 0 : pts.Count;
            if (n < 3)
            {
                MessageBox.Show(
                    "成对样本不足 3 对（当前 " + n + " 对）。\n\n"
                  + "2×3 仿射有 6 个未知数，按行拆成两次 3 未知数最小二乘 ⇒ 最少 3 对才有唯一解。"
                  + "现场建议 5~9 对，且让 Mark 落在画幅不同区域 —— 都挤在一起看不出边缘误差。",
                    "样本不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double[] m;
            string err;
            if (!TryFitAffine(pts, out m, out err))
            {
                context.AppendLog("[跨相机映射] ⛔ 拟合失败：" + err);
                MessageBox.Show("拟合失败：\n" + err, "拟合失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // ---- 残差（单位：下相机像素）----
            double sse = 0, maxR = 0;
            foreach (var p in pts)
            {
                double rx = m[0] * p.UpX + m[1] * p.UpY + m[2] - p.DownX;
                double ry = m[3] * p.UpX + m[4] * p.UpY + m[5] - p.DownY;
                double r2 = rx * rx + ry * ry;
                sse += r2;
                double r = Math.Sqrt(r2);
                if (r > maxR) maxR = r;
            }
            double rms = Math.Sqrt(sse / (2.0 * n));   // 每对贡献 x/y 两个残差分量

            // ---- 形状判据：线性部分 A 的两个奇异值之比 ----
            // ★用 σ1/σ2（比值），**不要**用 |det|/(σ1·σ2)：对任何 2×2 矩阵 |det| ≡ σ1·σ2，
            //   那个比值恒等于 1，是个**恒真判据**（"恒真的判据比没有判据更危险"）。
            //   比值偏离 1 = 采样位姿下两相机当量比/畸变使映射非等比 ⇒跨相机映射不是纯"旋转×等比"。
            double t11 = m[0] * m[0] + m[3] * m[3];
            double t12 = m[0] * m[1] + m[3] * m[4];
            double t22 = m[1] * m[1] + m[4] * m[4];
            double tr = t11 + t22;
            double det = t11 * t22 - t12 * t12;
            double disc = Math.Sqrt(Math.Max(0.0, tr * tr - 4.0 * det));
            double s1 = Math.Sqrt(Math.Max(0.0, (tr + disc) / 2.0));
            double s2 = Math.Sqrt(Math.Max(0.0, (tr - disc) / 2.0));
            double ratio = s2 > 1e-12 ? s1 / s2 : double.PositiveInfinity;

            var shape = new List<string>();
            if (double.IsInfinity(ratio) || Math.Abs(ratio - 1.0) > 0.03)
                shape.Add("⛔ 形状不合格：σ1/σ2 = " + ratio.ToString("F4")
                        + "（判据 |σ1/σ2 − 1| ≤ 0.03）⇒ 采样位姿下两相机的当量比不一致"
                        + "（物距/Z 不同、或畸变未校正）⇒ **这份跨相机映射不要用**。");
            else
                shape.Add("✅ 形状合格：σ1/σ2 = " + ratio.ToString("F4") + "（两相机当量比一致，纯旋转×等比）。");

            if (rms > 2.0)
                shape.Add("⚠ 残差偏大：RMS = " + rms.ToString("F3") + " px（> 2px）⇒ 检查特征识别是否稳定、Mark 是否在两腿之间移动过。");

            string report = new StringBuilder()
                .AppendLine("跨相机映射（上相机像素 → 下相机像素，2×3 仿射）")
                .AppendLine("  A11=" + m[0].ToString("F8") + "   A12=" + m[1].ToString("F8") + "   Tx=" + m[2].ToString("F4"))
                .AppendLine("  A21=" + m[3].ToString("F8") + "   A22=" + m[4].ToString("F8") + "   Ty=" + m[5].ToString("F4"))
                .AppendLine("残差 RMS = " + rms.ToString("F4") + " px（最大 " + maxR.ToString("F3") + " px，共 " + n + " 对）")
                .AppendLine(string.Join("\n", shape.ToArray()))
                .AppendLine()
                .AppendLine("生产算式：δ = H_down(CrossCameraMap(u_up)) − H_down(R_cdown)")
                .AppendLine("⛔ 未写入档案：跨相机映射的落盘/入档（" + (context.TargetProfile == null ? "本方案" : context.TargetProfile.Name)
                          + " 的 tup）本轮未接入，所以现在这份结果**只用于核验**，还不能被生产消费。")
                .ToString();

            context.AppendLog("[跨相机映射] 拟合完成 —— " + report.Replace("\r\n", " | ").Replace("\n", " | "));
            MessageBox.Show(report, "跨相机映射拟合结果（未写档）", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>
        /// 最小二乘拟合 (UpX,UpY) → (DownX,DownY) 的 2×3 仿射：
        ///   down = A · up + t，A = [[m0,m1],[m3,m4]]，t = (m2,m5)。
        /// 两行共用同一个 3×3 正规矩阵（设计矩阵相同），只换右端项。
        /// </summary>
        private static bool TryFitAffine(IList<CrossCameraPairSample> pts, out double[] m, out string error)
        {
            m = new double[6];
            error = null;

            double m00 = 0, m01 = 0, m02 = 0, m11 = 0, m12 = 0, m22 = 0;
            double bx0 = 0, bx1 = 0, bx2 = 0, by0 = 0, by1 = 0, by2 = 0;
            foreach (var p in pts)
            {
                double ux = p.UpX, uy = p.UpY;
                m00 += ux * ux; m01 += ux * uy; m02 += ux;
                m11 += uy * uy; m12 += uy;      m22 += 1.0;
                bx0 += ux * p.DownX; bx1 += uy * p.DownX; bx2 += p.DownX;
                by0 += ux * p.DownY; by1 += uy * p.DownY; by2 += p.DownY;
            }

            double[] px, py;
            if (!Solve3(m00, m01, m02, m11, m12, m22, bx0, bx1, bx2, out px, out error)) return false;
            if (!Solve3(m00, m01, m02, m11, m12, m22, by0, by1, by2, out py, out error)) return false;

            m[0] = px[0]; m[1] = px[1]; m[2] = px[2];
            m[3] = py[0]; m[4] = py[1]; m[5] = py[2];
            return true;
        }

        /// <summary>解 3×3 对称方程组（高斯消元，列主元选最大；奇异 ⇒ 报"样本退化"）</summary>
        private static bool Solve3(double a00, double a01, double a02, double a11, double a12, double a22,
                                   double b0, double b1, double b2, out double[] x, out string error)
        {
            x = new double[3];
            error = null;
            var A = new double[3, 4]
            {
                { a00, a01, a02, b0 },
                { a01, a11, a12, b1 },
                { a02, a12, a22, b2 },
            };

            for (int c = 0; c < 3; c++)
            {
                int piv = c;
                for (int r = c + 1; r < 3; r++)
                {
                    if (Math.Abs(A[r, c]) > Math.Abs(A[piv, c])) piv = r;
                }
                if (Math.Abs(A[piv, c]) < 1e-9)
                {
                    error = "样本退化：采样点在画幅里几乎共线或重合，设计矩阵奇异、解不唯一。"
                          + "请让几对样本的 Mark 位置在画幅内分散开（别都挤在一起或排在一条直线上）。";
                    return false;
                }
                if (piv != c)
                {
                    for (int k = c; k < 4; k++)
                    {
                        double t = A[c, k]; A[c, k] = A[piv, k]; A[piv, k] = t;
                    }
                }
                for (int r = 0; r < 3; r++)
                {
                    if (r == c) continue;
                    double f = A[r, c] / A[c, c];
                    for (int k = c; k < 4; k++) A[r, k] -= f * A[c, k];
                }
            }

            for (int i = 0; i < 3; i++) x[i] = A[i, 3] / A[i, i];
            return true;
        }
    }
}
