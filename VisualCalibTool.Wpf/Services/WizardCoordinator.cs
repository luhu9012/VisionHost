using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>向导一次运行的可调项（界面上的旋钮全部落在这里）。</summary>
    public sealed class WizardRunOptions
    {
        /// <summary>旋转链的绝对角序列（度）。</summary>
        public double[] RotationAngles = new double[] { -120.0, -90.0, -60.0, -30.0, 0.0, 30.0, 60.0, 90.0, 120.0 };

        /// <summary>偏心链的对针角序列（度）。★ 必须含非对称角才验得出"口径选错"（对称角会掩盖它）。</summary>
        public double[] TipAngles = new double[] { -45.0, 0.0, 45.0 };

        public bool ArchiveFrames = true;
        public bool CaptureTrace = true;
        public bool ExportFiles = true;
        public bool PublishToHost = true;

        /// <summary>会话名前缀（落盘目录 / 产物文件名都带它，便于区分"哪一次"）。</summary>
        public string SessionPrefix = "WIZ";

        /// <summary>反投影判读容差（px）。</summary>
        public double ReprojectionTolerancePx = 1.0;

        /// <summary>
        /// 世界 mm / 像素（X 轴）。&lt;=0 = 未知。
        /// ★ 只是<b>兜底</b>：一旦本会话里已经解出 H，就用 H 的列范数当作 mm/px
        ///   （列范数是旋转不变量，见 MatrixQualityEvaluator 的口径）—— 比任何外部输入都准。
        /// </summary>
        public double MmPerPixel;

        /// <summary>帧留档时同时存一份会话 JSON。</summary>
        public bool ExportSessionJson = true;

        // ── 内参链（"镜头有没有把人拍歪"）专用 ──

        /// <summary>
        /// 标定板模型。★ null = 用默认的 40 mm 官方标定板（<c>calplate_40mm.cpd</c>）。
        /// 板模型就是内参链的"尺子"，换了实物板必须换这里 —— 板文件与实物不符时
        /// 标定会"成功"，只是所有数都错。
        /// </summary>
        public Algorithm.BoardModel Board;

        /// <summary>内参起点（标称焦距 + 像元尺寸）。null = 按图像尺寸与 12 mm / 3.45 µm 兜底。</summary>
        public CameraIntrinsicsGuess IntrinsicsGuess;

        /// <summary>内参链的摆板脚本。null = 用默认脚本。</summary>
        public List<Algorithm.PosePrompt> BoardScript;

        /// <summary>
        /// 逐帧可视化回调（可选）。★ 目前只有内参链要用它 ——
        /// H/e/t 三链是"跑完再画一次"，而内参链要靠它"边摆边画"。
        /// null = 不画（离线自检走这条路，但会挂自己的收集器做断言）。
        /// </summary>
        public Action<CalibVisualizationRequest> Visualize;
    }

    /// <summary>
    /// 向导一次运行的产出。
    /// ★ 界面只认它：<see cref="Steps"/> 给人看"跑了哪几步"，<see cref="Summary"/> 给人看"结果如何"，
    ///   <see cref="Primary"/> 给诊断面板拿去画反投影。
    /// </summary>
    public sealed class WizardRunOutcome
    {
        public WizardGoal Goal;
        public CalibChainKind PrimaryChain;

        /// <summary>实际执行的链（按执行顺序）。被跳过的前置链不在里面。</summary>
        public readonly List<ChainRunResult> Chains = new List<ChainRunResult>();

        /// <summary>实际执行链的名字（人话，与 <see cref="Chains"/> 一一对应）。</summary>
        public readonly List<string> ChainLabels = new List<string>();

        /// <summary>被跳过的前置链（已有产物，直接复用）。</summary>
        public readonly List<string> SkippedLabels = new List<string>();

        /// <summary>用户目标对应的那条链（最后执行的那条）。</summary>
        public ChainRunResult Primary;

        public bool Success;
        public bool Cancelled;
        public CalibError Error;

        /// <summary>人话原因（成功了也写，说明"怎么算过的"）。</summary>
        public string Message;

        /// <summary>产物的落盘根目录（最近一次导出）。</summary>
        public string ArtifactRoot;

        /// <summary>
        /// ★★ <b>九点工作区</b>的畸变影响（H 在原图上标 vs 在去畸变图上标）。
        ///
        /// ★ 与内参链自己报的"板面口径"那个数是**两件事**：内参链报的范围是标定板有多大，
        ///   这个报的范围是九点采样把工件放在哪 —— 也就是生产时真正消费的范围。
        ///   决策 3（畸变要不要管、H 定到矫正图还是生产端补）要的正是后者。
        ///
        /// ★ 只有在本会话两条链都跑过、或宿主 <c>AdoptCameraModel</c> 进来时才有值；
        ///   否则为 null，并给出 <see cref="NinePointDistortionSkipReason"/>。
        /// </summary>
        public DistortionImpactAssessment NinePointDistortionImpact;

        /// <summary>
        /// 没算出九点口径影响的原因。
        /// ★ 跳过必须有理由：沉默的 null 会被读成"没有影响"（本项目吃过一次这个亏）。
        /// </summary>
        public string NinePointDistortionSkipReason;

        public readonly List<ChainIssue> Issues = new List<ChainIssue>();

        public string GoalPlainName
        {
            get { return WizardGoalCatalog.PlainName(Goal); }
        }

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

        public int WarnCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Issues.Count; i++)
                {
                    if (Issues[i].Severity == PlanIssueSeverity.Warn)
                    {
                        n++;
                    }
                }

                return n;
            }
        }

        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(GoalPlainName).Append(" —— ");

            if (Cancelled)
            {
                sb.Append("已取消。");
            }
            else if (Success)
            {
                sb.Append("完成。");
            }
            else
            {
                sb.Append("未完成：").Append(string.IsNullOrEmpty(Message) ? "原因见日志" : Message);
            }

            sb.AppendFormat(CultureInfo.InvariantCulture, "（跑了 {0} 条链", Chains.Count);
            if (SkippedLabels.Count > 0)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "，复用已有结果跳过 {0} 条", SkippedLabels.Count);
            }

            sb.Append("）");
            return sb.ToString();
        }

        public void Add(ChainIssue issue)
        {
            if (issue != null)
            {
                Issues.Add(issue);
            }
        }
    }

    /// <summary>
    /// ★★ 向导编排器：把"用户选的人话目标"翻译成"该跑哪几条链、按什么顺序、缺什么前置"。
    ///
    /// 它存在的理由是<b>前置关系必须只有一处定义</b>：
    ///   · e = O − H(p_tip) ⇒ 偏心链缺 O 就算不出来；
    ///   · O 要靠"逐点 H 映射后在映射域定圆" ⇒ 旋转中心缺 H 就定不了圆。
    /// 这两条如果散落在界面里，就会出现"按钮灰不灰"和"真跑起来会不会失败"两套判断不一致
    /// —— 那种不一致最后一定以"界面说能跑，一点就报错"的形式暴露给用户。
    ///
    /// ★ 另一条设计：<b>已完成的前置自动复用，不重复跑</b>。
    ///   用户点"吸嘴偏了多少"时，如果本会话里 H 与 O 都已经标好，就直接跑偏心链；
    ///   只有缺的时候才自动补齐。而<b>用户目标那条链永远重跑</b>（他点它就是想重标）。
    ///
    /// ★ 未实现的链一律<b>如实说"没实现"</b>，绝不返回一个"成功"来让界面显得完整。
    /// </summary>
    public sealed class WizardCoordinator
    {
        private readonly IVisualCalibEnvironment _env;
        private readonly SamplingOrchestrator _sampler;

        /// <summary>偏心链的对针方式。null = 该环境不支持，偏心链会在规划步明确失败（不是假装成功）。</summary>
        private readonly ITipApproachProvider _approach;

        public WizardCoordinator(IVisualCalibEnvironment env, SamplingOrchestrator sampler,
            ITipApproachProvider approach = null)
        {
            if (env == null)
            {
                throw new ArgumentNullException("env");
            }

            if (sampler == null)
            {
                throw new ArgumentNullException("sampler");
            }

            _env = env;
            _sampler = sampler;
            _approach = approach;
        }

        /// <summary>对针方式是否可用（界面据此提示"要不要先对针"）。</summary>
        public bool TipApproachAvailable
        {
            get { return _approach != null; }
        }

        #region Vault：会话内已解出的量

        /// <summary>已解出的 H（像素 → 世界）。</summary>
        public HomMat2D? HandEye { get; private set; }

        /// <summary>已解出的旋转中心 O（世界）。</summary>
        public Vec2? RotationCenterWorld { get; private set; }

        /// <summary>已解出的吸嘴偏心（含方法、离散度）。</summary>
        public ToolOffsetResult ToolOffset { get; private set; }

        public bool HandEyeKnown
        {
            get { return HandEye.HasValue && HandEye.Value.IsFinite; }
        }

        public bool RotationCenterKnown
        {
            get { return RotationCenterWorld.HasValue && RotationCenterWorld.Value.IsFinite; }
        }

        public bool ToolOffsetKnown
        {
            get { return ToolOffset != null && ToolOffset.Success; }
        }

        /// <summary>"已经标好了什么"（界面顶部的进度徽章）。</summary>
        /// <summary>
        /// 「已经标好了哪些量」徽章（显示在向导顶部，随时可看）。
        /// ★ 修前开头写的是"已就绪：" —— 一项都没标（三项全 ✘）的时候还说"已就绪"，
        ///   句子本身自相矛盾；而且 ✔ / ✘ 对应哪个量得靠用户自己对齐顺序。
        ///   改成"标定进度："并把量名写全（就是第 1 步那几张卡片上的名字，措辞保持一致）。
        /// </summary>
        public string VaultText()
        {
            var sb = new System.Text.StringBuilder("标定进度：");
            sb.Append(HandEyeKnown ? "看得准 ✔" : "看得准 ✘").Append("　");
            sb.Append(RotationCenterKnown ? "转到哪 ✔" : "转到哪 ✘").Append("　");
            sb.Append(ToolOffsetKnown ? "偏多少 ✔" : "偏多少 ✘");
            return sb.ToString();
        }

        public void ResetVault()
        {
            HandEye = null;
            RotationCenterWorld = null;
            ToolOffset = null;
            // ★ 采样集与相机模型一起复位：它们是"算九点口径畸变影响"的原料，
            //   留着就等于界面上会出现一个"来路不明的数"（上一次会话的产物）。
            //   宿主带进来的模型也一并清 —— 清空重来就该是干净的，宿主需要的话再 Adopt 一次。
            NinePointSamples = null;
            CameraModel = null;
        }

        /// <summary>允许外部直接把已经验过的结果喂进来（例如从上一版标定载入）。</summary>
        public void AdoptHandEye(HomMat2D h)
        {
            HandEye = h;
        }

        public void AdoptRotationCenter(Vec2 o)
        {
            RotationCenterWorld = o;
        }

        /// <summary>
        /// 已解出的<b>相机模型</b>（内参链的产物，或宿主 / 上一次标定带进来的）。
        ///
        /// ★★ 为什么协调器要攒它：<see cref="DistortionImpactAnalyzer.MeasureNinePoint"/>（入口 ②）
        ///   需要 <b>H + 九点采样集 + 相机参数同时在手</b>，而这几样分属两条链 ——
        ///   并且 <see cref="PlanChains"/> 里**没有任何目标会同时跑这两条链**
        ///   （内参是「镜头有没有把人拍歪」、九点是「相机看得准不准」）。
        ///   于是：
        ///     · 内参链自己只能报<b>板面口径</b>的数（`MeasureBoard`，见 IntrinsicsRunner：
        ///       「不需要等九点数据」）—— 那个数是对的，但**范围不对口**；
        ///     · <b>九点工作区</b>的数只有在本会话两条链都跑过（或宿主 Adopt 进来）时才算得出来。
        ///   ⇒ 不把状态攒在协调器上，入口 ② 就永远只是"实验室里跑得通"（一条死接缝）。
        /// </summary>
        public DivisionCameraModel? CameraModel { get; private set; }

        /// <summary>九点链的采样集（跨链会话状态；九点口径的量化需要它）。</summary>
        public IList<CalibSample> NinePointSamples { get; private set; }

        /// <summary>
        /// 宿主 / 上一次标定把已知的相机模型喂进来（与 <see cref="AdoptHandEye"/> 同一个用意）。
        /// ★ 有了它，"只跑九点链"的那次也能算出九点口径的畸变影响 ——
        ///   不必强迫用户按"先内参、后九点"的顺序跑。
        /// </summary>
        public void AdoptCameraModel(DivisionCameraModel cam)
        {
            CameraModel = cam;
        }

        #endregion

        #region 计划：目标 → 链序列

        /// <summary>该目标<b>理论上</b>需要哪几条链（前置在前、目标链在最后）。</summary>
        public static List<CalibChainKind> PlanChains(WizardGoal goal)
        {
            var list = new List<CalibChainKind>();

            switch (goal)
            {
                case WizardGoal.CameraAccuracy:
                    list.Add(CalibChainKind.NinePoint);
                    break;

                case WizardGoal.NozzleRotationCenter:
                    list.Add(CalibChainKind.NinePoint);
                    list.Add(CalibChainKind.RotationCenter);
                    break;

                case WizardGoal.HandEye:
                case WizardGoal.NozzleOffset:
                    // 眼在手全链 = 看得准 → 转到哪 → 偏多少（e 依赖 O，O 依赖 H）
                    list.Add(CalibChainKind.NinePoint);
                    list.Add(CalibChainKind.RotationCenter);
                    list.Add(CalibChainKind.ToolOffset);
                    break;

                case WizardGoal.LensDistortion:
                    list.Add(CalibChainKind.Intrinsics);
                    break;

                default:
                    list.Add(CalibChainKind.NinePoint);
                    break;
            }

            return list;
        }

        /// <summary>链的人话名字（界面文案，不出现 H / O / e 这些符号）。</summary>
        public static string ChainPlainName(CalibChainKind chain)
        {
            switch (chain)
            {
                case CalibChainKind.NinePoint: return "相机看得准不准";
                case CalibChainKind.RotationCenter: return "吸嘴转到哪";
                case CalibChainKind.ToolOffset: return "吸嘴偏了多少";
                case CalibChainKind.Intrinsics: return "镜头有没有把人拍歪";
                default: return chain.ToString();
            }
        }

        /// <summary>
        /// 开跑之前能不能跑。返回 null = 可以；非空 = 人话原因。
        /// ★ 界面必须用它来置灰按钮，而不是自己判断 —— 否则会出现"界面说能跑、一点就报错"。
        /// </summary>
        public string BlockedReason(WizardGoal goal)
        {
            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();

            // ★ 「镜头有没有把人拍歪」不靠机械手走位（靠人工摆板），所以"基准位/Z 没定"这类
            //   前置条件对它不适用 —— 拦住它反而会让一条本来能独立跑的链永远跑不了。
            //   它自己的前置条件是"有相机 + 有板模型"，由 IntrinsicsRunner 的预检负责。
            if (goal == WizardGoal.LensDistortion)
            {
                if (_env.Camera == null)
                {
                    return "没有相机 → 取不到图，内参无从标定。";
                }

                return null;
            }

            if (PlanChains(goal).Contains(CalibChainKind.RotationCenter) && !HandEyeKnown
                && _env.Camera == null)
            {
                return "没有相机 → 无法取图，旋转中心无从标定。";
            }

            if (PlanChains(goal).Contains(CalibChainKind.ToolOffset) && _approach == null)
            {
                return "偏心链需要「把吸嘴尖对到基准特征上」这一步：每个角度都要先对针。"
                     + "本环境没有提供对针方式（真机要操作员把关，仿真要给对针机位），所以无法进行。";
            }

            if (!topo.BasePosKnown)
            {
                return "还没有「标定基准位」：九点/旋转的采样网格都从它展开。"
                     + "请先到目标位置点「设为基准位」。";
            }

            string zSource;
            double z = SceneInference.ResolveWorkZ(topo, out zSource);
            if (double.IsNaN(z))
            {
                return "工作 Z 三级回退（对针 Z → 基准位 Z → 标定 Z）都没有值。"
                     + "H 是 2D 单应，但像素尺度取决于拍摄高度 —— Z 没定下来，整张 H 都是错的。";
            }

            return null;
        }

        #endregion

        #region 运行

        /// <summary>
        /// 跑一次。★ 这是向导"第 3 步：自动跑"的唯一入口。
        /// </summary>
        public WizardRunOutcome Run(WizardGoal goal, MarkSpec mark, WizardRunOptions opt,
            Action<RunProgress> report = null, Func<bool> cancel = null)
        {
            if (opt == null)
            {
                opt = new WizardRunOptions();
            }

            var outcome = new WizardRunOutcome
            {
                Goal = goal,
                PrimaryChain = WizardGoalCatalog.ChainOf(goal)
            };

            string blocked = BlockedReason(goal);
            if (!string.IsNullOrEmpty(blocked))
            {
                outcome.Success = false;
                outcome.Error = CalibError.Create(CalibFailureKind.Internal, blocked);
                outcome.Message = blocked;
                outcome.Add(ChainIssue.Blocker("WIZARD_NOT_RUNNABLE", blocked));
                _env.Log.Warn("[" + outcome.GoalPlainName + "] 未能开跑：" + blocked);
                return outcome;
            }

            if (mark == null)
            {
                mark = new MarkSpec { Kind = FeatureKind.CircleMark };
            }

            List<CalibChainKind> plan = PlanChains(goal);

            // ★ 前置链能复用就复用；目标链（最后一条）永远重跑。
            var toRun = new List<CalibChainKind>();
            for (int i = 0; i < plan.Count; i++)
            {
                bool isPrimary = i == plan.Count - 1;
                if (!isPrimary && AlreadyHave(plan[i]))
                {
                    outcome.SkippedLabels.Add(ChainPlainName(plan[i]) + "（复用本会话已有结果）");
                    continue;
                }

                toRun.Add(plan[i]);
            }

            if (toRun.Count == 0)
            {
                outcome.Success = true;
                outcome.Message = "没有需要执行的链（全部已有可用结果）。";
                return outcome;
            }

            _env.Log.Step("wizard", string.Format(CultureInfo.InvariantCulture,
                "=========== 向导开跑：{0}（{1} 条链）===========", outcome.GoalPlainName, toRun.Count));

            for (int i = 0; i < toRun.Count; i++)
            {
                CalibChainKind chain = toRun[i];
                string label = ChainPlainName(chain);

                if (cancel != null && cancel())
                {
                    outcome.Cancelled = true;
                    outcome.Message = "在第 " + (i + 1) + " 条链（" + label + "）之前被取消。";
                    break;
                }

                _env.Log.Step("wizard", string.Format(CultureInfo.InvariantCulture,
                    "▶▶ [{0}/{1}] {2}", i + 1, toRun.Count, label));

                ChainRunResult r;
                try
                {
                    r = RunOne(chain, mark, opt, WrapReport(report, i, toRun.Count, label), cancel);
                }
                catch (Exception ex)
                {
                    r = null;
                    _env.Log.Error("[" + label + "] 抛出异常：" + ex.GetType().Name + "：" + ex.Message, ex);
                }

                if (r == null)
                {
                    outcome.Success = false;
                    outcome.Error = CalibError.Create(CalibFailureKind.Internal, label + " 没有返回结果（内部异常）");
                    outcome.Message = label + " 执行时内部异常，详见日志。";
                    outcome.Add(ChainIssue.Blocker("WIZARD_CHAIN_CRASH", outcome.Message));
                    break;
                }

                outcome.Chains.Add(r);
                outcome.ChainLabels.Add(label);

                if (r.StoreRootOrExportDir() != null)
                {
                    outcome.ArtifactRoot = r.StoreRootOrExportDir();
                }

                if (r.Cancelled)
                {
                    outcome.Cancelled = true;
                    outcome.Message = label + " 被取消。";
                    break;
                }

                if (!r.Success)
                {
                    outcome.Success = false;
                    outcome.Error = r.Error;
                    outcome.Message = label + " 没通过：" + DescribeFailure(r);
                    for (int k = 0; k < r.Issues.Count; k++)
                    {
                        outcome.Add(r.Issues[k]);
                    }

                    break;
                }

                // 成功 → 把结果存进 Vault，供下游链与"前置判断"使用
                Harvest(chain, r);
            }

            // ★★ 收尾：如果本会话把"九点工作区的畸变影响"的原料攒齐了，就把它算出来并回挂。
            //   放在这里（而不是某个 runner 里）的原因：它需要**跨链**的状态，
            //   而只有协调器同时看得见两条链的产物。
            //   ★ 只落盘不回传 = 等于没算 —— 所以结果挂在 outcome 上，UI/宿主从那里取。
            QuantifyNinePointDistortion(outcome);

            if (outcome.Chains.Count > 0 && !outcome.Cancelled && !outcome.HasBlocker && outcome.Error == null)
            {
                outcome.Success = true;
                outcome.Primary = outcome.Chains[outcome.Chains.Count - 1];
                if (string.IsNullOrEmpty(outcome.Message))
                {
                    outcome.Message = string.Format(CultureInfo.InvariantCulture,
                        "全部通过（{0} 条链）。", outcome.Chains.Count);
                }
            }
            else if (outcome.Chains.Count > 0)
            {
                outcome.Primary = outcome.Chains[outcome.Chains.Count - 1];
            }

            _env.Log.Step("wizard", string.Format(CultureInfo.InvariantCulture,
                "=========== 向导结束：{0} ===========", outcome.Summary()));

            return outcome;
        }

        private bool AlreadyHave(CalibChainKind chain)
        {
            switch (chain)
            {
                case CalibChainKind.NinePoint: return HandEyeKnown;
                case CalibChainKind.RotationCenter: return RotationCenterKnown;
                case CalibChainKind.ToolOffset: return ToolOffsetKnown;
                default: return false;
            }
        }

        private void Harvest(CalibChainKind chain, ChainRunResult r)
        {
            switch (chain)
            {
                case CalibChainKind.NinePoint:
                    if (r.NinePoint != null && r.NinePoint.Success)
                    {
                        HandEye = r.NinePoint.H;
                    }

                    // ★ 采样集也要留下：九点口径的畸变影响需要它。
                    //   只留 H 的话，QuantifyNinePointDistortion() 就永远"缺料"。
                    if (r.Sampling != null && r.Sampling.Samples.Count > 0)
                    {
                        NinePointSamples = r.Sampling.Samples;
                    }

                    break;

                case CalibChainKind.RotationCenter:
                    if (r.RotationCenter != null && r.RotationCenter.Success)
                    {
                        RotationCenterWorld = r.RotationCenter.Center;
                    }

                    break;

                case CalibChainKind.ToolOffset:
                    if (r.ToolOffset != null && r.ToolOffset.Success)
                    {
                        ToolOffset = r.ToolOffset;
                    }

                    break;

                case CalibChainKind.Intrinsics:
                    // ★ 内参链以前**只被 Harvest 忽略**：链跑成功了，相机参数却没人接住，
                    //   于是"九点口径的畸变影响"永远等不到它要的第二样东西。
                    //   ★ 只有 Success 才接：失败了必须保持"没有"，不能拿半成品当有（诚实性）。
                    if (r.IntrinsicsOutcome != null && r.IntrinsicsOutcome.Success && r.Intrinsics != null)
                    {
                        CameraModel = DivisionCameraModel.FromIntrinsics(
                            r.Intrinsics, r.IntrinsicsOutcome.FocusM, r.IntrinsicsOutcome.PixelPitchM);
                    }

                    break;
            }
        }

        /// <summary>
        /// 把<b>九点工作区</b>的畸变影响算出来并挂到本次结果上
        /// （<see cref="DistortionImpactAnalyzer.MeasureNinePoint"/>，入口 ②）。
        ///
        /// ★ 三条规矩，缺一条就变成隐患：
        ///   ① **只在原料齐了才算**，缺哪样就明说缺哪样 —— 沉默的 null 会被读成"没有影响"
        ///      （本项目已经在"畸变影响没能量化"上吃过一次）；
        ///   ② 算出来的毫米数**超过阈值就提一条 Warn** —— 这正是决策 3（畸变要不要管）
        ///      要问的那句话，而且必须能在"卡哪说哪"里看见；
        ///   ③ 算不出来**不许**当 0。
        ///
        /// ★ 顺带说明它与板面口径的分工：内参链报的数范围是**标定板有多大**，
        ///   这里报的范围是**九点采样把工件放在哪**（= 生产时真正消费的范围）。
        ///   两个数不一样不是 bug —— 板铺满视野时它们几乎相同，板只占一角时会差很远。
        /// </summary>
        private void QuantifyNinePointDistortion(WizardRunOutcome outcome)
        {
            if (outcome.NinePointDistortionImpact != null)
            {
                return;
            }

            if (!HandEyeKnown)
            {
                outcome.NinePointDistortionSkipReason =
                    "还没有九点标定结果 —— 先在「相机看得准不准」里标一次。";
                return;
            }

            if (NinePointSamples == null || NinePointSamples.Count == 0)
            {
                outcome.NinePointDistortionSkipReason =
                    "本次会话没留下九点采样集（H 是复用/外部带进来的）—— 重标一次才能量化。";
                return;
            }

            if (!CameraModel.HasValue)
            {
                outcome.NinePointDistortionSkipReason =
                    "还不知道镜头参数（内参）—— 先在「镜头有没有把人拍歪」里跑一次。"
                    + "板面口径的影响数内参链已经报过，但那个范围不等于九点工作区。";
                return;
            }

            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            Vec2? center = null;
            if (_env.Camera != null && _env.Camera.Width > 0 && _env.Camera.Height > 0)
            {
                center = new Vec2(_env.Camera.Width / 2.0, _env.Camera.Height / 2.0);
            }

            DistortionImpactAssessment a = DistortionImpactAnalyzer.MeasureNinePoint(
                NinePointSamples, HandEye.Value, topo, CameraModel.Value, center);
            outcome.NinePointDistortionImpact = a;

            if (!a.Measured)
            {
                outcome.Add(ChainIssue.Warn("DISTORTION_IMPACT_9P_UNMEASURED",
                    "九点工作区的畸变影响没能量化：" + (a.Reason ?? "未知原因"), ServiceStage.Visualize));
                return;
            }

            if (!double.IsNaN(a.MaxShiftMm) && a.MaxShiftMm >= DistortionImpactAnalyzer.NotableShiftMm)
            {
                outcome.Add(ChainIssue.Warn("DISTORTION_IMPACT_9P",
                    string.Format(CultureInfo.InvariantCulture,
                        "在九点工作范围内，把标定换到「去畸变后的图」上做，落点最多会差 {0:F2} mm"
                        + "（{1:F0} px）—— 已超过「值得处理」的 {2:F2} mm 门槛，这个范围里畸变不能忽略。",
                        a.MaxShiftMm, a.MaxShiftPx, DistortionImpactAnalyzer.NotableShiftMm),
                    ServiceStage.Visualize));
                return;
            }

            _env.Log.Step("wizard", string.Format(CultureInfo.InvariantCulture,
                "九点工作区畸变影响：最大位移 {0:F3} mm（低于 {1:F2} mm 门槛，这个范围里可以维持现状）",
                a.MaxShiftMm, DistortionImpactAnalyzer.NotableShiftMm));
        }

        private ChainRunResult RunOne(CalibChainKind chain, MarkSpec mark, WizardRunOptions opt,
            Action<RunProgress> report, Func<bool> cancel)
        {
            CalibTopology topo = _env.ReadTopology() ?? new CalibTopology();
            int shortSide = _env.Camera == null ? 0 : Math.Min(_env.Camera.Width, _env.Camera.Height);
            double mmPerPixel = ResolveMmPerPixel(opt);

            var runOpt = new ChainRunOptions
            {
                ExportFiles = opt.ExportFiles,
                PublishToHost = opt.PublishToHost,
                ReprojectionTolerancePx = opt.ReprojectionTolerancePx
            };

            switch (chain)
            {
                case CalibChainKind.NinePoint:
                {
                    var pbr = new PlanBuildRequest
                    {
                        Chain = CalibChainKind.NinePoint,
                        Topology = topo,
                        ImageShortSidePx = shortSide,
                        ImageWidthPx = _env.Camera == null ? 0 : _env.Camera.Width,
                        ImageHeightPx = _env.Camera == null ? 0 : _env.Camera.Height,
                        MmPerPixel = mmPerPixel
                    };

                    var req = new SamplingRequest
                    {
                        Chain = CalibChainKind.NinePoint,
                        Plan = SamplePlanBuilder.Build(pbr),
                        Mark = mark,
                        SessionId = opt.SessionPrefix + "-NinePoint-" + Stamp(),
                        ArchiveFrames = opt.ArchiveFrames,
                        CaptureTrace = opt.CaptureTrace,
                        LiftToSafeZ = true,
                        LiftBetweenPoints = false,
                        PrecheckEnabled = true,
                        SkipUnreachable = true
                    };

                    return new NinePointRunner(_env, _sampler).Run(req, runOpt, report, cancel);
                }

                case CalibChainKind.RotationCenter:
                {
                    HomMat2D h = HandEye.Value;
                    double[] angles = opt.RotationAngles;

                    var req = new SamplingRequest
                    {
                        Chain = CalibChainKind.RotationCenter,
                        Plan = RotationCenterRunner.BuildPlan(topo, angles, shortSide, mmPerPixel),
                        Mark = mark,
                        SessionId = opt.SessionPrefix + "-RotationCenter-" + Stamp(),
                        ArchiveFrames = opt.ArchiveFrames,
                        CaptureTrace = opt.CaptureTrace,
                        LiftToSafeZ = true,

                        // ★ 转 U 必须抬刀：法兰上挂着吸嘴，侧向摆动是撞机的经典成因
                        LiftBetweenPoints = true,
                        PrecheckEnabled = true,
                        SkipUnreachable = true,
                        AbsoluteAngles = new List<double>(angles)
                    };

                    return new RotationCenterRunner(_env, _sampler).Run(req, h, runOpt, report, cancel);
                }

                case CalibChainKind.ToolOffset:
                {
                    HomMat2D h = HandEye.Value;
                    Vec2 o = RotationCenterWorld.Value;
                    var tipAngles = new List<double>(opt.TipAngles);

                    var req = new SamplingRequest
                    {
                        Chain = CalibChainKind.ToolOffset,
                        Mark = mark,
                        SessionId = opt.SessionPrefix + "-ToolOffset-" + Stamp(),
                        ArchiveFrames = opt.ArchiveFrames,
                        CaptureTrace = opt.CaptureTrace,
                        LiftToSafeZ = true,
                        LiftBetweenPoints = true,
                        PrecheckEnabled = true,
                        SkipUnreachable = true,
                        AbsoluteAngles = tipAngles
                    };

                    return new ToolOffsetRunner(_env, _sampler).Run(req, tipAngles, h, o, _approach, runOpt, report, cancel);
                }

                case CalibChainKind.Intrinsics:
                {
                    // ★ 这条链不靠机械手走位，靠人摆板 —— 所以不构造 SamplingRequest，
                    //   直接把"板模型 + 起点 + 摆板脚本"交给内参 runner。
                    var iopt = new IntrinsicsRunOptions
                    {
                        Board = opt.Board ?? Algorithm.BoardModel.Calplate("calplate_40mm.cpd"),
                        Guess = opt.IntrinsicsGuess,
                        Script = opt.BoardScript,
                        Common = runOpt,
                        // ★ 内参链必须"边摆边画"：它的输入是操作员的手，晚了就白拍一轮。
                        Visualize = opt.Visualize
                    };

                    return new IntrinsicsRunner(_env).Run(iopt, report, cancel);
                }

                default:
                    throw new NotSupportedException("向导编排器不认识链：" + chain);
            }
        }

        /// <summary>
        /// 把"单条链的 0~1 进度"换算成"整次向导的 0~1 进度"，并在步骤名上带出链名。
        /// ★ 不做这一步的话，进度条会跑三次 0→100%，用户会以为"跑完了又重来"。
        /// </summary>
        private Action<RunProgress> WrapReport(Action<RunProgress> inner, int chainIndex, int chainCount, string label)
        {
            if (inner == null)
            {
                return null;
            }

            return p =>
            {
                if (p == null)
                {
                    return;
                }

                var shifted = new RunProgress
                {
                    StepKey = p.StepKey,
                    StepTitle = string.Format(CultureInfo.InvariantCulture, "{0} · {1}", label, p.StepTitle),
                    SampleIndex = p.SampleIndex,
                    SampleTotal = p.SampleTotal,
                    SampleOk = p.SampleOk,
                    SampleFailed = p.SampleFailed,
                    StageIndex = p.StageIndex,
                    StageTotal = p.StageTotal,
                    LastObservation = p.LastObservation,
                    Error = p.Error,
                    Cancelled = p.Cancelled,

                    // 整次向导的进度 = (已完成链数 + 本链内进度) / 总链数
                    Fraction = chainCount <= 0
                        ? p.Fraction
                        : (chainIndex + Clamp01(p.Fraction)) / chainCount
                };

                inner(shifted);
            };
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v))
            {
                return 0.0;
            }

            return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);
        }

        private static string Stamp()
        {
            return DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        }

        private static string DescribeFailure(ChainRunResult r)
        {
            if (r.Error != null)
            {
                return r.Error.ToString();
            }

            for (int i = 0; i < r.Issues.Count; i++)
            {
                if (r.Issues[i].Severity == PlanIssueSeverity.Blocker)
                {
                    return r.Issues[i].Message;
                }
            }

            return "没有明确指出原因（这本身是个缺陷，请附日志反馈）";
        }

        /// <summary>
        /// 解析世界 mm/像素。
        /// ★ 优先用已解出的 H 的第一列范数 <c>√(h11² + h21²)</c>：
        ///   H 是"像素 → 世界"，第一列就是"像素 X 轴一格的位移矢量"，它的长度即 mm/px。
        ///   用<b>范数</b>而不是 <c>|h11|</c>：后者在图像旋转到 90° 时会退化到 ~1e-15
        ///   （真机图像旋转约 151°，这个坑在本项目里已经踩过一次）。
        /// </summary>
        private double ResolveMmPerPixel(WizardRunOptions opt)
        {
            if (HandEyeKnown)
            {
                HomMat2D h = HandEye.Value;
                double norm = Math.Sqrt(h.H11 * h.H11 + h.H21 * h.H21);
                if (norm > 1e-9 && !double.IsNaN(norm) && !double.IsInfinity(norm))
                {
                    return norm;
                }
            }

            if (opt != null && opt.MmPerPixel > 0.0)
            {
                return opt.MmPerPixel;
            }

            return 0.0;
        }

        #endregion
    }

    /// <summary>结果对象上的小工具（避免把"产物目录在哪"这种口径写散在各处）。</summary>
    internal static class ChainRunResultExtensions
    {
        public static string StoreRootOrExportDir(this ChainRunResult r)
        {
            if (r == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(r.ExportDir))
            {
                return r.ExportDir;
            }

            if (r.Export != null && !string.IsNullOrEmpty(r.Export.FramesDir))
            {
                return r.Export.FramesDir;
            }

            return null;
        }
    }
}
