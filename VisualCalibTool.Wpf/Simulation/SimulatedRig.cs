using System;
using System.Globalization;
using System.IO;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;
using VisualCalibTool.Services;

namespace VisualCalibTool.Simulation
{
    /// <summary>仿真台架的装配参数。默认值就是"可直接跑真链"的那一套。</summary>
    public sealed class SimulatedRigOptions
    {
        /// <summary>九点步长（mm）。</summary>
        public double StepX = 10.0;
        public double StepY = 10.0;

        /// <summary>吸嘴偏心 e 的真值（法兰坐标系、u = U0 时，mm）。</summary>
        public Vec2 TipEcc = new Vec2(8.0, -3.0);

        /// <summary>合成图灰度噪声幅度（0 = 完全干净）。★ 取 0 会让"提取器其实很脆"这件事被掩盖。</summary>
        public double GrayNoise = 6.0;

        /// <summary>模拟曝光延时（ms）。自检里设 0 省时间；界面里留着才看得见"等帧"。</summary>
        public int GrabLatencyMs;

        /// <summary>合成图里画不画工具尖。★ 九点/旋转链必须关：尖会变成"更深的第二个圆"干扰提取。</summary>
        public bool RenderTip;

        /// <summary>像面是否随法兰滚转（眼在手的物理事实）。</summary>
        public bool CameraRotatesWithFlange = true;

        /// <summary>U 基准角（度）。</summary>
        public double U0Deg;

        /// <summary>产物根目录。null / 空 = 临时目录。</summary>
        public string StoreRoot;
    }

    /// <summary>
    /// ★★ 仿真台架：把"合成世界"装配成一个<b>能跑真链</b>的完整环境
    /// （合成图相机 + 理想运动学 + 自洽拓扑 + 采样编排器）。
    ///
    /// 为什么要单独抽出来：自检和界面<b>必须用同一套装配</b>。
    /// 如果自检自己装一套、界面另装一套，就会出现本项目最怕的那种故障 ——
    /// "离线 77 条断言全绿，界面上一点却 0/9 点被提取"，
    /// 而这种偏差极难查（两边的差别可能只是一个 <c>MarkWorldXy</c>）。
    ///
    /// 另一个容易踩的点：<b>必须用 <see cref="SimulatedWorld.CreatePhysical"/> 而不是裸
    /// <c>new SimulatedWorld()</c></b>。后者的默认 Mark 位置 (120, −30) 与默认基准位 (300, 200)
    /// 相距 290 mm ≫ 视野（1280 px × 0.05 mm/px = 64 mm），
    /// 于是"拍一帧"是一张纯背景图、九点必然 0/9 —— 表面看是提取器的问题，实际是世界没配好。
    /// </summary>
    public sealed class SimulatedRig
    {
        public SimulatedWorld World;

        /// <summary>
        /// 建台架时写进环境的那份拓扑。
        ///
        /// ★★ 它是个**快照引用，不是实时的**：谁调过一次
        /// <see cref="Abstractions.IVisualCalibEnvironment.ApplyTopology"/>（例如向导第 2 步
        /// 用户确认了他的改动），环境内部就换成另一个对象了，而这个字段仍然指着旧的那个。
        /// ⇒ <b>任何时候要判断"现在环境的参数是什么"，一律读
        /// <see cref="Env"/> 的 <c>ReadTopology()</c></b>，不要读这里。
        /// 拿它当判据会得出"环境没变"的**假绿**，而实际上早就变了（自检里踩过）。
        /// </summary>
        public CalibTopology Topology;

        public SimulatedEnvironment Env;
        public SyntheticFrameCamera Camera;
        public SamplingOrchestrator Sampler;

        /// <summary>
        /// ★ 特征提取器（模板匹配/训练的实现者）。
        /// ★★ 必须与 <see cref="Sampler"/> 里用的是<b>同一个实例</b>：
        ///   模板训练写在提取器内存里（_specs/_shapeModels），不是同一个实例的话
        ///   "界面刚训练的模板、采样器却看不见" —— 又是一个接缝存在但没接通的假绿。
        /// </summary>
        public IFeatureExtractor Extractor;

        /// <summary>模板训练门面（与 <see cref="Extractor"/> 同一个对象；向导第 2.5 步的示教入口）。</summary>
        public ITemplateTrainer Trainer;

        public ICalibLog Log;

        /// <summary>
        /// ★ 仿真/独立模式下的发布器。
        /// 必须真的挂进 <see cref="Env"/>，否则 <c>ChainRunnerSupport.ExportAndPublish</c>
        /// 里那段发布逻辑（含发布前体检）<b>永远不会被走到</b> —— 没被执行过的接缝等于没接。
        /// </summary>
        public SimulatedCalibrationPublisher Publisher;

        /// <summary>产物根目录（界面要显示"东西落在哪"）。</summary>
        public string StoreRoot;

        public static SimulatedRig Build(SimulatedRigOptions opt = null, ICalibLog log = null)
        {
            if (opt == null)
            {
                opt = new SimulatedRigOptions();
            }

            var rig = new SimulatedRig();

            // ── ① 世界：必须是"物理自洽"的那一套 ──
            SimulatedWorld world = SimulatedWorld.CreatePhysical();
            world.ResetRandom();
            world.U0Deg = opt.U0Deg;
            world.CameraLateralOffset = Vec2.Zero;
            world.CameraRotatesWithFlange = opt.CameraRotatesWithFlange;
            world.TipEcc = opt.TipEcc;
            world.RenderTip = opt.RenderTip;

            rig.World = world;

            rig.StoreRoot = string.IsNullOrEmpty(opt.StoreRoot)
                ? Path.Combine(Path.GetTempPath(),
                    "vct_rig_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))
                : opt.StoreRoot;

            var store = new FileSystemCalibStore(rig.StoreRoot);
            var prompt = new AutoYesUserPrompt();

            // ── ② 相机：拿的是"真值位姿"的延迟求值闭包（构造时 env 还不存在）──
            SimulatedEnvironment env = null;
            var camera = new SyntheticFrameCamera(world, () => env.SimulatedMotion.TruePosition);
            camera.GrabLatencyMs = opt.GrabLatencyMs;
            camera.GrayNoise = opt.GrayNoise;

            // ★ 发布器必须显式挂上：传 null 的话那段发布逻辑与发布前体检都不会被走到，
            //   等到真接主项目那天才发现"门禁根本没拦过"，代价太大。
            var publisher = new SimulatedCalibrationPublisher();

            env = new SimulatedEnvironment(world, camera, store, log ?? new SimpleCalibLog(), prompt, publisher);

            // ── ③ 拓扑：把世界里的真值写进拓扑，链路才有依据 ──
            CalibTopology topo = env.BuildDefaultTopology();
            topo.StepX = opt.StepX;
            topo.StepY = opt.StepY;
            topo.BasePosXY = world.BaseFlangeXy;
            topo.BasePosKnown = true;
            topo.BasePosZ = topo.WorkZ;
            topo.BasePosU = topo.CalibU0;
            topo.ZMin = -144.0;
            topo.ZMax = 0.0;
            topo.SafeZ = 0.0;
            topo.CameraRollsWithFlange = opt.CameraRotatesWithFlange;
            env.ApplyTopology(topo);

            // ── ④ 采样编排器：唯一知道"用哪个特征提取器"的地方 ──
            //   ★ 提取器先建出来再喂给编排器：向导的模板示教（框选→训练）要与采样链
            //     共享同一份模板库（见 Extractor/Trainer 字段注释）。
            var extractor = new Imaging.HalconMarkExtractor();
            var sampler = new SamplingOrchestrator(
                env.Motion, env.Camera, extractor, env.SimulatedMotion.Safety,
                env.Store, env.Log);

            rig.Env = env;
            rig.Camera = camera;
            rig.Sampler = sampler;
            rig.Extractor = extractor;
            rig.Trainer = extractor;
            rig.Topology = topo;
            rig.Log = env.Log;
            rig.Publisher = publisher;

            return rig;
        }

        /// <summary>
        /// 仿真的"对针"方式：真机上这一步是操作员把吸嘴尖挪到压住基准特征（现实真值，几何算不出来），
        /// 仿真里我们手里有真值，于是按解析式反解法兰该停到哪。
        /// ★ 它<b>只存在于仿真</b>；真机必须换成 <see cref="ManualTipApproachProvider"/>。
        /// </summary>
        public ITipApproachProvider CreateTipApproach()
        {
            return new SimulatedTipApproachProvider(World.MarkWorldXy, World.TipEcc, World.U0Deg);
        }

        /// <summary>装配一个向导编排器（默认带仿真对针方式）。</summary>
        public WizardCoordinator CreateCoordinator()
        {
            return new WizardCoordinator(Env, Sampler, CreateTipApproach());
        }

        /// <summary>一次会话用的默认向导参数（角度序列与真值口径保持一致）。</summary>
        public WizardRunOptions CreateWizardOptions(string sessionPrefix)
        {
            return new WizardRunOptions
            {
                SessionPrefix = sessionPrefix,
                MmPerPixel = World.MmPerPixel,
                ArchiveFrames = true,
                CaptureTrace = true,
                ExportFiles = true,
                PublishToHost = true
            };
        }

        /// <summary>合成图的"Mark 像素真值"（只用于显示自检，绝不参与解算）。</summary>
        public Vec2 GroundTruthMarkPixel()
        {
            return Camera.GroundTruthPixel(Env.SimulatedMotion.TruePosition);
        }
    }
}
