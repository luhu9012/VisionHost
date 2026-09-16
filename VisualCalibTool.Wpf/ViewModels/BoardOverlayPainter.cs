using System;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;

namespace VisualCalibTool.ViewModels
{
    /// <summary>
    /// 把「板检出」画到叠加层上（内参链的"边摆边画"）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么单独抽成一个纯函数（而不是留在视图模型里就地画）
    /// ══════════════════════════════════════════════════════════════════
    /// 画图这件事有三处<b>错了也不报错</b>的地方，而它们都只能靠在测试里"记下画了什么"才查得出：
    ///   ① <b>row / col 写反</b>：图会整体转 90°，而"看起来还在画面里"，能一直不被发现；
    ///   ② <b>一个 mark 都没检出时画了 0×0 的框</b>：屏幕上多一个点，谁也不会去查；
    ///   ③ <b>该橙的没橙</b>：板偏小是这条链的头号故障，颜色不换等于这条判据没进人眼。
    /// 抽成纯函数之后，自检里喂一个"会记账"的假叠加层，上面三件事就都变成可断言的。
    ///
    /// ★ 本类<b>只碰</b> <see cref="ICalibOverlayTarget"/>（纯普通类型），不碰 HALCON ——
    ///   所以它可以跟着离线自检一起跑。
    /// </summary>
    public static class BoardOverlayPainter
    {
        /// <summary>检出 mark 的颜色。</summary>
        public const string MarkColor = "green";

        /// <summary>检出范围框：尺寸够用时的颜色。</summary>
        public const string ExtentColor = "cyan";

        /// <summary>检出范围框：板偏小（危险）时的颜色。</summary>
        public const string TooSmallColor = "orange";

        /// <summary>角标文字颜色。</summary>
        public const string TextColor = "yellow";

        /// <summary>mark 十字的臂长（像素）。★ 要小：一屏可能有几百个，画大了会糊成一片。</summary>
        public const double MarkCrossSize = 3.0;

        private const double LabelRow = 12.0;
        private const double VerdictRow = 30.0;
        private const double TextCol = 12.0;

        /// <summary>
        /// 画出来，并返回一行给人看的说明（调用方拿去显示 / 记日志）。
        /// 顺序固定：<b>清叠加 → 画 mark → 画范围框 → 画角标 → 重放</b>。
        /// </summary>
        public static string Paint(ICalibOverlayTarget ov, BoardOverlayPayload b)
        {
            if (ov == null || b == null)
            {
                return "（没有可画的板检出）";
            }

            ov.ResetOverlay();

            // ① 检出点：全部画出来，一个不漏。
            //    ★ 这是本叠加存在的理由：操作员要看的恰恰是"哪一块没认出来"。
            //      只画一个总范围会让人以为整块板都认全了 —— 而"倾斜 25° 只认出 387/837"
            //      正是这种假象最容易盖住的事实。
            ov.SetLineWidth(1);
            ov.SetColor(MarkColor);
            for (int i = 0; i < b.Marks.Count; i++)
            {
                // marks 存的是 (X=列, Y=行)；绘制 API 一律 (row, col) —— 顺序别记反
                ov.DrawCross(b.Marks[i].Y, b.Marks[i].X, MarkCrossSize);
            }

            // ② 检出范围框：**一个 mark 都没有时不画**。
            //    这时 MinCol 等都是 NaN；若拿 NaN 去画，HALCON 要么画在 (0,0) 要么静默丢弃 ——
            //    而"画了个 0×0 的框"在现场看起来就像"找到了一个小点"。
            if (b.HasMarks && !double.IsNaN(b.MinCol))
            {
                ov.SetColor(b.TooSmall ? TooSmallColor : ExtentColor);
                ov.SetLineWidth(2);
                ov.DrawRectangle(b.MinRow, b.MinCol, b.MaxRow, b.MaxCol);
            }

            // ③ 角标：两行 —— "这是哪一张" + 判据文字。
            ov.SetColor(TextColor);
            ov.SetLineWidth(1);
            ov.DrawText(LabelRow, TextCol, b.Label);
            ov.DrawText(VerdictRow, TextCol, b.Verdict);

            ov.Redraw();

            return string.Format(CultureInfo.InvariantCulture,
                "{0}：绿=检出的 mark（{1} 个），{2}框=检出范围。{3}",
                b.Label, b.Marks.Count, b.TooSmall ? "橙" : "青", b.Verdict);
        }
    }
}
