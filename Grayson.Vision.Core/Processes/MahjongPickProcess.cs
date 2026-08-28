using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using System;
using System.Diagnostics;
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
    ///     Z下探/真空检知 → 移放料位 → Z下探/破空 → 回待机。
    ///
    /// 轴结构：0=Z(升降) 1=X(相机+吸嘴左右) 2=右Y(右工台) 3=左Y(左工台)。
    ///
    /// IO 映射（默认值见 MahjongPickConfig，按本机接线表）：
    ///   IN0: 左Y轴原点（轴3）；IN1: Z轴原点（轴0）；IN2: X轴原点（轴1）；IN3: 右Y轴原点（轴2）；
    ///   IN9: 急停按钮；IN10: 右启动按钮；IN11: 左启动按钮；IN12: 复位按钮；
    ///   IN13: Z轴吸嘴真空按钮；IN16: Z轴真空检知（真空表）；
    ///   OUT0: 三色灯绿灯；OUT1: 三色灯黄灯；OUT2: 三色灯红灯；OUT3: 三色灯蜂鸣器；
    ///   OUT4: 右启动按钮灯；OUT5: 左启动按钮灯；OUT6: Z轴吸嘴真空电磁阀。
    ///
    /// 三色灯状态机：
    ///   - 周期运行中 → 绿灯（OUT0）
    ///   - 周期完成/待机 → 黄灯（OUT1）
    ///   - NG（视觉分数不足/吸取失败）→ 红灯+蜂鸣器（NgIndicateMs 提示后自动回黄灯）
    ///   - 异常/急停 → 红灯+蜂鸣器持续（等待操作员复位，复位后回黄灯）
    ///
    /// 硬件安全（本次新增）：
    ///   - 急停按钮 IN9 软件轮询：运动等待/真空等待期间每 20ms 采样，命中即中止
    ///     并补触发 Worker 级急停（ErrorLocked 锁定 + 红灯蜂鸣，与 UI 急停同一链路）；
    ///   - 真空检知 IN16 确认：开真空后等待真空表检知，超时判定吸取失败 NG
    ///     （防止"没吸住麻将被误认为吸取成功"空跑放料）。
    /// </summary>
    public class MahjongPickProcess : StationProcessBase
    {
        private readonly MahjongPickConfig _cfg;

        /// <summary>业务过程唯一键（与 StationConfigModel.ProcessKey 对应）</summary>
        public override string ProcessKey => "MahjongPick";

        /// <summary>硬件急停按钮轮询命中标记（catch(OCE) 依据它补触发 Worker 级急停）</summary>
        private volatile bool _eStopSignalled;

        /// <summary>三色灯当前状态缓存（避免无变化重复写 OUT）</summary>
        private (bool green, bool yellow, bool red, bool buzzer)? _lastLight;

        public MahjongPickProcess(StationWorker worker, MahjongPickConfig config = null)
            : base(worker, (config ?? new MahjongPickConfig()).CardAlias)
        {
            _cfg = config ?? new MahjongPickConfig();
        }

        /// <summary>
        /// 执行一次完整吸取摆盘循环。
        /// 返回 true = 成功；false = 视觉 NG / 吸取失败 / 中途异常（Z 轴已抬至安全高度）。
        /// </summary>
        public override async Task<bool> RunAsync(CancellationToken token = default)
        {
            Log("========== 麻将吸取摆盘 开始 ==========");
            _eStopSignalled = false;
            try
            {
                // ---- Phase 0: 安全检查 ----
                EnsureNoEStop();
                LogOriginStates();
                await EnsureSafeZAsync(token).ConfigureAwait(false);
                LightRunning("周期开始");
                SetStartLamps(true);

                // ---- Phase 1: 左工台拍照定位 ----
                Log("[Phase1] 移动至左工台拍照位...");
                await MoveAbsAsync(_cfg.AxisLeftY, _cfg.LeftPhotoY, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();
                await MoveAbsAsync(_cfg.AxisX, _cfg.LeftPhotoX, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();

                Log("[Phase1] 触发视觉流（采图 → 匹配 → 标定换算）...");
                var swVision = Stopwatch.StartNew();
                await RunVisionFlowAsync($"MJ_{DateTime.Now:HHmmss}").ConfigureAwait(false);
                swVision.Stop();
                Log($"[Phase1] 视觉流执行完成，耗时 {swVision.ElapsedMilliseconds} ms（执行链共 {Worker.ActiveExecutionChain?.Count ?? 0} 个节点）");
                EnsureNoEStop();

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

                Log($"[Phase1] 视觉结果: Score={score:F3}, World=({worldX:F3}, {worldY:F3})（阈值 {_cfg.MinScore:F2}）");

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    await IndicateNgAsync("视觉 NG", token).ConfigureAwait(false);
                    return false;
                }

                // ---- Phase 2: 移动至麻将上方并吸取 ----
                Log("[Phase2] 移动至吸取位...");
                await MoveAbsAsync(_cfg.AxisX, (float)(worldX + _cfg.NozzleXOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();
                await MoveAbsAsync(_cfg.AxisLeftY, (float)(worldY + _cfg.NozzleYOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();

                Log("[Phase2] Z 下探吸取...");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.PickZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                SetOutput(_cfg.VacuumIo, true);

                // 真空检知确认：吸住才继续，超时判定吸取失败（防空跑放料）
                bool sensed = await WaitVacuumSenseAsync(token).ConfigureAwait(false);
                if (!sensed)
                {
                    Log("❌ [Phase2] 真空检知超时，未吸住麻将，本次 NG（关真空抬 Z 回待机）");
                    try { SetOutput(_cfg.VacuumIo, false); } catch (Exception ex) { Log($"⚠️ 关闭真空失败: {ex.Message}"); }
                    await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    await IndicateNgAsync("吸取失败", token).ConfigureAwait(false);
                    return false;
                }

                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);

                // ---- Phase 3: 右工台放料 ----
                Log("[Phase3] 移动至右工台放料位...");
                await MoveAbsAsync(_cfg.AxisRightY, _cfg.RightPlaceY, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();
                await MoveAbsAsync(_cfg.AxisX, _cfg.RightPlaceX, _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();

                Log("[Phase3] Z 下探放料...");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.PlaceZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                SetOutput(_cfg.VacuumIo, false);
                await Task.Delay(_cfg.VacuumOffDelayMs, token).ConfigureAwait(false);
                await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);

                // ---- Phase 4: 回待机 ----
                await BackToStandbyAsync(token).ConfigureAwait(false);

                SetStartLamps(false);
                LightStandby("周期完成");
                Log("========== 麻将吸取摆盘 成功 ==========");
                return true;
            }
            catch (OperationCanceledException)
            {
                // 急停轮询命中且工位尚未锁定 → 补触发 Worker 级急停（RapidStop + ErrorLocked + 红灯蜂鸣）。
                // 场景：操作员按硬件急停按钮 IN9，此时 UI 急停按钮未被触发、Worker 状态仍为 Running。
                if (_eStopSignalled && Worker.State != StationState.ErrorLocked)
                {
                    try
                    {
                        await Worker.EmergencyStopAsync("硬件急停按钮(IN9)轮询触发").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ 急停补触发失败: {ex.Message}");
                    }
                }
                // 停止/急停：取消令牌触发。急停时 Worker 已下发 RapidStop 停轴，
                // 这里直接上抛（由 Worker 统一收尾），**不执行抬 Z**——急停后不应再下发任何运动指令。
                Log("业务周期被取消（停止/急停），不执行抬 Z 收尾。");
                throw;
            }
            catch (Exception ex)
            {
                Log($"❌ 业务过程异常: {ex.Message}");
                // 异常兜底：尽力抬 Z 至安全高度，避免吸着料悬停低位；红灯+蜂鸣持续提示（等待复位）
                LightAlarm("业务异常");
                SetStartLamps(false);
                await EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed).ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// 急停：先关闭真空（防带料悬停）+ 红灯蜂鸣持续，再下发控制器级全轴急停（立即停住所有轴）。
        /// 与 SafeStopAsync 的区别：急停**不再下发抬 Z 运动指令**，由操作员确认机械安全后手动复位。
        /// 复位后由 Worker.SoftResetAsync → OnResetAsync 清除报警灯恢复待机黄灯。
        /// </summary>
        public override async Task EmergencyStopAsync(CancellationToken token = default)
        {
            Log("⛔ 急停：关闭真空 + 红灯蜂鸣 + 全轴急停...");
            try
            {
                if (Card != null) SetOutput(_cfg.VacuumIo, false);
            }
            catch (Exception ex)
            {
                Log($"⚠️ 急停关闭真空失败: {ex.Message}");
            }
            LightAlarm("急停");
            await base.EmergencyStopAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// 安全收尾：关闭真空（防带料悬停）+ Z 抬至安全高度 + 回待机黄灯。
        /// 由 Worker.StopAsync 在停止时调用，幂等可重复执行。
        /// </summary>
        public override async Task SafeStopAsync(CancellationToken token = default)
        {
            Log("安全收尾：关闭真空，Z 抬至安全高度...");
            try
            {
                if (Card != null) SetOutput(_cfg.VacuumIo, false);
            }
            catch (Exception ex)
            {
                Log($"⚠️ 安全收尾关闭真空失败: {ex.Message}");
            }
            SetStartLamps(false);
            LightStandby("停止收尾");
            await EmergencyRaiseZAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, token).ConfigureAwait(false);
        }

        /// <summary>
        /// 工位复位通知（Worker.SoftResetAsync 完成后调用）：
        /// 清除报警状态，恢复待机指示（黄灯 + 启动按钮灯亮）。
        /// </summary>
        public override Task OnResetAsync(CancellationToken token = default)
        {
            _eStopSignalled = false;
            Log("工位复位：清除报警灯，恢复待机指示（黄灯）。");
            try
            {
                LightStandby("复位完成");
                SetStartLamps(true);
            }
            catch (Exception ex)
            {
                Log($"⚠️ 复位恢复指示失败: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        // ==================== 硬件急停轮询 ====================

        /// <summary>
        /// 硬件急停按钮（IN9）轮询：运动等待/真空等待期间由基类每 20ms 采样。
        /// 命中即置 _eStopSignalled 并返回 true，等待循环抛 OCE 中止当前动作。
        /// 读取失败不误触发（防 IO 抖动误急停），仅记日志。
        /// </summary>
        protected override bool PollEmergencyStop()
        {
            if (Card == null) return false;
            try
            {
                var res = Card.GetInput(_cfg.InEStop);
                if (!res.Success) return false;

                bool pressed = _cfg.EStopActiveHigh ? res.Data : !res.Data;
                if (pressed && !_eStopSignalled)
                {
                    _eStopSignalled = true;
                    Log($"⛔ 检测到硬件急停按钮(IN{_cfg.InEStop})按下！");
                }
                return pressed;
            }
            catch (Exception ex)
            {
                Log($"⚠️ 急停按钮(IN{_cfg.InEStop})读取异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>运动/视觉步骤间的急停快速检查（同步，抛 OCE 由调用方统一收尾）</summary>
        private void EnsureNoEStop()
        {
            if (PollEmergencyStop())
                throw new OperationCanceledException("检测到硬件急停按钮(IN9)按下");
        }

        // ==================== 三色灯状态机 ====================

        /// <summary>
        /// 设置三色灯组合（OUT0 绿 / OUT1 黄 / OUT2 红 / OUT3 蜂鸣器）。
        /// 状态未变化时跳过写输出（避免轮询期重复写）；每切换输出一条汇总日志。
        /// </summary>
        private void SetLight(bool green, bool yellow, bool red, bool buzzer = false, string reason = null)
        {
            if (Card == null)
            {
                Log("⚠️ 运动卡不可用，跳过三色灯设置。");
                return;
            }
            var cur = (green, yellow, red, buzzer);
            if (_lastLight.HasValue && _lastLight.Value == cur) return; // 无变化不重复写

            try
            {
                Card.SetOutput(_cfg.OutLightGreen, green);
                Card.SetOutput(_cfg.OutLightYellow, yellow);
                Card.SetOutput(_cfg.OutLightRed, red);
                //Card.SetOutput(_cfg.OutBuzzer, buzzer);// 先不使用蜂鸣器
                _lastLight = cur;
                var label = $"{(green ? "绿" : "")}{(yellow ? "黄" : "")}{(red ? "红" : "")}{(buzzer ? "+蜂鸣" : "")}".Trim();
                Log($"💡 三色灯 → [{label}]" + (string.IsNullOrEmpty(reason) ? "" : $"（{reason}）"));
            }
            catch (Exception ex)
            {
                Log($"⚠️ 三色灯设置失败: {ex.Message}");
            }
        }

        private void LightRunning(string reason = null) => SetLight(true, false, false, false, reason);
        private void LightStandby(string reason = null) => SetLight(false, true, false, false, reason);
        private void LightAlarm(string reason = null) => SetLight(false, false, true, true, reason);

        /// <summary>
        /// NG 提示：红灯+蜂鸣器持续 NgIndicateMs，到时自动回黄灯待机。
        /// 急停打断时抛 OCE 由外层 catch(OCE) 统一收尾。
        /// </summary>
        private async Task IndicateNgAsync(string reason, CancellationToken token)
        {
            SetStartLamps(false);
            LightAlarm(reason);
            Log($"🔔 NG 提示（{reason}）：红灯+蜂鸣 {_cfg.NgIndicateMs}ms 后自动回待机黄灯。");
            await Task.Delay(_cfg.NgIndicateMs, token).ConfigureAwait(false);
            LightStandby("NG 提示结束");
        }

        // ==================== 启动按钮指示灯 ====================

        /// <summary>启动按钮指示灯（OUT4/OUT5）：运行中亮、结束灭（指示设备工作中）</summary>
        private void SetStartLamps(bool on)
        {
            if (!_cfg.LightStartLamps || Card == null) return;
            try
            {
                Card.SetOutput(_cfg.OutRightStartLamp, on);
                Card.SetOutput(_cfg.OutLeftStartLamp, on);
                Log($"启动按钮指示灯(OUT{_cfg.OutRightStartLamp}/OUT{_cfg.OutLeftStartLamp}) → {(on ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ 启动按钮指示灯设置失败: {ex.Message}");
            }
        }

        // ==================== 真空检知 ====================

        /// <summary>
        /// 开真空后等待真空检知（IN16 真空表）。启用检知时轮询直到检出真空或超时；
        /// 未启用时仅按 VacuumOnDelayMs 延时保压（兼容未接真空表的设备）。
        /// 返回 true = 已吸住；false = 超时未检出（吸取失败）。
        /// 等待期间同时轮询急停按钮（命中抛 OCE）。
        /// </summary>
        private async Task<bool> WaitVacuumSenseAsync(CancellationToken token)
        {
            if (!_cfg.EnableVacuumSense)
            {
                await Task.Delay(_cfg.VacuumOnDelayMs, token).ConfigureAwait(false);
                Log($"真空检知未启用，按保压延时 {_cfg.VacuumOnDelayMs}ms 判定吸取成功。");
                return true;
            }

            Log($"等待真空检知 IN{_cfg.InVacuumSense}（超时 {_cfg.VacuumSenseTimeoutMs}ms）...");
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < _cfg.VacuumSenseTimeoutMs)
            {
                token.ThrowIfCancellationRequested();
                if (PollEmergencyStop())
                    throw new OperationCanceledException("检测到硬件急停按钮(IN9)按下");

                bool raw = GetInput(_cfg.InVacuumSense);
                bool active = _cfg.VacuumSenseActiveHigh ? raw : !raw;
                if (active)
                {
                    Log($"✅ 真空检知到位（IN{_cfg.InVacuumSense} = {(raw ? "ON" : "OFF")}），麻将已吸住。");
                    return true;
                }
                await Task.Delay(20, token).ConfigureAwait(false);
            }

            Log($"⚠️ 真空检知超时：IN{_cfg.InVacuumSense} 在 {_cfg.VacuumSenseTimeoutMs}ms 内未检出真空，判定吸取失败。");
            return false;
        }

        // ==================== 诊断与安全 ====================

        /// <summary>周期开始前记录各轴原点信号状态（诊断用，不影响流程）</summary>
        private void LogOriginStates()
        {
            if (!_cfg.LogOriginOnStart || Card == null) return;
            try
            {
                Log($"原点状态: 左Y(IN{_cfg.InLeftYHome})={(GetInput(_cfg.InLeftYHome) ? "ON" : "OFF")}, " +
                    $"Z(IN{_cfg.InZHome})={(GetInput(_cfg.InZHome) ? "ON" : "OFF")}, " +
                    $"X(IN{_cfg.InXHome})={(GetInput(_cfg.InXHome) ? "ON" : "OFF")}, " +
                    $"右Y(IN{_cfg.InRightYHome})={(GetInput(_cfg.InRightYHome) ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                Log($"⚠️ 读取原点状态失败: {ex.Message}");
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
