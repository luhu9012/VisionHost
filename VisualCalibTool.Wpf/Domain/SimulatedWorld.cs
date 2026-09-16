using System;
using System.Globalization;

namespace VisualCalibTool.Domain
{
    /// <summary>
    /// ★ 仿真世界模型（P0 的核心资产之一）。
    ///
    /// 目标：<b>在没有任何硬件的情况下把九点全流程跑通</b>，而且不是"造假结果"——
    /// 是<b>造真图让真算法去解</b>。所以这里给出一个可解析的"理想相机 + 理想运动学"模型：
    ///   · <see cref="Project"/>   —— 给定机械手位置，算出固定的基准 Mark 落在哪个像素（即"造图"）；
    ///   · <see cref="ExpectedH"/> —— 由同一模型解析地推出 H；
    ///   · 解算器解出来的 H 必须 ≈ <see cref="ExpectedH"/>，否则算法有 bug。
    ///
    /// 可注入的偏差（用来检验各种检测器真的有效）：
    ///   · <see cref="PixelNoisePx"/>     图像噪声 → 制造非零残差、让 LOO 有意义；
    ///   · <see cref="FeedbackNoiseMm"/>  反馈位噪声 → 模拟伺服跟随误差；
    ///   · <see cref="InjectMirror"/>     注入镜像 → <b>形状检测必须报 det(A) &lt; 0</b>；
    ///   · <see cref="Distortion"/>       注入径向畸变 → 内参链必须能标出近似系数。
    /// </summary>
    public sealed class SimulatedWorld
    {
        public SimulatedWorld()
        {
            MarkWorldXy = new Vec2(120.0, -30.0);
        }

        // ── 相机 / 图像 ──
        public int Width = 1280;
        public int Height = 1024;

        /// <summary>世界（mm）到像素的比例（X 轴）。</summary>
        public double MmPerPixel = 0.05;

        /// <summary>
        /// Y 轴当量相对 X 轴的倍率。1.0 = 方形像素。
        /// ★ 这个参数存在的唯一理由：让"两轴当量不一致"这件事<b>可以被注入和验证</b> ——
        ///   真机上的像素非方形/尺度失真是真实存在的，而它正是"对角元判据在大旋转下漏报"的证明前提。
        /// </summary>
        public double MmPerPixelYFactor = 1.0;

        /// <summary>图像坐标系相对世界坐标系的旋转（度）。真实工位可能是 151° 这类大角，专门用来压测列范数判据。</summary>
        public double ImageRotationDeg = 0.0;

        // ── 被拍的固定 Mark（★ 九点是"走 9 位、每次拍同一个固定 Mark"，不是拍一整块板）──
        public Vec2 MarkWorldXy;

        /// <summary>Mark 在图像里的半径（像素），合成图用。</summary>
        public double MarkRadiusPx = 18.0;

        /// <summary>Mark 灰度值（0~255）。</summary>
        public int MarkGray = 40;

        /// <summary>背景灰度。</summary>
        public int BackgroundGray = 220;

        // ══════════════════════════════════════════════════════════════
        // 旋转模型（O 链 / e 链能仿真的前提）
        // ══════════════════════════════════════════════════════════════
        // 为什么必须补这一块：原来的 Project 与法兰角 U 无关。
        // 而 EIH（眼在手）下相机是**刚性固定在法兰上**的 —— 法兰一转，画面对固定 Mark 就
        // 同时产生两个效应：① 相机绕轴公转（横向位移 c0 被转过去）；② **像面一起滚转**。
        // 少了第 ②，转动 U 后 Mark 的像素纹丝不动，旋转中心链在仿真里根本跑不起来。

        /// <summary>法兰转角基准（度）。<see cref="Project"/> 等价于 <c>ProjectAt(P, U0Deg)</c>。</summary>
        public double U0Deg = 0.0;

        /// <summary>
        /// 相机光轴相对旋转轴的横向偏移（法兰坐标系，u = U0 时，mm）。
        /// 零向量 = 光轴正好落在旋转轴上 → Mark 只会绕像心公转 → <b>半径 = |Mark−法兰|</b>。
        /// </summary>
        public Vec2 CameraLateralOffset;

        /// <summary>像面朝向是否随法兰角一起滚转（EIH 的真实物理：相机跟着工具转，画面会转）。</summary>
        public bool CameraRotatesWithFlange = true;

        /// <summary>
        /// 相机公转方向符号（+1 / −1）。★ 只影响仿真里"转正角度时 Mark 往哪边偏"，
        /// 不影响链条自洽性（真机的正方向由控制器定义，上位机不假设）。
        /// </summary>
        public double CameraOrbitSign = 1.0;

        /// <summary>工具尖相对旋转轴的偏心 <b>e 的真值</b>（法兰坐标系，u = U0 时，mm）。</summary>
        public Vec2 TipEcc;

        /// <summary>工具尖在图像里的半径（px）。偏心链需要"同一帧里同时看见特征与工具尖"。</summary>
        public double TipRadiusPx = 6.0;

        /// <summary>
        /// 是否把工具尖也画进合成图（默认关）。
        /// ★ 九点链必须保持关闭：工具尖是个更深的小圆，开着会变成"伪特征"干扰提取。
        ///   偏心链才打开 —— 而且此时"±40% 参考半径滤伪"正好被压测到（真实用例）。
        /// </summary>
        public bool RenderTip;

        /// <summary>工具尖灰度（比背景深，但比 Mark 浅，便于区分）。</summary>
        public int TipGray = 90;

        // ── 注入的偏差 ──
        public double PixelNoisePx = 0.0;
        public double FeedbackNoiseMm = 0.0;
        public bool InjectMirror = false;

        /// <summary>
        /// 径向畸变系数 [k1, k2, k3, p1, p2]。
        /// ★ 归一化约定：r_n = |u − 主点| / (Width/2)，即图像边缘处 r_n = 1。
        ///   内参链的自检必须用同一约定（"注入 k1=−0.15 → 解出 ≈ −0.15"）。
        /// </summary>
        public double[] Distortion;

        /// <summary>噪声随机种子（固定种子 → 可复现）。</summary>
        public int Seed = 20260914;

        private Random _rand;

        public Vec2 ImageCenterPx
        {
            get { return new Vec2(Width / 2.0, Height / 2.0); }
        }

        /// <summary>镜像注入开关对应的 y 翻转符号。</summary>
        private double Flip
        {
            get { return InjectMirror ? -1.0 : 1.0; }
        }

        private Random Rand
        {
            get { return _rand ?? (_rand = new Random(Seed)); }
        }

        /// <summary>重置随机序列（每次会话开始调用，保证复现）。</summary>
        public void ResetRandom()
        {
            _rand = new Random(Seed);
        }

        /// <summary>造图：给定机械手位置，算出 Mark 应该出现在哪个像素（等价于法兰角 = U0）。</summary>
        public Vec2 Project(Vec2 robotXy)
        {
            return ProjectAt(robotXy, U0Deg);
        }

        /// <summary>
        /// 造图（含法兰角）：给定机械手位置 <b>与该次的法兰角 U</b>，算出 Mark 落在哪个像素。
        ///
        /// 模型（EIH，相机刚性固定在法兰上）：
        ///   相机中心  camCenter = P + c0·R(sign·Δu)      ← 相机绕轴公转
        ///   像面朝向  θ_eff     = θ0 − Δu                ← 画面跟着滚转
        ///   像素      u = C + D⁻¹·F·R(θ_eff)·(W − camCenter)
        ///
        /// Δu = 0 时退化为原来的 <see cref="Project"/>，一字不差（c0 为零向量时连平移项都相同）。
        /// </summary>
        public Vec2 ProjectAt(Vec2 robotXy, double uDeg)
        {
            return ProjectWorldPoint(robotXy, uDeg, MarkWorldXy);
        }

        /// <summary>任意世界点在某一法兰角下的像素位置（工具尖成像走这条）。</summary>
        public Vec2 ProjectWorldPoint(Vec2 robotXy, double uDeg, Vec2 worldPoint)
        {
            double du = uDeg - U0Deg;

            Vec2 camCenter = robotXy;
            if (CameraLateralOffset.LengthSq > 0.0)
            {
                camCenter = robotXy + CameraLateralOffset.Rotate(CameraOrbitSign * du);
            }

            double imgRot = CameraRotatesWithFlange ? ImageRotationDeg - du : ImageRotationDeg;

            Vec2 d = worldPoint - camCenter;
            Vec2 r = d.Rotate(imgRot);

            Vec2 u = new Vec2(
                ImageCenterPx.X + r.X / MmPerPixel,
                ImageCenterPx.Y + Flip * r.Y / (MmPerPixel * MmPerPixelYFactor));

            if (Distortion != null && Distortion.Length >= 3)
            {
                u = ApplyDistortion(u);
            }

            return u;
        }

        /// <summary>
        /// 工具尖的 <b>世界</b>位置：tip = P + R(u − U0)·e_true。
        /// ★ 与消费式 <c>P_go = X_obj − R(ΔU)·e</c> 是同一个旋转约定的逆运算：
        ///   想让它落在 X_obj，就得下发 P_go，代回来正好等于 X_obj。
        /// </summary>
        public Vec2 TipWorld(Vec2 robotXy, double uDeg)
        {
            return robotXy + TipEcc.Rotate(uDeg - U0Deg);
        }

        /// <summary>工具尖在图像里的像素位置。</summary>
        public Vec2 ProjectTip(Vec2 robotXy, double uDeg)
        {
            return ProjectWorldPoint(robotXy, uDeg, TipWorld(robotXy, uDeg));
        }

        /// <summary>
        /// ★ 旋转中心 O 的解析真值。
        ///
        /// 口径来自唯一真源 <c>CalibrationGeometry</c> 的明文结论：
        ///   「旋转标定求出的 O，在数值上就等于<b>基准特征的世界位置</b>」。
        ///
        /// 可以自己推一遍（EIH，XY 不动、只转 U）：
        ///   观测像素 u_k ⇒ 逐点经 H 映射到世界：
        ///       H(u_k) = W − R(−Δu_k)·(W − P)
        ///   即：映射点在以 <b>W 为圆心、|W − P| 为半径</b>的圆上。
        ///   所以圆心 = 基准特征的世界位置 = O，半径 = 基准特征到法兰轴的距离（★ 必须丢弃）。
        ///
        /// 同时也能看出"为什么像素域定圆会偏"：
        ///   世界域的点是正圆，经 H⁻¹（各向异性 + 旋转）拉回像素域后变成<b>椭圆</b>，
        ///   对椭圆（尤其是弧段）做 Kåsa 定圆，圆心必然偏。
        /// </summary>
        public Vec2 ExpectedRotationCenterWorld(Vec2 fixedRobotXy)
        {
            // 模型里基准特征固定不动，O 就是它的世界位置，与法兰当前 XY 无关。
            // 参数保留是为了在调用点表达"当时法兰停在哪儿"，便于阅读。
            return MarkWorldXy;
        }

        /// <summary>旋转采样点的解析映射位置（自检用来与解算器逐点比对）。</summary>
        public Vec2 ExpectedMappedPoint(Vec2 fixedRobotXy, double uDeg)
        {
            double du = uDeg - U0Deg;
            Vec2 v = MarkWorldXy - fixedRobotXy;
            return MarkWorldXy - v.Rotate(-du);
        }

        /// <summary>在真实像素上叠加提取噪声（模拟特征提取误差）。</summary>
        public Vec2 ProjectNoisy(Vec2 robotXy)
        {
            Vec2 u = Project(robotXy);
            if (PixelNoisePx > 0.0)
            {
                u = new Vec2(
                    u.X + (Rand.NextDouble() - 0.5) * 2.0 * PixelNoisePx,
                    u.Y + (Rand.NextDouble() - 0.5) * 2.0 * PixelNoisePx);
            }

            return u;
        }

        /// <summary>在反馈位上叠加噪声（模拟伺服跟随误差）。</summary>
        public Vec2 NoisyFeedback(Vec2 robotXy)
        {
            if (FeedbackNoiseMm <= 0.0)
            {
                return robotXy;
            }

            return new Vec2(
                robotXy.X + (Rand.NextDouble() - 0.5) * 2.0 * FeedbackNoiseMm,
                robotXy.Y + (Rand.NextDouble() - 0.5) * 2.0 * FeedbackNoiseMm);
        }

        /// <summary>
        /// 由同一模型解析推出 H（像素 → 世界）。
        ///
        /// 推导（设 D = diag(mmX, mmY)，F = diag(1, f)，R = R(θ)）：
        ///   u = C + D⁻¹·R·F·(W − P)   ⇒   P = W − F·R(−θ)·D·(u − C)
        /// ⇒ A = −F·R(−θ)·D，于是
        ///   h11 = −cosθ·mmX      h12 = −sinθ·mmY
        ///   h21 =  sinθ·f·mmX    h22 = −cosθ·f·mmY
        /// ★ 直接可读出：两列的范数恰好是 mmX 与 mmY —— 与旋转角无关。
        ///   这就是"两轴当量必须用列向量范数"的数学依据。
        /// </summary>
        public HomMat2D ExpectedH()
        {
            double th = ImageRotationDeg * Math.PI / 180.0;
            double c = Math.Cos(th);
            double s = Math.Sin(th);
            double f = Flip;
            double mmX = MmPerPixel;
            double mmY = MmPerPixel * MmPerPixelYFactor;
            Vec2 ctr = ImageCenterPx;

            // 相机横向偏移把"特征位置"整体挪了一格：有效特征位 = W − c0（c0 为零向量时与原式一字不差）
            Vec2 w = MarkWorldXy - CameraLateralOffset;

            double h11 = -c * mmX;
            double h12 = -s * f * mmY;
            double h21 = s * mmX;
            double h22 = -c * f * mmY;

            double h13 = w.X + (c * mmX * ctr.X + s * f * mmY * ctr.Y);
            double h23 = w.Y + (-s * mmX * ctr.X + c * f * mmY * ctr.Y);

            return new HomMat2D(h11, h12, h13, h21, h22, h23);
        }

        /// <summary>
        /// ★ 把世界调成"整条链能真跑"的物理合理配置（P0 全流程仿真用）。
        ///
        /// 为什么需要它：默认配置是为<b>纯代数自检</b>准备的（Mark 离法兰几百毫米，
        /// 像素坐标可以跑到图像外，反正九点解算只看代数）。但一旦接上"合成图 → 真提取"，
        /// Mark 就必须真的落在视野里，且九个点跨出去也不能出框。
        ///
        /// 取值依据（可逐条核算）：
        ///   · 视野 64 × 51.2 mm（1280 × 1024 @ 0.05 mm/px）；
        ///   · 步长 10 mm ⇒ 网格跨 ±10 mm = ±200 px，落在框内；
        ///   · Mark 距法兰轴 (5, 2) mm ⇒ 九点里最远 19 mm ⇒ 380 px，仍在框内；
        ///   · Mark 半径 18 px ≈ 0.9 mm，肉眼可见且够圆度判定。
        /// </summary>
        public static SimulatedWorld CreatePhysical()
        {
            var w = new SimulatedWorld();
            w.Width = 1280;
            w.Height = 1024;
            w.MmPerPixel = 0.05;
            w.MarkRadiusPx = 18.0;

            // ★ 极性必须与特征提取器的<b>主路径</b>一致：圆提取的默认全局阈值是 [100, 255]（选亮），
            //   所以 Mark 必须画成"亮底上的亮点"。反过来的话主路径永远不可能命中，
            //   整条链只能靠动态阈值兜底活着 —— 那不叫跑通，叫"恰好没踩到坑"。
            w.MarkGray = 230;
            w.BackgroundGray = 40;

            // 工具尖比 Mark 暗、比背景亮：既与背景分得开，也不会在 Mark 内部再制造一个 128 交叉边
            w.TipGray = 120;

            w.ImageRotationDeg = 0.0;

            // Mark 就在法兰轴旁边一点点 —— 这是"基准特征放在旋转轴附近"的现场做法，
            // 也是旋转链能扫大角度的前提（离得越远，转一点就出框）。
            w.BaseFlangeXy = new Vec2(300.0, 200.0);
            w.MarkWorldXy = new Vec2(w.BaseFlangeXy.X + 5.0, w.BaseFlangeXy.Y + 2.0);
            return w;
        }

        /// <summary>整条链仿真时法兰的基准 XY（与 <see cref="CalibTopology"/> 的基准位保持一致）。</summary>
        public Vec2 BaseFlangeXy = new Vec2(300.0, 200.0);

        /// <summary>径向 + 切向畸变合成（合成用简化模型，归一化见 <see cref="Distortion"/> 注释）。</summary>
        public Vec2 ApplyDistortion(Vec2 u)
        {
            double k1 = Distortion.Length > 0 ? Distortion[0] : 0.0;
            double k2 = Distortion.Length > 1 ? Distortion[1] : 0.0;
            double k3 = Distortion.Length > 2 ? Distortion[2] : 0.0;
            double p1 = Distortion.Length > 3 ? Distortion[3] : 0.0;
            double p2 = Distortion.Length > 4 ? Distortion[4] : 0.0;

            double halfWidth = Width / 2.0;
            Vec2 d = u - ImageCenterPx;
            double xn = d.X / halfWidth;
            double yn = d.Y / halfWidth;
            double r2 = xn * xn + yn * yn;
            double radial = 1.0 + k1 * r2 + k2 * r2 * r2 + k3 * r2 * r2 * r2;

            double xd = xn * radial + 2.0 * p1 * xn * yn + p2 * (r2 + 2.0 * xn * xn);
            double yd = yn * radial + p1 * (r2 + 2.0 * yn * yn) + 2.0 * p2 * xn * yn;

            return new Vec2(ImageCenterPx.X + xd * halfWidth, ImageCenterPx.Y + yd * halfWidth);
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "SimulatedWorld {0}x{1}, mm/px={2}, rot={3}°, mark={4}, mirror={5}",
                Width, Height, MmPerPixel, ImageRotationDeg, MarkWorldXy, InjectMirror);
        }
    }
}
