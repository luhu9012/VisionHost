using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// ★★2026-09-15 旋转偏心 e（=b）换算 —— **全仓唯一**实现。
    ///
    /// 为什么必须收到 Contracts：
    ///   ① 这条公式曾在向导里**两处各写一遍**（杆辅助路径 / 吸放式路径），只改一处就是
    ///      "判据写两遍=靠巧合正确"；收成一份后，回归断言也能直接打在它上面（WPF 层私有方法测不到）。
    ///   ② 现场事故（2026-09-15）：重标 Cam_A 的 e 后校验台落点仍偏 106mm。
    ///      日志里『校核·旋转①/②』双双 FAIL（定圆半径 656.45px/131.38mm vs 偏心矢模 256.48px/51.26mm，差 61%），
    ///      而机位固定 0.000mm / U 到位 0.001° / 匹配分 97~98 全 PASS —— 采样干净，错在换算。
    ///
    /// 物理恒等式（本类一切校验的立足点）：
    ///   e 就是"杆端 mark 相对回转轴"的矢量，而各采样点到圆心的距离正是同一个量
    ///   ⇒ **|e| ≡ 定圆半径**。半径来自径向距离、e 来自角度模型，两者**不同源**，所以互为独立校验。
    ///
    /// 根因（已修）：旋转方向被写死。旧式假定"特征在图像里的转向 = 指令 U 的转向"
    ///   （e = mean(R(-θ)·(p−C))），而图像转向取决于【运动卡 U 正方向 + 相机安装是否镜像】，
    ///   上下相机可以相反、翻装镜头也会变。方向写反时 e = mean(R(-2θ)·e_true)：
    ///   各点相位互消，量级塌成 |mean(R(-2θ))| 倍、方向也被旋转（本例 132.3mm→51.3mm，方位 87°→137°）。
    ///   ⚠ 最阴的是它**不炸只塌**：3 点小弧长仍给一个"看着像真的数"（51.260mm 有三位有效数字）。
    ///
    /// 处置：符号 σ 由数据**实测**（像素角随 U 的变化方向），与半径/尺度无关，因此随后的
    ///   |e| vs 半径 校验仍是独立的，不构成自证。
    /// </summary>
    public static class RotationEccentricity
    {
        /// <summary>
        /// 实测旋转方向 σ = sign(dθ / dU)：+1 同向，-1 反向，0 不可判（点&lt;2 或 U 跨度≈0）。
        /// 做法：按 U 升序，累加 (Δ角 × ΔU) 的符号（Δ角先归一到 (-180,180]）。
        /// ★ 只看角度、不看尺度 ⇒ 像素域与映射域都能用，且不必假设两域手性相同。
        /// </summary>
        public static double DetectRotationSign(IReadOnlyList<double> xs, IReadOnlyList<double> ys,
            IReadOnlyList<double> anglesDeg, double cx, double cy)
        {
            if (xs == null || ys == null || anglesDeg == null) return 0;
            int n = Math.Min(Math.Min(xs.Count, ys.Count), anglesDeg.Count);
            if (n < 2) return 0;
            var order = Enumerable.Range(0, n).OrderBy(i => anglesDeg[i]).ToList();
            double acc = 0;
            for (int k = 1; k < order.Count; k++)
            {
                int i0 = order[k - 1], i1 = order[k];
                double a0 = Math.Atan2(ys[i0] - cy, xs[i0] - cx) * 180.0 / Math.PI;
                double a1 = Math.Atan2(ys[i1] - cy, xs[i1] - cx) * 180.0 / Math.PI;
                acc += Wrap180(a1 - a0) * (anglesDeg[i1] - anglesDeg[i0]);
            }
            if (Math.Abs(acc) < 1e-9) return 0;   // U 跨度≈0 ⇒ 两方向不可分（此时两种口径数值也接近）
            return acc > 0 ? 1.0 : -1.0;
        }

        /// <summary>角度归一到 (-180,180]</summary>
        public static double Wrap180(double deg)
        {
            while (deg > 180.0) deg -= 360.0;
            while (deg <= -180.0) deg += 360.0;
            return deg;
        }

        /// <summary>
        /// 偏心矢（θ=0 方向）：e = mean( R(-σ·θ) · (p_θ − 圆心) )。
        /// σ=+1 等价旧写法；σ=-1（本工位实测）必须用 R(+θ)。
        /// </summary>
        public static void AverageEccentricity(IReadOnlyList<double> xs, IReadOnlyList<double> ys,
            IReadOnlyList<double> anglesDeg, double cx, double cy, double sigma,
            out double ex, out double ey)
        {
            ex = 0; ey = 0;
            if (xs == null || ys == null || anglesDeg == null) return;
            int n = Math.Min(Math.Min(xs.Count, ys.Count), anglesDeg.Count);
            if (n == 0) return;
            for (int i = 0; i < n; i++)
            {
                double rad = (-sigma * anglesDeg[i]) * Math.PI / 180.0;
                double dx = xs[i] - cx;
                double dy = ys[i] - cy;
                double c = Math.Cos(rad), sn = Math.Sin(rad);
                ex += dx * c - dy * sn;
                ey += dx * sn + dy * c;
            }
            ex /= n;
            ey /= n;
        }

        /// <summary>
        /// 口径自洽校验：|e| 与定圆半径的相对偏差（%）。二者是同一个物理量，必须≈0。
        /// 调用方据此判 PASS/FAIL —— 偏差大说明采样与"刚体绕同一圆心旋转"不符（脏点/角度记错/方向判错）。
        /// </summary>
        public static double MagnitudeDeviationPercent(double ex, double ey, double fitRadius)
        {
            if (fitRadius <= 1e-12) return double.NaN;   // 半径不可得 ⇒ 显式 NaN（别静默当 0）
            return Math.Abs(Math.Sqrt(ex * ex + ey * ey) - fitRadius) / fitRadius * 100.0;
        }
    }
}
