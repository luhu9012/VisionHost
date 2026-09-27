//===================================================================================
// 文件名: ChainEngine.cs
// 说 明: 范式2 链式求值引擎——像素沿链推到机械坐标 + 法兰目标逆解 + fail-closed 图门禁。
//
// 数学真源（总则第四章消费闭环五步曲，全部化归为 HomMat2D 链乘）：
//   ETH 世界求值：P_base = H_Cam→Robot(u,v)
//   EIH 世界求值：P_base = T_F→B(X_arm,Y_arm,U_arm) ∘ H_Cam→Flange (u,v)
//     其中 T_F→B = [cosU,-sinU,X_arm; sinU,cosU,Y_arm; 0,0,1]
//   法兰目标逆解（TCP 偏心 + U 旋转补偿，总则步骤4）：
//     X_target = X_obj - (dx·cosUf - dy·sinUf)
//     Y_target = Y_obj - (dx·sinUf + dy·cosUf)
//   副工具：ΔT_world = R(Uf) ∘ ΔT_slave（相对主工具的矢量随 U 旋转）
//
// 门禁纪律（迁移设计 §4，吸收范式1 血泪）：
//   ★ fail-closed：图缺失/退化/身份不符 → 抛 ChainResolveException（带节点名），
//     严禁"取默认值继续跑"（ST_007 抄别工位档案静默开跑的教训）。
//   ★ 硬拦四条 → 图完整性检查 G1~G5（见 Validate）。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using Grayson.Vision.Contracts.Calibration.Services;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>链求值失败（fail-closed）。Message 必须指名缺哪个节点/哪条边。</summary>
    public sealed class ChainResolveException : Exception
    {
        public ChainResolveException(string message) : base(message) { }
    }

    /// <summary>机器人当前位姿（SCARA 平面：X/Y 平移 + U 旋转）</summary>
    public struct ChainRobotPose
    {
        public double X, Y, U;

        /// <summary>运动学正解 T_F→B（总则规则②的 2D 形态）</summary>
        public HomMat2D ToFlangeToBase()
        {
            HomMat2D r = HomMat2D.RotationDeg(U);
            return HomMat2D.FromElements(r.A11, r.A12, X, r.A21, r.A22, Y);
        }
    }

    public static class ChainEngine
    {
        //---------------------------------------------------------------------
        // 图门禁（Validate：可独立调用；Resolve 前内部强制跑一遍）
        //---------------------------------------------------------------------

        /// <summary>图完整性校验。返回错误列表；空 = 通过。（范式1 硬拦四条的图等价物）</summary>
        public static List<string> Validate(StationCalibGraph graph, string expectedStationCode)
        {
            var errs = new List<string>();
            if (graph == null) { errs.Add("链图为 null"); return errs; }

            // G0（原"档案不可得⇒没核过"的 fail-closed 化）：身份必须匹配
            if (string.IsNullOrWhiteSpace(graph.StationCode))
                errs.Add("G0 链图缺 StationCode（未绑定身份，拒绝求值）");
            else if (!string.IsNullOrWhiteSpace(expectedStationCode)
                     && !string.Equals(graph.StationCode, expectedStationCode, StringComparison.OrdinalIgnoreCase))
                errs.Add("G0 链图身份不符：图=" + graph.StationCode + " 工位=" + expectedStationCode);

            // G1：相机节点完整 + 矩阵非退化
            foreach (var c in graph.Cameras ?? new List<ChainCameraNode>())
            {
                if (string.IsNullOrWhiteSpace(c.CameraId)) { errs.Add("G1 存在缺 CameraId 的相机节点"); continue; }
                if (!IsFinite6(c.Matrix))
                    errs.Add("G1 相机 " + c.CameraId + " 矩阵缺失/非法（须 6 个有限数）");
                else if (Math.Abs(new Mat6(c.Matrix).Determinant) < 1e-12)
                    errs.Add("G1 相机 " + c.CameraId + " 矩阵退化（行列式≈0）");
            }

            // G2：工具节点完整 + 非零偏移必须已声明符号（09-16 教训的图化）
            foreach (var t in graph.Tools ?? new List<ChainTcpNode>())
            {
                if (string.IsNullOrWhiteSpace(t.ToolId)) { errs.Add("G2 存在缺 ToolId 的工具节点"); continue; }
                bool hasOffset = t.Offset != null && t.Offset.Length == 2
                                 && (Math.Abs(t.Offset[0]) > 1e-9 || Math.Abs(t.Offset[1]) > 1e-9);
                if (hasOffset)
                {
                    if (t.Meta == null || t.Meta.OffsetSource == ChainOffsetSource.Undeclared)
                        errs.Add("G2 工具 " + t.ToolId + " 带非零偏移但 OffsetSource 未声明（禁止消费）");
                    else if (!t.Meta.SignDeclared)
                        errs.Add("G2 工具 " + t.ToolId + " 偏移符号未判定（SignDeclared=false，选错偏 264mm 教训）");
                }
                if (!t.IsMaster && string.IsNullOrWhiteSpace(t.BindMasterToolId))
                    errs.Add("G2 副工具 " + t.ToolId + " 未绑定主工具");
            }

            // G3：边的两端必须存在
            foreach (var e in graph.Edges ?? new List<ChainEdge>())
            {
                if (graph.FindCamera(e.FromCameraId) == null)
                    errs.Add("G3 边引用了不存在的相机: " + (e.FromCameraId ?? "(null)"));
                if (graph.FindTool(e.ToToolId) == null)
                    errs.Add("G3 边引用了不存在的工具: " + (e.ToToolId ?? "(null)"));
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
        // 求值：像素 → 世界（Robot Base）
        //---------------------------------------------------------------------

        /// <summary>
        /// 像素点沿链推到 Robot Base 世界坐标（毫米）。
        /// fail-closed：任何环节缺失即抛 ChainResolveException。
        /// expectedStationCode 非空时强制校验图身份（G0）。
        /// </summary>
        public static void ResolvePixelToWorld(StationCalibGraph graph, string cameraId,
                                               double u, double v, ChainRobotPose pose,
                                               out double wx, out double wy,
                                               string expectedStationCode = null)
        {
            var errs = Validate(graph, expectedStationCode);
            if (errs.Count > 0)
                throw new ChainResolveException("链图未通过门禁，拒绝求值: " + string.Join("；", errs));

            var cam = graph.FindCamera(cameraId);
            if (cam == null)
                throw new ChainResolveException("相机节点不存在: " + cameraId);
            if (!IsFinite6(cam.Matrix))
                throw new ChainResolveException("相机 " + cameraId + " 矩阵缺失/非法，拒绝求值");

            var m = new Mat6(cam.Matrix);
            if (cam.Mount == ChainCameraMount.EyeToHand)
            {
                m.ToHomMat2D().Map(u, v, out wx, out wy);          // P = H_Cam→Robot(u,v)
                return;
            }
            // EIH：P = T_F→B(实时位姿) ∘ H_Cam→Flange
            var chain = HomMat2D.Compose(pose.ToFlangeToBase(), m.ToHomMat2D());
            chain.Map(u, v, out wx, out wy);
        }

        //---------------------------------------------------------------------
        // 逆解：世界目标 → 法盘目标（含 TCP 偏心 + U 旋转补偿）
        //---------------------------------------------------------------------

        /// <summary>
        /// 求法兰目标坐标：让工具尖端在 U=U_final 时压在世界点 (wx,wy) 上。
        /// fail-closed 同上；副工具偏移在法兰系下与主工具求和（刚性阵列：o_i = o_master + δ_i，
        /// δ_i 定义在法兰系），旋转统一由主公式的 R(Uf) 项承担——严禁预旋转后二次旋转。
        /// </summary>
        public static void ResolveFlangeTarget(StationCalibGraph graph, string toolId,
                                               double wx, double wy, double uFinalDeg,
                                               out double fx, out double fy,
                                               string expectedStationCode = null)
        {
            var errs = Validate(graph, expectedStationCode);
            if (errs.Count > 0)
                throw new ChainResolveException("链图未通过门禁，拒绝逆解: " + string.Join("；", errs));

            var tool = graph.FindTool(toolId);
            if (tool == null)
                throw new ChainResolveException("工具节点不存在: " + toolId);

            double dx = 0, dy = 0;
            if (tool.IsMaster)
            {
                ReadOffset(tool, out dx, out dy);
            }
            else
            {
                var master = graph.FindTool(tool.BindMasterToolId);
                if (master == null)
                    throw new ChainResolveException("副工具 " + tool.ToolId + " 绑定的主工具不存在: "
                                                    + (tool.BindMasterToolId ?? "(null)"));
                ReadOffset(master, out double mx, out double my);
                ReadOffset(tool, out double sx, out double sy);
                // 刚性阵列：副工具偏移定义在法兰系，与主工具直接求和；旋转由主公式 R(Uf) 统一承担
                dx = mx + sx;
                dy = my + sy;
            }

            double radF = uFinalDeg * Math.PI / 180.0;
            double cf = Math.Cos(radF), sf = Math.Sin(radF);
            fx = wx - (dx * cf - dy * sf);
            fy = wy - (dx * sf + dy * cf);
        }

        //---------------------------------------------------------------------
        // 私有工具
        //---------------------------------------------------------------------

        private static void ReadOffset(ChainTcpNode tool, out double dx, out double dy)
        {
            if (tool.Offset == null || tool.Offset.Length != 2)
                throw new ChainResolveException("工具 " + tool.ToolId + " 偏移矢量缺失/非法，拒绝逆解");
            dx = tool.Offset[0];
            dy = tool.Offset[1];
        }

        private static bool IsFinite6(double[] m)
        {
            if (m == null || m.Length != 6) return false;
            foreach (double d in m)
                if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            return true;
        }

        /// <summary>6 元组的轻量算术视图（避免为校验构造完整 HomMat2D）</summary>
        private readonly struct Mat6
        {
            private readonly double _a11, _a12, _tx, _a21, _a22, _ty;
            public Mat6(double[] m)
            {
                _a11 = m[0]; _a12 = m[1]; _tx = m[2];
                _a21 = m[3]; _a22 = m[4]; _ty = m[5];
            }
            public double Determinant => _a11 * _a22 - _a12 * _a21;
            public HomMat2D ToHomMat2D()
            {
                return HomMat2D.FromElements(_a11, _a12, _tx, _a21, _a22, _ty);
            }
        }
    }
}
