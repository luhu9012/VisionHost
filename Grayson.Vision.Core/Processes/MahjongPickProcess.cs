using Grayson.Vision.Contracts.Flow.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 双滑台工位「麻将吸取摆盘」业务过程。
    ///
    /// 职责分层：
    /// - 视觉定位段（编辑器节点编排，换产品调参）：
    ///     AcquireImage → ShapeMatch → CalibrationApply
    ///   跑完后从 ShapeMatch.MatchScore 与 CalibrationApply.OutputX/OutputY 读回结果；
    /// - 运动时序段（本类代码写死，机械逻辑不变）：
    ///     安全检查 → 移拍照位 → 触发视觉流 → 判分数 → 移吸取位 →
    ///     Z下探/真空 → 移放料位 → Z下探/破空 → 回待机。
    ///
    /// 轴结构：0=Z(升降) 1=X(相机+吸嘴左右) 2=右Y(右工台) 3=左Y(左工台)。
    /// </summary>
    public class MahjongPickProcess : StationProcessBase
    {
        private readonly MahjongPickConfig _cfg;

        public MahjongPickProcess(StationWorker worker, MahjongPickConfig config = null)
            : base(worker, (config ?? new MahjongPickConfig()).CardAlias)
        {
            _cfg = config ?? new MahjongPickConfig();
        }

        /// <summary>
        /// 执行一次完整吸取摆盘循环。
        /// 返回 true = 成功；false = 视觉 NG 或中途异常（Z 轴已抬至安全高度）。
        /// </summary>
        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            Log("========== 麻将吸取摆盘 开始 ==========");
            try
            {
                // ---- Phase 0: 安全检查 ----
                await EnsureSafeZAsync(token).ConfigureAwait(false);

                // ---- Phase 1: 左工台拍照定位 ----
                Log("[Phase1] 移动至左工台拍照位...");
                await MoveAbsAsync(_cfg.AxisLeftY, _cfg.LeftPhotoY, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisX, _cfg.LeftPhotoX, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                Log("[Phase1] 触发视觉流（采图 → 匹配 → 标定换算）...");
                await RunVisionFlowAsync($"MJ_{DateTime.Now:HHmmss}").ConfigureAwait(false);

                // ---- 读取视觉结果 ----
                var matchNode = FindNode(NodeType.ShapeMatch);
                var calibNode = FindNode(NodeType.CalibrationApply);
                if (matchNode == null || calibNode == null)
                {
                    throw new InvalidOperationException(
                        "当前配方缺少 ShapeMatch / CalibrationApply 节点，请确认编辑器中已编排视觉定位流");
                }

                double score = GetOutValue<double>(matchNode, "MatchScore");
                double worldX = GetOutValue<double>(calibNode, "OutputX");
                double worldY = GetOutValue<double>(calibNode, "OutputY");

                Log($"[Phase1] 视觉结果: Score={score:F3}, World=({worldX:F3}, {worldY:F3})");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // ---- Phase 2: 移动至麻将上方并吸取 ----
                Log("[Phase2] 移动至吸取位...");
                await MoveAbsAsync(_cfg.AxisX, (float)(worldX + _cfg.NozzleXOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisLeftY, (float)(worldY + _cfg.NozzleYOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                Log("[Phase2] Z 下探吸取...");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.PickZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                SetOutput(_cfg.VacuumIo, true);
                await Task.Delay(_cfg.VacuumOnDelayMs, token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);

                // ---- Phase 3: 右工台放料 ----
                Log("[Phase3] 移动至右工台放料位...");
                await MoveAbsAsync(_cfg.AxisRightY, _cfg.RightPlaceY, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisX, _cfg.RightPlaceX, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                Log("[Phase3] Z 下探放料...");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.PlaceZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                SetOutput(_cfg.VacuumIo, false);
                await Task.Delay(_cfg.VacuumOffDelayMs, token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);

                // ---- Phase 4: 回待机 ----
                await BackToStandbyAsync(token).ConfigureAwait(false);

                Log("========== 麻将吸取摆盘 成功 ==========");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 业务过程异常: {ex.Message}");
                // 异常兜底：尽力抬 Z 至安全高度，避免吸着料悬停低位
                try
                {
                    if (Card != null)
                    {
                        Card.MoveAbsolute(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed);
                        await WaitAxisIdleAsync(_cfg.AxisZ, 10000, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception zex)
                {
                    Log($"⚠️ 异常回安全高度失败，请手动确认 Z 轴状态: {zex.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// 确保 Z 轴处于安全高度：若当前低于安全高度则先抬升。
        /// </summary>
        private async Task EnsureSafeZAsync(CancellationToken token)
        {
            float z = GetAxisPos(_cfg.AxisZ);
            if (z < _cfg.SafeZ)
            {
                Log($"Z 轴当前 {z:F3} 低于安全高度 {_cfg.SafeZ:F3}，先抬升");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            }
        }

        private async Task BackToStandbyAsync(CancellationToken token)
        {
            Log("[Phase4] 回待机位...");
            await MoveAbsAsync(_cfg.AxisX, _cfg.StandbyX, _cfg.XySpeed, settleMs: 0, token: token).ConfigureAwait(false);
        }
    }
}
