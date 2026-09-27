//===================================================================================
// 文件名: StationCalibGraph.cs
// 说 明: 范式2（坐标系传导矩阵链）的工位标定图模型与 JSON 持久化。
//
// 设计真源：《标定范式2_坐标系传导矩阵链_迁移设计_2026-09-27.md》§3
//   数学真源：《工业 2D3D 视觉标定总则》三规则 ——
//   ① 唯一锚点：所有相机/TCP 只与 Robot Base 建立关系；
//   ② 正解解耦：随动相机只标 T_Cam→Flange，世界求值 = T_F→B(X,Y,U) ∘ H_Cam→Flange；
//   ③ Multi-TCP：主吸嘴标 T_TCP→Flange，副吸嘴存相对矢量。
//   消费端只做一件事：沿链矩阵相乘把像素推到机械坐标，再逆解法兰目标。
//
// 破坏式定案（2026-09-27 用户拍板）：
//   本模型**取代** ConsumptionKind 分型 + 工位档案 JSON 的标定部分，不做双轨；
//   域不再存储——由"链的形状"（节点组合方式）宣告，InferNozzleDomain 类推断退役。
//
// 单元纪律（沿 HomMat2D.cs）：
//   · 本文件只做数据结构 + 序列化 + 图结构校验；**不做任何业务求值**（那是 ChainEngine）。
//   · JSON 读写失败用 Try + out error 表达，不抛异常。
//   · double 序列化必须 InvariantCulture（严禁现场区域设置污染小数点）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>相机安装形态（总则规则②：决定矩阵挂在哪条链上）</summary>
    public enum ChainCameraMount
    {
        /// <summary>眼在手外（固定）：矩阵语义 = T_Cam→Robot</summary>
        EyeToHand = 0,
        /// <summary>眼在手上（随动）：矩阵语义 = T_Cam→Flange</summary>
        EyeInHand = 1,
    }

    /// <summary>
    /// ★ b（工具偏移）来源 Provenance——范式1 血泪的显式化：
    ///   同一个 6 元组，"谁测的"决定可信级（选错 = 偏 264mm 那族的根因）。
    /// </summary>
    public enum ChainOffsetSource
    {
        /// <summary>未声明（禁止消费带偏移的链）</summary>
        Undeclared = 0,
        /// <summary>对针直量（无残差，最高可信）</summary>
        TouchedTip = 1,
        /// <summary>残差反推（ToolEcc 类，1~3mm 量级）</summary>
        ToolEccResidual = 2,
        /// <summary>平台自测（≈8mm 是 O 的偏差不是 b，只能参考）</summary>
        SelfTest = 3,
    }

    /// <summary>标定元数据（复用迁移设计 §3：结果可信级与留痕）</summary>
    public sealed class ChainCalibMeta
    {
        public string Method { get; set; }              // WalkNinePoint / PivotFit / ProbeHomography / WorldPlaneRectify...
        public int PointPairs { get; set; }             // 点对数（九点=9，12 点=12）
        public double RmsMm { get; set; }               // 拟合残差
        public ChainOffsetSource OffsetSource { get; set; } = ChainOffsetSource.Undeclared;
        public bool SignDeclared { get; set; }          // ★ 偏移符号已判定（09-16 教训：选错偏 264mm）
        public string CapturedAt { get; set; }          // ISO 8601
        public string Operator { get; set; }
        public string Note { get; set; }
        /// <summary>
        /// 相机矩阵有效拍照 U（°，EIH 专属）。范式1 折叠导出的 H_Cam→F 只在 U=该值时等价；
        /// NaN=无限制（向导原生标定的 H_Cam→F 对任意拍照 U 成立）。ChainHydrator 消费时硬校验。
        /// </summary>
        public double ValidPhotoUDeg { get; set; } = double.NaN;
    }

    /// <summary>相机节点。ETH 挂 T_Cam→Robot；EIH 挂 T_Cam→Flange（O 已折叠进平移分量）。</summary>
    public sealed class ChainCameraNode
    {
        public string CameraId { get; set; }            // 与设备池逻辑相机名一致（如 Cam_A）
        public ChainCameraMount Mount { get; set; }
        /// <summary>ETH: T_Cam→Robot；EIH: T_Cam→Flange。6 元组 [a11,a12,tx,a21,a22,ty]</summary>
        public double[] Matrix { get; set; }
        public ChainCalibMeta Meta { get; set; }
    }

    /// <summary>工具头节点（吸嘴/延伸杆/标定针，总则规则③）</summary>
    public sealed class ChainTcpNode
    {
        public string ToolId { get; set; }
        /// <summary>true=主工具（直接标定）；false=副工具（T_TCP→F = 主工具 ∘ ΔT）</summary>
        public bool IsMaster { get; set; }
        /// <summary>T_TCP→Flange 平移矢量 [dx,dy]（法兰系，U=0 基准，带符号）</summary>
        public double[] Offset { get; set; }
        /// <summary>SCARA U 轴旋转中心（法兰系 XY）；同轴工具可缺省</summary>
        public double[] URotationCenter { get; set; }
        /// <summary>副工具绑定主工具 ToolId</summary>
        public string BindMasterToolId { get; set; }
        public ChainCalibMeta Meta { get; set; }
    }

    /// <summary>
    /// ★ 消费链显式声明（迁移设计 §6 风险 3：Edges 由向导按采集路径生成，不允许手填）。
    ///   链的形状宣告域：如 "Cam_A --PickAnchor--> RodMark" = 相机 A 引导杆端工具取料。
    /// </summary>
    public sealed class ChainEdge
    {
        public string FromCameraId { get; set; }
        public string ToToolId { get; set; }
        /// <summary>消费用途（PickAnchor / MoveToWork / Teach / DownCameraCorrect...）</summary>
        public string Usage { get; set; }
    }

    /// <summary>工位标定图（范式2 存储真源，替代旧档案 JSON 的标定部分）</summary>
    public sealed class StationCalibGraph
    {
        /// <summary>★ 工位绑定码（ST_007 教训：无绑定会被借用池捞走——这里直接是身份字段）</summary>
        public string StationCode { get; set; }
        public int SchemaVersion { get; set; } = 1;
        public List<ChainCameraNode> Cameras { get; set; } = new List<ChainCameraNode>();
        public List<ChainTcpNode> Tools { get; set; } = new List<ChainTcpNode>();
        public List<ChainEdge> Edges { get; set; } = new List<ChainEdge>();
        public string UpdatedAt { get; set; }

        public ChainCameraNode FindCamera(string cameraId)
        {
            return Cameras.Find(c => string.Equals(c.CameraId, cameraId, StringComparison.OrdinalIgnoreCase));
        }

        public ChainTcpNode FindTool(string toolId)
        {
            return Tools.Find(t => string.Equals(t.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
        }

        //---------------------------------------------------------------------
        // JSON 持久化（路径真源 = Workstations\{工位码}\Calib\{方案}_Chain.json）
        //---------------------------------------------------------------------

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
        };

        public static bool TrySave(StationCalibGraph graph, string filePath, out string error)
        {
            try
            {
                graph.UpdatedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                string dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(filePath, JsonConvert.SerializeObject(graph, JsonSettings));
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "链图写入失败: " + ex.Message;
                return false;
            }
        }

        public static bool TryLoad(string filePath, out StationCalibGraph graph, out string error)
        {
            graph = null;
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    error = "链图文件不存在: " + (filePath ?? "(null)");
                    return false;
                }
                graph = JsonConvert.DeserializeObject<StationCalibGraph>(File.ReadAllText(filePath), JsonSettings);
                if (graph == null)
                {
                    error = "链图反序列化为空: " + filePath;
                    return false;
                }
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = "链图读取失败: " + ex.Message;
                return false;
            }
        }
    }
}
