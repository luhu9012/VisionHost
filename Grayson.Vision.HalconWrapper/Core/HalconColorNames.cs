//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: HalconColorNames.cs
// 说 明: HALCON 绘图颜色名的【唯一真源】与守卫。
//
// 存在原因（2026-09-25 实机踩坑，日志里连出 344 条 "HALCON error #5105: Unknown color"）：
//   HALCON 的 set_color 只认它自己那张固定色表（本机 24.11 实测 64 个名字，由 query_color
//   给出），**不是 CSS/X11 全表**。节点里给每个边缘点画的十字用了 "lime" —— 不在表内
//   ⇒ 每条 set_color 都抛 #5105 ⇒ 整条场景条目被丢弃。
//
//   ★ 后果不是"报错"，而是【画面上什么都看不见】：拟合圆、拟合线、边缘点、匹配框
//     全都不上屏，日志里只剩一句 Warn（而且是**每个点一条**：一帧 172 条、两帧 344 条）。
//     现场看到的是"卡尺效果不对/什么都没有"，而没有一条错误指向颜色名。
//
// 因此本类做两件事：
//   ① 合法色名的名单与判据（IsLegal / Normalize）：把"色名写错"从【静默消失】
//      改成【响亮一次】——只报一次、并回退到一个确定画得出来的颜色；
//   ② 语义化常量（EdgePoint 等）：同一个色名不再在 4 个节点里各写一遍字面量。
//
// ★★ 分层约束（别改坏了）：本类**刻意不含任何 halcondotnet 类型**（没有 HWindow 参数、
//   没有 HTuple）。原因：Grayson.Vision.Nodes 只 ProjectReference 了 HalconWrapper、
//   **没有引用 halcondotnet**（其 node-facing API 一律走 object 弱类型），一旦本类签名里
//   出现 HWindow，Nodes 侧编译会直接 CS0012。
//   色表需要向 HALCON 现取时，由**有窗口的一侧**调 ReplaceTable 灌进来
//   （见 HalconImageDisplayHost.RefreshHalconColorTableFromHalcon）。
//
// 判据纪律：
//   · 有窗口时优先用 query_color 的权威名单；没有就退回内置 64 名快照，并在日志里
//     标明来源，避免把一张可能过期的表当权威用。
//   · Normalize 对 null / 空串**原样返回**——调用方用 null 表达"不要改颜色"是真实语义
//     （底图对象 Color==null ⇒ 按 fill 画），绝不能被替换成兜底色，否则底图从 fill 变 margin。
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Logging;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// HALCON 绘图颜色名（set_color / disp_* 的 color 参数）的合法名单与守卫。
    /// 零 HALCON 类型依赖，Nodes 层可直接引用。
    /// </summary>
    public static class HalconColorNames
    {
        /// <summary>
        /// 【边缘点 / 候选点】标记色。
        /// ★ 原为 "lime" —— 不在 HALCON 色表内（set_color 抛 #5105），导致 FitCircle / FitLine /
        ///   CaliperMeasure / ShapeMatch 四个节点给每个边缘点画的十字**全被丢弃**。
        ///   2026-09-25 实测后改为 "lime green"（在表内，且与拟合结果用的 "green" 区分得开）。
        /// 想换观感只改这一处；改完请确认新名在 <see cref="LegalNames"/> 内（或形如 #RRGGBB）。
        /// </summary>
        public const string EdgePoint = "lime green";

        /// <summary>内置快照：本机 HALCON 24.11 由 query_color 实测得到的 64 个合法色名。</summary>
        private static readonly string[] Snapshot =
        {
            "black", "white", "red", "green", "blue", "dim gray", "gray", "light gray",
            "cyan", "magenta", "yellow", "medium slate blue", "coral", "slate blue",
            "spring green", "orange red", "dark olive green", "pink", "cadet blue",
            "goldenrod", "orange", "gold", "forest green", "cornflower blue", "navy",
            "turquoise", "dark slate blue", "light blue", "indian red", "violet red",
            "light steel blue", "medium blue", "khaki", "violet", "firebrick",
            "midnight blue", "sea green", "dark turquoise", "orchid", "sienna",
            "medium orchid", "medium forest green", "medium turquoise", "medium violet red",
            "salmon", "blue violet", "tan", "pale green", "sky blue", "medium goldenrod",
            "plum", "thistle", "dark orchid", "maroon", "dark green", "steel blue",
            "medium spring green", "medium sea green", "yellow green", "medium aquamarine",
            "lime green", "aquamarine", "wheat", "green yellow"
        };

        private static readonly object Gate = new object();
        private static readonly HashSet<string> Table = new HashSet<string>(Snapshot, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static volatile string _source = "内置快照";

        /// <summary>当前生效的合法色名（只读，供面板/诊断列出）。</summary>
        public static ICollection<string> LegalNames
        {
            get { lock (Gate) { return new List<string>(Table); } }
        }

        /// <summary>当前色表来源（"query_color" 或 "内置快照"），用于诊断与日志措辞。</summary>
        public static string Source { get { return _source; } }

        /// <summary>
        /// 用 HALCON 现取的名单替换色表（由持有 HWindow 的一侧调用，见
        /// HalconImageDisplayHost.RefreshHalconColorTableFromHalcon）。传空则忽略、保留原表。
        /// </summary>
        public static void ReplaceTable(IEnumerable<string> names, string source)
        {
            if (names == null) return;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in names)
            {
                if (!string.IsNullOrWhiteSpace(s)) set.Add(s.Trim());
            }
            if (set.Count == 0) return;
            lock (Gate)
            {
                Table.Clear();
                foreach (string s in set) Table.Add(s);
                _source = string.IsNullOrEmpty(source) ? "外部注入" : source;
            }
            LogBus.Info(nameof(HalconColorNames), $"色表已刷新为 {set.Count} 个合法名（来源={_source}）。");
        }

        /// <summary>色名是否合法（在 HALCON 色表内，或形如 #RRGGBB / #RRGGBBAA 的十六进制）。</summary>
        public static bool IsLegal(string color)
        {
            if (string.IsNullOrEmpty(color)) return false;
            if (LooksLikeHex(color)) return true;
            lock (Gate) { return Table.Contains(color); }
        }

        /// <summary>
        /// 把色名规范化成"一定画得出来"的值。
        /// null / 空串原样返回（调用方语义：不改颜色）；合法名原样返回；
        /// 非法名 **只报一次** Warn 并返回 <paramref name="fallback"/>。
        /// </summary>
        public static string Normalize(string color, string fallback)
        {
            if (string.IsNullOrEmpty(color)) return color;
            if (LooksLikeHex(color)) return color; // 十六进制写法直接放行
            lock (Gate)
            {
                if (Table.Contains(color)) return color;
                if (Warned.Add(color))
                {
                    LogBus.Warn(nameof(HalconColorNames),
                        $"颜色名「{color}」不在 HALCON 色表内（合法名 {Table.Count} 个，来源={_source}）" +
                        $"⇒ set_color 会抛 #5105、整条叠加被丢弃（画面上直接看不到）。已回退为「{fallback}」。" +
                        $"同类色名本次会话只报一次，避免逐点刷屏。");
                }
                return fallback;
            }
        }

        /// <summary>#RRGGBB / #RRGGBBAA（HALCON 接受十六进制写法，实测 #00FF00 可用）</summary>
        private static bool LooksLikeHex(string s)
        {
            if (s.Length != 7 && s.Length != 9) return false;
            if (s[0] != '#') return false;
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
    }
}
