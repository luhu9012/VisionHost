//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ConsumptionPublishAudit.cs
// 说 明: 生产端【口径跨源核对】——把"档案判定的口径"与"工位配置实读的口径"比一遍。
//
// 为什么需要它（2026-09-16 现场实锤，撞机事故的正因）：
//   X_obj 的口径有两个数据源：
//     · 校验台 / 发布链：读【标定档案】Config\Calibrations\<方案名>__<Id8>.json
//     · 生产端        ：读【工位配置】ProcessConfigJson 的一套扁平字段
//   两者之间靠"发布链"这一根桥连起来。桥断了（忘了发布 / 发布顺序被覆盖 / 手工改库），
//   两边就各自判定、**没有任何机制会发现**——直到机器撞上去。
//
//   ST_002 实况（Cam_A：H 档声明 杆端域 + RodOffsetInProduction=true，
//   e 档 ToolEccW=(5.943,132.172)mm ⇒ |b|=132.3mm）：
//     档案侧判定 = ③固定相机+杆端域(补b) ⇒ 吸点 = H(u) + b
//     工位侧实读 = ②固定相机直拍工件    ⇒ 吸点 = H(u)        ← 少 132mm
//   ⇒ 引导定位扎在离工件 132mm 的地方 ⇒ 撞机。
//
//   ⚠ 为什么"生产端自己跟自己比"查不出来（口径漂移对账为何这次失效）：
//     ConsumptionTag 与 HandEyeInNozzleDomain/HasRodOffset 都写在**同一套扁平字段**里 ⇒
//     **同源**。同源的两个量一起错时永远"一致"（发布链没跑 ⇒ 两个键都不存在 ⇒ 双双取默认）。
//     互校两量必须**不同源**——档案就是那个不同源的第二把尺子。
//
// 分工（重要，别让本类越权）：
//   · 本类**只读、只报告**：不改判分型真源（生产端仍以工位配置为准，即"发布冻结"语义不变）。
//   · 是否"拦下不许运动"由 StationProcessBase.EnforceConsumptionGate 决定。
//   · 档案里出现多个非下相机槽（真·多上相机工位）时**不猜**：只报"无法定位上相机槽"。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Repository.Implementations;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>生产端口径跨源核对（档案 vs 工位配置）。</summary>
    public static class ConsumptionPublishAudit
    {
        /// <summary>核对结果（纯数据；Lines 是给人看的逐行说明，已含处方）。</summary>
        public sealed class AuditResult
        {
            /// <summary>是否定位到了本工位唯一的【上相机槽】档案（false = 无法核对，只能告警）</summary>
            public bool ArchiveFound;

            /// <summary>命中的相机槽键（Cam_A …）</summary>
            public string SlotKey;

            /// <summary>档案侧判定的口径</summary>
            public ConsumptionDecision ArchiveDecision;

            /// <summary>档案侧判定的标签（与 ConsumptionTag 同格式）</summary>
            public string ArchiveTag;

            /// <summary>档案侧 b 的量级（mm）——用来告诉现场"这一档会偏多少"</summary>
            public double RodOffsetMagnitudeMm;

            /// <summary>档案侧 b 的来源档案名（t 或 e 档）</summary>
            public string RodOffsetSource;

            /// <summary>两侧口径是否冲突（档案可核对方能判定；档案不可得时恒 false）</summary>
            public bool Conflict;

            /// <summary>
            /// ★档案里的**独立证据**是否指向"杆端域"——判据与契约 InferNozzleDomain 同源：
            ///   ① 旋转拟合圆半径 &gt; 20mm（卡片被吸嘴吸住只会画 1~2mm；延伸杆要画 100+mm）；
            ///   ② 特征模板名含「杆 / 延申 / 延伸 / mark」。
            /// 用途：与"生效分型"对账 —— 证据说杆端域、分型却不带 b ⇒ 自相矛盾，必须拦。
            /// </summary>
            public bool RodEndEvidence;

            /// <summary>上述证据的文字说明（日志用）</summary>
            public string RodEndEvidenceText;

            /// <summary>逐行说明（可直接打进工位日志）</summary>
            public List<string> Lines { get; } = new List<string>();

            // ── 以下三个字段是 2026-09-16 新增的**复用载体**（纯附加，不改本类任何既有判据）：
            //    数值对账（ReconcileValues）要用同一个槽门面，而"按工位→按槽→建门面"的查找
            //    只应存在一份（判据写两遍＝靠巧合正确）。故把查找产物挂在这里由调用方复用，
            //    不再另写一套查找。

            /// <summary>本工位命中的【上相机槽】消费门面（ArchiveFound 为 true 时非 null）</summary>
            public CameraCalibrationBundle ArchiveBundle;

            /// <summary>
            /// 本工位命中的【下相机槽】消费门面（无下相机槽时为 null）。
            /// 数值对账要用它的 R_cdown 像素中心与标定高度 CalibZ。
            /// 多个下相机槽时**不猜**：只记第一个。
            /// </summary>
            public CameraCalibrationBundle DownCameraBundle;

            /// <summary>本工位绑定的全部标定档案（数值对账复查用；不参与分型判定）</summary>
            public List<CalibrationProfile> StationProfiles;
        }

        /// <summary>
        /// 把工位配置实读的口径与档案判定的口径比一遍。
        /// 全程只读；任何异常都降级为"无法核对"（不抛、不影响生产启动）。
        /// </summary>
        /// <param name="stationCode">工位码（ST_002）</param>
        /// <param name="stationDecision">生产端按工位配置判出的口径</param>
        public static AuditResult Audit(string stationCode, ConsumptionDecision stationDecision)
        {
            var res = new AuditResult();
            if (stationDecision == null) return res;
            if (string.IsNullOrWhiteSpace(stationCode))
            {
                res.Lines.Add("（工位码为空 ⇒ 无法定位标定档案，本次不做跨源核对）");
                return res;
            }

            List<CalibrationProfile> profiles;
            try
            {
                var repo = new JsonCalibrationProfileRepository();
                profiles = repo.GetAll()
                               .Where(po => po?.Model != null)
                               .Select(po => po.Model)
                               .ToList();
                // 损坏文件不静默（仓库把它记录在 LastError 里）
                if (!string.IsNullOrWhiteSpace(repo.LastError))
                    res.Lines.Add("⚠ 读取标定档案时有异常（下列判定可能不完整）：" + repo.LastError);
            }
            catch (Exception ex)
            {
                res.Lines.Add("⚠ 读取标定档案失败 ⇒ 本次无法做跨源核对（不是「通过」，是「没核」）：" + ex.Message);
                return res;
            }

            var inStation = profiles
                .Where(p => string.Equals((p.BoundStationCode ?? "").Trim(), stationCode.Trim(),
                                          StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (inStation.Count == 0)
            {
                res.Lines.Add($"（工位 {stationCode} 在标定档案目录里没有任何档案 ⇒ 无法跨源核对；"
                              + "若本工位本该有标定产物，说明档案目录/工位绑定不对）");
                return res;
            }

            // 按相机槽分组 → 每槽建门面（与校验台、发布链**同一个** CameraCalibrationBundle）
            var cands = new List<(string Slot, CameraCalibrationBundle Bundle, ConsumptionDecision Dec)>();
            foreach (var g in inStation.GroupBy(SlotOf, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var bundle = CameraCalibrationBundle.Build(inStation, stationCode, g.Key, DummyMapper);
                    if (bundle == null) continue;
                    if (bundle.IsDownCamera)
                    {
                        // 下相机槽语义正交，不参与"口径"核对（行为与改动前一致）；
                        // 但数值对账需要它的 R_cdown / CalibZ，故先记下门面再跳过。
                        if (res.DownCameraBundle == null) res.DownCameraBundle = bundle;
                        continue;
                    }
                    cands.Add((g.Key, bundle, bundle.ResolveDecision()));
                }
                catch (Exception ex)
                {
                    res.Lines.Add($"⚠ 槽 {g.Key} 的档案门面构建失败，已跳过：" + ex.Message);
                }
            }

            if (cands.Count == 0)
            {
                res.Lines.Add($"（工位 {stationCode} 只有下相机槽 / 或上相机槽档案不可用 ⇒ 无法跨源核对）");
                return res;
            }
            if (cands.Count > 1)
            {
                // 真·多上相机工位：不猜哪一台在引导定位 ⇒ 只报告，不判定冲突
                res.Lines.Add($"（工位 {stationCode} 有 {cands.Count} 个非下相机档案槽 "
                              + $"[{string.Join("、", cands.Select(c => c.Slot))}] ⇒ 无法确定哪一台在引导定位，"
                              + "本次不做跨源核对。若是复合工位的上/下相机，请给档案补齐 CameraSlotKey）");
                return res;
            }

            var (slot, b, archDec) = cands[0];
            res.ArchiveFound = true;
            res.SlotKey = slot;
            res.ArchiveDecision = archDec;
            res.ArchiveTag = archDec.Tag;
            res.ArchiveBundle = b;          // 数值对账复用（见 AuditResult 注释）
            res.StationProfiles = inStation;
            res.RodOffsetMagnitudeMm = Math.Sqrt(b.RodOffsetWx * b.RodOffsetWx + b.RodOffsetWy * b.RodOffsetWy);
            res.RodOffsetSource = b.RodOffsetSource;

            res.Lines.Add($"跨源核对（槽 {slot}）——");
            res.Lines.Add($"  · 档案判定口径 ：[{archDec.Tag}]  {archDec.Formula}");
            res.Lines.Add($"  · 工位配置实读 ：[{stationDecision.Tag}]  {stationDecision.Formula}");
            if (res.RodOffsetMagnitudeMm > 1e-9)
            {
                res.Lines.Add($"  · 档案里的 b   ：({b.RodOffsetWx:F3},{b.RodOffsetWy:F3})mm "
                              + $"|b|={res.RodOffsetMagnitudeMm:F3}mm（来源「{res.RodOffsetSource ?? "未记录"}」）");
            }
            res.Lines.Add($"  · 档案声明摘要 ：{archDec.DeclSummary}");

            res.Conflict = !string.Equals(archDec.Tag, stationDecision.Tag, StringComparison.Ordinal);

            // ---- 独立证据：这一槽到底是不是"杆端域"（与契约 InferNozzleDomain 同源的两条判据）----
            var binp = b.BuildInputs();
            var ev = new List<string>();
            if (binp.RotationFitRadiusMm.HasValue
                && Math.Abs(binp.RotationFitRadiusMm.Value) > CalibrationConsumptionContract.LongRodRadiusMm)
            {
                ev.Add($"旋转拟合圆半径 {binp.RotationFitRadiusMm.Value:F3}mm > "
                       + $"{CalibrationConsumptionContract.LongRodRadiusMm:F0}mm（卡片吸在吸嘴上只画 1~2mm）");
            }
            string tpl = binp.FeatureTemplateName;
            if (!string.IsNullOrWhiteSpace(tpl)
                && (tpl.IndexOf("杆", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("延申", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("延伸", StringComparison.Ordinal) >= 0
                    || tpl.IndexOf("mark", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                ev.Add($"特征模板名「{tpl}」含杆/mark 字样");
            }
            res.RodEndEvidence = ev.Count > 0;
            res.RodEndEvidenceText = res.RodEndEvidence ? string.Join("；", ev) : null;
            if (res.RodEndEvidence)
                res.Lines.Add($"  · 独立证据（指向杆端域）：{res.RodEndEvidenceText}");

            return res;
        }

        // ==================== 数值对账（2026-09-16 新增：发布链最小化的"对账期"） ====================
        //
        // 背景（为什么要有它）：
        //   工位配置里那套扁平字段是**标定档案的手抄副本**——发布链把档案里的数改名后抄一份，
        //   生产端只读副本。抄写一旦漏做 / 被别的槽覆盖 / 被手工改库，**没有任何机制会发现**，
        //   直到落点偏出去（132.305mm 撞机就是这条链断掉的结果）。
        //   现有闸门只对账了【分型】（口径标签），数值这一层仍是"各算各的"。
        //
        // 本方法只做一件事：把**档案侧复算值**与**工位配置实读值**逐项摆出来比。
        // 语义边界（守死，不许扩）：
        //   · **只比不改**：不写任何配置、不改任何数值来源、不抛异常（失败也是"打日志报告"）。
        //     生产端数值仍取工位配置 —— 本方法不参与决定，只负责把"看不见的分叉"变可见。
        //   · **判据不重写**：档案侧一律走 `CameraCalibrationBundle`（与校验台/发布链/闸门同一个门面），
        //     不在此处重抄"该取哪个字段"。b 的"t 优先"由门面内部 ResolveRodOffset 决定，与本类无关。
        //   · **不可得就明说"没核过"**：拿不到映射器的项（下相机轴投影常量需像素→世界映射，
        //     而本层不引用 HalconWrapper）必须显式标"未核过"，不许折叠成"通过"。

        /// <summary>一条"档案 vs 工位快照"的数值对账行。</summary>
        public sealed class ValueRow
        {
            /// <summary>工位配置键名（与 ProcessConfigJson 里的键对应）</summary>
            public string Key;

            /// <summary>档案侧复算值（人话文本；不可比时写原因）</summary>
            public string ArchiveText;

            /// <summary>工位配置实读值（人话文本）</summary>
            public string SnapshotText;

            /// <summary>false = 本项**没核过**（必须写明理由，不许当成"通过"）</summary>
            public bool Comparable;

            /// <summary>仅在 Comparable=true 时有意义</summary>
            public bool Match;
        }

        /// <summary>数值对账结果（纯数据 + 可直打的日志行）。</summary>
        public sealed class ValueReconciliation
        {
            public bool ArchiveFound;
            public string SlotKey;

            /// <summary>可比项数（**分母**：报"0 处不一致"时必须连它一起说）</summary>
            public int ComparableCount;

            /// <summary>不一致项数</summary>
            public int MismatchCount;

            /// <summary>不可比项数（需要映射器 / 档案缺字段等）</summary>
            public int NotComparableCount;

            public List<ValueRow> Rows { get; } = new List<ValueRow>();
            public List<string> Lines { get; } = new List<string>();
        }

        /// <summary>
        /// 浮点容差：工位配置是 `float` 存储、档案是 `double` ⇒ 同值的差异在 1e-4 量级（mm/px）。
        /// 取 1e-3 留一个数量级余量：真分叉（漏发=0、被覆盖=另一个数）都远大于它，
        /// 不会把"存储舍入"误报成分叉（否则判据一上线就被噪声淹掉＝等于没有）。
        /// </summary>
        private const double ValueTolerance = 1e-3;

        /// <summary>
        /// 把档案侧复算值与工位配置实读值逐项对账（**只比不改**，永不抛）。
        /// </summary>
        /// <param name="audit">闸门已算出的跨源核对结果（复用其档案查找，不二次查找）</param>
        /// <param name="snapshot">生产端**正在使用**的工位配置（权威口径，非盘中重读）</param>
        public static ValueReconciliation ReconcileValues(AuditResult audit, VisionPickPlaceConfig snapshot)
        {
            var res = new ValueReconciliation();
            if (audit == null || snapshot == null)
            {
                res.Lines.Add("⚠ [对账] 未执行：缺少跨源核对结果或工位配置 ⇒ **判据失效：没核过**。");
                return res;
            }
            if (!audit.ArchiveFound || audit.ArchiveBundle == null)
            {
                res.Lines.Add("⚠ [对账] 未定位到本工位的上相机槽档案（或无档案绑定）⇒ "
                              + "**判据失效：没核过，不是通过了**；本次数值仍取工位配置快照。");
                return res;
            }

            res.ArchiveFound = true;
            res.SlotKey = audit.SlotKey;
            var up = audit.ArchiveBundle;
            var upInp = up.BuildInputs();          // 与发布链/校验台同一门面（b 的 t 优先在此内部决定）
            var archDec = audit.ArchiveDecision;
            var down = audit.DownCameraBundle;

            res.Lines.Add($"[对账] 档案 vs 工位配置快照（槽 {audit.SlotKey}）—— 逐项核数值");

            // ---- 上相机：b（发布链从同槽 e 档 ToolEccW 或对针 t 取，写进 RodOffsetWx/Wy）----
            AddNum(res, "RodOffsetWx", upInp.RodOffsetWx, snapshot.RodOffsetWx, "mm");
            AddNum(res, "RodOffsetWy", upInp.RodOffsetWy, snapshot.RodOffsetWy, "mm");

            // ---- 上相机：HasRodOffset（发布链的 enableB = 槽内声明 && |b|>1mm）----
            double upBi = Math.Sqrt(upInp.RodOffsetWx * upInp.RodOffsetWx + upInp.RodOffsetWy * upInp.RodOffsetWy);
            AddBool(res, "HasRodOffset", up.RodOffsetInProductionDeclared && upBi > 1.0, snapshot.HasRodOffset,
                $"（档案侧判据=槽内 RodOffsetInProduction 且 |b|={upBi:F4}mm>1mm）");

            // ---- 上相机：O / 拍照位 / U0（档案在 e 档，回退 H 档）----
            AddNum(res, "RotCenterWx", up.RotationCenterWx, snapshot.RotCenterWx, "mm");
            AddNum(res, "RotCenterWy", up.RotationCenterWy, snapshot.RotCenterWy, "mm");
            AddNum(res, "PhotoBaseX", up.RotationCenterProfile?.BasePosX, snapshot.PhotoBaseX, "mm");
            AddNum(res, "PhotoBaseY", up.RotationCenterProfile?.BasePosY, snapshot.PhotoBaseY, "mm");
            AddNum(res, "ToolAlignU", up.H?.CalibU0, snapshot.ToolAlignU, "°");
            AddNum(res, "CalibPlaceU", up.H?.PickBaseU, snapshot.CalibPlaceU, "°");

            // ---- 上相机：口径声明（发布链抄的是档案里的同名声明）----
            AddBool(res, "HandEyeInNozzleDomain", up.H?.HandEyeInNozzleDomain, snapshot.HandEyeInNozzleDomain);
            AddBool(res, "NozzleAxisCoaxial", up.NozzleAxisCoaxialDeclared, snapshot.NozzleAxisCoaxial);

            // ---- 上相机：派生标志（发布链写的是 dec.NeedO）----
            double? needO = archDec == null ? (double?)null : (archDec.NeedO ? 1.0 : 0.0);
            AddNum(res, "NeedsOCompensation", needO, snapshot.NeedsOCompensation ? 1.0 : 0.0, "bool",
                "（档案侧=契约判定 NeedO；0=否 1=是）");
            AddNum(res, "CameraMountEih", needO, snapshot.CameraMountEih ? 1.0 : 0.0, "bool",
                "（档案侧=契约判定 NeedO；0=否 1=是）");
            AddNum(res, "IsEyeInHand(派生)", needO,
                (snapshot.NeedsOCompensation || snapshot.CameraMountEih) ? 1.0 : 0.0, "bool",
                "（生产端读的是两者或；与档案侧同一判据）");

            // ---- 下相机槽：R_cdown（像素）与标定高度 ----
            if (down == null)
            {
                const string noDownSlot = "（档案里没有下相机槽；工位配置却有值）";
                AddNum(res, "DownCameraRotCenterCol", null, snapshot.DownCameraRotCenterCol, "px", noDownSlot);
                AddNum(res, "DownCameraRotCenterRow", null, snapshot.DownCameraRotCenterRow, "px", noDownSlot);
                AddNum(res, "DownCameraCalibZ", null, snapshot.DownCameraCalibZ, "mm", noDownSlot);
            }
            else
            {
                var rcp = down.DownRotCenterProfile;
                AddNum(res, "DownCameraRotCenterCol", rcp?.DownRotCenterCol, snapshot.DownCameraRotCenterCol, "px",
                    $"（下相机槽 {down.SlotKey} 的 e 档 R_cdown）");
                AddNum(res, "DownCameraRotCenterRow", rcp?.DownRotCenterRow, snapshot.DownCameraRotCenterRow, "px",
                    $"（下相机槽 {down.SlotKey} 的 e 档 R_cdown）");
                AddNum(res, "DownCameraCalibZ", down.H?.CalibZ, snapshot.DownCameraCalibZ, "mm",
                    $"（下相机槽 {down.SlotKey} 的 H 档标定高度）");
            }

            // ---- 现场 A/B 结论（b 的符号）：档案里**根本没有**这两个字段 ⇒ 只能明说"没核过" ----
            //   ⚠ 这是"工位配置不再是档案副本"的**唯一真缺口**（其余键都是别名/同名/可复算派生）。
            //     在它进档案之前，本项永远核不了 —— 对账表里留一行，就是为了让"核不了"这件事可见，
            //     而不是让整张表看起来全绿。
            res.Rows.Add(new ValueRow
            {
                Key = "RodOffsetSign/Declared",
                ArchiveText = "档案无此字段（现场 A/B 结论，尚未进档案）",
                SnapshotText = $"{(snapshot.RodOffsetSign > 0 ? "+1" : "-1")}"
                               + $"（Declared={YesNo(snapshot.RodOffsetSignDeclared)}）",
                Comparable = false
            });

            // ---- 需要像素→世界映射的项：本层没有映射器，**只能明说没核** ----
            res.Rows.Add(new ValueRow
            {
                Key = "DownCameraAxisWx/Wy",
                ArchiveText = "需像素→世界映射（本层无映射器）",
                SnapshotText = $"({snapshot.DownCameraAxisWx:F4},{snapshot.DownCameraAxisWy:F4})",
                Comparable = false
            });
            res.Lines.Add("  · DownCameraAxisWx/Wy = H_down(R_cdown)：**未核过** —— 它是"
                          + "「用下相机 H 矩阵把 R_cdown 映射到机械位」的派生量，"
                          + $"而 Grayson.Vision.Core 不引用 HalconWrapper（无像素→世界映射能力）。"
                          + $"工位配置现值=({snapshot.DownCameraAxisWx:F4},{snapshot.DownCameraAxisWy:F4})mm。"
                          + "要核它得在【手里有矩阵的那一步】算（下相机子流程的 CalibrationApply 节点）。");

            // ---- 汇总：报结论必须连**分母**一起报 ----
            foreach (var row in res.Rows)
            {
                if (!row.Comparable)
                {
                    res.NotComparableCount++;
                    res.Lines.Add($"  ⚠ {row.Key,-24} 档案 {row.ArchiveText,-30} ｜ 工位 {row.SnapshotText}");
                    continue;
                }
                res.ComparableCount++;
                if (row.Match) continue;
                res.MismatchCount++;
                res.Lines.Add($"  ❌ {row.Key,-24} 档案 {row.ArchiveText,-30} ｜ 工位 {row.SnapshotText}");
            }

            string tail = $"⇒ 可比 {res.ComparableCount} 项，不一致 {res.MismatchCount} 项，未核过 {res.NotComparableCount} 项。";
            if (res.MismatchCount > 0)
            {
                res.Lines.Add("❌ [对账] 工位配置快照与标定档案**不一致**：" + tail);
                res.Lines.Add("   ⇒ 快照已过期 / 被别的槽覆盖 / 被手工改过。本次**不改行为**（数值仍取工位配置，"
                              + "口径仍按档案），但请按上面列出的键到标定中心对本槽重新发布一次，消除分叉。");
            }
            else
            {
                res.Lines.Add("✔ [对账] 快照与档案在可比项上一致：" + tail);
            }
            return res;
        }

        // ---------- 对账行的两种加法器（不可比时**必须**写清理由）----------

        private static void AddNum(ValueReconciliation res, string key, double? archive, double snapshotValue,
            string unit, string archiveNote = null)
        {
            if (!archive.HasValue)
            {
                res.Rows.Add(new ValueRow
                {
                    Key = key,
                    ArchiveText = archiveNote == null ? "（档案无此字段/未标定）" : "（档案不可得）",
                    SnapshotText = Fmt(snapshotValue) + unit,
                    Comparable = false
                });
                return;
            }
            double a = archive.Value;
            res.Rows.Add(new ValueRow
            {
                Key = key,
                ArchiveText = Fmt(a) + unit,
                SnapshotText = Fmt(snapshotValue) + unit,
                Comparable = true,
                Match = Math.Abs(a - snapshotValue) <= ValueTolerance
            });
        }

        private static void AddBool(ValueReconciliation res, string key, bool? archive, bool snapshotValue,
            string archiveNote = null)
        {
            if (!archive.HasValue)
            {
                res.Rows.Add(new ValueRow
                {
                    Key = key,
                    ArchiveText = archiveNote == null ? "（档案未声明）" : "（档案不可得）",
                    SnapshotText = YesNo(snapshotValue),
                    Comparable = false
                });
                return;
            }
            res.Rows.Add(new ValueRow
            {
                Key = key,
                ArchiveText = YesNo(archive.Value),
                SnapshotText = YesNo(snapshotValue),
                Comparable = true,
                Match = archive.Value == snapshotValue
            });
        }

        private static string Fmt(double v) => v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);

        private static string YesNo(bool b) => b ? "是" : "否";

        /// <summary>槽键：显式 CameraSlotKey 优先，否则按与迁移器/规划器同口径的 GuessSlotKey 猜。</summary>
        private static string SlotOf(CalibrationProfile p)
        {
            if (!string.IsNullOrWhiteSpace(p.CameraSlotKey)) return p.CameraSlotKey.Trim();
            try { return CalibrationProfileSessionPlanner.GuessSlotKey(p); }
            catch { return "Cam_01"; }
        }

        /// <summary>
        /// 占位映射器：本核对只取"口径分型/声明"，从不消费像素→世界映射
        /// （CameraCalibrationBundle.Build 存下委托但构建期不调用它）。
        /// </summary>
        private static readonly CameraCalibrationBundle.PixelMapper DummyMapper =
            (double px, double py, out double wx, out double wy, out string error) =>
            {
                wx = 0; wy = 0; error = "口径核对不需要像素映射";
                return false;
            };
    }
}
