using System;
using System.Collections.Generic;

namespace VisualCalibTool.Domain
{
    /// <summary>向导一步的执行记录（"卡哪说哪"依赖它）。</summary>
    public sealed class WizardStepRecord
    {
        public string Key;
        public string Title;
        public WizardStepState State = WizardStepState.Pending;
        public DateTime StartedUtc;
        public long ElapsedMs;
        public CalibError Error;
        public string Detail;

        public override string ToString()
        {
            return string.Format("{0} [{1}] {2}", Title, State, Detail);
        }
    }

    /// <summary>
    /// 标定会话。★ 会话是<b>可回放</b>的完整事实记录：
    /// 拓扑 + 采样 + 结果 + 诊断 + 步骤耗时，全部落盘。
    /// 历史上"究竟是算法错还是图错"的争论，只有留全量的会话能终结。
    /// </summary>
    public sealed class CalibSession
    {
        public string SessionId;

        public string StationCode;
        public string StationName;
        public string CameraSlotKey;

        public CalibChainKind Chain = CalibChainKind.NinePoint;
        public CalibRunState State = CalibRunState.Idle;

        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime? FinishedUtc;

        public string ToolVersion;
        public string Operator;

        /// <summary>物理拓扑快照（向导第 2 步确认后的结果）。</summary>
        public CalibTopology Topology = new CalibTopology();

        /// <summary>本次运行环境：Simulation / ContractsDevice / LocalDevice。</summary>
        public string EnvironmentKind;

        public readonly CalibSampleSet Samples = new CalibSampleSet();
        public readonly List<WizardStepRecord> Steps = new List<WizardStepRecord>();

        // ── 各链结果（按 Chain 取用其一，其余为 null）──
        public NinePointResult NinePoint;
        public RotationCenterResult RotationCenter;
        public ToolOffsetResult ToolOffset;
        public IntrinsicsResult Intrinsics;

        public CalibDiagnostics Diagnostics;

        public CalibError Error;

        public long ElapsedMs
        {
            get
            {
                var end = FinishedUtc.HasValue ? FinishedUtc.Value : DateTime.UtcNow;
                return (long)(end - StartedUtc).TotalMilliseconds;
            }
        }

        /// <summary>加一步记录并开始计时。</summary>
        public WizardStepRecord BeginStep(string key, string title)
        {
            var rec = new WizardStepRecord
            {
                Key = key,
                Title = title,
                State = WizardStepState.Running,
                StartedUtc = DateTime.UtcNow
            };
            Steps.Add(rec);
            return rec;
        }

        /// <summary>结束一步记录。</summary>
        public static void EndStep(WizardStepRecord rec, WizardStepState state, string detail = null, CalibError error = null)
        {
            if (rec == null)
            {
                return;
            }

            rec.State = state;
            rec.Detail = detail;
            rec.Error = error;
            rec.ElapsedMs = (long)(DateTime.UtcNow - rec.StartedUtc).TotalMilliseconds;
        }
    }
}
