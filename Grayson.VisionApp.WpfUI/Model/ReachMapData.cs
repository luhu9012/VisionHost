using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace Grayson.Vision.WpfUI.Model
{
    /// <summary>
    /// 可达域扫描的单个方向记录（来自 .workbuddy/map_workspace.py 输出）。
    /// RIn/ROut 为 null 表示该方向完全不可达（例如撞上关节限位扇区）。
    /// </summary>
    public sealed class ReachMapDirection
    {
        public double Deg { get; set; }

        /// <summary>该方向可达内边界半径 mm（null = 该方向整体不可达）</summary>
        public double? RIn { get; set; }

        /// <summary>该方向可达外边界半径 mm（null = 该方向整体不可达）</summary>
        public double? ROut { get; set; }
    }

    /// <summary>
    /// SCARA 真实可达域快照。
    ///
    /// 【为什么是实测多边形而不是理想圆环】
    ///   真实可达域 = 圆环 ∩ 关节限位区域。只有 J1 全周【且】J2 全范围时它才退化成圆环；
    ///   只要存在 J1 限位，环带就会被切掉扇区；J2 不对称时内外弧半径还随 θ 变化。
    ///   用圆环（一个 rMin/rMax）画会犯两类错：
    ///     · 假安全 —— 画在环内其实够不着（后果不可接受，会撞机）；
    ///     · 假报警 —— 画在环外其实可达（操作员会不再信任这张图）。
    ///   故这里存的是 36 个方向径向扫描得到的 [r_in, r_out] 实测序列，绘制时连成真实边界。
    ///
    /// 【数据来源】.workbuddy/map_workspace.py（只发只读 CHECK，不发运动指令）。
    /// </summary>
    public sealed class ReachMapData
    {
        public string ScannedAt { get; set; } = string.Empty;
        public string Host { get; set; } = string.Empty;
        public string HandReply { get; set; } = string.Empty;
        public string PosReply { get; set; } = string.Empty;

        /// <summary>扫描平面：Z / U（可达域与姿态有关，换 Z/U 或手系都需重扫）</summary>
        public double PlaneZ { get; set; }
        public double PlaneU { get; set; }

        /// <summary>本次扫描消耗的 CHECK 调用次数（只读，用于自证"零运动"）</summary>
        public int CheckCalls { get; set; }

        /// <summary>各方向记录，按 Deg 升序</summary>
        public List<ReachMapDirection> Directions { get; set; } = new List<ReachMapDirection>();

        /// <summary>来源文件路径（合成数据为空）</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>true = 理想圆环合成数据（仅用于没有实测数据时预览界面，不可作为现场依据）</summary>
        public bool IsSynthetic { get; set; }

        public bool IsEmpty => Directions == null || Directions.Count < 3;

        /// <summary>数据自述摘要（显示在画布角落，防止把合成数据当实测）</summary>
        public string Describe()
        {
            if (IsEmpty) return "无可达域数据";
            string tag = IsSynthetic ? "示意数据(非实测)" : (string.IsNullOrEmpty(ScannedAt) ? "" : ScannedAt);
            string hand = string.IsNullOrWhiteSpace(HandReply) ? "" : $" 手系:{HandReply}";
            string plane = IsSynthetic ? "" : string.Format(CultureInfo.InvariantCulture, " 平面 Z={0:F1} U={1:F1}", PlaneZ, PlaneU);
            return $"{Directions.Count} 方向{plane}{hand} {tag}".Trim();
        }

        // ------------------------------------------------------------------
        // 加载
        // ------------------------------------------------------------------

        /// <summary>从 map_workspace.py 落盘的 JSON 读取；失败返回 null 并给出可读原因</summary>
        public static ReachMapData LoadFromFile(string path, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    error = "文件不存在: " + path;
                    return null;
                }
                return FromJson(File.ReadAllText(path), path, out error);
            }
            catch (Exception ex)
            {
                error = "读取失败: " + ex.Message;
                return null;
            }
        }

        /// <summary>解析 JSON 文本（同时兼容直接粘贴 JSON 的场景）</summary>
        public static ReachMapData FromJson(string json, string sourcePath, out string error)
        {
            error = null;
            try
            {
                var root = JObject.Parse(json);
                var data = new ReachMapData
                {
                    ScannedAt = (string)root["scanned_at"] ?? string.Empty,
                    Host = (string)root["host"] ?? string.Empty,
                    HandReply = (string)root["hand_reply"] ?? string.Empty,
                    PosReply = (string)root["pos_reply"] ?? string.Empty,
                    CheckCalls = root["check_calls"] != null ? (int)root["check_calls"] : 0,
                    SourcePath = sourcePath ?? string.Empty,
                };

                var plane = root["plane"] as JObject;
                if (plane != null)
                {
                    data.PlaneZ = plane["z"] != null ? (double)plane["z"] : 0;
                    data.PlaneU = plane["u"] != null ? (double)plane["u"] : 0;
                }

                var arr = root["directions"] as JArray;
                if (arr == null || arr.Count < 3)
                {
                    error = "JSON 里没有可用的 directions（至少需要 3 个方向）";
                    return null;
                }

                foreach (var item in arr)
                {
                    var d = new ReachMapDirection
                    {
                        Deg = item["deg"] != null ? (double)item["deg"] : 0,
                        RIn = item["r_in"] != null && item["r_in"].Type != JTokenType.Null ? (double?)item["r_in"] : null,
                        ROut = item["r_out"] != null && item["r_out"].Type != JTokenType.Null ? (double?)item["r_out"] : null,
                    };
                    data.Directions.Add(d);
                }

                data.Directions.Sort((a, b) => a.Deg.CompareTo(b.Deg));
                return data;
            }
            catch (Exception ex)
            {
                error = "JSON 解析失败: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 生成理想圆环示意数据 —— 仅在【还没有实测结果】时用于把界面跑起来看效果。
        /// 会在画布上明确标注"示意数据(非实测)"：这不是现场依据，真实边界必须用扫描器实测。
        /// </summary>
        public static ReachMapData CreateSynthetic(double rIn, double rOut, int dirs = 36)
        {
            if (dirs < 3) dirs = 3;
            var data = new ReachMapData
            {
                IsSynthetic = true,
                ScannedAt = string.Empty,
                SourcePath = string.Empty,
            };
            for (int i = 0; i < dirs; i++)
            {
                data.Directions.Add(new ReachMapDirection
                {
                    Deg = 360.0 * i / dirs,
                    RIn = rIn,
                    ROut = rOut,
                });
            }
            return data;
        }
    }
}
