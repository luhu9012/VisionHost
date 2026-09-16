using System;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// ★★ 特征提取的"策略性判据"——那些一旦算错就会<b>静默失效</b>的量的计算规则。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么这几行必须从提取器里搬出来，单独放在纯代数层
    /// ══════════════════════════════════════════════════════════════════
    /// 它们原本长在 <c>HalconMarkExtractor</c> 的兜底分支里，而兜底分支的特点是：
    /// <b>正常画面永远不会走到它</b>。于是它算得对不对，靠"读代码"是看不出来的 ——
    /// 主项目就栽在这上面：
    ///
    ///   均值窗口按 <c>MaxArea</c> 反推（默认推到 3385px），而 ROI 短边只有 300px，
    ///   于是 HALCON 直接报 #3033，<b>动态阈值兜底从未真正生效过</b>，
    ///   表现为"暗 Mark 在图像边缘必失败"却查不出原因。
    ///
    /// 所以这里的立场是：凡"跑不到的分支"里的算术，都要搬到纯函数里，
    /// 让离线自检可以<b>直接喂边界值</b>把它钉死（见 ExtractionFallbackChecks）。
    /// 纯函数还有一个附带好处：窗口口径全工程只有一处，不会再出现两个数字各说各话。
    ///
    /// 本类零外部依赖（不引 HALCON、不引 WPF），因此可以被最便宜的纯代数自检直接调用。
    /// </summary>
    public static class ExtractionPolicy
    {
        /// <summary>均值窗口的下限（px）。低于这个值，局部均值退化成"像素自己"，动态阈值就失去意义。</summary>
        public const int MeanWindowMin = 31;

        /// <summary>均值窗口相对特征尺寸的倍数。窗口要显著大于特征，均值才代表"背景"而不是"特征自己"。</summary>
        public const double MeanWindowFeatureFactor = 3.0;

        /// <summary>尚未记录参考半径时的保守特征直径（px）。宁可圈大也别圈小 —— 圈小了均值被特征自身污染。</summary>
        public const double DefaultFeatureDiameterPx = 60.0;

        /// <summary>
        /// 动态阈值兜底用的均值窗口边长（px，恒为奇数）。
        ///
        /// ★ 返回 <b>0 表示"这张图不该跑兜底"</b>——这是一个有价值的返回值，不是错误码：
        ///   图像短边小到连最小窗口都放不下时，硬跑只会得到一次 HALCON 异常，
        ///   而异常被上层吞掉后就变成"兜底静默失效"（主项目事故的形态）。
        ///   返回 0 让调用方有机会把"为什么没跑"说清楚。
        /// </summary>
        /// <param name="featureDiameterPx">特征直径（px）。≤0 时取 <see cref="DefaultFeatureDiameterPx"/>。</param>
        /// <param name="shortSide">图像短边（px）。ReduceDomain 后的图矩阵尺寸不变，这里应传<b>矩阵</b>短边。</param>
        public static int MeanWindow(double featureDiameterPx, double shortSide)
        {
            if (shortSide <= 0.0 || double.IsNaN(shortSide) || double.IsInfinity(shortSide))
            {
                return 0;
            }

            double diameter = featureDiameterPx > 0.0 && !double.IsNaN(featureDiameterPx) && !double.IsInfinity(featureDiameterPx)
                ? featureDiameterPx
                : DefaultFeatureDiameterPx;

            // 向上取整后再置最低位 → 保证奇数（HALCON 的 mean_image 要求奇数窗口）
            int win = (int)Math.Ceiling(diameter * MeanWindowFeatureFactor) | 1;
            if (win < MeanWindowMin)
            {
                win = MeanWindowMin;
            }

            // ★ 上限是"图像短边 − 2"（再取奇数），而不是特征尺寸的某个倍数。
            //   主项目把上限设成"没有上限"，窗口 3385 > ROI 300 → #3033 → 兜底从未生效。
            int cap = ((int)shortSide - 2) | 1;
            if (cap >= MeanWindowMin && win > cap)
            {
                win = cap;
            }

            if (win >= MeanWindowMin && win < shortSide)
            {
                return win;
            }

            return 0;
        }

        /// <summary>
        /// 把"没跑兜底"的原因说成人话；跑得动时返回 null。
        /// ★ 有它才叫"可见的降级"——否则用户看到的是"全局阈值与动态阈值都没出候选"，
        ///   而真相是"动态阈值根本没跑"，两者要靠人猜，这正是最贵的那类故障。
        /// </summary>
        public static string ExplainNoMeanWindow(double featureDiameterPx, double shortSide)
        {
            if (MeanWindow(featureDiameterPx, shortSide) > 0)
            {
                return null;
            }

            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "图像短边仅 {0:F0}px，放不下最小动态阈值窗口 {1}×{1} → 本轮未执行动态阈值兜底"
                + "（不是「没有可用特征」，是这张图太小）。",
                shortSide, MeanWindowMin);
        }
    }
}
