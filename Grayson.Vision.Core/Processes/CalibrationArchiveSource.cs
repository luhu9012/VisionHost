//===================================================================================
// 文件名: CalibrationArchiveSource.cs
// 说 明: 标定产物的**单一读值入口** —— 开工时把工位配置里那批"标定产物键"用
//        本工位相机槽标定档案里的值覆写一遍，之后生产端读到的就全是档案值。
//
// 为什么要这一步（2026-09-16，发布链最小化 P1）：
//   改造前是"档案 → 发布链手抄 → 工位配置扁平字段 → 生产端读扁平字段"（发布链已于 2026-09-16 P2 整条删除）。
//   手抄这一步没有任何机制保证它做了、做对了、没被别人覆盖（ST_002 少补 |b|=132mm
//   撞机就是这么来的：那份快照从来没按口径契约发布过）。
//   实测已经把"该不该删这根桥"量清楚了（P0 对账期：档案复算 vs 工位快照 逐项比
//   17 项可比 · 0 项不一致 · 2 项需映射器）⇒ 桥两岸本来就是等价的，桥可以拆。
//
// 本类的做法（**不新增存储、不新增入口**）：
//   把工位配置对象的那批标定产物键**在内存里**覆写成档案值 —— 就像原来发布链做的事，
//   区别是：① 不再落盘（工位配置那份快照从此不再承担这个职责）；② 每次开工都重做
//   （不会再"过期"）；③ 覆写明细逐条打在日志里，看得见。
//   工位配置对象由 StationProcessFactory 现造、只被本过程持有 ⇒ 内存覆写不会漏回持久层。
//
// 边界（守死）：
//   · 只覆写**标定产物**（H/e/O/b/符号/声明/下相机三常量），工艺位点与节拍参数一个都不碰。
//   · 档案不可得 / 字段缺失 ⇒ **不静默按默认值蒙**：把"没被覆写"的键列出来（那是回退到快照的项）。
//   · 失败也只是"没换源"，不抛异常 —— 换源失败要能开出可诊断的日志，而不是让工位起不来。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>标定产物读值真源的唯一入口（档案优先；见类头注释）。</summary>
    public static class CalibrationArchiveSource
    {
        /// <summary>换源结果（纯数据 + 可直打的日志行）。</summary>
        public sealed class HydrationReport
        {
            /// <summary>是否真的用档案覆写了（false = 仍按工位配置快照跑）</summary>
            public bool Applied;

            /// <summary>命中的相机槽键（Cam_A …）</summary>
            public string SlotKey;

            /// <summary>未换源的原因（Applied=false 时必有内容；写明"判据失效：没核过"这类字样）</summary>
            public string Reason;

            /// <summary>实际发生变化的键（值确实变了）</summary>
            public List<string> Changed { get; } = new List<string>();

            /// <summary>档案有此值、但与快照相同 ⇒ 未变化（仍是档案值，只是没变）</summary>
            public List<string> SameAsSnapshot { get; } = new List<string>();

            /// <summary>档案**没有**这个值 ⇒ 该键仍取工位配置快照（回退项，必须可见）</summary>
            public List<string> NotCovered { get; } = new List<string>();

            /// <summary>非致命告警（槽内符号矛盾、矩阵读不到等）</summary>
            public List<string> Warnings { get; } = new List<string>();
        }

        /// <summary>
        /// 按档案覆写 <paramref name="cfg"/> 上的标定产物键。
        /// 调用点：`VisionPickPlaceProcess.BuildConsumptionDecision()` 的最前面（唯一入口）。
        /// </summary>
        /// <param name="cfg">工位过程配置（**就地**覆写；调用方应保证它是本过程私有实例）</param>
        /// <param name="stationCode">工位码（用于按 BoundStationCode 定位档案）</param>
        public static HydrationReport Hydrate(VisionPickPlaceConfig cfg, string stationCode)
        {
            var rep = new HydrationReport();
            if (cfg == null)
            {
                rep.Reason = "配置为空";
                return rep;
            }

            try
            {
                // ① 先按【快照】判一次口径 —— 只用作 ConsumptionPublishAudit 的入参（它内部会把两侧都算出来）
                var snapshotDec = CalibrationConsumptionContract.FromPublishedConfig(
                    isDownCamera: false,
                    handEyeInNozzleDomain: cfg.HandEyeInNozzleDomain,
                    needsOCompensation: cfg.NeedsOCompensation,
                    cameraMountEih: cfg.CameraMountEih,
                    hasRodOffset: cfg.HasRodOffset,
                    nozzleAxisCoaxial: cfg.NozzleAxisCoaxial,
                    rodOffsetSign: cfg.RodOffsetSign,
                    upCameraViaDownCamera: cfg.UseUpCameraViaDownCamera,
                    hasCrossCameraMap: cfg.CrossCameraMapReady);

                // ② 复用现成的"按工位→按槽→建门面"解析（与闸门同一个，不另写一套查找）
                var audit = ConsumptionPublishAudit.Audit(stationCode, snapshotDec);
                if (!audit.ArchiveFound || audit.ArchiveBundle == null)
                {
                    rep.Reason = "未定位到本工位的上相机槽档案 ⇒ 仍按工位配置快照跑"
                                 + "（**这不是「通过」，是「没核过」**）";
                    return rep;
                }

                var b = audit.ArchiveBundle;
                var inp = b.BuildInputs();
                var dec = b.ResolveDecision();
                rep.Applied = true;
                rep.SlotKey = audit.SlotKey;

                // ---- ③ 口径声明（决定"算哪一档"的三个布尔 + 两个声明）----
                Bool(rep, "HandEyeInNozzleDomain", cfg.HandEyeInNozzleDomain,
                     b.H?.HandEyeInNozzleDomain, v => cfg.HandEyeInNozzleDomain = v);
                Bool(rep, "NozzleAxisCoaxial", cfg.NozzleAxisCoaxial,
                     b.NozzleAxisCoaxialDeclared, v => cfg.NozzleAxisCoaxial = v);

                // NeedsOCompensation / CameraMountEih / IsEyeInHand 是**派生量**（都是契约判定 NeedO），
                // 不再单独存三份真源：档案侧算出来是啥就是啥。
                Bool(rep, "NeedsOCompensation", cfg.NeedsOCompensation, dec.NeedO,
                     v => cfg.NeedsOCompensation = v);
                Bool(rep, "CameraMountEih", cfg.CameraMountEih, dec.NeedO,
                     v => cfg.CameraMountEih = v);

                // ---- ④ 杆端偏移 b（数值 + 是否带进生产 + 符号）----
                double bMag = Math.Sqrt(inp.RodOffsetWx * inp.RodOffsetWx + inp.RodOffsetWy * inp.RodOffsetWy);
                Bool(rep, "HasRodOffset", cfg.HasRodOffset,
                     b.RodOffsetInProductionDeclared && bMag > 1.0, v => cfg.HasRodOffset = v);
                Num(rep, "RodOffsetWx", cfg.RodOffsetWx, inp.RodOffsetWx, v => cfg.RodOffsetWx = v);
                Num(rep, "RodOffsetWy", cfg.RodOffsetWy, inp.RodOffsetWy, v => cfg.RodOffsetWy = v);

                // ★★符号：这是"工位配置不再是档案副本"的最后一个真缺口，2026-09-16 才补进档案。
                //   两个字段分工必须分清：
                //     RodOffsetSign         = 判定成了什么（+1/−1）—— 只在档案里**真的判过**时才覆写；
                //     RodOffsetSignDeclared = **有没有人真的判过** —— 闸门硬拦 D 条读的是它。
                //   绝不能写成"取档案 RodOffsetSign ?? 1"：那会把"从没判定过"伪装成"已判定 +1"，
                //   而符号选错偏 2|b|（本工位 264mm），比不补更危险 ⇒ 这一项 fail-closed。
                if (b.RodOffsetSignDeclared.HasValue)
                {
                    if (b.RodOffsetSignConflicting)
                    {
                        rep.Warnings.Add("槽内多份档案对 b 的符号判定**互相矛盾**"
                                         + $"（{b.RodOffsetSignDeclaredCount} 份里取值不只一种）⇒ "
                                         + $"本槽不采信，仍按工位配置的 {(cfg.RodOffsetSign > 0 ? "+1" : "-1")}。"
                                         + "请把槽内档案的 RodOffsetSign 统一。");
                    }
                    else
                    {
                        double sign = b.RodOffsetSignDeclared.Value < 0 ? -1.0 : 1.0;
                        Bool(rep, "RodOffsetSignDeclared", cfg.RodOffsetSignDeclared, true,
                             v => cfg.RodOffsetSignDeclared = v);
                        if (Math.Abs(cfg.RodOffsetSign - sign) > 1e-6)
                        {
                            string oldSign = cfg.RodOffsetSign < 0 ? "-1" : "+1";
                            cfg.RodOffsetSign = (float)sign;
                            rep.Changed.Add($"RodOffsetSign: 快照 {oldSign} → 档案 {(sign < 0 ? "-1" : "+1")}");
                        }
                        else
                        {
                            rep.SameAsSnapshot.Add("RodOffsetSign");
                        }
                    }
                }
                else
                {
                    // 档案里没有 ⇒ 保持快照值，但把"没判定过"这件事显式留在报告里（闸门会据此硬拦）
                    rep.NotCovered.Add("RodOffsetSign/Declared（档案里没判定过 ⇒ 仍取快照 "
                                       + $"{(cfg.RodOffsetSign > 0 ? "+1" : "-1")}"
                                       + $"，Declared={YesNo(cfg.RodOffsetSignDeclared)}）");
                }

                // ---- ⑤ 旋转中心 O / 拍照基准 / 基准角 U0 / 取放基准角 ----
                Num(rep, "RotCenterWx", cfg.RotCenterWx, b.RotationCenterWx, v => cfg.RotCenterWx = v);
                Num(rep, "RotCenterWy", cfg.RotCenterWy, b.RotationCenterWy, v => cfg.RotCenterWy = v);
                Num(rep, "PhotoBaseX", cfg.PhotoBaseX, b.RotationCenterProfile?.BasePosX, v => cfg.PhotoBaseX = v);
                Num(rep, "PhotoBaseY", cfg.PhotoBaseY, b.RotationCenterProfile?.BasePosY, v => cfg.PhotoBaseY = v);
                Num(rep, "ToolAlignU", cfg.ToolAlignU, b.H?.CalibU0, v => cfg.ToolAlignU = v);
                Num(rep, "CalibPlaceU", cfg.CalibPlaceU, b.H?.PickBaseU, v => cfg.CalibPlaceU = v);

                // ---- ⑥ 吸嘴偏心 e（吸嘴域化的那条路线用）----
                Num(rep, "Nozzle1EccX", cfg.Nozzle1EccX, b.E?.ToolOffsetPureWx, v => cfg.Nozzle1EccX = v);
                Num(rep, "Nozzle1EccY", cfg.Nozzle1EccY, b.E?.ToolOffsetPureWy, v => cfg.Nozzle1EccY = v);

                // ---- ⑦ 下相机：R_cdown（像素）与标定高度（数值不走映射器，直接取）----
                var down = audit.DownCameraBundle;
                if (down == null)
                {
                    rep.NotCovered.Add("DownCameraRotCenterCol/Row、DownCameraCalibZ"
                                       + "（档案里没有下相机槽）");
                }
                else
                {
                    var rcp = down.DownRotCenterProfile;
                    Num(rep, "DownCameraRotCenterCol", cfg.DownCameraRotCenterCol,
                        rcp?.DownRotCenterCol, v => cfg.DownCameraRotCenterCol = v);
                    Num(rep, "DownCameraRotCenterRow", cfg.DownCameraRotCenterRow,
                        rcp?.DownRotCenterRow, v => cfg.DownCameraRotCenterRow = v);
                    Num(rep, "DownCameraCalibZ", cfg.DownCameraCalibZ,
                        down.H?.CalibZ, v => cfg.DownCameraCalibZ = v);
                }

                // ---- ⑧ 下相机轴投影常量 H_down(R_cdown)：**就地复算**，不再依赖发布链抄一份 ----
                //   此前这里只能"未核过"，因为旧认识是"要 Halcon 才能做像素→世界映射"。
                //   实际上那只是 `CalibrationService.MapPixelToWorld` 的两步：
                //   读 6 个数的 .tup + 一次 2×3 仿射点乘 ⇒ 纯算术，不需要 Halcon 运行时
                //   （见 HomMat2D；实测复算值与**原发布链写入值**差 2e-6mm = float32 存储舍入量级）。
                // ---- ⑨ ★上下机映射（2026-09-17 增补）：本槽产物是跨相机映射还是手眼矩阵 ----
                //   判据只看【量】，不看路径（理由见 CameraCalibrationBundle.IsUpCameraViaDownCamera）。
                //   ⚠ 这里**绝不**"读不出跨相机映射就静默改走别的口径"：路由照写，读不出的后果由契约
                //     ⑦ 档硬拦承担。静默换档 = 拿下相机的像素去解释上相机的图，落点与工件无关，
                //     比直接报错危险得多。
                if (b.H != null && b.H.Quantity == CalibrationQuantity.CrossCameraMap)
                {
                    Bool(rep, "UseUpCameraViaDownCamera", cfg.UseUpCameraViaDownCamera, true,
                         v => cfg.UseUpCameraViaDownCamera = v);

                    //跨相机映射矩阵必须**真的能读**才算齐备；读不出 ⇒ 保持 false ⇒ 契约 ⑦ 档硬拦。
                    HomMat2D h1;
                    string h1err;
                    bool h1ok = HomMat2D.TryLoadForProfile(b.H, out h1, out h1err);
                    Bool(rep, "CrossCameraMapReady", cfg.CrossCameraMapReady, h1ok,
                         v => cfg.CrossCameraMapReady = v);
                    if (h1ok)
                        rep.Warnings.Add("本槽口径 = 上下机映射（⑦）：产物是跨相机映射（上相机像素 → 下相机像素），"
                                         + "矩阵 " + System.IO.Path.GetFileName(h1.SourceFile == null ? "?" : h1.SourceFile)
                                         + " 可读。★生产端【应用跨相机映射】的取像/映射环节尚未接入 —— 现在只到"
                                         + "「口径判定 + 产物齐备性」为止，详见 CalibrationMethodCatalog"
                                         + " 里 UpCameraViaDownCamera 行的说明。");
                    else
                        rep.Warnings.Add("本槽声明走上下机映射（⑦），但跨相机映射矩阵不可读（" + h1err + "）"
                                         + " ⇒ 契约会硬拦（不会退化成⑥/②）。请先补跨相机映射产物。");
                }
                else
                {
                    rep.SameAsSnapshot.Add("UseUpCameraViaDownCamera");
                }

                ComputeDownAxis(rep, cfg, down);

                return rep;
            }
            catch (Exception ex)
            {
                rep.Applied = false;
                rep.Reason = "换源过程异常，本次**仍按工位配置快照跑**（不是通过，是没换成）：" + ex.Message;
                return rep;
            }
        }

        /// <summary>下相机轴投影常量 = H_down(R_cdown)。矩阵缺失/R_cdown 缺失 ⇒ 记 NotCovered（不许猜）。</summary>
        private static void ComputeDownAxis(
            HydrationReport rep, VisionPickPlaceConfig cfg,
            CameraCalibrationBundle down)
        {
            const string keys = "DownCameraAxisWx/Wy";
            if (down == null)
            {
                rep.NotCovered.Add(keys + "（档案里没有下相机槽）");
                return;
            }
            var rcp = down.DownRotCenterProfile;
            if (rcp?.DownRotCenterCol == null || rcp.DownRotCenterRow == null)
            {
                rep.NotCovered.Add(keys + "（下相机槽的档案里没有 R_cdown 像素中心）");
                return;
            }
            if (down.H == null)
            {
                rep.NotCovered.Add(keys + "（下相机槽没有 H 档）");
                return;
            }

            HomMat2D hm;
            string err;
            if (!HomMat2D.TryLoadForProfile(down.H, out hm, out err))
            {
                rep.NotCovered.Add(keys + "（矩阵不可用： " + err + "）");
                return;
            }

            double ax, ay;
            hm.Map(rcp.DownRotCenterCol.Value, rcp.DownRotCenterRow.Value, out ax, out ay);
            rep.Warnings.Add($"下相机轴常量由本端就地复算：H_down(R_cdown) "
                             + $"= ({ax:F4},{ay:F4})mm ← 矩阵 {System.IO.Path.GetFileName(hm.SourceFile ?? "?")}"
                             + $"，R_cdown=({rcp.DownRotCenterCol.Value:F3},{rcp.DownRotCenterRow.Value:F3})px"
                             + $"（不再依赖人工发布的常量，开工时就地复算）");
            Num(rep, "DownCameraAxisWx", cfg.DownCameraAxisWx, ax, v => cfg.DownCameraAxisWx = v);
            Num(rep, "DownCameraAxisWy", cfg.DownCameraAxisWy, ay, v => cfg.DownCameraAxisWy = v);
        }

        // ---------- 两个覆写器：值缺失 ⇒ 记 NotCovered（回退项必须可见），不许默默用默认 ----------

        private static void Num(HydrationReport rep, string key, double snapshot, double? archive,
            Action<float> set)
        {
            if (!archive.HasValue)
            {
                rep.NotCovered.Add(key + "（档案缺该值）");
                return;
            }
            float nv = (float)archive.Value;
            set(nv);
            if (Math.Abs(snapshot - nv) > 1e-6)
            {
                rep.Changed.Add($"{key}: 快照 {snapshot:F4} → 档案 {nv:F4}");
            }
            else
            {
                rep.SameAsSnapshot.Add(key);
            }
        }

        private static void Bool(HydrationReport rep, string key, bool snapshot, bool? archive, Action<bool> set)
        {
            if (!archive.HasValue)
            {
                rep.NotCovered.Add(key + "（档案未声明）");
                return;
            }
            set(archive.Value);
            if (snapshot != archive.Value)
            {
                rep.Changed.Add($"{key}: 快照 {YesNo(snapshot)} → 档案 {YesNo(archive.Value)}");
            }
            else
            {
                rep.SameAsSnapshot.Add(key);
            }
        }

        private static string YesNo(bool b) => b ? "是" : "否";
    }
}
