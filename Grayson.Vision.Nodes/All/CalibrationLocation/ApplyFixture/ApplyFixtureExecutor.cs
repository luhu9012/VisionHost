using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Flow.Attributes;
using Grayson.Vision.Contracts.Flow.Enums;
using Grayson.Vision.Contracts.Flow.Contexts;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.ApplyFixture
{
    [Node(NodeType.ApplyFixture, NodeCategory.CalibrationLocation, typeof(ApplyFixtureParam))]
    [NodePort("HomMat2D", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#3498DB")]
    [NodePort("InputRegion", PortType.In, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    // 角度跟随（2026-09-25）：Δθ 从 CreateFixture.DeltaAngle 接入；OutputAngle = BaseAngle − Δθ
    // （是"减"：HALCON 的朝向角与 vector_angle_to_rigid 的旋转手性相反，实测见 ExecuteCoreAsync 内注释）。
    // 角度族端口统一青色（与 ShapeMatch.MatchAngle / FitLine.SeedPhi 同色配对）。
    [NodePort("DeltaAngle", PortType.In, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    [NodePort("OutputRow", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("OutputCol", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#E74C3C")]
    [NodePort("OutputRegion", PortType.Out, PortCategory.Data, dataType: "object", colorHex: "#2ECC71")]
    [NodePort("OutputAngle", PortType.Out, PortCategory.Data, dataType: "double", colorHex: "#1ABC9C")]
    public class ApplyFixtureExecutor : NodeExecutorBase<ApplyFixtureParam>
    {
        public const string PORT_IN_HOMMAT = "HomMat2D";
        public const string PORT_IN_REGION = "InputRegion";
        public const string PORT_IN_DELTA_ANGLE = "DeltaAngle";
        public const string PORT_OUT_ROW = "OutputRow";
        public const string PORT_OUT_COL = "OutputCol";
        public const string PORT_OUT_REGION = "OutputRegion";
        public const string PORT_OUT_ANGLE = "OutputAngle";

        protected override async Task ExecuteCoreAsync(FlowNodeBase node, ApplyFixtureParam param, NodeExecutionContext context, CancellationToken token)
        {
            await Task.Yield();

            object homMat = context.GetInputValue<object>(node, PORT_IN_HOMMAT);
            if (homMat == null)
            {
                context.Log("[" + node.DisplayName + "] 错误：未输入有效的变换矩阵 HomMat2D！");
                return;
            }

            if (param.TargetType == FollowType.Point)
            {
                var ptRes = FixtureTool.ApplyFixtureToPoint(param.BaseRow, param.BaseCol, homMat);
                if (ptRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_ROW, ptRes.Data.newRow);
                    context.SetOutputValue(node, PORT_OUT_COL, ptRes.Data.newCol);

                    // 角度跟随：BaseAngle − Δθ。
                    // Δθ（工件相对基准的旋转量，°；= CreateFixture.DeltaAngle = MatchAngle − BaselineAngle）
                    // 由上游接入；未接线或值不合法时按 0 处理（= 不跟随，等同旧行为，向后兼容）。
                    //
                    // ★★ 为什么是"减"而不是"加"（2026-09-25 实测定案，勿凭直觉改回"加"）：
                    //   HALCON 里"物体朝向角"（gen_measure_rectangle2 的 Phi、fit_line 的 atan2(ΔRow,ΔCol)）
                    //   与 vector_angle_to_rigid 的旋转【手性相反】，故刚体跟着工件转 Δθ 时，
                    //   朝向角的正确合成是 φ_cur = φ_base − Δθ。
                    //   证据：离线探针把一条边的两个【基准端点】各自 affine_trans_point_2d 跟随，
                    //   直接量出真值角，四张图都与 −(90.57° + Δθ) 吻合：
                    //     lk_01 Δθ=0      → 真值  −90.57°
                    //     lk_02 Δθ=+20.94 → 真值 −111.51°
                    //     lk_03 Δθ=+3.96  → 真值  −94.53°
                    //     lk_04 Δθ=−43.92 → 真值  −46.65°
                    //   用"+"时误差 = 2Δθ：Δθ=0 正常、21°→7 条探针只剩 3 条、44°→ 0 条（拟合直接失败）。
                    double deltaAngle = 0.0;
                    object deltaObj = context.GetInputValue<object>(node, PORT_IN_DELTA_ANGLE, null);
                    if (deltaObj != null)
                    {
                        try { deltaAngle = Convert.ToDouble(deltaObj, CultureInfo.InvariantCulture); } catch { /* 端口数值不合法：按 0 处理 */ }
                    }
                    double followedAngle = param.BaseAngle - deltaAngle;
                    context.SetOutputValue(node, PORT_OUT_ANGLE, followedAngle);

                    context.Log("[" + node.DisplayName + "] 点坐标跟随成功 -> Row: " + ptRes.Data.newRow.ToString("F2", CultureInfo.InvariantCulture) +
                        ", Col: " + ptRes.Data.newCol.ToString("F2", CultureInfo.InvariantCulture) +
                        ", 角度: " + followedAngle.ToString("F2", CultureInfo.InvariantCulture) + "°（基准 " +
                        param.BaseAngle.ToString("F2", CultureInfo.InvariantCulture) + "° − Δθ " + deltaAngle.ToString("F2", CultureInfo.InvariantCulture) + "°）");
                }
                else
                {
                    context.Log("[" + node.DisplayName + "] 点坐标跟随失败: " + ptRes.Message);
                }
            }
            else
            {
                object inRegion = context.GetInputValue<object>(node, PORT_IN_REGION);
                if (inRegion == null)
                {
                    context.Log("[" + node.DisplayName + "] 错误：未输入待跟随的 Region 区域！");
                    return;
                }

                var regRes = FixtureTool.ApplyFixtureToRegion(inRegion, homMat);
                if (regRes.Success)
                {
                    context.SetOutputValue(node, PORT_OUT_REGION, regRes.Data);
                    context.Log("[" + node.DisplayName + "] Region 区域位置跟随成功");
                }
                else
                {
                    context.Log("[" + node.DisplayName + "] Region 区域位置跟随失败: " + regRes.Message);
                }
            }
        }
    }
}