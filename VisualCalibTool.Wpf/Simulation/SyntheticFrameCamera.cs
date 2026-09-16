using System;
using System.Threading;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Simulation
{
    /// <summary>
    /// 仿真相机：按 <see cref="SimulatedWorld"/> 的理想相机模型<b>合成真图</b>，
    /// 交给真算法去解 —— 而不是直接给出"正确答案"。
    ///
    /// ★ 这是 P0 能"0 硬件闭环"的关键：
    ///   走位 → 软触发 → 合成一帧（Mark 落在真实像素位置 + 噪声）→ 特征提取 →
    ///   解算 → 诊断。整条链一个字都不用改，换真机时只换 <c>ICameraGateway</c> 的实现。
    ///
    /// 输出的帧是<b>单通道 8 位灰度裸帧</b>（length = Width×Height），
    /// 与真机相机交给 HALCON 的东西是同一种东西（真机可能是 BGR，这里用灰度足够压标定链）。
    /// </summary>
    public sealed class SyntheticFrameCamera : ICameraGateway
    {
        private readonly SimulatedWorld _world;
        private readonly Func<MotionPose> _truePoseProvider;
        private readonly Random _rand;

        /// <summary>模拟曝光 + 传输耗时（ms）。</summary>
        public int GrabLatencyMs = 25;

        /// <summary>合成噪声的灰度幅度（0 = 干净图）。</summary>
        public double GrayNoise = 6.0;

        /// <summary>true = 画十字；false = 画实心圆（默认）。</summary>
        public bool RenderAsCross;

        /// <summary>累计取图次数。</summary>
        public int GrabCount;

        public SyntheticFrameCamera(SimulatedWorld world, Func<MotionPose> truePoseProvider)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }

            if (truePoseProvider == null)
            {
                throw new ArgumentNullException("truePoseProvider");
            }

            _world = world;
            _truePoseProvider = truePoseProvider;
            _rand = new Random(world.Seed);
        }

        public string EnvironmentKind
        {
            get { return "Simulation"; }
        }

        public bool IsSimulated
        {
            get { return true; }
        }

        public int Width
        {
            get { return _world.Width; }
        }

        public int Height
        {
            get { return _world.Height; }
        }

        /// <summary>仿真相机永远"支持软触发"，但语义上必须真的每次只出一帧（不是自由流）。</summary>
        public OpResult ConfigureSoftwareTrigger()
        {
            return OpResult.Success();
        }

        /// <summary>
        /// 取一帧。★ 语义与真机一致：<b>每次调用 = 一次软触发 = 本点的新帧</b>。
        /// </summary>
        public byte[] GrabFrame(int timeoutMs, out CalibError error)
        {
            error = null;
            GrabCount++;

            if (GrabLatencyMs > 0)
            {
                if (timeoutMs > 0 && GrabLatencyMs > timeoutMs)
                {
                    error = CalibError.Create(
                        CalibFailureKind.GrabTimeout,
                        string.Format("仿真取图耗时 {0} ms 超过超时 {1} ms", GrabLatencyMs, timeoutMs));
                    return null;
                }

                Thread.Sleep(GrabLatencyMs);
            }

            return RenderFrame();
        }

        /// <summary>合成一帧灰度图（也可被界面直接调用来"预览当前视野"）。</summary>
        public byte[] RenderFrame()
        {
            int w = _world.Width;
            int h = _world.Height;
            byte[] buf = new byte[w * h];

            byte bg = (byte)Math.Max(0, Math.Min(255, _world.BackgroundGray));
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i] = bg;
            }

            MotionPose pose = _truePoseProvider();
            Vec2 px = _world.ProjectAt(pose.Xy, pose.U);

            // 像素 → (row, col)，row = Y，col = X
            double col = px.X;
            double row = px.Y;

            if (RenderAsCross)
            {
                DrawCross(buf, w, h, row, col, Math.Max(2.0, _world.MarkRadiusPx * 0.35), _world.MarkRadiusPx, (byte)_world.MarkGray);
            }
            else
            {
                DrawDisk(buf, w, h, row, col, _world.MarkRadiusPx, (byte)_world.MarkGray);
            }

            // ── 工具尖（偏心链才画）──
            // ★ 尖比 Mark 小得多 → 正好逼着"±40% 参考半径滤伪"这条纪律真的工作起来，
            //   而不是永远只看到一个候选（那等于这条纪律从没被验证过）。
            if (_world.RenderTip)
            {
                Vec2 tipPx = _world.ProjectTip(pose.Xy, pose.U);
                DrawDisk(buf, w, h, tipPx.Y, tipPx.X, _world.TipRadiusPx, (byte)_world.TipGray);
            }

            if (GrayNoise > 0.0)
            {
                AddNoise(buf, GrayNoise);
            }

            return buf;
        }

        private static void DrawDisk(byte[] buf, int w, int h, double row, double col, double radius, byte gray)
        {
            int r0 = (int)Math.Floor(row - radius) - 1;
            int r1 = (int)Math.Ceiling(row + radius) + 1;
            int c0 = (int)Math.Floor(col - radius) - 1;
            int c1 = (int)Math.Ceiling(col + radius) + 1;

            if (r0 < 0) r0 = 0;
            if (c0 < 0) c0 = 0;
            if (r1 >= h) r1 = h - 1;
            if (c1 >= w) c1 = w - 1;

            double r2 = radius * radius;
            for (int y = r0; y <= r1; y++)
            {
                double dy = y - row;
                int baseIdx = y * w;
                for (int x = c0; x <= c1; x++)
                {
                    double dx = x - col;
                    if (dx * dx + dy * dy <= r2)
                    {
                        buf[baseIdx + x] = gray;
                    }
                }
            }
        }

        private static void DrawCross(byte[] buf, int w, int h, double row, double col, double halfWidth, double length, byte gray)
        {
            DrawBar(buf, w, h, row, col, length, halfWidth, true, gray);
            DrawBar(buf, w, h, row, col, length, halfWidth, false, gray);
        }

        private static void DrawBar(byte[] buf, int w, int h, double row, double col, double length, double halfWidth, bool horizontal, byte gray)
        {
            int r0 = (int)Math.Floor(row - (horizontal ? halfWidth : length));
            int r1 = (int)Math.Ceiling(row + (horizontal ? halfWidth : length));
            int c0 = (int)Math.Floor(col - (horizontal ? length : halfWidth));
            int c1 = (int)Math.Ceiling(col + (horizontal ? length : halfWidth));

            if (r0 < 0) r0 = 0;
            if (c0 < 0) c0 = 0;
            if (r1 >= h) r1 = h - 1;
            if (c1 >= w) c1 = w - 1;

            for (int y = r0; y <= r1; y++)
            {
                int baseIdx = y * w;
                for (int x = c0; x <= c1; x++)
                {
                    buf[baseIdx + x] = gray;
                }
            }
        }

        private void AddNoise(byte[] buf, double amplitude)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                double n = (_rand.NextDouble() - 0.5) * 2.0 * amplitude;
                int v = (int)Math.Round(buf[i] + n);
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                buf[i] = (byte)v;
            }
        }

        /// <summary>
        /// 合成图的<b>真值像素</b>（Mark 圆心/十字中心在哪）。
        /// ★ 只给"自检"用：用来验证"提取器提出来的点 ≈ 真值"，
        ///   <b>绝不能</b>拿它去替代特征提取参与标定解算 —— 那就是造假结果。
        /// </summary>
        public Vec2 GroundTruthPixel(MotionPose pose)
        {
            return _world.ProjectAt(pose.Xy, pose.U);
        }
    }
}
