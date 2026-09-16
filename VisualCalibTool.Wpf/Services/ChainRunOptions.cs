using System.Collections.Generic;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>一条链的运行选项（默认可直接用，现场按需改）。</summary>
    public sealed class ChainRunOptions
    {
        /// <summary>矩阵质量门限（默认口径来自主项目 BuildHomMatHealthReport）。</summary>
        public MatrixQualityThresholds Thresholds = new MatrixQualityThresholds();

        /// <summary>"看见重合"的像素容差（反投影目视判据）。</summary>
        public double ReprojectionTolerancePx = 1.0;

        /// <summary>是否由宿主回调发布（没有宿主时自动退化为只导文件）。</summary>
        public bool PublishToHost = true;

        /// <summary>是否写产物文件（.calib.json + .tup）。</summary>
        public bool ExportFiles = true;

        /// <summary>人工剔掉的采样点序号（1 起）。★ 剔点必须能精确到点，不能逼人重跑整轮。</summary>
        public int[] ExcludeIndices;

        /// <summary>解算至少需要几个可用点。</summary>
        public int MinUsablePoints = 3;

        /// <summary>低质量分告警线（0~1）。</summary>
        public double LowQualityWarn = 0.60;

        /// <summary>工具署名（写进产物，便于溯源"这份是哪个版本的工具出的"）。</summary>
        public string ProducedBy = "VisualCalibTool";
    }

    /// <summary>
    /// ★ 工具尖"对到特征上"的方式（偏心链的前置动作）。
    ///
    /// 真机上这是"松手把吸嘴挪到压住特征"的人工或半自动过程 —— 工具不该假装自己能算出来，
    /// 所以把它做成一个显式接缝：
    ///   · <see cref="ManualTipApproachProvider"/>：真机默认，问操作员，然后读当前位；
    ///   · 仿真/回归用另一个实现（直接按仿真真值给出机位），P0 才能无人跑通。
    /// </summary>
    public interface ITipApproachProvider
    {
        /// <summary>是否需要工具先走位（false = 操作员已经就位，直接拍照）。</summary>
        bool MovesAutomatically { get; }

        /// <summary>
        /// 给出第 <paramref name="stepIndex"/> 个角度下，法兰应该停到的 XY。
        /// 返回 false 表示无法给出（由调用方走"就位后确认"的路径）。
        /// </summary>
        bool TryGetApproachXy(int stepIndex, double absoluteU, CalibTopology topology,
            out Vec2 xy, out string note);
    }

    /// <summary>人工对针：问一句，然后读当前位（真机默认路径）。</summary>
    public sealed class ManualTipApproachProvider : ITipApproachProvider
    {
        private readonly Abstractions.IUserPrompt _prompt;
        private readonly Abstractions.IMotionGateway _motion;

        public ManualTipApproachProvider(Abstractions.IUserPrompt prompt, Abstractions.IMotionGateway motion)
        {
            _prompt = prompt;
            _motion = motion;
        }

        public bool MovesAutomatically
        {
            get { return false; }
        }

        public bool TryGetApproachXy(int stepIndex, double absoluteU, CalibTopology topology,
            out Vec2 xy, out string note)
        {
            xy = Vec2.Zero;
            note = null;

            if (_prompt != null)
            {
                _prompt.Inform("对针（第 " + stepIndex + " 个角度）",
                    "请把吸嘴尖挪到压住基准特征的位置，U 转到 " + absoluteU.ToString("F2") + "°，然后确认。");
            }

            if (_motion == null)
            {
                return false;
            }

            MotionPose p = _motion.GetPosition();
            if (!p.IsFinite)
            {
                return false;
            }

            xy = p.Xy;
            note = "按操作员就位后的当前位置";
            return true;
        }
    }
}
