using System;
using System.Collections.Generic;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// 向导步骤。★ 顺序就是用户走的路：告诉我在标什么 → 确认我的理解 → 确认盯哪个特征 → 自动跑 → 验收。
    /// </summary>
    public enum WizardStepId
    {
        /// <summary>第 1 步：告诉我在标定什么（<b>人话选项</b>，不出现 H/e/t/O/k1）。</summary>
        Goal = 1,

        /// <summary>第 2 步：确认我理解的场景（自动推导 + 回显）。</summary>
        Scene = 2,

        /// <summary>第 2.5 步：确认我盯的是哪个特征（含模板示教与参数实时预览）。</summary>
        Feature = 3,

        /// <summary>第 3 步：自动跑（进度 + 实时可视化 + 卡哪说哪）。</summary>
        Run = 4,

        /// <summary>第 4 步：验收（看见才算通过）。</summary>
        Accept = 5
    }

    /// <summary>
    /// 第 1 步的"用户目标"。★ 这是降低心智模型的核心设计：
    ///   用户选的是"相机看得准不准"，而不是"H"；是"吸嘴偏了多少"，而不是"e"。
    ///   术语换算在 <see cref="WizardGoalCatalog"/> 里一次做完，界面不再出现符号。
    /// </summary>
    public enum WizardGoal
    {
        /// <summary>"标定相机看得准不准" → 九点 → H。</summary>
        CameraAccuracy = 0,

        /// <summary>"标定吸嘴转到哪" → 旋转中心 → O（e 的前置）。</summary>
        NozzleRotationCenter = 1,

        /// <summary>"标定相机看到的 = 机械手在哪" → 手眼（EyeInHand 全链）。</summary>
        HandEye = 2,

        /// <summary>"标定吸嘴偏了多少" → 偏心 → e（依赖 O）。</summary>
        NozzleOffset = 3,

        /// <summary>"标定镜头有没有把人拍歪" → 内参 + 畸变。</summary>
        LensDistortion = 4
    }

    /// <summary>第 1 步的一个可选目标（界面直接绑定它）。</summary>
    public sealed class WizardGoalOption
    {
        public WizardGoal Goal;

        /// <summary>人话标题（界面主文案）。</summary>
        public string PlainTitle;

        /// <summary>人话副标题（带来什么好处）。</summary>
        public string PlainCaption;

        /// <summary>对应哪条链（仅日志/产物用，界面不显示）。</summary>
        public CalibChainKind Chain;

        /// <summary>是否需要先做完别的链（例如偏心必须先有旋转中心）。</summary>
        public WizardGoal? Requires;

        /// <summary>为什么不可选（非空 = 该选项当前禁用，界面置灰并显示原因）。</summary>
        public string DisabledReason;

        public override string ToString()
        {
            return PlainTitle;
        }
    }

    /// <summary>第 1 步的选项清单（术语换算的唯一定义处）。</summary>
    public static class WizardGoalCatalog
    {
        public static List<WizardGoalOption> Build(bool sevenAxisAvailable = false)
        {
            var list = new List<WizardGoalOption>
            {
                new WizardGoalOption
                {
                    Goal = WizardGoal.CameraAccuracy,
                    Chain = CalibChainKind.NinePoint,
                    PlainTitle = "相机看得准不准",
                    PlainCaption = "让相机算出来的位置 = 机械手真正走到的位置（最常用，先做这一步）"
                },
                new WizardGoalOption
                {
                    Goal = WizardGoal.NozzleRotationCenter,
                    Chain = CalibChainKind.RotationCenter,
                    PlainTitle = "吸嘴转到哪",
                    PlainCaption = "找出旋转轴的中心，用来校正「转头之后位置会偏」的问题"
                },
                new WizardGoalOption
                {
                    Goal = WizardGoal.HandEye,
                    Chain = CalibChainKind.NinePoint,
                    PlainTitle = "相机看到的 = 机械手在哪",
                    PlainCaption = "相机装在机械手上、跟着一起动的场合（眼在手）"
                },
                new WizardGoalOption
                {
                    Goal = WizardGoal.NozzleOffset,
                    Chain = CalibChainKind.ToolOffset,
                    PlainTitle = "吸嘴偏了多少",

                    // ★ 这句话原来写的是"量出吸嘴尖相对旋转中心的偏心量" —— 恰好是设计文档
                    //   禁止出现在界面上的符号（"旋转中心""偏心量"）。文案体检脚本把它抓了出来，
                    //   这也说明那条规则是可执行、不是口号。
                    PlainCaption = "量出吸嘴尖相对「转头中心」偏了多少毫米，用来校正贴装时的偏移"
                                   + "（要先做完「吸嘴转到哪」）",
                    Requires = WizardGoal.NozzleRotationCenter
                },
                new WizardGoalOption
                {
                    Goal = WizardGoal.LensDistortion,
                    Chain = CalibChainKind.Intrinsics,
                    PlainTitle = "镜头有没有把人拍歪",
                    PlainCaption = "标出镜头畸变，看看它对精度到底有多大影响（需要人工摆标定板）"
                }
            };

            return list;
        }

        public static string PlainName(WizardGoal goal)
        {
            List<WizardGoalOption> all = Build();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Goal == goal)
                {
                    return all[i].PlainTitle;
                }
            }

            return goal.ToString();
        }

        public static CalibChainKind ChainOf(WizardGoal goal)
        {
            switch (goal)
            {
                case WizardGoal.CameraAccuracy:
                case WizardGoal.HandEye:
                    return CalibChainKind.NinePoint;
                case WizardGoal.NozzleRotationCenter:
                    return CalibChainKind.RotationCenter;
                case WizardGoal.NozzleOffset:
                    return CalibChainKind.ToolOffset;
                case WizardGoal.LensDistortion:
                    return CalibChainKind.Intrinsics;
                default:
                    return CalibChainKind.NinePoint;
            }
        }
    }

    /// <summary>
    /// 第 2 步里"让用户选一下"的一个候选。
    ///
    /// ★ 为什么是"选项"而不是"填空"：这个工具存在的理由就是上一版名词太多、没人敢用。
    ///   凡是能变成二选一/三选一的信息（相机装在哪、左手还是右手、几个工具头），
    ///   就不该让操作员去填一个数字或敲一个枚举名 —— 那是把判断责任推给了最不该承担的人。
    /// </summary>
    public sealed class WizardChoiceOption
    {
        /// <summary>
        /// 稳定标识（回写拓扑时按它分派，**不按列表顺序**）。
        /// ★ 为什么不能靠顺序：选项顺序一旦调整（加一档步长、把两个选项换个位置），
        ///   按索引回写就会**静默错位** —— 用户选"10 mm"却写回成"2 mm"，界面上一点征兆都没有。
        /// </summary>
        public string Key;

        /// <summary>按钮上的字（人话，界面直接显示）。</summary>
        public string Label;

        /// <summary>选它意味着什么（小字，帮用户判断后果，例如"跟着动的相机会随 Z 升降"）。</summary>
        public string Note;

        /// <summary>
        /// 是否<b>默认选中</b>（通常 = 系统里当前的值，让用户"确认"而不是"重填"）。
        /// ★ 界面上不要把它翻译成"推荐"二字：对"九点步长"这类项，
        ///   当前值未必是工程上的最优，标成"推荐"等于替用户下了结论。
        /// </summary>
        public bool Recommended;

        public override string ToString()
        {
            return Label;
        }
    }

    /// <summary>
    /// 第 2 步里「要用户拍板的那几行」的标签**真源**。
    ///
    /// ★★ 为什么必须集中定义、不许两处各写一份字面量：
    ///   这些字符串是一条**跨层契约** —— 推导器（<c>SceneInference</c>）用它们给行命名，
    ///   回写逻辑（<c>SceneAnswers</c>）按它们分派字段。两边各写一份的话，
    ///   哪天有人改了文案（哪怕只是加一个字），就会**静默失配**：
    ///   界面照常显示这一行、用户点了照常高亮，只是那一项**根本没写进副本** ——
    ///   而它的表现恰好是本项目最怕的形状（看起来全对，实际上什么都没发生）。
    /// </summary>
    public static class WizardSceneLabels
    {
        public const string CameraMount = "相机装在机械手上（跟着动）";
        public const string Hand = "手系";
        public const string ToolHeadCount = "工具头数量";
        public const string BasePos = "标定基准位（九点中心）";
        public const string GridStep = "九点步长";

        /// <summary>特征那一行（第 2 步不展示它，第 3 步整页在问 —— 见向导视图模型）。</summary>
        public const string Feature = "我盯的特征";
    }

    /// <summary>回显的一行事实（"我检测到：…"）。</summary>
    public sealed class WizardSceneLine
    {
        /// <summary>人话标签，例如"相机装在机械手上（跟着动）"。</summary>
        public string Label;

        /// <summary>取值（人类可读）。</summary>
        public string Value;

        /// <summary>这个结论从哪来的（拓扑/契约/用户输入/未知）——让用户知道"我凭什么这么认为"。</summary>
        public string Source;

        /// <summary>是否已推断确定；false = 未知（界面提示用户确认或改）。</summary>
        public bool Known;

        /// <summary>非空 = 该行有风险提示（例如"未知 → 按最保守处理"）。</summary>
        public string Caution;

        /// <summary>是否允许用户改（第 2 步的"不对，我改"）。</summary>
        public bool Editable = true;

        /// <summary>
        /// ★★ 非空 = 这一行是【要用户拍板的问题】，而不是"念给你听的结论"。
        ///
        /// 这是 2026-09-14 现场反馈「用起来很懵 / 怎么都是自问自答」之后加的。
        /// 修前的第 2 步：10 行结论一次性铺开，**整个面板里零个可交互控件**，
        /// 而标题却写着「我承诺：不对就说」，正文写着「手工改拓扑（不对，我改）」——
        /// 界面上一丁点能"说"的地方都没有。用户唯一能做的动作是"按下一步"，
        /// 那当然就是自问自答。
        ///
        /// ★ 与 <see cref="Editable"/> 的分工（两个都要，缺一不可）：
        ///   · <c>Options</c> 非空 ⇒ 这一行**有候选**，界面把它渲染成一组可选按钮；
        ///   · <c>Editable = false</c> ⇒ 这一行**根本不该问**（例如"控制器支不支持零运动校核"
        ///     是设备能力，不是用户能决定的），界面只读展示并写明来源。
        ///   · <c>Editable = true</c> 但 <c>Options</c> 为空 ⇒ 语义上可改、但当前没有候选，
        ///     界面**如实说明**，绝不给一个点了没用的假控件。
        /// </summary>
        public List<WizardChoiceOption> Options;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}：{1}{2}",
                Label, Value, string.IsNullOrEmpty(Caution) ? string.Empty : " ⚠ " + Caution);
        }
    }

    /// <summary>
    /// 第 2 步的"回显场景"。★ 手段 #2：<b>能推导出来的一律回显给用户确认，不问参数</b>。
    /// 这里只装"我推导出的结论"，用户点"对"之后才写回会话拓扑。
    /// </summary>
    public sealed class WizardScene
    {
        public CalibTopology Topology;

        public readonly List<WizardSceneLine> Lines = new List<WizardSceneLine>();

        /// <summary>是否存在必须用户拍板的未知项（未知项会被保守处理，但必须告知）。</summary>
        public bool HasUnknown;

        /// <summary>是否有硬性障碍（如"相机不能倾斜 → 内参链必须人工摆板"）。</summary>
        public bool HasBlocker;

        public bool Confirmed;

        public void Add(WizardSceneLine line)
        {
            if (line != null)
            {
                Lines.Add(line);
                if (!line.Known)
                {
                    HasUnknown = true;
                }
            }
        }

        public WizardSceneLine Find(string label)
        {
            for (int i = 0; i < Lines.Count; i++)
            {
                if (string.Equals(Lines[i].Label, label, StringComparison.Ordinal))
                {
                    return Lines[i];
                }
            }

            return null;
        }

        public string ToText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Lines.Count; i++)
            {
                sb.Append(" · ").AppendLine(Lines[i].ToString());
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 一次标定运行的进度快照（界面绑定它，实现"边跑边看"）。
    /// 纯数据：不含任何 UI 类型，因此可离线单测。
    /// </summary>
    public sealed class RunProgress
    {
        /// <summary>当前步骤（对应会话 Steps 的 Key，如 "plan"/"precheck"/"sample"/"solve"/"verify"/"export"）。</summary>
        public string StepKey;

        /// <summary>人话步骤名（界面显示"正在：逐点走位取图 3/9"）。</summary>
        public string StepTitle;

        public int SampleIndex;
        public int SampleTotal;
        public int SampleOk;
        public int SampleFailed;

        /// <summary>本次循环内的大步序号（①~⑧）与总数。</summary>
        public int StageIndex;
        public int StageTotal = 8;

        /// <summary>0~1 总进度（用于进度条）。</summary>
        public double Fraction;

        /// <summary>最近一个点的观测（界面用它实时画十字）。</summary>
        public CalibObservation LastObservation;

        /// <summary>非空 = 出错卡住了；内容即原因（"卡哪说哪"）。</summary>
        public CalibError Error;

        public bool Cancelled;

        public string Describe()
        {
            if (Error != null)
            {
                return string.Format(CultureInfo.InvariantCulture, "卡住：{0}", Error);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}（{1}/{2} 点，成功 {3}，失败 {4}）",
                StepTitle, SampleIndex, SampleTotal, SampleOk, SampleFailed);
        }
    }

    /// <summary>
    /// ★ 第 4 / 5 步"看见才算通过"要画的<b>东西</b>（不含"画在哪"）。
    ///
    /// 为什么不直接把 <c>HWindow</c> 传进视图模型：一旦那样做，视图模型就绑死在 HALCON 上，
    /// 而本项目要的是"核心可离线单测"。所以这里只描述"有哪些点、各自的像素与世界坐标"，
    /// 由宿主决定画到哪个显示面上。
    /// </summary>
    public sealed class CalibVisualizationRequest
    {
        /// <summary>画这次叠加时用的 H（像素 → 世界）。null = 没有可用 H，只能画观测点。</summary>
        public HomMat2D? H;

        /// <summary>标题（画在图上，让人知道"这是哪一步的图"）。</summary>
        public string Title;

        /// <summary>逐点观测：<c>Pixel</c> 是实测像素，<c>World</c> 是机器人反馈位对应的世界点。</summary>
        public IList<CalibObservation> Observations;

        /// <summary>旋转链的映射点（世界域，已在映射域算过），用于画拟合圆。</summary>
        public IList<Vec2> MappedPoints;

        /// <summary>旋转中心 O（世界）。HasRotCenter = false 时忽略。</summary>
        public Vec2 RotCenterWorld;
        public bool HasRotCenter;

        /// <summary>拟合圆半径（<b>仅供显示</b>：它会被延伸杆 / 偏心污染，绝不进产物）。</summary>
        public double FittedRadiusMm;

        /// <summary>反投影像素残差 RMS（画在角标上，AR 是最难作假的判据）。</summary>
        public double ReprojectionRmsPx;

        /// <summary>
        /// 内参链专用：这一帧的<b>板检出</b>载荷（板在不在、认出了哪些 mark、够不够大）。
        /// null = 不是内参链（H/e/t 三链走 <see cref="Observations"/> 那套）。
        /// </summary>
        public BoardOverlayPayload Board;
    }

    /// <summary>
    /// 内参链"这一帧板检出成什么样"的叠加载荷。
    ///
    /// ★ 为什么必须有它（不是锦上添花）：
    ///   内参链的头号故障是"找不着板"，而它的表现是最难解释的一种 ——
    ///   <b>画面里明明看得见板，算法却报检出 0 个</b>。根因是 HALCON 会把"小于期望尺寸的
    ///   mark 当噪声整片剔掉"，实测板宽占画面 49% 时检出 818 个、掉到 47.6% 就直接变 0。
    ///   只给数字（"检出 0 个 mark"），操作员必然怀疑是软件坏了；
    ///   把<b>检出结果画回画面上</b>，他立刻能看懂"是板太小，不是没找到"。
    ///   所以这个载荷要带原始帧（对照"看得见"）与检出点（对照"认出了什么"）。
    /// </summary>
    public sealed class BoardOverlayPayload
    {
        /// <summary>这一帧的 8 位灰度裸帧（宿主直接显示，用来核对"板到底在不在画面里"）。</summary>
        public byte[] RawGray;
        public int Width;
        public int Height;

        /// <summary>这一帧的名字（例如"第 3 张 · 左倾 25°"）。</summary>
        public string Label;

        /// <summary>检出 mark 的像素坐标（<c>X</c> = 列，<c>Y</c> = 行）。空 = 一个都没认出来。</summary>
        public IList<Vec2> Marks = new List<Vec2>();

        /// <summary>检出 mark 的像素外接范围（列 / 行，min / max）。一个都没检出时全为 0。</summary>
        public double MinCol;
        public double MaxCol;
        public double MinRow;
        public double MaxRow;

        /// <summary>板视在宽度占图像宽度的比例（0~1）。量不到时为 0。</summary>
        public double ApparentWidthFraction;

        /// <summary>
        /// 相邻 mark 的像面间距（px，中位数）。
        /// ★ 它是"找板成败的直接原因"，不是装饰性指标（断崖就在 ~18.8 px 附近）。
        /// </summary>
        public double ApparentSpacingPx;

        /// <summary>★ 板是否小到危险（低于 "板宽应占画面" 的建议下限）。</summary>
        public bool TooSmall;

        /// <summary>检出率 = 本帧检出 mark 数 ÷ 板应有 mark 数（0~1）。</summary>
        public double MarkCoverage;

        /// <summary>一句话结论（画在角标上，人话）。</summary>
        public string Verdict;

        public bool HasMarks
        {
            get { return Marks != null && Marks.Count > 0; }
        }
    }
}
