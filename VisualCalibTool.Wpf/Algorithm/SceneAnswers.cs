using System;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>"用户对第 2 步某一项的回答"被应用之后的结果。</summary>
    public enum SceneAnswerOutcome
    {
        /// <summary>已经改好工作副本（调用方无需再做别的）。</summary>
        Applied = 0,

        /// <summary>
        /// 需要调用方先去读"机械手现在在哪"，再写进副本的基准位。
        /// ★ 之所以不由这一层自己读：这里在算法层，**不允许**依赖环境接口
        ///   （ACL：<c>Algorithm/</c> 与 <c>Domain/</c> 零外部依赖）。
        ///   把"读位置"这件事留给调用方，这一层就仍然是可以离线单测的纯函数。
        /// </summary>
        NeedCurrentPosition = 1,

        /// <summary>这个标签没有接上（界面必须把这件事说出来，不许静默忽略）。</summary>
        NotSupported = 2,

        /// <summary>键不认识（候选列表被人改了、或者键被手改坏了）。</summary>
        BadOption = 3
    }

    /// <summary>
    /// ★★ 第 2 步「用户点了哪个候选 → 改副本里哪个字段」的**唯一真源**。
    ///
    /// 为什么把它从视图模型搬到这里（2026-09-14）：
    ///   它原来是视图模型里的一个 <c>private void ApplyAnswer</c>，而它有一条
    ///   <c>default</c> 分支 —— "这个标签我不认识" —— 在视图模型里**永远走不到**
    ///   （界面上的候选只可能来自推导器已认识的标签）。
    ///   于是那条分支就成了"因为我没走到，所以它没问题"的典型：写着、审着、从来没执行过。
    ///   搬成纯函数之后，自检可以直接拿一个不认识的标签调它，断言它确实**不静默**。
    ///
    /// ★ 分派一律按<b>标签</b>与<b>稳定 Key</b>，绝不按列表顺序：
    ///   顺序一调整（加一档步长、把两个选项换个位置）按索引回写就会**静默错位**。
    /// </summary>
    public static class SceneAnswers
    {
        /// <summary>
        /// 把用户对 <paramref name="lineLabel"/> 这一项的回答应用到 <paramref name="draft"/> 上。
        /// ★ 只动副本，不碰任何环境 —— 写回环境是调用方在"用户确认"之后的动作。
        /// </summary>
        public static SceneAnswerOutcome Apply(CalibTopology draft, string lineLabel, string optionKey)
        {
            if (draft == null || string.IsNullOrEmpty(lineLabel) || string.IsNullOrEmpty(optionKey))
            {
                return SceneAnswerOutcome.NotSupported;
            }

            switch (lineLabel)
            {
                case WizardSceneLabels.CameraMount:
                    if (optionKey != CameraMountEyeInHandKey && optionKey != CameraMountEyeToHandKey)
                    {
                        return SceneAnswerOutcome.BadOption;
                    }

                    draft.CameraMount = optionKey == CameraMountEyeInHandKey
                        ? CameraMountKind.EyeInHand
                        : CameraMountKind.EyeToHand;

                    // ★ 跟着改"像面是否随法兰滚转"：眼在手时相机跟着法兰转（画面滚），
                    //   眼在外时相机纹丝不动。这两项在物理上是同一件事的两面，
                    //   分开让用户设一定会出现自相矛盾的组合，然后偏心链按错的归一方式算。
                    draft.CameraRollsWithFlange = optionKey == CameraMountEyeInHandKey;
                    return SceneAnswerOutcome.Applied;

                case WizardSceneLabels.Hand:
                    if (optionKey != HandLeftyKey && optionKey != HandRightyKey)
                    {
                        return SceneAnswerOutcome.BadOption;
                    }

                    draft.Hand = optionKey == HandLeftyKey ? Handedness.Lefty : Handedness.Righty;
                    return SceneAnswerOutcome.Applied;

                case WizardSceneLabels.ToolHeadCount:
                    if (optionKey != ToolHeadSingleKey && optionKey != ToolHeadDualKey)
                    {
                        return SceneAnswerOutcome.BadOption;
                    }

                    draft.ToolHeadCount = optionKey == ToolHeadDualKey ? 2 : 1;
                    return SceneAnswerOutcome.Applied;

                case WizardSceneLabels.GridStep:
                    {
                        double v;
                        if (!double.TryParse(optionKey, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v) || v <= 0.0)
                        {
                            return SceneAnswerOutcome.BadOption;
                        }

                        draft.StepX = v;
                        draft.StepY = v;
                        return SceneAnswerOutcome.Applied;
                    }

                case WizardSceneLabels.BasePos:
                    if (optionKey == BasePosKeepKey)
                    {
                        // "保持" = 副本里已经有的值就是用户要的，什么都不用做。
                        return SceneAnswerOutcome.Applied;
                    }

                    if (optionKey == BasePosCurrentKey)
                    {
                        // XY 得由调用方去问机械手；这里只负责说"该去问了"。
                        return SceneAnswerOutcome.NeedCurrentPosition;
                    }

                    return SceneAnswerOutcome.BadOption;

                default:
                    return SceneAnswerOutcome.NotSupported;
            }
        }

        /// <summary>
        /// 把行标签翻译成人话问句。
        ///
        /// ★ 不认识的标签**原样返回**（而不是返回空、也不是抛异常）：
        ///   界面会把它照原样显示出来，于是"这里有个问题没被接上"是可见的。
        ///   静默变成空白才是最坏的结果 —— 又一条"看起来没问题"的行。
        /// </summary>
        public static string PromptOf(string lineLabel)
        {
            switch (lineLabel)
            {
                case WizardSceneLabels.CameraMount:
                    return "相机装在哪？";
                case WizardSceneLabels.Hand:
                    return "机械手是左手系还是右手系？";
                case WizardSceneLabels.ToolHeadCount:
                    return "这次标几个工具头？";
                case WizardSceneLabels.GridStep:
                    return "九点走位步长用多大？";
                case WizardSceneLabels.BasePos:
                    return "九点网格的中心放在哪？";
                default:
                    return lineLabel;
            }
        }

        // ── 稳定 Key（与 SceneInference 的候选工厂一一对应）────────────────
        //    ★ 定义在这里而不是各写各的：Key 是"回写分派"的输入，
        //      散在两个字面量列表里就等于没有契约。
        public const string CameraMountEyeInHandKey = "eih";
        public const string CameraMountEyeToHandKey = "eth";
        public const string HandLeftyKey = "lefty";
        public const string HandRightyKey = "righty";
        public const string ToolHeadSingleKey = "1";
        public const string ToolHeadDualKey = "2";
        public const string BasePosCurrentKey = "current";
        public const string BasePosKeepKey = "keep";
    }
}
