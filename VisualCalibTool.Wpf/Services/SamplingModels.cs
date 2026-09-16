using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// 一次采样运行的输入（"该怎么走、该怎么拍"全在这里）。
    /// ★ 注意它<b>不含任何行为</b>，因此可以被界面完整地预览与固化进会话。
    /// </summary>
    public sealed class SamplingRequest
    {
        public CalibChainKind Chain = CalibChainKind.NinePoint;

        /// <summary>采样计划（由 <c>SamplePlanBuilder</c> 产出）。</summary>
        public SamplePlan Plan;

        /// <summary>要找的特征。</summary>
        public MarkSpec Mark;

        public string SessionId;

        /// <summary>帧留档（默认开：复盘时唯一物证）。</summary>
        public bool ArchiveFrames = true;

        /// <summary>过程叠加（走一步画一步）。采样期可关掉省开销，调参预览期必开。</summary>
        public bool CaptureTrace = true;

        public int GrabTimeoutMs = 3000;

        // ── 运动参数 ──
        public double MoveSpeedMmPerSec = 30.0;
        public double MoveAccelMmPerSec2 = 200.0;
        public double RotateSpeedDegPerSec = 15.0;
        public double RotateAccelDegPerSec2 = 60.0;

        /// <summary>开跑前/收尾后抬到安全 Z。默认开。</summary>
        public bool LiftToSafeZ = true;

        /// <summary>
        /// 每个点之间是否也抬到安全 Z。
        /// ★ 平面九点（Z 恒定、步长只有几毫米）默认**不抬**：夹爪贴着工件横向挪几毫米，
        ///   比"抬起来再压下去"更快、且不会因为反复接触而扰动工件。
        ///   但只要涉及<b>转 U</b>（旋转链 / 偏心链），就必须抬 —— 侧向摆动是撞机的经典成因。
        /// </summary>
        public bool LiftBetweenPoints;

        /// <summary>安全抬升 Z（mm）。NaN = 用 Plan.Z + 30。</summary>
        public double SafeZ = double.NaN;

        /// <summary>
        /// 旋转链的绝对角序列（度）。null / 空 = 本轮不做旋转。
        /// </summary>
        public List<double> AbsoluteAngles;

        /// <summary>是否做零运动校核。默认开（唯一能回答"可达吗"的手段）。</summary>
        public bool PrecheckEnabled = true;

        /// <summary>该点不可达时：true = 跳过继续；false = 整轮中止（默认跳过，因为九点少一两点仍可解）。</summary>
        public bool SkipUnreachable = true;

        /// <summary>用计划里的预测像素作为提取期望（有已知 H 时才有意义）。</summary>
        public bool UsePlanPrediction = true;

        /// <summary>为 true 时不动机械手（"只看当前视野"预览用，绝不发车）。</summary>
        public bool DryRunNoMotion;

        /// <summary>
        /// ★ 走位前的"目标位调整"钩子（stepIndex, 规划目标位）→ 实际目标位。
        ///
        /// 偏心链要它：每个角度都得先把吸嘴尖对到特征上，而"对到哪"取决于对针结果或操作员操作，
        /// 不是纯几何能算出来的。null = 不调整（九点 / 旋转链都是这条路）。
        /// </summary>
        public Func<int, MotionPose, MotionPose> PoseAdjuster;
    }

    /// <summary>一次采样运行的结果。</summary>
    public sealed class SamplingOutcome
    {
        public bool Cancelled;
        public CalibError Error;

        public readonly List<CalibObservation> Observations = new List<CalibObservation>();
        public readonly List<CalibSample> Samples = new List<CalibSample>();
        public readonly List<ChainIssue> Issues = new List<ChainIssue>();

        // ── 统计（"卡哪说哪"要用）──
        public int CheckOk;
        public int CheckNg;
        public int CheckUnanswered;

        public int MoveCount;
        public int MoveRejected;
        public int GrabCount;
        public int GrabFailed;
        public int ExtractFailed;
        public int SkipUnreachableCount;

        public int FramesArchived;

        public bool Ok
        {
            get { return Error == null && !Cancelled; }
        }

        public int UsableCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Observations.Count; i++)
                {
                    if (Observations[i] != null && Observations[i].IsUsable)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public void Add(ChainIssue issue)
        {
            if (issue != null)
            {
                Issues.Add(issue);
            }
        }

        public string Describe()
        {
            if (Error != null)
            {
                return "采样中止：" + Error;
            }

            if (Cancelled)
            {
                return "采样被取消";
            }

            return string.Format(CultureInfo.InvariantCulture,
                "采样完成：可用 {0}/{1} 点，运动 {2} 次（被拒 {3}），取图 {4} 次（失败 {5}），"
                + "校核 OK {6} / NG {7} / 未答复 {8}",
                UsableCount, Observations.Count, MoveCount, MoveRejected,
                GrabCount, GrabFailed, CheckOk, CheckNg, CheckUnanswered);
        }
    }

    /// <summary>
    /// 一条链跑完的完整结果（八步产物全在这里）。
    /// ★ 界面只认这一个对象：它的 <see cref="Issues"/> + <see cref="Summary"/> 就够渲染"卡哪说哪"，
    ///   不需要界面去猜"现在该看哪个字段"。
    /// </summary>
    public sealed class ChainRunResult
    {
        public CalibChainKind Chain = CalibChainKind.NinePoint;

        public bool Success;
        public bool Cancelled;
        public CalibError Error;

        /// <summary>
        /// 失败发生在<b>八步里的第几步</b>（<see cref="ServiceStage"/>；0 = 没失败 / 不知道）。
        ///
        /// ★★ 为什么单独立这个字段（2026-09-14）：<see cref="CalibError"/> 只带
        ///   <c>Kind / RawCode / SampleIndex / Message</c>，**没有步骤号**。
        ///   于是「卡哪说哪」实际上只有两条路，而两条都不好用：
        ///     · 进度流里那一帧（<c>RunProgress.Error</c> + <c>StepTitle</c>）—— 它是<b>瞬时</b>的：
        ///       <c>WizardCoordinator.Run</c> 一发现链失败就 break 返回，界面随即被
        ///       <c>OnRunCompleted</c> 的「未完成：…」覆盖 ⇒ 用户几乎看不到；
        ///     · 会话里 <c>Session.Steps</c> 的逐步状态 —— 那是过程复盘的长表，
        ///       不是抬头就能看见的一句话。
        ///   ⇒ 结果是：**durable 的失败文案里说不清卡在第几步**。
        ///   这个字段把步骤号带到 <see cref="Summary()"/>，让"卡哪说哪"落进终态文字。
        ///   ★ 只在失败时写（成功保持 0），并且与 <c>ChainIssue</c> 的 <c>FAIL_STAGE&lt;N&gt;</c>
        ///     是<b>同一个数</b>（都来自 <c>ChainRunContext.Fail</c> 的 <c>stage</c> 形参）。
        /// </summary>
        public int FailedStage;

        public CalibSession Session;
        public SamplingOutcome Sampling;

        /// <summary>第 1 步的采样计划（无论成败都留着，界面要能把"错在哪"画出来）。</summary>
        public SamplePlan Plan;

        // ── 第 5 步产物（按 Chain 取用其一）──
        public NinePointResult NinePoint;
        public RotationCenterResult RotationCenter;
        public ToolOffsetResult ToolOffset;
        public IntrinsicsResult Intrinsics;

        /// <summary>
        /// 内参链的完整解算产物（含覆盖度判据、逐张几何事实、参数不确定度）。
        /// ★ 与 <see cref="Intrinsics"/>（只装发布用的那几个数）分开：
        ///   内参标定最容易犯的错是"只看一个总数就收工"，所以过程证据必须一起留下来。
        /// </summary>
        public Algorithm.IntrinsicsSolveOutcome IntrinsicsOutcome;

        // ── 第 6 / 7 步产物 ──
        public CalibDiagnostics Diagnostics;
        public ReprojectionReport Reprojection;

        /// <summary>第 7 步：旋转链的映射点 / 拟合圆（界面直接画）。</summary>
        public RotationCenterResult RotationVisual;

        // ── 第 8 步产物 ──
        public CalibExport Export;
        public string ExportDir;
        public bool Published;
        public string PublishMessage;

        public readonly List<ChainIssue> Issues = new List<ChainIssue>();

        public bool HasBlocker
        {
            get
            {
                for (int i = 0; i < Issues.Count; i++)
                {
                    if (Issues[i].Severity == PlanIssueSeverity.Blocker)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public void Add(ChainIssue issue)
        {
            if (issue != null)
            {
                Issues.Add(issue);
            }
        }

        public void AddRange(IEnumerable<ChainIssue> issues)
        {
            if (issues == null)
            {
                return;
            }

            foreach (ChainIssue i in issues)
            {
                Add(i);
            }
        }

        /// <summary>人话总结（界面顶部大字 & 报告首段都用它）。</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(Chain).Append(" 链：");

            if (Cancelled)
            {
                sb.Append("已取消");
                return sb.ToString();
            }

            if (Error != null)
            {
                // ★★ 带上步骤号 —— 这是"卡哪说哪"的 durable 版本。
                //   修前只有「失败 —— SolveFailed: 相机标定失败。」：说了"什么错了"，
                //   但**没说卡在第几步**。而步骤名（八步里的哪一步）是把人从
                //   "相机坏了？"拉到"其实是解算那步"的关键定位信息。
                //   ★ 步骤号缺失时（FailedStage = 0，例如异常兜底路径）如实不写，
                //     不许编一个第 1 步出来（未测到的量不留假值）。
                if (FailedStage >= ServiceStage.Plan && FailedStage <= ServiceStage.Export)
                {
                    sb.Append("失败在第 ").Append(FailedStage).Append(" 步「")
                      .Append(ServiceStage.Title(FailedStage)).Append("」 —— ").Append(Error);
                }
                else
                {
                    sb.Append("失败 —— ").Append(Error);
                }

                return sb.ToString();
            }

            sb.Append(Success ? "完成" : "未通过");
            if (Sampling != null)
            {
                sb.Append("（").Append(Formatting.Inline(Sampling.Describe())).Append("）");
            }

            switch (Chain)
            {
                case CalibChainKind.NinePoint:
                    if (NinePoint != null && NinePoint.Success)
                    {
                        sb.Append("；").Append(Formatting.Inline(string.Format(CultureInfo.InvariantCulture,
                            "H = {0}，RMS {1:F4} mm，最近角点半径 {2:F2} mm",
                            NinePoint.H, NinePoint.RmsMm, NinePoint.NearestCornerRadiusMm)));
                    }

                    break;
                case CalibChainKind.RotationCenter:
                    if (RotationCenter != null)
                    {
                        sb.Append("；").Append(Formatting.Inline(RotationCenter.Describe()));
                    }

                    break;
                case CalibChainKind.ToolOffset:
                    if (ToolOffset != null)
                    {
                        sb.Append("；").Append(Formatting.Inline(ToolOffset.Describe()));
                    }

                    break;
            }

            if (Export != null && !string.IsNullOrEmpty(ExportDir))
            {
                sb.Append("；产物已落 ").Append(ExportDir);
            }

            if (Published)
            {
                sb.Append("；已由宿主发布");
            }

            return sb.ToString();
        }
    }

    /// <summary>内部小工具：把人话文本压成一行（日志/标题用）。</summary>
    internal static class Formatting
    {
        public static string Inline(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
