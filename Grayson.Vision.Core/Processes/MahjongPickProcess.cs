using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 双滑台工位「工件吸取摆盘」业务过程。
    /// 
    /// 硬件结构复述：

    //1. ** 相机、吸嘴机械刚性固定在 X 滑台上，两者之间 XY 机械偏移是固定常量，Z 轴也挂载在 X 滑台；X 轴左右移动；**
    //2. ** 左工件工台：由左 Y 轴（轴 3）前后滑动；右摆盘工台：右 Y 轴（轴 2）前后滑动；工台带着工件跑，相机 / 吸嘴不动 Y，工台动 Y。**

    //> 
    //> 关键点：** 目标物体（工件）在 Y 方向是工台带着它运动，相机、吸嘴本身 Y 坐标不变化！**
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
    ///     （防止"没吸住工件被误认为吸取成功"空跑放料）。
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

        /// <summary>上一周期的匹配结果（跨周期一致性自检用，仅诊断，不参与运动控制）</summary>
        private double _lastMatchRow, _lastMatchCol, _lastMatchAngle;
        private bool _lastMatchValid;

        /// <summary>上一次示教的视觉输出（跨周期公式判定用，仅诊断）</summary>
        private double _lastTeachWx, _lastTeachWy;
        private bool _lastTeachValid;

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
            Log("========== 工件吸取摆盘 开始 ==========");
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

                double matchRow = GetOutValue<double>(matchNode, "MatchRow");
                double matchCol = GetOutValue<double>(matchNode, "MatchCol");
                double matchAng = GetOutValue<double>(matchNode, "MatchAngle");
                Log($"[Phase1] 视觉结果: Score={score:F3}, World=({worldX:F3}, {worldY:F3})（阈值 {_cfg.MinScore:F2}）");
                Log($"[Phase1] 匹配像素: Row={matchRow:F1} Col={matchCol:F1} Angle={matchAng:F2}°");

                // ---- 匹配一致性自检 ----
                // 「换了位置/角度就吸不到」的头号根因往往不是引导公式，而是匹配压根没跟上目标：
                // 模板 ROI 远大于目标本体时，find_shape_model 学到的是 ROI 内的背景（工台纹理），
                // 于是无论目标怎么挪，输出的像素与角度都几乎不变（锁死在模板创建时的位姿）。
                // 这里跨周期比对，一旦「两次结果几乎一模一样」就直接点名，省去现场逐项排查。
                if (_lastMatchValid)
                {
                    double dRow = matchRow - _lastMatchRow;
                    double dCol = matchCol - _lastMatchCol;
                    double dPix = Math.Sqrt(dRow * dRow + dCol * dCol);
                    if (dPix < 1.0 && Math.Abs(matchAng - _lastMatchAngle) < 0.5)
                    {
                        Log("⚠️ [Phase1] 匹配结果与上一周期几乎一致（位移 < 1px、角度差 < 0.5°）——" +
                            "若工件的位置/角度确实变了，说明匹配锁死在背景或模板原位姿，并未真正找到工件。" +
                            "请检查模板 ROI 是否远大于工件本体（建议目标本体占 ROI 面积 50% 以上）并重建模板。");
                    }
                }
                _lastMatchRow = matchRow;
                _lastMatchCol = matchCol;
                _lastMatchAngle = matchAng;
                _lastMatchValid = true;

                if (score < _cfg.MinScore)
                {
                    Log($"❌ [Phase1] 匹配分数 {score:F3} 低于阈值 {_cfg.MinScore:F2}，本次 NG 跳过");
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    await IndicateNgAsync("视觉 NG", token).ConfigureAwait(false);
                    return false;
                }

                // ---- Phase 2: 移动至工件上方并吸取 ----
                // 🌟 视觉引导坐标的镜像修正（详见 MahjongPickConfig.MirrorGuideEnabled 注释）：
                //    矩阵输出 = "让标定位置的 Mark 出现在该像素所需的轴坐标"；
                //    吸取需要 = "追上已挪窝的目标" = 2 × 拍照位 − 矩阵输出。
                //    两者在工件恰处标定(模板)点位时相等，一偏移就差 2×偏移量。
                double guideX = _cfg.MirrorGuideEnabled ? (2 * _cfg.LeftPhotoX - worldX) : worldX;
                double guideY = _cfg.MirrorGuideEnabled ? (2 * _cfg.LeftPhotoY - worldY) : worldY;

                double curX = worldX + _cfg.NozzleXOffset;
                double curY = worldY + _cfg.NozzleYOffset;
                double mirX = 2 * _cfg.LeftPhotoX - worldX + _cfg.NozzleXOffset;
                double mirY = 2 * _cfg.LeftPhotoY - worldY + _cfg.NozzleYOffset;
                Log($"[Phase2] 视觉输出 wx={worldX:F3}, wy={worldY:F3}" +
                    $"（= 工件相对「标定 Mark 位置」的偏移量 mm；把工件放回标定时 Mark 所在处，这两个数应接近 0）；" +
                    $"拍照位=({_cfg.LeftPhotoX:F3}, {_cfg.LeftPhotoY:F3})");
                Log($"[Phase2] 落点候选 → 当前公式 X={curX:F3} Y={curY:F3} ｜ 镜像公式 X={mirX:F3} Y={mirY:F3}" +
                    $"（MirrorGuideEnabled={_cfg.MirrorGuideEnabled} → 采用{(_cfg.MirrorGuideEnabled ? "镜像" : "当前")}）");

                // NozzleOffset 的现场标定提示：这两个值是「相机→吸嘴」的机械常量，只需标定一次；
                // 标定方法是走位到落点后目测吸嘴与工件中心的偏差 (ΔX, ΔY)，直接加到原值上即可。
                // 与工件每次放在工台哪个位置无关——位置差异由 wx/wy 反映，不靠这两个偏移吸收。
                Log($"[Phase2] 修偏方法：走位后若吸嘴与工件中心差 (ΔX, ΔY) mm，" +
                    $"把 NozzleXOffset 由 {_cfg.NozzleXOffset:F1} 改为 {_cfg.NozzleXOffset:F1}+ΔX、" +
                    $"NozzleYOffset 由 {_cfg.NozzleYOffset:F1} 改为 {_cfg.NozzleYOffset:F1}+ΔY（标定一次即可，之后工件放哪都不用再改）。");

                // ---- 示教模式：不走位、不吸取，只把「反算 NozzleOffset 的公式」摆出来 ----
                // 基准用「吸嘴对准工件背面图案」而不是「相机视野中心」：后者屏幕上没有参照物，
                // 肉眼估不准；前者下探目测或真空吸附一试便知。标定矩阵只提供相对量，
                // 绝对基准就靠这一步定死——定完之后工件放工台任何位置都不用再改。
                if (_cfg.TeachMode)
                {
                    Log("[Phase2] 👉【示教模式】不执行走位与吸取。请手动点动轴，让吸嘴正对工件背面圆/十字的中心，");
                    Log("[Phase2] 👉【示教模式】然后记下此刻 X 轴(轴1)读数 X* 与 左Y 轴(轴3)读数 Y*，代入下面两式：");
                    Log($"[Phase2] 👉【示教模式】  采用【当前公式】(+wx/+wy)：" +
                        $"NozzleXOffset = X* − ({worldX:F3})    NozzleYOffset = Y* − ({worldY:F3})");
                    Log($"[Phase2] 👉【示教模式】  采用【镜像公式】(−wx/−wy)：" +
                        $"NozzleXOffset = X* + ({worldX:F3})    NozzleYOffset = Y* + ({worldY:F3})");
                    Log($"[Phase2] 👉【示教模式】对照：程序算出的落点是 X={curX:F3} Y={curY:F3}（镜像为 X={mirX:F3} Y={mirY:F3}）；" +
                        $"若手动对准后读回的 X*、Y* 与其中一组接近，那组公式就是对的。");
                    // 一行式反馈记录：把本周期所有关键数收进一行，人工对准工件读回 X*/Y* 后，
                    // 直接复制此行（补上 X*、Y*）发回即可完成 NozzleOffset 标定，无需任何界面操作。
                    Log($"[示教记录] wx={worldX:F3}, wy={worldY:F3}, 拍照位=({_cfg.LeftPhotoX:F2},{_cfg.LeftPhotoY:F2}), " +
                        $"当前NozzleOffset=({_cfg.NozzleXOffset:F1},{_cfg.NozzleYOffset:F1}), 当前落点=({curX:F2},{curY:F2})/({mirX:F2},{mirY:F2}), " +
                        $"→ 对准工件后读回 X*=____, Y*=____（连同此行一起反馈）");

                    // 跨周期判定：同一个 NozzleOffset 必须能同时满足两个不同位置。
                    // 把两次的 X*/Y* 分别代入两式，算出的 N 一致的那组公式才是对的——
                    // 错的那组会相差 2×(wx₂−wx₁)，两次一比就露馅，且全程不走位零风险。
                    if (_lastTeachValid)
                    {
                        Log($"[Phase2] 👉【示教模式】上一次：wx={_lastTeachWx:F3} wy={_lastTeachWy:F3} → " +
                            $"本次变化 Δwx={worldX - _lastTeachWx:+0.000;-0.000} Δwy={worldY - _lastTeachWy:+0.000;-0.000}");
                        Log("[Phase2] 👉【示教模式】判定：把两次的 X*/Y* 各按两式算出 N，" +
                            "两次结果一致（差值 <1mm）的那组公式正确；另一组会差出 2×Δwx。");
                    }
                    _lastTeachWx = worldX;
                    _lastTeachWy = worldY;
                    _lastTeachValid = true;

                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    return true;
                }

                Log("[Phase2] 移动至吸取位...");
                await MoveAbsAsync(_cfg.AxisX, (float)(guideX + _cfg.NozzleXOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();
                await MoveAbsAsync(_cfg.AxisLeftY, (float)(guideY + _cfg.NozzleYOffset), _cfg.XySpeed, _cfg.SettleMs, token: token).ConfigureAwait(false);
                EnsureNoEStop();

                Log("[Phase2] Z 下探吸取...");
                await MoveAbsAsync(_cfg.AxisZ, _cfg.PickZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                SetOutput(_cfg.VacuumIo, true);

                // 真空检知确认：吸住才继续，超时判定吸取失败（防空跑放料）
                bool sensed = await WaitVacuumSenseAsync(token).ConfigureAwait(false);
                if (!sensed)
                {
                    // 失败时把本次用到的完整坐标链回显一遍：吸取失败几乎都是落点偏了，
                    // 把"看到什么 → 算成什么 → 走到哪"摊开，配合成功案例对比即可定位到是哪一环错。
                    Log("❌ [Phase2] 真空检知超时，未吸住工件，本次 NG（关真空抬 Z 回待机）");
                    Log($"❌ [Phase2] 失败回显：匹配 Score={score:F3} 像素=({matchRow:F1}, {matchCol:F1}) 角度={matchAng:F2}° " +
                        $"→ wx={worldX:F3} wy={worldY:F3} → 实际落点 X={curX:F3} Y={curY:F3}（镜像本应 X={mirX:F3} Y={mirY:F3}）");
                    Log("❌ [Phase2] 排查提示：抬 Z 后请保持工件不动，手动点动轴到吸嘴正对工件，读回 X*/Y* " +
                        "与上面的落点比较——差多少就是 NozzleOffset 要补多少（或开示教模式 TeachMode 直接取数）。");
                    try { SetOutput(_cfg.VacuumIo, false); } catch (Exception ex) { Log($"⚠️ 关闭真空失败: {ex.Message}"); }
                    await MoveAbsAsync(_cfg.AxisZ, _cfg.SafeZ, _cfg.ZSpeed, settleMs: 0, token: token).ConfigureAwait(false);
                    await BackToStandbyAsync(token).ConfigureAwait(false);
                    await IndicateNgAsync("吸取失败", token).ConfigureAwait(false);
                    return false;
                }

                // 检知到位 ≠ 吸牢：真空度刚过门槛时吸附力仍在爬升，立即抬 Z 有概率带不牢。
                // 额外保压一段时间，确保吸附充分建立后 Z 轴才动作（可界面调 VacuumDwellAfterSenseMs）。
                if (_cfg.VacuumDwellAfterSenseMs > 0)
                {
                    Log($"[Phase2] 真空保压 {_cfg.VacuumDwellAfterSenseMs}ms（等吸附充分建立后再抬 Z）...");
                    await Task.Delay(_cfg.VacuumDwellAfterSenseMs, token).ConfigureAwait(false);
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
                Log("========== 工件吸取摆盘 成功 ==========");
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
                    Log($"✅ 真空检知到位（IN{_cfg.InVacuumSense} = {(raw ? "ON" : "OFF")}），工件已吸住。");
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
            await MoveAbsAsync(_cfg.AxisLeftY, _cfg.StandbyY, _cfg.XySpeed, settleMs: 0, token: token).ConfigureAwait(false);
        }
    }
}
