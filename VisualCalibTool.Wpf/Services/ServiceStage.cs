using System;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// ★ 八步骨架（三条链共用）。
    ///
    /// 为什么要把"步"做成共享常量而不是各链自己随手写字符串：
    ///   向导的"卡哪说哪"要靠 <c>StepKey</c> 定位（会话 Steps / 日志 / 进度条都按它对齐），
    ///   三链各写一套的话，"偏心链卡在第 4 步"和"九点链卡在第 4 步"就不是同一个意思了。
    ///   统一八步之后，进度条、日志筛选、问题定位全部可以按 key 复用。
    /// </summary>
    public static class ServiceStage
    {
        /// <summary>① 规划：把"当前知道的一切"换成可发车的采样计划（不发车）。</summary>
        public const int Plan = 1;

        /// <summary>② 预检：零运动校核（CHECK）+ 上位机粗筛，逐个点问控制器"合法吗"。</summary>
        public const int Precheck = 2;

        /// <summary>③ 准备：CP 速度、软触发配置、参考半径重置、安全抬升。</summary>
        public const int Prepare = 3;

        /// <summary>④ 采样：逐点 走位 → 稳定 → 读反馈位 → 软触发 → 取帧 → 提取 → 记录。</summary>
        public const int Sample = 4;

        /// <summary>⑤ 解算：把观测变成几何量（H / O / e）。</summary>
        public const int Solve = 5;

        /// <summary>⑥ 校验：形状门禁、LOO、残差、方法时效性 —— 算出来还得敢用。</summary>
        public const int Verify = 6;

        /// <summary>⑦ 可视化：AR 反投影 / 映射点与拟合圆 —— "看见才算通过"。</summary>
        public const int Visualize = 7;

        /// <summary>⑧ 落地：导出中性产物 + 宿主回调发布（无宿主则只导文件）。</summary>
        public const int Export = 8;

        public const int Total = 8;

        public static string Key(int stage)
        {
            switch (stage)
            {
                case Plan: return "plan";
                case Precheck: return "precheck";
                case Prepare: return "prepare";
                case Sample: return "sample";
                case Solve: return "solve";
                case Verify: return "verify";
                case Visualize: return "visualize";
                case Export: return "export";
                default: return "stage" + stage;
            }
        }

        public static string Title(int stage)
        {
            switch (stage)
            {
                case Plan: return "规划采样";
                case Precheck: return "零运动校核";
                case Prepare: return "准备设备";
                case Sample: return "逐点走位取图";
                case Solve: return "解算";
                case Verify: return "质量校验";
                case Visualize: return "反投影可视化";
                case Export: return "导出与发布";
                default: return "第 " + stage + " 步";
            }
        }

        /// <summary>整段进度（八步等分，<paramref name="within"/> 为该步内的 0~1 进度）。</summary>
        public static double FractionAt(int stage, double within = 0.0)
        {
            if (within < 0.0)
            {
                within = 0.0;
            }
            else if (within > 1.0)
            {
                within = 1.0;
            }

            int s = stage < 1 ? 1 : (stage > Total ? Total : stage);
            return (s - 1 + within) / Total;
        }
    }

    /// <summary>链运行结果里的一条问题（与 <see cref="PlanIssue"/> 同构，但可带来源步骤）。</summary>
    public sealed class ChainIssue
    {
        public PlanIssueSeverity Severity;
        public string Code;
        public string Message;
        public int SampleIndex;

        /// <summary>出现在第几步（<see cref="ServiceStage"/>）。</summary>
        public int Stage;

        public static ChainIssue Info(string code, string message, int stage = 0, int sampleIndex = 0)
        {
            return new ChainIssue { Severity = PlanIssueSeverity.Info, Code = code, Message = message, Stage = stage, SampleIndex = sampleIndex };
        }

        public static ChainIssue Warn(string code, string message, int stage = 0, int sampleIndex = 0)
        {
            return new ChainIssue { Severity = PlanIssueSeverity.Warn, Code = code, Message = message, Stage = stage, SampleIndex = sampleIndex };
        }

        public static ChainIssue Blocker(string code, string message, int stage = 0, int sampleIndex = 0)
        {
            return new ChainIssue { Severity = PlanIssueSeverity.Blocker, Code = code, Message = message, Stage = stage, SampleIndex = sampleIndex };
        }

        public static ChainIssue From(PlanIssue issue, int stage)
        {
            if (issue == null)
            {
                return null;
            }

            return new ChainIssue
            {
                Severity = issue.Severity,
                Code = issue.Code,
                Message = issue.Message,
                SampleIndex = issue.SampleIndex,
                Stage = stage
            };
        }

        public override string ToString()
        {
            return string.Format("[{0}] {1}{2}: {3}",
                Severity, Code,
                SampleIndex > 0 ? " #" + SampleIndex : string.Empty,
                Message);
        }
    }
}
