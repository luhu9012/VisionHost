//===================================================================================
// 文件名: ChainHydrator.cs
// 说 明: 范式2 消费侧水合器——①范式1 档案快照 → 链图导出（等价折叠）②三消费入口
//        （物位 / 吸点 / 下相机纠偏，对齐旧 ResolveObjectBase / ResolveCommand /
//        ResolveDownCameraOffset 的语义与留痕风格）。
//
// ★ R1 等价折叠的数学（迁移设计附录A R1 的验收核心，chain_selftest E 组逐位验证）：
//
//   范式1 EIH：  X_obj = P_photo + O − H(u)          （H 真值=U0 下九点命令位，O=旋转中心）
//   范式2 EIH：  X_obj = T_F→B(P_photo,U0) ∘ H_Cam→F(u)
//   令 H_Cam→F(u) = R(−U0)·(O − H(u))，则两条式子对一切 (u, P_photo) 逐位相等。
//   ∵ T_F→B(P,U0)∘h = P + R(U0)·h，取 h(u)=R(−U0)(O−H(u)) ⟹ P + (O − H(u))。∎
//
//   线性部分：H(u)=A·u+t ⟹ h(u) = −R(−U0)·A·u + R(−U0)·(O−t)。
//   工具偏移：范式1 P_go = X_obj − R(U_go−U0)·e（e 世界系 U0 基准）
//             范式2 command = X_obj − R(U_final)·offset_flange
//             ⟹ offset_flange = R(−U0)·e。（同轴免旋转项 ⟹ offset=[0,0]）
//   杆端域③：X_obj = H(u) + sign·b（b 世界常量，与 U 无关——同心吸嘴语义）
//             ⟹ 折叠进 T_Cam→Robot 平移分量：t' = t + sign·b（Meta 留痕）。
//   下相机：  δ = H_down(R_img) − H_down(R_cdown)——链图按 ETH 相机两次求值做差，
//             与旧式逐位相等（同一矩阵同一线性映射，平移项相减消掉）。
//
// ★ fail-closed 护栏（新增，范式1 没有）：
//   - 导出的 EIH 相机在 Meta.ValidPhotoUDeg 记录 U0；ResolveObject 发现 pose.U≠U0
//     即抛 ChainResolveException（范式1 的 H 是 U0 域折叠产物，换个拍照 U 静默用=错）。
//   - 带 e 的工具必须 OffsetSource≠Undeclared 且 SignDeclared=true（G2，选错偏 264mm 教训）。
//
// 单元纪律：本文件不做 IO；JSON 读写走 StationCalibGraph；异常只用 ChainResolveException。
//===================================================================================
using System;
using System.Globalization;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>范式1 遗留档案的标定量快照（导出器唯一输入，字段=VisionPickPlaceConfig/MahjongDualNozzleConfig 扁平字段同构）</summary>
    public sealed class LegacyCalibSnapshot
    {
        public string StationCode;
        public string CameraId = "Cam_A";
        public string ToolId = "Nozzle1";

        /// <summary>范式1 消费分型（六档枚举的字符串形态，导出后即退役）</summary>
        public LegacyConsumptionKind Kind = LegacyConsumptionKind.EthDirect;

        /// <summary>H 九点矩阵 6 元组 [a11,a12,tx,a21,a22,ty]（像素→U0 命令域）</summary>
        public double[] H;

        /// <summary>基准角 U0（°）。范式1 档案的一切量都定义在 U=U0 上。</summary>
        public double U0Deg;

        /// <summary>旋转中心 O（世界/H 域，EIH 两档必填）</summary>
        public double RotCenterWx, RotCenterWy;

        /// <summary>吸嘴偏心 e（世界系，U0 基准）</summary>
        public double EccX, EccY;
        public bool HasEcc;
        /// <summary>档案声明：吸嘴与 U 轴同轴（免 R(U−U0) 旋转项）</summary>
        public bool NozzleAxisCoaxial;

        /// <summary>③ 杆端域：H(u)+sign·b 的 b（世界常量）</summary>
        public double RodOffsetWx, RodOffsetWy, RodOffsetSign = 1.0;

        public ChainCalibMeta CameraMeta;
        public ChainCalibMeta ToolMeta;
    }

    /// <summary>范式1 六档（仅导出器输入用，导出完成后整个枚举随旧契约一起退役）</summary>
    public enum LegacyConsumptionKind
    {
        NozzleDomainDirect = 0,   // ① 吸嘴域直吸
        EthDirect = 1,            // ② 固定相机直拍工件
        FixedCameraRodOffset = 2, // ③ 固定相机+延伸杆标定（H(u)+sign·b）
        EihDirect = 3,            // ④ EIH+同轴（X=P+O−H(u)，免旋转项）
        EihWithRotation = 4,      // ⑤ EIH+偏心（X=P+O−H(u)，吸点带 R(U−U0)·e）
        DownCameraRelative = 5,   // ⑥ 下相机相对纠偏（δ，不产生绝对物位）
    }

    public static class ChainHydrator
    {
        //---------------------------------------------------------------------
        // ① 导出：范式1 快照 → 范式2 链图
        //---------------------------------------------------------------------

        /// <summary>
        /// 把范式1 档案标定量等价折叠成链图。数学见文件头；每一步折叠由 chain_selftest
        /// E 组探针与 CalibrationGeometry 旧公式做逐位对照（容差 1e-9）。
        /// </summary>
        public static StationCalibGraph ExportFromLegacy(LegacyCalibSnapshot s, out string error)
        {
            error = null;
            if (s == null) { error = "快照为 null"; return null; }
            if (string.IsNullOrWhiteSpace(s.StationCode)) { error = "快照缺 StationCode"; return null; }
            if (s.H == null || s.H.Length != 6)
            { error = "H 矩阵缺失/非法（须 6 元组）"; return null; }
            foreach (double d in s.H)
                if (double.IsNaN(d) || double.IsInfinity(d)) { error = "H 矩阵含 NaN/Inf"; return null; }

            double rad0 = s.U0Deg * Math.PI / 180.0;
            // R(−U0) = [[cosU0, sinU0], [−sinU0, cosU0]]
            double c0 = Math.Cos(rad0), s0 = Math.Sin(rad0);

            bool isEih = s.Kind == LegacyConsumptionKind.EihDirect
                      || s.Kind == LegacyConsumptionKind.EihWithRotation;

            double a11 = s.H[0], a12 = s.H[1], tx = s.H[2];
            double a21 = s.H[3], a22 = s.H[4], ty = s.H[5];

            double[] matrix;
            ChainCameraMount mount;
            var camMeta = s.CameraMeta != null ? CloneMeta(s.CameraMeta) : new ChainCalibMeta();

            if (isEih)
            {
                // H_Cam→F：线性 = −R(−U0)·A；平移 = R(−U0)·(O − t)
                // R(−U0)·A = [[c0·a11+s0·a21, c0·a12+s0·a22], [−s0·a11+c0·a21, −s0·a12+c0·a22]]
                double r11 = c0 * a11 + s0 * a21, r12 = c0 * a12 + s0 * a22;
                double r21 = -s0 * a11 + c0 * a21, r22 = -s0 * a12 + c0 * a22;
                double ox = s.RotCenterWx - tx, oy = s.RotCenterWy - ty;
                matrix = new[] { -r11, -r12, c0 * ox + s0 * oy, -r21, -r22, -s0 * ox + c0 * oy };
                mount = ChainCameraMount.EyeInHand;
                // ★ U0 域护栏：折叠产物只在拍照 U=U0 时与范式1 等价
                camMeta.ValidPhotoUDeg = s.U0Deg;
                if (camMeta.Note == null) camMeta.Note = "";
                camMeta.Note += "[导出] 由范式1 EIH 档案折叠：H_Cam→F = R(−U0)·(O − H(u))，仅拍照 U=" +
                                s.U0Deg.ToString("F2", CultureInfo.InvariantCulture) + "° 有效";
            }
            else
            {
                // ETH 族：T_Cam→Robot = H；③ 把 sign·b 折进平移（同心 b 与 U 无关）
                if (s.Kind == LegacyConsumptionKind.FixedCameraRodOffset)
                {
                    tx += s.RodOffsetSign * s.RodOffsetWx;
                    ty += s.RodOffsetSign * s.RodOffsetWy;
                    camMeta.Note = (camMeta.Note ?? "") + "[导出] 杆端域 b(sign=" +
                        s.RodOffsetSign.ToString("F0", CultureInfo.InvariantCulture) +
                        ")=(" + s.RodOffsetWx.ToString("F3", CultureInfo.InvariantCulture) + "," +
                        s.RodOffsetWy.ToString("F3", CultureInfo.InvariantCulture) +
                        ") 已折叠进平移（H(u)+sign·b）";
                }
                matrix = new[] { a11, a12, tx, a21, a22, ty };
                mount = ChainCameraMount.EyeToHand;
            }

            // 工具偏移：offset_flange = R(−U0)·e；同轴/免旋转项 ⟹ [0,0]
            // 范式1 旋转项判据：EihWithRotation 恒带；其余档由 NozzleAxisCoaxial 声明决定
            bool rotTerm = s.Kind == LegacyConsumptionKind.EihWithRotation
                           || (s.Kind != LegacyConsumptionKind.EihDirect && !s.NozzleAxisCoaxial);
            bool hasE = s.HasEcc && (Math.Abs(s.EccX) > 1e-9 || Math.Abs(s.EccY) > 1e-9);
            double[] offset = rotTerm && hasE
                ? new[] { c0 * s.EccX + s0 * s.EccY, -s0 * s.EccX + c0 * s.EccY }
                : new[] { 0.0, 0.0 };

            var toolMeta = s.ToolMeta != null ? CloneMeta(s.ToolMeta) : new ChainCalibMeta();
            if (rotTerm && hasE && toolMeta.OffsetSource == ChainOffsetSource.Undeclared)
            {
                // 导出器不替档案撒谎：来源未声明就如实写 Undeclared，让 G2 拦下（fail-closed）
                toolMeta.OffsetSource = ChainOffsetSource.Undeclared;
            }
            if (offset[0] != 0 || offset[1] != 0)
            {
                toolMeta.Note = (toolMeta.Note ?? "") + "[导出] offset_flange = R(−U0)·e";
            }

            var graph = new StationCalibGraph
            {
                StationCode = s.StationCode,
                Cameras =
                {
                    new ChainCameraNode
                    {
                        CameraId = s.CameraId, Mount = mount, Matrix = matrix, Meta = camMeta,
                    },
                },
                Tools =
                {
                    new ChainTcpNode
                    {
                        ToolId = s.ToolId, IsMaster = true, Offset = offset, Meta = toolMeta,
                        // O 的法兰系投影（参考值；SCARA U 中心在法兰系= R(−U0)·O − R(−U0)·0）
                        URotationCenter = isEih
                            ? new[] { c0 * s.RotCenterWx + s0 * s.RotCenterWy,
                                      -s0 * s.RotCenterWx + c0 * s.RotCenterWy }
                            : null,
                    },
                },
                Edges =
                {
                    new ChainEdge
                    {
                        FromCameraId = s.CameraId, ToToolId = s.ToolId,
                        Usage = s.Kind == LegacyConsumptionKind.DownCameraRelative
                            ? "DownCameraCorrect" : "PickAnchor",
                    },
                },
            };
            return graph;
        }

        //---------------------------------------------------------------------
        // ② 消费入口一：像素 → 工件物位 X_obj（对齐旧 ResolveObjectBase 语义）
        //---------------------------------------------------------------------

        /// <summary>
        /// 像素沿链推到工件物位（Robot Base，毫米）。fail-closed；EIH 校验拍照 U=U0（U0 域护栏）。
        /// </summary>
        public static void ResolveObject(StationCalibGraph graph, string cameraId,
                                         double u, double v, ChainRobotPose photoPose,
                                         out double objX, out double objY,
                                         string expectedStationCode = null, Action<string> trace = null)
        {
            var cam = RequireCamera(graph, cameraId, expectedStationCode);
            if (cam.Mount == ChainCameraMount.EyeInHand
                && cam.Meta != null && !double.IsNaN(cam.Meta.ValidPhotoUDeg)
                && Math.Abs(NormAngle(photoPose.U - cam.Meta.ValidPhotoUDeg)) > 1e-6)
            {
                throw new ChainResolveException("相机 " + cameraId +
                    " 为范式1 折叠导出（仅拍照 U=" + cam.Meta.ValidPhotoUDeg.ToString("F2") +
                    "° 有效），实际拍照 U=" + photoPose.U.ToString("F2") + "° —— 拒绝求值（U0 域护栏）");
            }

            ChainEngine.ResolvePixelToWorld(graph, cameraId, u, v, photoPose, out objX, out objY,
                                            expectedStationCode);
            trace?.Invoke("  X_obj 口径=链求值(" + (cam.Mount == ChainCameraMount.EyeInHand ? "EIH" : "ETH") +
                          "): X_obj=链(" + cameraId + ")(" + u.ToString("F1") + "," + v.ToString("F1") + ")=(" +
                          objX.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          objY.ToString("F3", CultureInfo.InvariantCulture) + ")");
        }

        //---------------------------------------------------------------------
        // ③ 消费入口二：物位 → 吸点命令位（对齐旧 ResolveCommand 语义）
        //---------------------------------------------------------------------

        /// <summary>
        /// 物位 → 机械手命令位（工具尖端在 U=U_go 压住物位）。副工具自动经主工具合成。
        /// </summary>
        public static void ResolvePick(StationCalibGraph graph, string toolId,
                                       double objX, double objY, double uGoDeg,
                                       out double cmdX, out double cmdY,
                                       string expectedStationCode = null, Action<string> trace = null)
        {
            ChainEngine.ResolveFlangeTarget(graph, toolId, objX, objY, uGoDeg,
                                            out cmdX, out cmdY, expectedStationCode);
            var tool = graph.FindTool(toolId);
            bool withRot = tool != null && tool.Offset != null
                           && (Math.Abs(tool.Offset[0]) > 1e-9 || Math.Abs(tool.Offset[1]) > 1e-9);
            trace?.Invoke(withRot
                ? "  吸点口径=链逆解(带偏心): 吸点=X_obj−R(" + uGoDeg.ToString("F2") + "°)·offset=(" +
                  cmdX.ToString("F3", CultureInfo.InvariantCulture) + "," +
                  cmdY.ToString("F3", CultureInfo.InvariantCulture) + ")"
                : "  吸点口径=链逆解(同轴/免旋转): 吸点=物位(" +
                  cmdX.ToString("F3", CultureInfo.InvariantCulture) + "," +
                  cmdY.ToString("F3", CultureInfo.InvariantCulture) + ")");
        }

        //---------------------------------------------------------------------
        // ④ 消费入口三：下相机相对纠偏 δ（对齐旧 ResolveDownCameraOffset 语义）
        //---------------------------------------------------------------------

        /// <summary>
        /// δ = H_down(R_img) − H_down(R_cdown)——两像素各自沿链求值后做差。
        /// 平移项在差分中消掉，与旧"两映射相减"逐位相等；绝对读数依然无物理意义（纪律不变）。
        /// </summary>
        public static void ResolveDownDelta(StationCalibGraph graph, string cameraId,
                                            double uImg, double vImg, double uRot, double vRot,
                                            ChainRobotPose pose,
                                            out double dx, out double dy,
                                            string expectedStationCode = null, Action<string> trace = null)
        {
            ChainEngine.ResolvePixelToWorld(graph, cameraId, uImg, vImg, pose, out double ix, out double iy,
                                            expectedStationCode);
            ChainEngine.ResolvePixelToWorld(graph, cameraId, uRot, vRot, pose, out double rx, out double ry,
                                            expectedStationCode);
            dx = ix - rx;
            dy = iy - ry;
            trace?.Invoke("  下相机口径=链差分: δ=H(R_img)−H(R_c)=(" +
                          dx.ToString("F3", CultureInfo.InvariantCulture) + "," +
                          dy.ToString("F3", CultureInfo.InvariantCulture) + ")");
        }

        //---------------------------------------------------------------------
        // 私有
        //---------------------------------------------------------------------

        private static ChainCameraNode RequireCamera(StationCalibGraph graph, string cameraId,
                                                     string expectedStationCode)
        {
            var errs = ChainEngine.Validate(graph, expectedStationCode);
            if (errs.Count > 0)
                throw new ChainResolveException("链图未通过门禁: " + string.Join("；", errs));
            var cam = graph.FindCamera(cameraId);
            if (cam == null)
                throw new ChainResolveException("相机节点不存在: " + cameraId);
            return cam;
        }

        /// <summary>角度差归一到 (−180,180]</summary>
        private static double NormAngle(double deg)
        {
            while (deg > 180.0) deg -= 360.0;
            while (deg <= -180.0) deg += 360.0;
            return deg;
        }

        private static ChainCalibMeta CloneMeta(ChainCalibMeta m)
        {
            return new ChainCalibMeta
            {
                Method = m.Method, PointPairs = m.PointPairs, RmsMm = m.RmsMm,
                OffsetSource = m.OffsetSource, SignDeclared = m.SignDeclared,
                CapturedAt = m.CapturedAt, Operator = m.Operator, Note = m.Note,
                ValidPhotoUDeg = m.ValidPhotoUDeg,
            };
        }
    }
}
