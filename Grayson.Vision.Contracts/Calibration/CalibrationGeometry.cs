using System;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// 标定几何·唯一真源（2026-09-08 定案）。
    /// 校验台、两个生产引擎（MahjongDualNozzle / VisionPickPlace）、发布链全部调这里，
    /// 任何地方都不要再手抄一份公式（历史上 v3/v4/v5 各抄一份，就是反复调不准的根源）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 一、先搞懂 H 到底是什么（不懂这个，后面全白搭）
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// 九点标定的做法：机械手走到一个个位置，相机去拍【一个固定不动的基准特征】，
    /// 记下"机械手位置 P" 和 "特征出现在哪个像素 u"，拟合出矩阵 H，使得 H(u) = P。
    ///
    /// 所以 H 回答的问题是：
    ///     "要让【基准特征】出现在像素 u，机械手该停在哪？"    答案是 H(u)
    ///
    /// ⚠ 注意两件事：
    ///   ① H 说的是【机械手该停哪】，不是【工件在哪】。它俩差着一个固定偏移。
    ///   ② H 是拿【基准特征】标定出来的。拿它去看【别的特征】，读数会差一个位移。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 二、人话推导（跟着数字走一遍就懂了）
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// 假设：标定时的基准特征，放在世界坐标 (100, 100)。
    ///
    ///   第 1 步（九点标定）：机械手走到 (200,200) 时，基准特征出现在像素 u1
    ///           ⟹ H(u1) = (200,200)     —— 机械手位置原样记下来
    ///
    ///   第 2 步（现场拍照）：机械手现在停在 P_photo = (250,230)，
    ///           拍到【工件特征】出现在像素 u2，算得 H(u2) = (180,190)
    ///
    ///   第 3 步（关键对比）：同样是像素 u2 ——
    ///           · 机械手停在 (180,190) 时，出现在这里的是【基准特征】
    ///           · 机械手停在 (250,230) 时，出现在这里的是【工件特征】
    ///           ⟹ 工件特征比基准特征，偏移了机械手多走的那一段：
    ///              工件特征 = 基准特征 + [ (250,230) − (180,190) ]
    ///                       = 基准特征 + (P_photo − H(u2))
    ///
    ///   第 4 步（基准特征在哪？）—— 这正是【旋转中心 O】要回答的问题：
    ///           旋转标定求出的 O，在数值上就等于"基准特征的世界位置"（因为 H 的坐标原点
    ///           就是基准特征）。本例 O = (100,100)，正是我们放的基准特征位置。
    ///
    ///   第 5 步（得到工件特征位置）：
    ///           X_obj = O + (P_photo − H(u)) = P_photo + O − H(u)
    ///                 = (250,230) + (100,100) − (180,190) = (170,140)   ← 工件真位
    ///
    ///   第 6 步（吸嘴要压住它，机械手停哪？）：
    ///           吸嘴尖装在机械手回转中心旁边，偏一个 e（U 转时 e 跟着转）。
    ///           要吸嘴尖落在 (170,140)，机械手就得退开这个偏心：
    ///           P_go = X_obj − R(U_go − U0)·e
    ///           若 e = (12,−7) 且 U=0：P_go = (170,140) − (12,−7) = (158,147)
    ///           验算：吸嘴尖 = (158,147) + (12,−7) = (170,140) ✓ 正压特征
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 三、就这两个公式
    /// ══════════════════════════════════════════════════════════════════
    ///
    ///     X_obj = P_photo + O − H(u)          工件特征的真实位置
    ///     P_go  = X_obj − R(U_go − U0) · e    机械手该走到的位置
    ///
    /// 放料时把 X_obj 换成"要摆到的位置"，用同一个 P_go 公式即可。
    ///
    /// ── 三个输入量的来历 ──────────────────────────────────────────────
    ///   H       九点标定得到，直接用
    ///   P_photo 拍照那一瞬间机械手在哪（必须，且必须是"拍这张图时"的位置）
    ///   O       旋转标定：延伸杆末端绕 U 转几个角度画圆 → 取【圆心】
    ///           ★ 半径（=杆长）直接扔掉，所以杆多长、偏不偏心都无所谓，只要采样时杆不松动
    ///   e       真吸嘴偏心 = O − H(p_tip)
    ///           p_tip：U=U0 时吸嘴尖压住某个特征，抬 Z 拍到它，那个像素就是 p_tip
    ///           ★ 一次物理对针就能算出 e，不需要同心短杆
    ///           ★ 偏心延伸杆测出来的 ToolEccW 是【杆末端偏心】，不是 e，别混用
    ///
    /// ── 固定相机（ETH）工位 ────────────────────────────────────────────
    ///   相机不动、只拍照（如麻将工位"相机固定于料盘上方"）时，H 直接就是工件的真实位置，
    ///   不需要 O 也不需要 P_photo：X_obj = H(u)。用 CameraMountEih=false 走这条分支。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 四、走过的弯路（别再改回去）
    /// ══════════════════════════════════════════════════════════════════
    ///   v5  R_go = H(u) − TCO           仿真最大误差 451 mm
    ///       TCO = H(p_tip) − R_n 里混进了绝对坐标 R_n，放大镜式放大误差
    ///   v4  P_photo + H(p_tip) − H(u)   仿真最大误差 27.8 mm
    ///       少了旋转项，只在 U=U0 时对（现保留为"缺 O 或缺 e"时的退路）
    ///   求 O 用"像素域定圆→圆心再映射"  仿真误差 8.46 mm
    ///       像素域那几个点映射到世界后是【椭圆】上的点，拿圆去套椭圆，圆心必偏。
    ///       必须"先逐点 H 映射到世界，再在世界坐标里定圆"。
    ///
    /// 回归自检：python .workbuddy/verify_calib_geometry.py
    /// </summary>
    public static class CalibrationGeometry
    {
        /// <summary>把偏心矢量转过一个角度：R(deg)·(x,y) = (x·cos−y·sin, x·sin+y·cos)。</summary>
        public static (double X, double Y) Rot(double deg, double x, double y)
        {
            double r = deg * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            return (x * c - y * s, x * s + y * c);
        }

        /// <summary>
        /// 物理对针反解真吸嘴偏心 e：e = O − H(p_tip)，再归一到 U0 参考。
        /// 直白说：对针时吸嘴尖压住的那个点，它在 H 里的读数是 H(p_tip)；
        /// 回转中心在 H 里的读数是 O；两者之差就是"吸嘴尖伸出去多远"= 偏心 e。
        /// </summary>
        /// <param name="ox">旋转中心 O 的 X（世界/H 域，三点定圆得到）</param>
        /// <param name="oy">旋转中心 O 的 Y</param>
        /// <param name="wTipX">对针像素 p_tip 经 H 映射后的 X</param>
        /// <param name="wTipY">对针像素 p_tip 经 H 映射后的 Y</param>
        /// <param name="uAlignDeg">对针当时机械手的 U 角（°）</param>
        /// <param name="u0Deg">基准角 U0（°，通常 0）</param>
        public static (double X, double Y) SolveNozzleEcc(
            double ox, double oy, double wTipX, double wTipY, double uAlignDeg, double u0Deg)
        {
            double mx = ox - wTipX;
            double my = oy - wTipY;
            return Rot(u0Deg - uAlignDeg, mx, my);
        }

        /// <summary>
        /// 像素 → 工件特征的真实位置 X_obj。
        /// 眼在手(EIH，相机跟着机械手走)：X_obj = 拍照机位 + 旋转中心 − H(像素)
        /// 固定相机(ETH，相机不动)：      X_obj = H(像素)
        /// </summary>
        public static (double X, double Y) ObjectBase(
            double wx, double wy, double photoX, double photoY, double ox, double oy, bool eih)
        {
            if (!eih) return (wx, wy);
            return (photoX + ox - wx, photoY + oy - wy);
        }

        /// <summary>
        /// 工件真实位置 → 机械手该走到的位置：P_go = X_obj − R(U_go − U0)·e。
        /// 即"把吸嘴偏心退掉"。吸取（U_go=吸取角）和放料（U_go=放料角，X_obj=摆放位）同一式。
        /// </summary>
        public static (double X, double Y) CommandFor(
            double objX, double objY, double ex, double ey, double uGoDeg, double u0Deg)
        {
            var (rx, ry) = Rot(uGoDeg - u0Deg, ex, ey);
            return (objX - rx, objY - ry);
        }

        /// <summary>
        /// 一步到位：像素 → 机械手命令位（内部就是上面两步，供校验台与生产引擎共用）。
        /// </summary>
        public static (double CmdX, double CmdY, double ObjX, double ObjY) Solve(
            double wx, double wy,
            double photoX, double photoY,
            double ox, double oy,
            double ex, double ey,
            double uGoDeg, double u0Deg,
            bool eih)
        {
            var (oxb, oyb) = ObjectBase(wx, wy, photoX, photoY, ox, oy, eih);
            var (cx, cy) = CommandFor(oxb, oyb, ex, ey, uGoDeg, u0Deg);
            return (cx, cy, oxb, oyb);
        }
    }
}
