//===================================================================================
// 文件名: ChainTopologyPlanner.cs
// 说 明: 工位档案事实 → 链骨架推导器（范式2 "链的形状由工位状况决定" 的落地实现）。
//
// 设计真源：《标定链路v2_工位驱动标定工作流设计_2026-09-27.md》§2~§3（2026-09-27 用户修正版）：
//   ★ 拓扑不是持久声明块——工位档案相机槽（VisionSlotInfo：InstallKind/Purpose/
//     AxisFollows/IsDisabled）+ 工具清单本来就是事实，推导器把这些事实折叠成链骨架；
//     不确定性只在两处，用人工裁决收敛：
//       ① 主工具是谁（默认取工具清单首个）；
//       ② 每台相机的链角色（PickGuide 吸点引导 / DownCorrect 下相机纠偏，默认按
//          安装方式+用途预填，拓扑确认页只改例外）。
//   其余全部自动裁决：边生成、副工具绑定（刚性阵列）、步骤顺序（向导侧）、
//     Δ 符号（拟合内蕴）、门禁——禁止手填边。
//   ★ 不设 DownCorrectEnabled / Coaxial 之类声明字段（用户定案）：
//       下相机纠偏是否启用 = 链上有没有 DownCameraCorrect 边（推导产物，不是输入）；
//       同轴 = 偏心的特例（Offset 实测为 (0,0)），模型不设同轴字段。
//
// 输入契约：
//   cameraSlots = 工位档案相机槽真类型（StationProfile.Requirement.CameraSlots 原样传入，零适配层）；
//   tools       = 工位工具清单（ToolId 必须与生产消费端一致，如 Nozzle1/Nozzle2）。
//
// 判词纪律：中文自由文本 → 布尔的翻译复用 Norm（CalibrationPlanEngineV2，编译内单一真源）；
//   仅 IsDownLooking（下固定/仰视）当前 Norm 没有，在链侧补齐——TODO: 并入 Norm 后删除本地实现。
//
// 骨架语义：节点+边齐全，矩阵/偏移全部留空（Meta.Method="Pending"）。
//   G1 矩阵完整性门在采集回填后由 ChainEngine.Validate 全量执行；
//   本类只做结构子集 ValidateStructure（G0 身份 / G2 工具结构 / G3 边端点 / G4 ID 唯一）。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Station.Models;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>消费边 Usage 字符串口径（与生产消费端 VisionPickPlaceProcess 常量逐字一致）</summary>
    public static class ChainUsage
    {
        public const string PickAnchor = "PickAnchor";
        public const string DownCameraCorrect = "DownCameraCorrect";
    }

    /// <summary>工具事实（链推导视角最小集；ToolId 与生产消费端常量一致）</summary>
    public sealed class ChainToolFact
    {
        public string ToolId { get; set; }
    }

    /// <summary>单台相机的链角色（人工裁决载体；默认推导结果也用它表达，UI 只改例外）</summary>
    public sealed class ChainCameraRoleChoice
    {
        public string CameraId { get; set; }
        /// <summary>建 PickAnchor 边（吸点引导）</summary>
        public bool PickGuide { get; set; }
        /// <summary>建 DownCameraCorrect 边（下相机差分纠偏 ⇒ 向导必测 DeltaRefPixel）</summary>
        public bool DownCorrect { get; set; }
    }

    /// <summary>人工裁决项。仅两处；其余全自动。null 字段 = 交回默认推导。</summary>
    public sealed class ChainPlanDecisions
    {
        /// <summary>主工具 ToolId；null/空 = 默认取工具清单首个</summary>
        public string MasterToolId { get; set; }

        /// <summary>相机角色指派（覆盖默认推导；未列出的相机走默认推导）</summary>
        public List<ChainCameraRoleChoice> CameraRoles { get; set; } = new List<ChainCameraRoleChoice>();

        public ChainCameraRoleChoice FindRole(string cameraId)
        {
            if (CameraRoles == null || string.IsNullOrWhiteSpace(cameraId)) return null;
            return CameraRoles.Find(c => string.Equals(c.CameraId, cameraId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>推导结果。Ok=true 时 Draft 可交给向导当步骤清单+回填骨架。</summary>
    public sealed class ChainPlanResult
    {
        /// <summary>链骨架（Ok=false 时为 null）</summary>
        public StationCalibGraph Draft { get; set; }
        /// <summary>自动裁决说明（给拓扑确认页展示"系统替你决定了什么"）</summary>
        public List<string> AutoNotes { get; } = new List<string>();
        /// <summary>须人工确认项（非致命；拓扑确认页逐条过）</summary>
        public List<string> OpenQuestions { get; } = new List<string>();
        /// <summary>致命错误（拓扑非法；非空 ⇒ 不产骨架）</summary>
        public List<string> Errors { get; } = new List<string>();

        public bool Ok { get { return Errors.Count == 0 && Draft != null; } }
    }

    public static class ChainTopologyPlanner
    {
        /// <summary>
        /// 档案事实 + 人工裁决 → 链骨架。
        /// 规则 R1~R5（全部出自总则三规则，无一项发明）：
        ///   R1 相机槽 → ChainCameraNode：随动(InstallKind 含眼在手上)⇒EIH 挂 Flange；固定⇒ETH 挂 Base；
        ///   R2 主工具 → ChainTcpNode(IsMaster)；其余工具 → 副节点自动绑定主（刚性阵列）；
        ///   R3 PickGuide 相机 × 每个工具 → PickAnchor 边；
        ///   R4 DownCorrect 相机 → DownCameraCorrect 边（指向主工具）；
        ///   R5 骨架过 ValidateStructure 结构门，不过 ⇒ 不产骨架。
        /// </summary>
        public static ChainPlanResult Plan(string stationCode, List<VisionSlotInfo> cameraSlots,
                                           List<ChainToolFact> tools, ChainPlanDecisions decisions = null)
        {
            var r = new ChainPlanResult();
            decisions = decisions ?? new ChainPlanDecisions();

            // ---------- 工具侧（R2）----------
            if (tools == null || tools.Count == 0)
            {
                r.Errors.Add("工具清单为空：链必须有至少一个工具节点（吸嘴/延伸杆）");
                return r;
            }
            var toolIds = new List<string>();
            foreach (var t in tools)
            {
                string id = (t == null ? null : t.ToolId);
                id = (id ?? "").Trim();
                if (id.Length == 0) { r.Errors.Add("存在空 ToolId 的工具事实"); continue; }
                if (toolIds.Exists(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)))
                { r.Errors.Add("工具 Id 重复: " + id); continue; }
                toolIds.Add(id);
            }
            if (r.Errors.Count > 0) return r;

            string master = (decisions.MasterToolId ?? "").Trim();
            if (master.Length == 0)
            {
                master = toolIds[0];
                r.AutoNotes.Add("主工具未指定 ⇒ 自动取首个工具 " + master + "（可改）");
            }
            else if (!toolIds.Exists(x => string.Equals(x, master, StringComparison.OrdinalIgnoreCase)))
            {
                r.Errors.Add("主工具 " + master + " 不在工具清单中");
                return r;
            }

            var draft = new StationCalibGraph { StationCode = (stationCode ?? "").Trim() };
            if (draft.StationCode.Length == 0)
            {
                r.Errors.Add("缺 StationCode：骨架必须绑定工位身份（G0，防借用池）");
                return r;
            }

            draft.Tools.Add(new ChainTcpNode { ToolId = master, IsMaster = true, Meta = PendingMeta() });
            foreach (var tid in toolIds)
            {
                if (string.Equals(tid, master, StringComparison.OrdinalIgnoreCase)) continue;
                draft.Tools.Add(new ChainTcpNode
                {
                    ToolId = tid,
                    IsMaster = false,
                    BindMasterToolId = master,
                    Meta = PendingMeta(),
                });
            }
            if (draft.Tools.Count > 1)
            {
                var slaves = new List<string>();
                foreach (var t in draft.Tools) if (!t.IsMaster) slaves.Add(t.ToolId);
                r.AutoNotes.Add("副工具 " + string.Join("/", slaves) + " 自动绑定主工具 " + master
                                + "（刚性阵列，Δ 待测；同轴工具 = 偏心实测 (0,0)，无独立字段）");
            }

            // ---------- 相机侧（R1/R3/R4）：默认角色推导 + 人工覆盖 ----------
            bool hasPickGuide = false, hasDownCorrect = false;
            if (cameraSlots != null)
            {
                foreach (var slot in cameraSlots)
                {
                    if (slot == null) continue;
                    if (slot.IsDisabled) { r.AutoNotes.Add("相机槽 " + SafeKey(slot) + " 已停用 ⇒ 不进链"); continue; }

                    // 判词折叠（复用 Norm 单一真源；下视判据暂链侧补齐，见文件头 TODO）
                    string key = SafeKey(slot);
                    string install = Norm.Trim(slot.InstallKind);
                    string purpose = Norm.Trim(slot.Purpose);
                    bool moving = Norm.IsMovingMount(install);
                    bool down = IsDownLooking(install);
                    bool guidance = Norm.IsGuidancePurpose(purpose) || Norm.IsGuidancePurpose(install);

                    bool pick, downRole;
                    var dec = decisions.FindRole(key);
                    if (dec != null)
                    {
                        pick = dec.PickGuide;
                        downRole = dec.DownCorrect;
                        r.AutoNotes.Add("相机 " + key + " 角色=人工指派（" + Describe(pick, downRole) + "）");
                    }
                    else
                    {
                        // 默认推导（安装方式+用途）：
                        //   下固定/仰视 ⇒ 只做下相机纠偏；
                        //   随动（眼在手上）⇒ 吸点引导；
                        //   固定非下视 ⇒ 用途含引导/定位语义才进链（测量/检测相机不建消费边）。
                        downRole = down;
                        if (downRole) pick = false;
                        else if (moving) pick = true;
                        else pick = guidance;

                        if (install.Length == 0)
                            r.OpenQuestions.Add("相机 " + key + " 未填安装方式 ⇒ 暂按固定相机推导，请在拓扑确认页核对");
                        else if (purpose.Length == 0 && !moving && !downRole)
                            r.OpenQuestions.Add("相机 " + key + " 未填用途且为固定相机 ⇒ 暂按非引导处理，请在拓扑确认页核对");
                        r.AutoNotes.Add("相机 " + key + " 角色=自动推导（" + Describe(pick, downRole) + "）");
                    }

                    if (!pick && !downRole)
                    {
                        r.OpenQuestions.Add("相机 " + key + " 未指派任何链角色 ⇒ 暂不进链（如需参与请在拓扑确认页指派）");
                        continue;
                    }
                    if (pick) hasPickGuide = true;
                    if (downRole) hasDownCorrect = true;

                    if (draft.FindCamera(key) != null)
                    {
                        r.Errors.Add("相机槽重复: " + key);
                        continue;
                    }
                    draft.Cameras.Add(new ChainCameraNode
                    {
                        CameraId = key,
                        // R1：安装事实定挂链——随动 ⇒ EIH 挂 Flange；固定 ⇒ ETH 挂 Base
                        Mount = moving ? ChainCameraMount.EyeInHand : ChainCameraMount.EyeToHand,
                        Meta = PendingMeta(),
                    });

                    if (pick)
                    {
                        // R3：引导相机 × 每个工具（多工具工位默认全连；不需要的边在拓扑确认页删）
                        foreach (var tid in toolIds)
                            draft.Edges.Add(new ChainEdge
                            {
                                FromCameraId = key,
                                ToToolId = tid,
                                Usage = ChainUsage.PickAnchor,
                            });
                    }
                    if (downRole)
                    {
                        // R4：下相机纠偏边指向主工具；启用与否由这条边存在性表达（无独立开关字段）
                        draft.Edges.Add(new ChainEdge
                        {
                            FromCameraId = key,
                            ToToolId = master,
                            Usage = ChainUsage.DownCameraCorrect,
                        });
                    }
                }
            }

            if (draft.Cameras.Count == 0)
                r.Errors.Add("没有任何相机进链：检查相机槽是否全部停用/未指派角色");
            if (!hasPickGuide)
                r.OpenQuestions.Add("链上没有吸点引导相机（无 PickAnchor 边）：确认该工位确实不需要视觉引导吸取");
            if (hasDownCorrect)
                r.AutoNotes.Add("存在 DownCameraCorrect 边 ⇒ 向导必须实测 DeltaRefPixel（下相机点选）；缺失将拒绝落盘");
            foreach (var c in draft.Cameras)
                if (c.Mount == ChainCameraMount.EyeInHand)
                    r.AutoNotes.Add("相机 " + c.CameraId + " 为随动（EIH）⇒ 九点采集须逐点记录拍照位姿（法兰系规范化后拟合）");

            // ---------- R5：结构门 ----------
            var structErrs = ValidateStructure(draft);
            foreach (var e in structErrs) r.Errors.Add(e);
            if (r.Errors.Count > 0) { r.Draft = null; return r; }

            r.Draft = draft;
            return r;
        }

        /// <summary>
        /// 骨架结构子集门（G0 身份 / G2 工具结构 / G3 边端点与用途 / G4 ID 唯一 / 规模下限）。
        /// 与 ChainEngine.Validate 的分工：那里是【回填后的全量门禁】（G1 矩阵完整性含在内），
        /// 这里允许矩阵/偏移为 null（骨架期）——落盘前向导仍须全量跑 ChainEngine.Validate。
        /// </summary>
        public static List<string> ValidateStructure(StationCalibGraph graph)
        {
            var errs = new List<string>();
            if (graph == null) { errs.Add("链图为 null"); return errs; }

            // G0：身份
            if (string.IsNullOrWhiteSpace(graph.StationCode))
                errs.Add("G0 链图缺 StationCode（未绑定身份）");

            // 规模下限：无相机/无工具的链没有可消费的形状
            if (graph.Cameras == null || graph.Cameras.Count == 0)
                errs.Add("链图无相机节点（至少需要一台相机）");
            if (graph.Tools == null || graph.Tools.Count == 0)
                errs.Add("链图无工具节点（至少需要一个工具）");

            // G2：工具结构（偏移允许 null = 待测；副工具必须绑主且主存在）
            foreach (var t in graph.Tools ?? new List<ChainTcpNode>())
            {
                if (string.IsNullOrWhiteSpace(t.ToolId)) { errs.Add("G2 存在缺 ToolId 的工具节点"); continue; }
                if (t.Offset != null && t.Offset.Length != 2)
                    errs.Add("G2 工具 " + t.ToolId + " 偏移矢量非法（须 2 元素或 null=待测）");
                if (!t.IsMaster)
                {
                    if (string.IsNullOrWhiteSpace(t.BindMasterToolId))
                        errs.Add("G2 副工具 " + t.ToolId + " 未绑定主工具");
                    else if (graph.FindTool(t.BindMasterToolId) == null)
                        errs.Add("G2 副工具 " + t.ToolId + " 绑定的主工具不存在: " + t.BindMasterToolId);
                }
            }
            bool hasMaster = false;
            foreach (var t in graph.Tools ?? new List<ChainTcpNode>())
                if (t.IsMaster) hasMaster = true;
            if (graph.Tools != null && graph.Tools.Count > 0 && !hasMaster)
                errs.Add("G2 链图缺主工具节点（IsMaster=true）");

            // G3：边端点存在 + 用途非空
            foreach (var e in graph.Edges ?? new List<ChainEdge>())
            {
                if (graph.FindCamera(e.FromCameraId) == null)
                    errs.Add("G3 边引用了不存在的相机: " + (e.FromCameraId ?? "(null)"));
                if (graph.FindTool(e.ToToolId) == null)
                    errs.Add("G3 边引用了不存在的工具: " + (e.ToToolId ?? "(null)"));
                if (string.IsNullOrWhiteSpace(e.Usage))
                    errs.Add("G3 边缺用途（" + (e.FromCameraId ?? "?") + "→" + (e.ToToolId ?? "?") + "）");
            }

            // G4：ID 唯一
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in graph.Cameras ?? new List<ChainCameraNode>())
                if (!ids.Add("C:" + c.CameraId)) errs.Add("G4 相机 Id 重复: " + c.CameraId);
            foreach (var t in graph.Tools ?? new List<ChainTcpNode>())
                if (!ids.Add("T:" + t.ToolId)) errs.Add("G4 工具 Id 重复: " + t.ToolId);

            return errs;
        }

        //---------------------------------------------------------------------
        // 私有工具
        //---------------------------------------------------------------------

        /// <summary>下固定/仰视判据（与已退役目录文件的口径一致；TODO 并入 Norm）</summary>
        private static bool IsDownLooking(string installText)
        {
            if (string.IsNullOrWhiteSpace(installText)) return false;
            return installText.Contains("下固定") || installText.Contains("仰视");
        }

        private static string SafeKey(VisionSlotInfo slot)
        {
            string k = Norm.Trim(slot.SlotKey);
            return k.Length == 0 ? "Cam_?" : k;
        }

        private static string Describe(bool pick, bool down)
        {
            string s = (pick ? "吸点引导" : "") + (pick && down ? "+" : "") + (down ? "下相机纠偏" : "");
            return s.Length == 0 ? "无" : s;
        }

        private static ChainCalibMeta PendingMeta()
        {
            return new ChainCalibMeta { Method = "Pending", Version = 1 };
        }
    }
}
