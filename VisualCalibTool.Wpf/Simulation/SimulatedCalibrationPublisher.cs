using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Simulation
{
    /// <summary>
    /// 仿真 / 独立模式下的发布器：把「发布」这条路径<b>真正走一遍</b>（不写任何外部库）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么必须有它（否则这个接缝等于没接）
    /// ══════════════════════════════════════════════════════════════════
    /// <see cref="ICalibrationPublisher"/> 在嵌入模式下由主项目实现，
    /// 但在仿真与独立模式下 <c>env.Publisher</c> 一直是 null ——
    /// 于是 <c>ChainRunnerSupport.ExportAndPublish</c> 里那段发布逻辑
    /// <b>从来没被真正执行过</b>。没被执行过的接缝等于没接：
    /// 等真接主项目那天才发现"参数对不上 / 次序错了 / 门禁根本没拦住"，代价就太大了。
    ///
    /// 它做的两件事都是真的，不是打日志假装：
    ///   ① 把每次发布的产物留档（<see cref="Published"/>）—— 自检能断言
    ///      "确实发布了、发布的是哪一份"；
    ///   ② <b>真的执行发布前体检</b>：带阻断项或矩阵形状非法就<b>拒绝</b>并计数。
    ///      ★ 门禁只有在"真的有发布器"时才会被走到 —— 这条正是第 11 步要补的。
    /// </summary>
    public sealed class SimulatedCalibrationPublisher : ICalibrationPublisher
    {
        private readonly List<CalibExport> _published = new List<CalibExport>();

        public string Name
        {
            get { return "仿真发布器（只留档，不写外部库）"; }
        }

        /// <summary>已发布的产物（按发布先后）。</summary>
        public IList<CalibExport> Published
        {
            get { return _published; }
        }

        /// <summary>被发布门禁拦下多少次（拦得住才算门禁）。</summary>
        public int RejectedCount { get; private set; }

        /// <summary>最近一次发布的说明（成功与拒绝都记）。</summary>
        public string LastMessage { get; private set; }

        /// <summary>最近一次发布成功的产物；一次都没成功过则是 null。</summary>
        public CalibExport Last
        {
            get { return _published.Count == 0 ? null : _published[_published.Count - 1]; }
        }

        public bool Publish(CalibExport export, out string message)
        {
            if (export == null)
            {
                RejectedCount++;
                LastMessage = "产物为空，不予发布。";
                message = LastMessage;
                return false;
            }

            // ★ 诊断挂在 Diagnostics 上，不是 CalibExport 的直接字段
            //   （曾经照直觉写 export.HasBlocker 编译不过 —— 记一笔防再犯）。
            CalibDiagnostics d = export.Diagnostics;

            // ★ 发布前体检①：产物自带阻断项
            if (d != null && d.HasBlocker)
            {
                RejectedCount++;
                LastMessage = "产物带阻断项，已拒绝发布（先修好再发）。";
                message = LastMessage;
                return false;
            }

            // ★ 发布前体检②：矩阵形状（σ1/σ2）
            //   落点用差分，形状失真会按差分距离线性放大 —— 残差小根本发现不了，
            //   所以发布这一关必须单独看形状。
            if (d != null && d.BadShape)
            {
                RejectedCount++;
                LastMessage = string.Format(CultureInfo.InvariantCulture,
                    "矩阵形状非法（σ1/σ2 = {0:F3}，失真 {1:F1}%），已拒绝发布。",
                    d.SigmaRatio, d.AnisotropyPct);
                message = LastMessage;
                return false;
            }

            _published.Add(export);
            LastMessage = string.Format(CultureInfo.InvariantCulture,
                "已发布：{0} 链（本会话第 {1} 次）。", export.Chain, _published.Count);
            message = LastMessage;
            return true;
        }
    }
}
