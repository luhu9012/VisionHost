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
    /// ★ 标定元数据（留痕，不参与求值/门禁）。
    ///   原生范式2 下链图只由向导产出：偏移量是向导实测的带符号矢量（符号内蕴），
    ///   不需要范式1 的"来源分级/符号声明"复杂度——那套是给来历不明的旧档案当护栏用的。
    /// </summary>
    public sealed class ChainCalibMeta
    {
        public string Method { get; set; }              // WalkNinePoint / PivotFit / ProbeHomography / WorldPlaneRectify...
        public int PointPairs { get; set; }             // 点对数（九点=9，12 点=12）
        public double RmsMm { get; set; }               // 拟合残差
        /// <summary>
        /// EIH 标定时的拍照 Z（法兰高度）。消费端校验判据：生产拍照 Z 必须等于该值，
        /// 否则 EIH 平移分量随 Z 线性漂移 ⇒ 乘性过纠（范式1 血泪）。null = ETH 或不适用。
        /// </summary>
        public double? CalibZHeightMm { get; set; }
        /// <summary>标定所用 Mark/工装标识（V2.1 CalibToolTag：残差可解释性的溯源锚点）。</summary>
        public string CalibToolTag { get; set; }
        /// <summary>链模型版本（结构演进时区分旧档案）。</summary>
        public int Version { get; set; }                // 当前 = 1
        public string CapturedAt { get; set; }          // ISO 8601
        public string Operator { get; set; }
        public string Note { get; set; }
    }

    /// <summary>相机节点。ETH 挂 T_Cam→Robot；EIH 挂 T_Cam→Flange（O 已折叠进平移分量）。</summary>
    public sealed class ChainCameraNode
    {
        public string CameraId { get; set; }            // 与设备池逻辑相机名一致（如 Cam_A）
        public ChainCameraMount Mount { get; set; }
        /// <summary>ETH: T_Cam→Robot；EIH: T_Cam→Flange。6 元组 [a11,a12,tx,a21,a22,ty]</summary>
        public double[] Matrix { get; set; }
        /// <summary>
        /// EIH 拍照基准位 [X,Y]（Robot Base 系；拍照 U 角由生产方承担，不存于此）。
        /// EIH 求值 X_obj = T_F→B(P_photo) ∘ H_Cam→Flange(像素) 的 P_photo 必须可复现：
        /// 向导实测录入，生产/示教消费 pose 从这里取。EIH 缺失 ⇒ 门禁 G1 硬拦。
        /// </summary>
        public double[] PhotoPose { get; set; }
        /// <summary>
        /// 下相机差分基准像素 [col,row]（= 吸嘴 U 轴在该相机图像里的投影 R_cdown，向导实测）。
        /// 消费：δ = Chain(卡片像素) − Chain(DeltaRefPixel)，同位姿两次链求值平移项相减消掉。
        /// null = 不做差分消费（该相机没有下相机纠偏用途）。
        /// </summary>
        public double[] DeltaRefPixel { get; set; }
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
