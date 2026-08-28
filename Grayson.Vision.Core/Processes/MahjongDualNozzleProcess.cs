using Grayson.Vision.Contracts.Flow.Enums;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 「巴斯勒相机 + Epson 4 轴 SCARA（双吸嘴）」麻将双牌分拣业务过程。
    ///
    /// 职责分层（与 MahjongPickProcess 同一约定）：
    /// - 视觉定位段（编辑器节点编排，换产品调参）：
    ///     AcquireImage(巴斯勒相机) → ShapeMatch(麻将模板) → CalibrationApply(像素→机器人坐标)
    ///   跑完后从 ShapeMatch.MatchScore 与 CalibrationApply.OutputX/OutputY 读回结果；
    /// - 运动时序段（本类代码写死，机械逻辑不随产品变化）：
    ///     安全检查 → 触发视觉流定位第一块牌 → 吸嘴1 吸取 → 吸嘴2 吸取
    ///     （第二块按阵列节距推算）→ 双吸嘴依次摆盘放料 → 回待机。
    ///
    /// 一次循环搬运两块麻将牌（双吸嘴产能翻倍的核心逻辑）：
    ///   Phase 0  安全检查：Z 抬至安全高度、双真空关闭、配方节点校验
    ///   Phase 1  视觉定位：触发视觉流，读回第一块牌的机器人坐标 + 匹配分数
    ///   Phase 2  吸嘴1 吸取：移到牌1上方 → Z 下探 → 真空1 ON → Z 抬起
    ///   Phase 3  吸嘴2 吸取：移到牌2上方（牌1坐标+节距，扣除吸嘴间距）→ Z 下探 → 真空2 ON → Z 抬起
    ///   Phase 4  吸嘴1 放料：移到放料位1 → Z 下探 → 真空1 OFF → Z 抬起
    ///   Phase 5  吸嘴2 放料：移到放料位2（扣除吸嘴间距）→ Z 下探 → 真空2 OFF → Z 抬起
    ///   Phase 6  回待机位
    /// </summary>
    public class MahjongDualNozzleProcess : StationProcessBase
    {
        private readonly MahjongDualNozzleConfig _cfg;

        /// <summary>业务过程唯一键（与 StationConfigModel.ProcessKey 对应）</summary>
        public override string ProcessKey => "MahjongDualNozzle";

        public MahjongDualNozzleProcess(StationWorker worker, MahjongDualNozzleConfig config = null)
            : base(worker, (config ?? new MahjongDualNozzleConfig()).CardAlias)
        {
            _cfg = config ?? new MahjongDualNozzleConfig();
        }

        /// <summary>
        /// 执行一次「双牌吸取摆盘」循环。
        /// 返回 true = 成功；false = 视觉 NG 或中途异常（Z 轴已抬至安全高度）。
        /// </summary>
        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            Log("========== 麻将双吸嘴分拣 开始 ==========");
            try
            {
                // ---- Phase 0: 安全检查 ----
                // Z 抬至安全高度（低于安全高度说明上次异常残留，先抬升）
                await EnsureSafeZAsync(token).ConfigureAwait(false);

                // 双真空强制关闭（防止带料悬停撞机）
                SetOutput(_cfg.VacuumIo1, false);
                SetOutput(_cfg.VacuumIo2, false);

                // U 轴转到作业角度（麻将牌无需旋转，仅首圈或被人为动过后有实际运动）
                await MoveAbsAsync(_cfg.AxisU, _cfg.WorkU, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);

                // ---- Phase 1: 视觉定位（相机固定于料盘上方，触发即拍）----
                Log("[Phase1] 触发视觉流（巴斯勒采图 → 麻将模板匹配 → 坐标标定换算）...");
                await RunVisionFlowAsync($"MJ2_{DateTime.Now:HHmmss}").ConfigureAwait(false);

                // 读取视觉结果：第一块牌的物理坐标与匹配分数
                var matchNode = FindNode(NodeType.ShapeMatch);
                var calibNode = FindNode(NodeType.CalibrationApply);
                if (matchNode == null || calibNode == null)
                {
                    throw new InvalidOperationException(
                        "当前配方缺少 ShapeMatch / CalibrationApply 节点，请确认编辑器中已编排视觉定位流");
                }

                double score = GetOutValue<double>(matchNode, "MatchScore");
                double worldX1 = GetOutValue<double>(calibNode, "OutputX");
                double worldY1 = GetOutValue<double>(calibNode, "OutputY");

                Log($"[Phase1] 牌1视觉结果: Score={score:F3}, World=({worldX1:F3}, {worldY1:F3})");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return false;
                }

                // 第二块牌 = 第一块牌 + 阵列节距（料盘按阵列摆放时的推算定位）
                double worldX2 = worldX1 + _cfg.TilePitchX;
                double worldY2 = worldY1 + _cfg.TilePitchY;
                Log($"[Phase1] 牌2推算位置: World=({worldX2:F3}, {worldY2:F3})（节距 {_cfg.TilePitchX:F1}/{_cfg.TilePitchY:F1}）");

                // ---- Phase 2: 吸嘴1 吸取牌1 ----
                // 吸嘴1 为基准吸嘴：机器人 XY = 牌1物理坐标 + 工具偏置
                Log("[Phase2] 吸嘴1 吸取牌1...");
                await MoveToNozzle1Async(worldX1, worldY1, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 3: 吸嘴2 吸取牌2 ----
                // 吸嘴2 与吸嘴1 有固定间距：吸嘴2 到牌2 时，
                // 机器人基准点（吸嘴1）应移到「牌2坐标 - 吸嘴2间距 + 工具偏置」
                Log("[Phase3] 吸嘴2 吸取牌2...");
                await MoveToNozzle2Async(worldX2, worldY2, token).ConfigureAwait(false);
                await PickOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);

                // ---- Phase 4: 吸嘴1 放料（摆盘位1）----
                Log("[Phase4] 吸嘴1 放料至摆盘位1...");
                await MoveToNozzle1Async(_cfg.Place1X, _cfg.Place1Y, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo1, "吸嘴1", token).ConfigureAwait(false);

                // ---- Phase 5: 吸嘴2 放料（摆盘位2）----
                Log("[Phase5] 吸嘴2 放料至摆盘位2...");
                await MoveToNozzle2Async(_cfg.Place2X, _cfg.Place2Y, token).ConfigureAwait(false);
                await PlaceOnceAsync(_cfg.VacuumIo2, "吸嘴2", token).ConfigureAwait(false);

                // ---- Phase 6: 回待机 ----
                await BackToStandbyAsync(token).ConfigureAwait(false);

                Log("========== 麻将双吸嘴分拣 成功（本次搬运 2 块） ==========");
                return true;
            }
            catch (Exception ex)
            {
                Log($"❌ 业务过程异常: {ex.Message}");
                // 异常兜底：尽力抬 Z 至安全高度，避免吸着料悬停低位
                await EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed).ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// 安全收尾：关闭双真空（防带料悬停）+ Z 抬至安全高度。
        /// 由 Worker.StopAsync 在停止/急停时调用，幂等可重复执行。
        /// </summary>
        public override Task SafeStopAsync(CancellationToken token = default)
        {
            Log("安全收尾：关闭双真空，Z 抬至安全高度...");
            try
            {
                if (Card != null)
                {
                    SetOutput(_cfg.VacuumIo1, false);
                    SetOutput(_cfg.VacuumIo2, false);
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ 安全收尾关闭真空失败: {ex.Message}");
            }
            return EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, token);
        }

        // ============================================================
        // 吸/放动作原语（Z 下探 + 真空 + Z 抬起，双吸嘴复用）
        // ============================================================

        /// <summary>
        /// 吸取原语：Z 下探至吸取高度 → 开真空 → 保压等待 → Z 抬至安全高度。
        /// 调用前机器人 XY 必须已在目标牌正上方。
        /// </summary>
        private async Task PickOnceAsync(int vacuumIo, string nozzleName, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisZ, _cfg.PickZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            SetOutput(vacuumIo, true);
            await Task.Delay(_cfg.VacuumOnDelayMs, token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            Log($"  {nozzleName} 吸取完成（真空 IO{vacuumIo} ON）");
        }

        /// <summary>
        /// 放料原语：Z 下探至放料高度 → 关真空(破空) → 等待脱落 → Z 抬至安全高度。
        /// </summary>
        private async Task PlaceOnceAsync(int vacuumIo, string nozzleName, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisZ, _cfg.PlaceZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            SetOutput(vacuumIo, false);
            await Task.Delay(_cfg.VacuumOffDelayMs, token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
            Log($"  {nozzleName} 放料完成（真空 IO{vacuumIo} OFF）");
        }

        // ============================================================
        // 移动原语（基准吸嘴1 与 从动吸嘴2 的坐标换算）
        // ============================================================

        /// <summary>
        /// 把吸嘴1 移到指定物理坐标（如牌1中心 / 放料位1）正上方。
        /// 机器人基准点（吸嘴1）= 目标坐标 + 工具偏置。
        /// </summary>
        private Task MoveToNozzle1Async(double targetX, double targetY, CancellationToken token)
        {
            return MoveXyAsync(targetX + _cfg.NozzleToolOffsetX,
                               targetY + _cfg.NozzleToolOffsetY, token);
        }

        /// <summary>
        /// 把吸嘴2 移到指定物理坐标（如牌2中心 / 放料位2）正上方。
        /// 吸嘴2 在基准点上偏移 (Nozzle2OffsetX, Nozzle2OffsetY)，
        /// 故基准点 = 目标坐标 - 吸嘴2间距 + 工具偏置。
        /// </summary>
        private Task MoveToNozzle2Async(double targetX, double targetY, CancellationToken token)
        {
            return MoveXyAsync(targetX - _cfg.Nozzle2OffsetX + _cfg.NozzleToolOffsetX,
                               targetY - _cfg.Nozzle2OffsetY + _cfg.NozzleToolOffsetY, token);
        }

        /// <summary>
        /// XY 平移（Z 已在安全高度，可直接平面移动）。
        /// </summary>
        private async Task MoveXyAsync(double x, double y, CancellationToken token)
        {
            await MoveAbsAsync(_cfg.AxisX, (float)x, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
            await MoveAbsAsync(_cfg.AxisY, (float)y, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
        }

        // ============================================================
        // 安全与待机
        // ============================================================

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
            Log("[Phase6] 回待机位...");
            await MoveXyAsync(_cfg.StandbyX, _cfg.StandbyY, token).ConfigureAwait(false);
        }
    }
}
