//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationCardDeriver.cs
// 说 明: 标定任务卡派生器 + 发布留痕 + 旋转残差纯函数（P3 标定中心收敛，2026-09-05）。
//        纯静态领域函数，不触持久化/UI，可由 console runner 数据级回归。
//        · Derive(profile, allProfiles) → 0..N 张任务卡：拆分口径对齐 CalibrationLegacyMapper
//          （NinePoint→H；旋转混合→H+e；吸放→H+e(有旋转结果)；PixelScale→s；棋盘/畸变→畸变预留；
//          IsToolOffsetCalibrated→t），并叠加状态机：
//            H/s  数据 = IsCalibrated ∧ 矩阵在位 → SampleComplete；有校验记录 → Verified；
//                  VerificationRecords 含 Kind=Publish/PublishBypass 的通过记录 → Published。
//            e    数据 = 旋转证据（CalibU0/ToolCenter/ToolEcc 之一）→ 继承；无证据 → Draft
//                  （旧档案 H 已标但旋转段未跑，详情提示补 e 会话）。
//            t    数据 = 对针字段 → 继承；EyeInHand 仅放行间接对针结果（旧图像对针残留 → Expired，须重标）。
//        · 依赖联动：e/t 卡 DependentArtifactId → 在 allProfiles 中找对应 H 卡状态；
//          依赖 H 未建档/草稿/过期 → DepOk=false（卡置灰原因），卡动作被禁用。
//        · AppendPublishMarker(profile, q, bypass, note)：发布/旁路发布留痕——
//          向 profile.VerificationRecords 追加 Kind=Publish|PublishBypass 记录（硬门禁拍板④留痕）。
//        · RotationResidualCalculator：旋转采样点 → 逐点偏差表 + 圆拟合残差 RMS/MAX
//          （e 会话计算页残差明细数据源；坐标域由调用方决定——像素/机械域同算法）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using Grayson.Vision.Contracts.Calibration.Models;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>任务卡派生器（静态纯函数）</summary>
    public static class CalibrationCardDeriver
    {
        // ==================== 主入口 ====================

        /// <summary>
        /// 单条旧 profile → 0..N 张任务卡（拆分口径 = CalibrationLegacyMapper.ToArtifacts）。
        /// allProfiles 用于跨卡依赖解析（可传 null/空：只解析同 profile 内的依赖）。
        /// </summary>
        public static List<CalibrationCardModel> Derive(
            CalibrationProfile profile, IReadOnlyCollection<CalibrationProfile> allProfiles = null)
        {
            var cards = new List<CalibrationCardModel>();
            if (profile == null) return cards;

            var artifacts = CalibrationLegacyMapper.ToArtifacts(profile);
            foreach (var a in artifacts)
            {
                var card = ToCard(a, profile);
                if (card == null) continue;

                // —— 逐量数据证据覆写（mapper 的状态基于整条 profile.IsCalibrated，需按量校正）——
                ApplyQuantityEvidence(card, a, profile);

                cards.Add(card);
            }

            // —— 可选 t 草稿卡（H 已标但尚无对针结果 → 提供 TCP 对针首标入口）——
            AppendOptionalToolOffsetDraft(cards, profile);

            // —— 依赖联动（跨 profile 找依赖 H 卡）——
            foreach (var card in cards)
            {
                ResolveDependency(card, cards, allProfiles);
            }
            return cards;
        }

        /// <summary>
        /// H 已标 + 尚无 t 卡 → 补一张可选 t 草稿卡（Draft/未对针），两种相机布局均可：
        ///   · EyeToHand  → 图像对针（固定相机观测工具尖落点）；
        ///   · EyeInHand  → 间接对针（工具压特征记基准位 R_n → 抬 Z 拍同点 → TCO = H(u) − R_n）；
        ///     仅当档案带旋转证据（CalibU0/ToolCenter/ToolEcc 之一）才补——旋转证据=工具中心偏心的实证，
        ///     同心档案无需 TCO（引擎按工位档案需求另行派生，这里只保证任务卡入口存在）。
        /// 首标 t 需求弱于主线（H/e），保持可选卡；引擎按档案需求派生必做 t 时另当别论。
        /// </summary>
        private static void AppendOptionalToolOffsetDraft(
            List<CalibrationCardModel> cards, CalibrationProfile profile)
        {
            if (profile == null) return;
            if (!profile.IsCalibrated || string.IsNullOrWhiteSpace(profile.HomMatFilePath)) return;

            bool eyeInHand = profile.EyeMode == EyeMode.EyeInHand;
            // EyeInHand：无旋转证据（偏心未实证）→ 视为同心，不补卡；EyeToHand 无条件可补
            if (eyeInHand && !HasRotationEvidenceOnProfile(profile)) return;

            bool hasT = false;
            foreach (var c in cards)
            {
                if (c.Quantity == CalibrationQuantity.ToolOffset) { hasT = true; break; }
            }
            if (hasT) return;

            string station = Norm.Trim(profile.BoundStationCode);
            // 2026-09-11：优先显式槽字段（与 Planner/LegacyMapper 同口径）——
            // 只看 CameraId 会在绑物理相机后恒归一成 Cam_01，上下相机任务卡撞同一槽。
            string slot = Norm.Trim(profile.CameraSlotKey);
            if (string.IsNullOrWhiteSpace(slot)) slot = Norm.Trim(profile.CameraId);
            if (!slot.StartsWith("Cam_", StringComparison.OrdinalIgnoreCase)) slot = "Cam_01";
            string nz = Norm.Trim(profile.NozzleKey);
            if (string.IsNullOrWhiteSpace(nz)) nz = "1";

            var t = new CalibrationCardModel
            {
                ArtifactId = CalibrationArtifact.BuildArtifactId(station, CalibrationQuantity.ToolOffset, slot, nz),
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Quantity = CalibrationQuantity.ToolOffset,
                QuantityBadge = CalibrationCardText.BadgeOf(CalibrationQuantity.ToolOffset),
                QuantityText = CalibrationCardText.NameOf(CalibrationQuantity.ToolOffset),
                Layout = profile.EyeMode,
                LayoutText = CalibrationCardText.LayoutOf(profile.EyeMode),
                SlotKey = slot,
                NozzleKey = nz,
                ScopeText = "工具头 " + nz,
                PrimaryPath = CalibrationAcquirePath.AlignTool,
                PathText = CalibrationCardText.PathOf(CalibrationAcquirePath.AlignTool),
                IsRequired = false,
                Reason = eyeInHand
                    ? "EyeInHand 工具中心偏置 TCO 对针（间接对针：压特征记 R_n → 抬Z拍同点；档案带旋转证据=偏心实证）"
                    : "EyeToHand 工具中心偏置 TCO 对针（图像对针；吸放式 H 真值已吸收偏距时无需执行）",
                State = CalibrationArtifactState.Draft,
                StateText = CalibrationCardText.StateOf(CalibrationArtifactState.Draft),
                StateDetail = eyeInHand
                    ? "尚未对针（间接对针：JOG 工具尖压住工件特征记基准位 R_n → 抬 Z 拍同一特征 → 点选 → 结算 TCO = H(u) − R_n）——点『标定』进向导引导首标"
                    : "尚未对针（图像对针：工具尖对准特征锁 M_tool → 移开抓拍 → 点选实际落点）——点『标定』进向导引导首标",
                DependentArtifactId = CalibrationArtifact.BuildArtifactId(
                    station, CalibrationQuantity.HandEye, slot, null),
                DepOk = true,
                HasData = false
            };
            cards.Add(t);
        }

        /// <summary>
        /// 档案级旋转证据（供草稿卡偏心实证用）：基准角 U0 / 回转中心 O（世界或像素）/ 偏心矢 任一在位。
        ///
        /// ★★2026-09-15 修正：**删掉 `p.HasToolOffset`**。该字段由向导在"会话规格含旋转段"时
        ///   **一进会话就自动置 true**（见 CalibrationWizardViewModel 的预设段，早于任何采样）⇒
        ///   它是【声明要转】不是【真的转出了结果】。拿它当证据 ⇒ "勾了旋转类型但旋转段没跑/跑失败"
        ///   的档案会被判成"有旋转证据"（假绿：草稿卡凭空出现、e 卡显示已完成）。
        ///   ⇒ 证据一律**只认数值**。判据与 CalibrationCardDeriver.HasRotationEvidence 同源同口径。
        /// </summary>
        private static bool HasRotationEvidenceOnProfile(CalibrationProfile p)
        {
            return p.CalibU0.HasValue
                   || Math.Abs(p.ToolCenterWx) > 1e-9
                   || Math.Abs(p.ToolCenterWy) > 1e-9
                   || Math.Abs(p.ToolEccWx) > 1e-9
                   || Math.Abs(p.ToolEccWy) > 1e-9
                   || Math.Abs(p.ToolCenterPx) > 1e-9
                   || Math.Abs(p.ToolCenterPy) > 1e-9;
        }

        // ==================== 卡片构建 ====================

        private static CalibrationCardModel ToCard(CalibrationArtifact a, CalibrationProfile profile)
        {
            return new CalibrationCardModel
            {
                ArtifactId = a.ArtifactId,
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Quantity = a.Quantity,
                QuantityBadge = CalibrationCardText.BadgeOf(a.Quantity),
                QuantityText = CalibrationCardText.NameOf(a.Quantity),
                Layout = a.Layout,
                LayoutText = CalibrationCardText.LayoutOf(a.Layout),
                SlotKey = a.SlotKey,
                NozzleKey = a.NozzleKey,
                ScopeText = ScopeOf(a),
                PrimaryPath = a.PrimaryPath,
                PathText = CalibrationCardText.PathOf(a.PrimaryPath),
                IsRequired = true,
                Reason = BuildReason(a, profile),
                State = a.State,
                StateText = CalibrationCardText.StateOf(a.State),
                DependentArtifactId = a.DependentArtifactId
            };
        }

        /// <summary>卡证据量级校正：逐量数据存在性 / 发布标记解析（按量）/ EyeInHand t 过期详情</summary>
        private static void ApplyQuantityEvidence(CalibrationCardModel card, CalibrationArtifact a, CalibrationProfile p)
        {
            var verifs = a.Verifications;
            bool anyVerif = verifs != null && verifs.Count > 0;

            // 1) 逐量数据证据（mapper 状态基于整条 profile.IsCalibrated，须按量校正）
            bool dataOk = true;
            string dataNote = null;
            switch (card.Quantity)
            {
                case CalibrationQuantity.HandEye:
                    if (string.IsNullOrWhiteSpace(a.HomMatFilePath))
                    {
                        dataOk = false;
                        dataNote = "矩阵文件缺失（H 段未完成/未保存）";
                    }
                    break;

                case CalibrationQuantity.ToolRotation:
                    if (!HasRotationEvidence(a, p))
                    {
                        dataOk = false;
                        dataNote = "旋转段未执行/无结果（该档案 H 可能已标但 e 未跑——用 e 段会话补标）";
                    }
                    break;
            }

            if (card.State != CalibrationArtifactState.Expired && !dataOk)
            {
                card.State = CalibrationArtifactState.Draft;
                card.StateText = CalibrationCardText.StateOf(card.State);
                card.StateDetail = dataNote;
            }
            else if (card.State == CalibrationArtifactState.Draft && dataOk)
            {
                // e/s/t 有结果但整条 profile 未标 IsCalibrated（如 e 会话只算偏心、H 未做）→ 按量提升
                card.State = CalibrationArtifactState.SampleComplete;
                card.StateText = CalibrationCardText.StateOf(card.State);
            }

            // 2) 发布标记（Kind=Publish/PublishBypass 且通过、Note 带 q=<量徽标>）→ Published（旁路留痕）
            if (card.State != CalibrationArtifactState.Expired)
            {
                var pub = FindPublishMarker(verifs, card.QuantityBadge);
                if (pub != null)
                {
                    // P4 快照联动：发布标记带当时数据指纹，若与当前档案不一致 → 数据已变更 → 该量过期需重标
                    string snap = BuildSnapshotKey(p, card.Quantity);
                    if (!string.IsNullOrWhiteSpace(pub.SnapshotKey)
                        && !string.IsNullOrWhiteSpace(snap)
                        && !string.Equals(pub.SnapshotKey, snap, StringComparison.Ordinal))
                    {
                        card.State = CalibrationArtifactState.Expired;
                        card.StateText = CalibrationCardText.StateOf(card.State);
                        card.StateDetail = "发布后档案数据已变更（发布快照不一致）：当前数据 ≠ 发布时指纹，需重新标定该量后再发布（已发布记录保留可查）";
                    }
                    else
                    {
                        card.State = CalibrationArtifactState.Published;
                        card.BypassPublished = string.Equals(pub.Kind, "PublishBypass", StringComparison.Ordinal);
                        card.StateText = CalibrationCardText.StateOf(card.State);
                        card.StateDetail = card.BypassPublished
                            ? "旁路发布（「我知道风险」已确认，留痕：" + TrimNote(pub.Note) + "）"
                            : "发布门禁通过（" + TrimNote(pub.Note) + "）";
                    }
                }
                else if (anyVerif && card.State == CalibrationArtifactState.SampleComplete)
                {
                    // 保守：有校验记录（非发布标记，如校验台验收）即视为过验收 → Verified
                    card.State = CalibrationArtifactState.Verified;
                    card.StateText = CalibrationCardText.StateOf(card.State);
                }
            }

            // 3) EyeInHand t 卡 Expired 的原因分级（2026-09-08）：
            //    · 历史残留（ToolOffsetMethod=LegacyUnknown/图像对针）→ 旧图像对针不适用，须按间接对针重标；
            //    · 间接对针结果也 Expired（发布后数据变更/快照不一致）→ 保留步骤 2 的快照文案，不再覆盖成"布局不适用"。
            if (card.State == CalibrationArtifactState.Expired
                && card.Quantity == CalibrationQuantity.ToolOffset
                && card.Layout == EyeMode.EyeInHand
                && p.ToolOffsetMethod != ToolOffsetMethod.EyeInHandIndirect)
            {
                card.EyeInHandTExpired = true;
                card.StateDetail = "EyeInHand 历史对针结果已过期：该结果非『间接对针』产出（旧图像对针/未知残留）——相机与工具同体观测不到工具尖。请重标：JOG 工具尖压住工件特征记基准位 R_n → 抬 Z 拍同一特征 → 点选 → 结算 TCO = H(u) − R_n";
            }

            // 4) 最迟校验时间
            var latest = GetLatestVerification(verifs);
            card.LatestVerifiedTime = latest != null ? (DateTime?)latest.VerifiedTime : null;
            // 可用数据 = 至少采样完成；Draft(未做) 与 Expired(已作废) 都不算可用
            card.HasData = card.State == CalibrationArtifactState.SampleComplete
                           || card.State == CalibrationArtifactState.Verified
                           || card.State == CalibrationArtifactState.Published;
        }

        /// <summary>
        /// 旋转数据证据（e 卡）：圆心/偏心/基准角任一有值即算跑过旋转段。
        ///
        /// ★★2026-09-15 修正：**删掉 `p.HasToolOffset`**。它是向导"会话含旋转段"时自动置位的**声明**，
        ///   不是结果（详见 HasRotationEvidenceOnProfile 的说明）。保留它的后果是本函数的调用点
        ///   （`case CalibrationQuantity.ToolRotation`）会把"旋转段未执行/无结果"的档案判成 dataOk
        ///   ⇒ e 卡从 Draft 被提升为 SampleComplete ⇒ **UI 显示已完成，实际没标**（假绿）。
        ///   该调用点的 dataNote 本来写的就是"旋转段未执行/无结果"，却被这个 OR 项短路了。
        /// </summary>
        private static bool HasRotationEvidence(CalibrationArtifact a, CalibrationProfile p)
        {
            return p.CalibU0.HasValue
                   || Math.Abs(a.RotCenterX) > 1e-9
                   || Math.Abs(a.RotCenterY) > 1e-9
                   || Math.Abs(p.ToolEccWx) > 1e-9
                   || Math.Abs(p.ToolEccWy) > 1e-9
                   || Math.Abs(p.ToolCenterPx) > 1e-9
                   || Math.Abs(p.ToolCenterPy) > 1e-9;
        }

        // ==================== 依赖联动 ====================

        private static void ResolveDependency(
            CalibrationCardModel card, List<CalibrationCardModel> ownCards,
            IReadOnlyCollection<CalibrationProfile> allProfiles)
        {
            if (string.IsNullOrWhiteSpace(card.DependentArtifactId)) return;

            CalibrationCardModel dep = FindCard(ownCards, card.DependentArtifactId);
            if (dep == null && allProfiles != null)
            {
                foreach (var other in allProfiles)
                {
                    if (other == null || string.Equals(other.Id, card.ProfileId, StringComparison.Ordinal)) continue;
                    var arts = CalibrationLegacyMapper.ToArtifacts(other);
                    foreach (var a in arts)
                    {
                        if (string.Equals(a.ArtifactId, card.DependentArtifactId, StringComparison.Ordinal))
                        {
                            dep = ToCard(a, other);
                            if (dep != null) ApplyQuantityEvidence(dep, a, other);
                            break;
                        }
                    }
                    if (dep != null) break;
                }
            }

            if (dep == null)
            {
                card.DepOk = false;
                card.DepText = "依赖 H 未建档（先完成并发布 H 段）";
                return;
            }
            switch (dep.State)
            {
                case CalibrationArtifactState.Published:
                    card.DepOk = true;
                    card.DepText = "依赖 H ✓ 已发布";
                    break;
                case CalibrationArtifactState.Verified:
                    card.DepOk = true;
                    card.DepText = "依赖 H ✓ 已验证";
                    break;
                case CalibrationArtifactState.SampleComplete:
                    card.DepOk = true;
                    card.DepText = "依赖 H ✓ 采样完成（可配合；建议先校验）";
                    break;
                case CalibrationArtifactState.Expired:
                    card.DepOk = false;
                    card.DepText = "依赖 H ✗ 已过期（需重新标定 H）";
                    break;
                default:
                    card.DepOk = false;
                    card.DepText = "依赖 H ✗ 未完成（先完成 H 段：引导 → 发布）";
                    break;
            }
        }

        private static CalibrationCardModel FindCard(List<CalibrationCardModel> cards, string artifactId)
        {
            if (cards == null) return null;
            foreach (var c in cards)
            {
                if (string.Equals(c.ArtifactId, artifactId, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        // ==================== 发布留痕（拍板④） ====================

        /// <summary>
        /// 发布/旁路发布留痕：向 profile.VerificationRecords 追加 Kind 标记记录。
        /// bypass=false → Kind=Publish（体检全绿）；true → Kind=PublishBypass（「我知道风险」，note 记原因）。
        /// 返回追加的记录；profile.VerificationRecords 为 null 时自动建表。
        /// </summary>
        public static CalibrationVerificationRecord AppendPublishMarker(
            CalibrationProfile profile, CalibrationQuantity quantity, bool bypass, string note)
        {
            if (profile == null) return null;
            if (profile.VerificationRecords == null) profile.VerificationRecords = new List<CalibrationVerificationRecord>();

            var rec = new CalibrationVerificationRecord
            {
                VerifiedTime = DateTime.Now,
                TotalPoints = 1,
                PassedCount = 1,
                Kind = bypass ? "PublishBypass" : "Publish",
                // P4 发布快照：记录该量当时的数据指纹，Derive 比对当前档案 → 数据变更自动 Expired（需重标）
                SnapshotKey = BuildSnapshotKey(profile, quantity),
                Note = "q=" + CalibrationCardText.BadgeOf(quantity)
                       + (bypass ? " 旁路发布（我知道风险）" : " 发布门禁通过")
                       + (string.IsNullOrWhiteSpace(note) ? string.Empty : "；" + note)
            };
            profile.VerificationRecords.Add(rec);
            profile.UpdatedAt = DateTime.Now;
            return rec;
        }

        /// <summary>取该量的发布标记记录（Note 含 q=&lt;量徽标&gt;；无则 null）</summary>
        private static CalibrationVerificationRecord FindPublishMarker(
            List<CalibrationVerificationRecord> verifs, string quantityBadge)
        {
            if (verifs == null) return null;
            string token = "q=" + (string.IsNullOrWhiteSpace(quantityBadge) ? "H" : quantityBadge);
            CalibrationVerificationRecord newest = null;
            foreach (var v in verifs)
            {
                if (v == null) continue;
                bool kind = string.Equals(v.Kind, "Publish", StringComparison.Ordinal)
                            || string.Equals(v.Kind, "PublishBypass", StringComparison.Ordinal);
                if (!kind || !IsRecordPassed(v)) continue;
                if (v.Note == null || v.Note.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (newest == null || v.VerifiedTime > newest.VerifiedTime) newest = v;
            }
            return newest;
        }

        // ==================== P4 发布快照（数据变更 → Expired 联动） ====================

        /// <summary>
        /// 该量当前的数据指纹（发布时记录 / Derive 时比对）。
        /// 覆盖 H（矩阵路径）/ e（旋转证据字段集）/ t（对针偏距）；s 与畸变无数值载体 → null（不判）。
        /// 数值用 InvariantCulture 固定精度格式化，保证发布与比对两次调用间确定性一致。
        /// null/空 → 该量无数据（未发布/无数值载体），不做快照判定。
        /// </summary>
        public static string BuildSnapshotKey(CalibrationProfile p, CalibrationQuantity quantity)
        {
            if (p == null) return null;
            switch (quantity)
            {
                case CalibrationQuantity.HandEye:
                    // ★★2026-09-15 修正：不能只用路径 —— 九点重标恰恰是【同路径覆盖内容】的
                    //   （矩阵文件名由方案名派生 ⇒ 方案名不变则路径不变）。旧写法 "H|路径" 让
                    //   "重标过、内容已变"的档案指纹与发布时**完全相同** ⇒ 卡片继续显示"已发布"
                    //   （数据已变、指纹不变 ⇒ Expired 联动失效 = 静默陈旧，与本次反复抓的假绿同族）。
                    //   现把矩阵【内容摘要】折进指纹：存在 → "H|路径|<md5前16>/<bytes>"。
                    //   ⚠ 格式变更后，历史发布标记（旧格式 "H|路径"）必然不等 ⇒ 首次比对即判 Expired。
                    //     这是**保守方向**（宁可要求重标/重发布），符合"发布后数据变更需重新确认"的本意。
                    return string.IsNullOrWhiteSpace(p.HomMatFilePath)
                        ? null
                        : "H|" + p.HomMatFilePath + "|" + FileDigest(p.HomMatFilePath);

                case CalibrationQuantity.ToolRotation:
                    return "e|" + FmtNum(p.CalibU0) + "|" + FmtNum(p.ToolCenterWx) + "|" + FmtNum(p.ToolCenterWy)
                         + "|" + FmtNum(p.ToolEccWx) + "|" + FmtNum(p.ToolEccWy) + "|" + FmtNum(p.CalibZ);

                case CalibrationQuantity.ToolOffset:
                    return "t|" + FmtNum(p.ToolOffsetWx) + "|" + FmtNum(p.ToolOffsetWy);

                default:
                    return null;
            }
        }

        private static string FmtNum(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.######", CultureInfo.InvariantCulture) : "-";
        }

        /// <summary>
        /// ★2026-09-15：矩阵文件内容摘要（md5 前 16 hex + 字节数），供 H 的发布指纹用。
        /// 文件不可读/不存在 → "?"（显式表示"取不到"，绝不静默当成"没变"）。
        /// </summary>
        private static string FileDigest(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return "?";
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var fs = System.IO.File.OpenRead(path))
                {
                    var hash = md5.ComputeHash(fs);
                    var sb = new System.Text.StringBuilder(20);
                    for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString() + "/" + new System.IO.FileInfo(path).Length;
                }
            }
            catch
            {
                return "?";
            }
        }

        private static string FmtNum(double v)
        {
            return v.ToString("0.######", CultureInfo.InvariantCulture);
        }

        /// <summary>记录整体判定通过（总数&gt;0 且通过数≥总数；发布标记也走此判定）</summary>
        private static bool IsRecordPassed(CalibrationVerificationRecord rec)
        {
            return rec != null && rec.TotalPoints > 0 && rec.PassedCount >= rec.TotalPoints;
        }

        private static CalibrationVerificationRecord GetLatestVerification(List<CalibrationVerificationRecord> verifs)
        {
            if (verifs == null || verifs.Count == 0) return null;
            CalibrationVerificationRecord best = null;
            foreach (var v in verifs)
            {
                if (v == null) continue;
                if (best == null || v.VerifiedTime > best.VerifiedTime) best = v;
            }
            return best;
        }

        private static string TrimNote(string note)
        {
            if (string.IsNullOrWhiteSpace(note)) return "无备注";
            string n = note.Trim();
            return n.Length > 60 ? n.Substring(0, 57) + "…" : n;
        }

        // ==================== 辅助 ====================

        private static string ScopeOf(CalibrationArtifact a)
        {
            bool cameraLevel = a.Quantity == CalibrationQuantity.HandEye
                               || a.Quantity == CalibrationQuantity.PixelScale
                               || a.Quantity == CalibrationQuantity.LensDistortion;
            return cameraLevel
                ? "相机槽 " + (string.IsNullOrWhiteSpace(a.SlotKey) ? "Cam_?" : a.SlotKey)
                : "吸嘴 " + (string.IsNullOrWhiteSpace(a.NozzleKey) ? "1" : a.NozzleKey);
        }

        private static string BuildReason(CalibrationArtifact a, CalibrationProfile p)
        {
            string baseReason;
            switch (a.Quantity)
            {
                case CalibrationQuantity.HandEye:
                    baseReason = "引导/纠偏定位 ⇒ 像素↔机械平面仿射 H（矩阵文件为生产消费协议，路径不变）";
                    break;
                case CalibrationQuantity.ToolRotation:
                    baseReason = "带角度对位/吸放偏心 ⇒ 旋转中心+偏心 e（依赖 H 做机械域映射）";
                    break;
                case CalibrationQuantity.ToolOffset:
                    baseReason = "工具中心偏置 TCO 对针 t（按布局：EyeToHand 图像对针 / EyeInHand 间接对针）";
                    break;
                case CalibrationQuantity.PixelScale:
                    baseReason = "飞拍/纯当量换算 ⇒ 像素当量 s";
                    break;
                default:
                    baseReason = "畸变内参（预留能力，暂不可执行）";
                    break;
            }
            return string.IsNullOrWhiteSpace(a.Note) ? baseReason : baseReason + "；" + a.Note;
        }
    }

    // ==================== 旋转残差计算（e 会话计算页数据源） ====================

    /// <summary>旋转采样点（坐标域由调用方统一：像素/机械域同算法）</summary>
    public class RotationResidualPoint
    {
        public double AngleDeg { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    /// <summary>旋转残差逐点行</summary>
    public class RotationResidualRow
    {
        public int Index { get; set; }
        public double AngleDeg { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>点到拟合圆心的半径</summary>
        public double Radius { get; set; }
        /// <summary>该点半径相对平均半径的偏差（|r_i − R|）</summary>
        public double Residual { get; set; }
    }

    /// <summary>旋转残差报告（e 会话计算页 / 发布体检数据源）</summary>
    public class RotationResidualReport
    {
        public bool CenterProvided { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        /// <summary>平均半径</summary>
        public double MeanRadius { get; set; }
        /// <summary>残差 RMS（mm/px 同源）</summary>
        public double RmsResidual { get; set; }
        /// <summary>最大单点偏差</summary>
        public double MaxResidual { get; set; }
        public double MaxResidualAngleDeg { get; set; }
        public int PointCount { get; set; }
        public List<RotationResidualRow> Rows { get; set; } = new List<RotationResidualRow>();
    }

    /// <summary>旋转残差计算器（静态纯函数；最少二乘圆拟合 = Kasa 法）</summary>
    public static class RotationResidualCalculator
    {
        /// <summary>
        /// 计算旋转采样残差。centerX/centerY 为已知圆心则直接使用（会话拟合结果），
        /// 否则对点列做 Kasa 圆拟合得圆心。点少于 3 → 返回空报告（PointCount=0）。
        /// </summary>
        public static RotationResidualReport Compute(
            IReadOnlyList<RotationResidualPoint> pts, double? centerX = null, double? centerY = null)
        {
            var report = new RotationResidualReport();
            if (pts == null || pts.Count < 3) return report;

            double cx, cy;
            if (centerX.HasValue && centerY.HasValue)
            {
                cx = centerX.Value;
                cy = centerY.Value;
                report.CenterProvided = true;
            }
            else
            {
                if (!FitCircle(pts, out cx, out cy)) return report;
            }
            report.CenterX = cx;
            report.CenterY = cy;
            report.PointCount = pts.Count;

            // 逐点半径
            var radii = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                radii[i] = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            }
            double sum = 0;
            foreach (var r in radii) sum += r;
            double meanR = sum / pts.Count;
            report.MeanRadius = meanR;

            // 逐行偏差
            double sumSq = 0;
            double maxRes = -1;
            double maxAngle = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                double res = Math.Abs(radii[i] - meanR);
                sumSq += res * res;
                if (res > maxRes)
                {
                    maxRes = res;
                    maxAngle = p.AngleDeg;
                }
                report.Rows.Add(new RotationResidualRow
                {
                    Index = i + 1,
                    AngleDeg = p.AngleDeg,
                    X = p.X,
                    Y = p.Y,
                    Radius = radii[i],
                    Residual = res
                });
            }
            report.RmsResidual = Math.Sqrt(sumSq / pts.Count);
            report.MaxResidual = maxRes < 0 ? 0 : maxRes;
            report.MaxResidualAngleDeg = maxAngle;
            return report;
        }

        /// <summary>Kasa 最小二乘圆拟合（正规方程，数值稳定；点数 ≥3）</summary>
        public static bool FitCircle(IReadOnlyList<RotationResidualPoint> pts, out double cx, out double cy)
        {
            cx = 0;
            cy = 0;
            if (pts == null || pts.Count < 3) return false;
            return FitCircleCore(pts, out cx, out cy);
        }

        /// <summary>Kasa 正规方程（Ax=b, 3×3）数值稳定实现</summary>
        private static bool FitCircleCore(IReadOnlyList<RotationResidualPoint> pts, out double cx, out double cy)
        {
            cx = 0;
            cy = 0;
            int n = pts.Count;
            // 中心化：减去均值提高数值稳定性
            double mx = 0, my = 0;
            foreach (var p in pts) { mx += p.X; my += p.Y; }
            mx /= n; my /= n;

            double uu = 0, vv = 0, uv = 0, uuu = 0, vvv = 0, uvv = 0, uuv = 0;
            foreach (var p in pts)
            {
                double u = p.X - mx, v = p.Y - my;
                uu += u * u; vv += v * v; uv += u * v;
                uuu += u * u * u; vvv += v * v * v;
                uuv += u * u * v; uvv += u * v * v;
            }
            // 正规方程： [uu uv; uv vv] * [a; b] = [0.5*(uuu+uvv); 0.5*(vvv+uuv)]
            double a11 = uu, a12 = uv, a21 = uv, a22 = vv;
            double r1 = 0.5 * (uuu + uvv);
            double r2 = 0.5 * (vvv + uuv);
            double det = a11 * a22 - a12 * a21;
            if (Math.Abs(det) < 1e-12) return false;
            double ca = (r1 * a22 - a12 * r2) / det;   // 圆心相对均值的 u 向
            double cb = (a11 * r2 - r1 * a21) / det;   // 圆心相对均值的 v 向
            cx = mx + ca;
            cy = my + cb;
            return true;
        }
    }
}
