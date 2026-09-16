using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Abstractions
{
    /// <summary>
    /// 仿真运动门面（0 硬件）。★ 刻意实现得"像真控制器"：
    ///   · 越界一律返回<b>带原始错误码</b>的失败（模拟 Epson 的 4001 / 4007），不让上层以为永远成功；
    ///   · 反馈位可注入噪声，与真值分离（<see cref="TruePosition"/> vs <see cref="GetFeedbackPosition"/>）；
    ///   · 零运动校核真的会返回 NG，让"可达性预检"这条链在仿真下也走得到。
    /// </summary>
    public sealed class SimulatedMotionGateway : IMotionGateway
    {
        private readonly SimulatedWorld _world;
        private MotionPose _pose;

        public ISafetyGuard Safety = new RectSafetyGuard();

        /// <summary>每次运动的模拟耗时（ms）。0 = 立即完成。</summary>
        public int MoveLatencyMs = 5;

        /// <summary>到位等待（模拟伺服整定）。</summary>
        public int SettleMs = 0;

        /// <summary>当前手系（默认左手 —— 本工位是左手工位）。</summary>
        public Handedness Hand = Handedness.Lefty;

        /// <summary>模拟 Epson 的 |XY| 上限。</summary>
        public double MaxRadiusXy = 2000.0;

        public double MinZ = -144.0;
        public double MaxZ = 0.0;

        /// <summary>置 true 时下一次运动必定被拒（用于压测错误码透出与重试路径）。</summary>
        public bool FailNextMove;

        /// <summary>模拟被拒时返回的原始错误码。</summary>
        public int FailRawCode = 4007;

        /// <summary>累计的运动次数（诊断用）。</summary>
        public int MoveCount;

        /// <summary>累计的 CHECK 次数。</summary>
        public int CheckCount;

        public SimulatedMotionGateway(SimulatedWorld world)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }

            _world = world;
            _pose = new MotionPose(0.0, 0.0, -5.0, 0.0);
        }

        /// <summary>★ 真实位（无噪声）。合成相机必须用它造图，否则会把噪声算两遍。</summary>
        public MotionPose TruePosition
        {
            get { return _pose; }
        }

        public string EnvironmentKind
        {
            get { return "Simulation"; }
        }

        public bool IsSimulated
        {
            get { return true; }
        }

        public MotionPose GetPosition()
        {
            return _pose;
        }

        public MotionPose GetFeedbackPosition()
        {
            Vec2 noisy = _world.NoisyFeedback(_pose.Xy);
            return new MotionPose(noisy.X, noisy.Y, _pose.Z, _pose.U);
        }

        public Handedness GetHandedness()
        {
            return Hand;
        }

        public OpResult MoveLinear(MotionPose target, double speedMmPerSec, double accelMmPerSec2)
        {
            MoveCount++;

            if (speedMmPerSec <= 0.0)
            {
                // ★ ZMC 铁律：速度 ≤ 0 轴不动且状态恒 IDLE —— 这是最常见的"看起来发了指令却没动"
                return OpResult.Fail("速度必须 > 0（速度 ≤ 0 时轴不会动，且状态恒为 IDLE）");
            }

            if (Safety != null)
            {
                CalibError guard = Safety.Validate(target);
                if (guard != null)
                {
                    return OpResult.Fail(guard.Message);
                }
            }

            if (FailNextMove)
            {
                FailNextMove = false;
                return OpResult.Fail("模拟：控制器拒绝该运动（例：手系不匹配）", FailRawCode);
            }

            if (Math.Abs(target.Z) > 0.0 || target.Z < MinZ || target.Z > MaxZ)
            {
                // 允许 Z=0（在范围内），仅拦截真正越界
            }

            if (target.Z < MinZ || target.Z > MaxZ)
            {
                return OpResult.Fail(
                    string.Format("Z={0:F3} 超出允许范围 [{1:F3}, {2:F3}]", target.Z, MinZ, MaxZ),
                    4001);
            }

            if (target.Xy.Length > MaxRadiusXy)
            {
                return OpResult.Fail(
                    string.Format("|XY|={0:F3} 超出行程上限 {1:F3}", target.Xy.Length, MaxRadiusXy),
                    4001);
            }

            Sleep(MoveLatencyMs);
            _pose = target;
            Sleep(SettleMs);
            return OpResult.Success();
        }

        public OpResult MoveRotate(double absoluteU, double speedDegPerSec, double accelDegPerSec2)
        {
            if (speedDegPerSec <= 0.0)
            {
                return OpResult.Fail("旋转速度必须 > 0");
            }

            Sleep(MoveLatencyMs);
            _pose = new MotionPose(_pose.X, _pose.Y, _pose.Z, absoluteU);
            Sleep(SettleMs);
            return OpResult.Success();
        }

        public OpResult Home()
        {
            Sleep(MoveLatencyMs);
            _pose = new MotionPose(0.0, 0.0, -5.0, 0.0);
            return OpResult.Success();
        }

        public OpResult Stop()
        {
            return OpResult.Success();
        }

        public OpResult ApplyCpSpeed(double speedMmPerSec, double accelMmPerSec2)
        {
            if (speedMmPerSec <= 0.0 || accelMmPerSec2 <= 0.0)
            {
                return OpResult.Fail("CP 速度 / 加速度必须 > 0");
            }

            return OpResult.Success();
        }

        /// <summary>
        /// 零运动校核。★ 如实回答"不可达"，让仿真下也能验证预检链真的在拦。
        /// </summary>
        public ReachCheckResult CheckTarget(MotionPose target)
        {
            CheckCount++;

            bool ok = target.IsFinite
                      && target.Z >= MinZ
                      && target.Z <= MaxZ
                      && target.Xy.Length <= MaxRadiusXy;

            return new ReachCheckResult
            {
                TargetOk = ok,
                Message = ok ? "CHECK OK" : "CHECK NG"
            };
        }

        private static void Sleep(int ms)
        {
            if (ms > 0)
            {
                Thread.Sleep(ms);
            }
        }
    }

    /// <summary>
    /// 仿真环境：0 硬件跑通全流程。
    /// 相机由外部注入（合成图需要 HALCON，而 HALCON 只允许出现在 Imaging 层），
    /// 因此这里只做<b>组合</b>，不碰图像。
    /// </summary>
    public sealed class SimulatedEnvironment : IVisualCalibEnvironment
    {
        private readonly SimulatedWorld _world;
        private CalibTopology _topology;

        public SimulatedEnvironment(
            SimulatedWorld world,
            ICameraGateway camera,
            ICalibStore store,
            ICalibLog log,
            IUserPrompt prompt,
            ICalibrationPublisher publisher = null)
        {
            if (world == null)
            {
                throw new ArgumentNullException("world");
            }

            _world = world;

            Motion = new SimulatedMotionGateway(world);
            Camera = camera;
            Store = store ?? new FileSystemCalibStore();
            Log = log ?? new SimpleCalibLog();
            Prompt = prompt ?? new AutoYesUserPrompt();
            Publisher = publisher;

            _topology = BuildDefaultTopology();
        }

        public SimulatedMotionGateway SimulatedMotion
        {
            get { return (SimulatedMotionGateway)Motion; }
        }

        public string Kind
        {
            get { return "Simulation"; }
        }

        public IMotionGateway Motion { get; private set; }

        public ICameraGateway Camera { get; private set; }

        public ICalibStore Store { get; private set; }

        public ICalibLog Log { get; private set; }

        public IUserPrompt Prompt { get; private set; }

        public ICalibrationPublisher Publisher { get; private set; }

        public DateTime Now
        {
            get { return DateTime.Now; }
        }

        /// <summary>仿真默认拓扑（0 硬件时"回显"给用户确认的那份场景）。</summary>
        public CalibTopology BuildDefaultTopology()
        {
            var t = new CalibTopology
            {
                StationCode = "SIM_001",
                StationName = "仿真工位",
                CameraMount = CameraMountKind.EyeInHand,
                CameraSlotKey = "Cam_Top",
                Hand = Handedness.Lefty,
                ToolHeadCount = 1,
                RotateAxisName = "U",
                CameraMovesWithZ = true,
                WorkZ = -5.0,
                SafeZ = 30.0,
                CalibU0 = 0.0,
                StepX = 10.0,
                StepY = 10.0,
                BasePosXY = new Vec2(300.0, 200.0),
                BasePosZ = -5.0,
                BasePosU = 0.0,
                XMin = -2000.0,
                XMax = 2000.0,
                YMin = -2000.0,
                YMax = 2000.0,
                ZMin = -144.0,
                ZMax = 0.0
            };

            return t;
        }

        public CalibTopology ReadTopology()
        {
            return _topology;
        }

        /// <summary>
        /// 见 <see cref="IVisualCalibEnvironment.ApplyTopology"/>。
        ///
        /// ★ 实现语义是<b>整体替换</b>，不是逐字段合并：调用方给过来的是"用户确认过的完整拓扑"。
        ///   逐字段合并会把"用户特意去掉的项"又留着，那比"改不生效"更危险 ——
        ///   用户会以为漏看的那一项已经被他关掉了。
        /// </summary>
        public bool ApplyTopology(CalibTopology topology)
        {
            if (topology == null)
            {
                return false;
            }

            _topology = topology;
            return true;
        }

        /// <summary>存储根目录（界面上要能看到"东西落在哪"）。</summary>
        public string StoreRootDir
        {
            get { return Store.RootDir; }
        }

        /// <summary>便捷：确保存储目录存在。</summary>
        public void EnsureStore()
        {
            try
            {
                if (!Directory.Exists(Store.RootDir))
                {
                    Directory.CreateDirectory(Store.RootDir);
                }
            }
            catch (Exception)
            {
                // 目录建不出来不影响启动
            }
        }

        /// <summary>仿真世界的只读暴露（界面/诊断用）。</summary>
        public SimulatedWorld World
        {
            get { return _world; }
        }

        /// <summary>仿真提示语（首屏展示"现在是仿真，不是真机"）。</summary>
        public string Disclaimer
        {
            get { return "仿真模式：不连接任何硬件。走位、取图、解算全流程真实执行，图像由理想相机模型合成。"; }
        }
    }
}
