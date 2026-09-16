using System;
using System.Collections.Generic;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// 一条链跑一次的运行上下文：统一管"记步、进度、日志、告警、失败"。
    ///
    /// ★ 为什么单独抽它：三条链的八步外壳是同一套（见 <see cref="ServiceStage"/>），
    ///   如果每条链各写一遍，必然出现"A 链失败时记了步、B 链忘了记"这种不对称，
    ///   而向导的"卡哪说哪"恰恰完全依赖这份对称。
    /// </summary>
    public sealed class ChainRunContext
    {
        private readonly ICalibLog _log;
        private readonly Action<RunProgress> _bridge;

        public readonly ChainRunResult Result = new ChainRunResult();

        public Action<RunProgress> Report;
        public Func<bool> IsCancelled;
        public CalibSession Session;

        private WizardStepRecord _openStep;

        // ★ 记住最近一次上报的采样计数：步骤收口时要补一次"本步 100%"，
        //   但绝不能因此把采样计数重置成 0/0（否则界面上"3/9 点"会突然跳回"0/0"）。
        private int _lastSampleIndex;
        private int _lastSampleTotal;
        private int _lastSampleOk;
        private int _lastSampleFailed;

        public ChainRunContext(ICalibLog log, Action<RunProgress> report = null, Func<bool> cancel = null)
        {
            _log = log;
            Report = report;
            IsCancelled = cancel;

            // ★ 采样期的进度是采样器直接回调 <see cref="Reporting"/> 的，不经过本类的 Progress()。
            //   于是这里做一层桥：先记下"成功/失败"，再转发给用户回调。
            //   没有这一层，界面在采样阶段只能看到"3/9"而看不到"成功 2、失败 1" ——
            //   而"哪一点没提到"恰恰是现场最需要立刻知道的信息。
            _bridge = p =>
            {
                if (p == null)
                {
                    return;
                }

                _lastSampleIndex = p.SampleIndex;
                _lastSampleTotal = p.SampleTotal;
                _lastSampleOk = p.SampleOk;
                _lastSampleFailed = p.SampleFailed;

                Action<RunProgress> h = Report;
                if (h != null)
                {
                    h(p);
                }
            };
        }

        /// <summary>
        /// 交给采样器的进度回调（带计数记录）。★ 采样链路上的进度<b>一律用这个</b>，
        /// 不要直接把 <see cref="Report"/> 透传给采样器。
        /// </summary>
        public Action<RunProgress> Reporting
        {
            get { return _bridge; }
        }

        public bool Cancelled
        {
            get { return IsCancelled != null && IsCancelled(); }
        }

        // ── 日志 ──

        public void Log(string message)
        {
            if (_log != null)
            {
                _log.Info("[" + Result.Chain + "] " + message);
            }
        }

        public void Warn(string message)
        {
            if (_log != null)
            {
                _log.Warn("[" + Result.Chain + "] " + message);
            }
        }

        public void Error(string message, Exception ex = null)
        {
            if (_log != null)
            {
                _log.Error("[" + Result.Chain + "] " + message, ex);
            }
        }

        // ── 步骤 ──

        public WizardStepRecord Begin(int stage, string detail = null)
        {
            CloseOpen(WizardStepState.Ok, null, null);

            if (Session != null)
            {
                _openStep = Session.BeginStep(ServiceStage.Key(stage), ServiceStage.Title(stage));
            }

            Progress(stage, 0.0, ServiceStage.Title(stage), 0, 0);
            Log("▶ 第 " + stage + " 步 " + ServiceStage.Title(stage)
                + (string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail));
            return _openStep;
        }

        public void End(int stage, WizardStepState state, string detail = null, CalibError error = null)
        {
            CloseOpen(state, detail, error);
            Log("■ 第 " + stage + " 步 " + ServiceStage.Title(stage) + " → " + state
                + (string.IsNullOrEmpty(detail) ? string.Empty : "：" + detail));
            ReportStageDone(stage);
        }

        /// <summary>
        /// ★ 步骤收口时补一次"本步 100%"的进度上报。
        ///
        /// 不补的话，进度<b>永远到不了 100%</b>：八步里每一步只在 <c>Begin</c> 时报了 within = 0，
        /// 于是全链最高只走到第 8 步的 0/1 = 87.5%；向导把三条链拼起来后表现为
        /// 进度条卡在 95.8% 不动，用户会以为"还在跑"。这个缺陷是端到端断言抓出来的
        /// （见 <c>CalibSimulationHarness.RunWizardGoals</c> 第 ⑤ 项）。
        /// </summary>
        private void ReportStageDone(int stage)
        {
            if (Report == null)
            {
                return;
            }

            Report(new RunProgress
            {
                StepKey = ServiceStage.Key(stage),
                StepTitle = ServiceStage.Title(stage),
                StageIndex = stage,
                StageTotal = ServiceStage.Total,
                Fraction = ServiceStage.FractionAt(stage, 1.0),

                // 保留采样计数，别让"3/9"跳回"0/0"
                SampleIndex = _lastSampleIndex,
                SampleTotal = _lastSampleTotal,
                SampleOk = _lastSampleOk,
                SampleFailed = _lastSampleFailed
            });
        }

        private void CloseOpen(WizardStepState state, string detail, CalibError error)
        {
            if (_openStep != null)
            {
                CalibSession.EndStep(_openStep, state, detail, error);
                _openStep = null;
            }
        }

        /// <summary>失败收口：记录原因（保留原始错误码）+ 落一条阻断告警 + 结束当前步。</summary>
        public void Fail(int stage, CalibError error, string message)
        {
            if (error != null && string.IsNullOrEmpty(error.Message) && !string.IsNullOrEmpty(message))
            {
                error.Message = message;
            }

            Result.Error = error;
            Result.Success = false;

            // ★ 把"卡在第几步"落到链结果上（`CalibError` 自己没有步骤字段）——
            //   这是 Summary() 里那句「失败在第 N 步「…」」的数据来源，
            //   也是"卡哪说哪"能进【终态文字】而不是只闪一下的原因。
            Result.FailedStage = stage;

            Error(message + (error == null ? string.Empty : " | " + error));

            Add(ChainIssue.Blocker("FAIL_STAGE" + stage, message, stage));
            CloseOpen(WizardStepState.Failed, message, error);

            if (Session != null)
            {
                Session.Error = error;
                Session.State = CalibRunState.Failed;
            }

            // ══════════════════════════════════════════════════════════════════════
            // ★★ 在【咽喉点】上接一次，而不是让 41 个调用点各自记得调 ProgressError。
            //
            //   修前实测：`ProgressError` 全仓**零调用**（只有声明）。于是：
            //     · `RunProgress.Error` 恒为 null ⇒ 界面 `ApplyProgress` 里那段
            //       「卡住：…」是**死分支**，用户只看到进度条停在原地，说不清卡在哪；
            //     · 而 `RunProgress.Cancelled` / `Describe()` 那条「卡住：{0}」的文案
            //       全都写好了、就是没人喂数据 —— 典型的"接缝存在但从没被走过"。
            //
            //   ★ 取消必须<b>不</b>走这里：所有取消路径都只置 `Result.Cancelled = true`
            //     然后 `Finish(ctx,false, UserCancelled)`，**不调 Fail**
            //     ⇒ 「取消」不会被误报成「卡住」（两者对用户是两件事）。
            //   ★ `Finish` 不上报进度，所以这里发的最后一帧不会被后续帧覆盖掉。
            // ══════════════════════════════════════════════════════════════════════
            ProgressError(stage, error, message);
        }

        public void Add(ChainIssue issue)
        {
            Result.Add(issue);
        }

        /// <summary>把计划里的告警搬进链结果（阻断项与警告项都要搬）。</summary>
        public void AddPlanIssues(SamplePlan plan, int stage)
        {
            if (plan == null)
            {
                return;
            }

            for (int i = 0; i < plan.Issues.Count; i++)
            {
                PlanIssue pi = plan.Issues[i];
                if (pi.Resolved)
                {
                    continue;
                }

                Result.Add(ChainIssue.From(pi, stage));
            }
        }

        public void AddSamplingIssues(SamplingOutcome outcome)
        {
            if (outcome == null)
            {
                return;
            }

            for (int i = 0; i < outcome.Issues.Count; i++)
            {
                Result.Add(outcome.Issues[i]);
            }
        }

        public void Progress(int stage, double within, string title, int sampleIndex, int sampleTotal)
        {
            // ★ 只在"真有采样总数"时覆盖：Begin() 会以 (0, 0) 报一次，若不加这个判断，
            //   每次换步都会把"3/9 点"抹成"0/0"。
            if (sampleTotal > 0)
            {
                _lastSampleIndex = sampleIndex;
                _lastSampleTotal = sampleTotal;
            }

            if (Report == null)
            {
                return;
            }

            Report(new RunProgress
            {
                StepKey = ServiceStage.Key(stage),
                StepTitle = title,
                StageIndex = stage,
                StageTotal = ServiceStage.Total,
                Fraction = ServiceStage.FractionAt(stage, within),
                SampleIndex = sampleIndex,
                SampleTotal = sampleTotal
            });
        }

        /// <summary>
        /// 把错误也塞进进度（界面据此显示「卡哪了」而不是干等）。
        ///
        /// ★★ 这是 <see cref="RunProgress.Error"/> 的<b>唯一生产者</b> —— 全仓只有它会给
        ///   进度帧填 <c>Error</c>（其余 5 处 <c>new RunProgress</c> 都不填）。
        ///
        /// ★ 为什么 <c>Fraction</c> 取<b>步骤起点</b>（<c>FractionAt(stage)</c>，不带 within）：
        ///   失败可能发生在任何一步的任意位置（例如「逐点走位取图 6/9」时相机掉线），
        ///   而这里<b>没有</b> within 这个信息；报步骤起点是唯一诚实的说法。
        ///   代价是：若界面照常更新进度条，条子会从"第 4 步 60%"<b>倒退</b>到"第 4 步 0%"。
        ///   ⇒ 所以消费方（<c>CalibWizardViewModel.ApplyProgress</c>）在收到带 Error 的帧时
        ///     必须 <b>提前 return、不动进度条</b>。那段早返回<b>不是</b>冗余代码。
        /// </summary>
        public void ProgressError(int stage, CalibError error, string title)
        {
            if (Report == null)
            {
                return;
            }

            Report(new RunProgress
            {
                StepKey = ServiceStage.Key(stage),
                StepTitle = title,
                StageIndex = stage,
                StageTotal = ServiceStage.Total,
                Fraction = ServiceStage.FractionAt(stage),
                Error = error,
                Cancelled = Result.Cancelled
            });
        }
    }
}
