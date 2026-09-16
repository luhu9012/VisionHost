using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using HalconDotNet;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Algorithm;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★★ 仿真"标定板相机"：用 HALCON 的 <c>sim_caltab</c> 合成带<b>已知内参</b>的标定板图，
    /// 每次取图吐下一个姿态。
    ///
    /// 它存在的意义：内参链是"纯新增能力"，如果只在真机上第一次跑，任何一个环节错
    /// （板文件、口径、位姿约定、覆盖度判据）都只能靠"结果看着不对"去猜。
    /// 有了它，<b>产品代码那条路</b>（<c>BoardModel → HalconBoardDetector → IntrinsicsRunner →
    /// 覆盖度判据 → 导出</c>）可以在没有相机、没有机械手的机器上跑通，并且判据是
    /// "解出的 κ 能不能还原注入值" —— 这与真机上关心的事情完全同构。
    ///
    /// ★ 与 <see cref="Simulation.SyntheticFrameCamera"/> 的分工：
    ///   那个合成的是<b>特征 mark</b>（验 H/O/e 三条链）；这个合成的是<b>标定板</b>（验内参链）。
    ///   两者都实现 <see cref="ICameraGateway"/>，上层编排器一个字都不用改。
    ///
    /// ★ 姿态序列由调用方给：合成器不认识"向左翘 25°"这种话，必须收到<b>数值位姿</b>。
    ///   这也是为什么摆板脚本（人话）与仿真位姿（数）要分开两处 —— 别试图让一个东西既说话又算数。
    /// </summary>
    public sealed class SimulatedBoardCamera : ICameraGateway, IPoseScriptedCamera
    {
        private readonly List<HTuple> _poses = new List<HTuple>();
        private readonly BoardModel _board;

        private int _index;
        private int _grabCount;
        private bool _posePinned;

        // 合成图的灰度配置：背景与板必须拉开，否则板在画面里变小时区域分割会失败（#8397）
        public int GrayBackground = 128;
        public int GrayPlate = 45;
        public int GrayMarks = 235;

        public double TrueFocusM = 0.01184;
        public double TrueKappa = -1070.0;
        public double TruePixelPitchM = 3.45e-6;
        public double TrueCx;
        public double TrueCy;

        public SimulatedBoardCamera(BoardModel board, int width, int height,
            double trueFocusM, double trueKappa, double truePixelPitchM)
        {
            if (board == null)
            {
                throw new ArgumentNullException("board");
            }

            if (!board.IsAvailable)
            {
                throw new InvalidOperationException("标定板模型不可用：" + board.SearchHint);
            }

            _board = board;
            Width = width;
            Height = height;
            TrueFocusM = trueFocusM;
            TrueKappa = trueKappa;
            TruePixelPitchM = truePixelPitchM;
            TrueCx = (width - 1) / 2.0 + 8.0;      // 故意偏离图像中心，检验主点能不能解回来
            TrueCy = (height - 1) / 2.0 - 6.0;
        }

        public string EnvironmentKind
        {
            get { return "SimulatedBoard"; }
        }

        public bool IsSimulated
        {
            get { return true; }
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>已经取了几帧（用于断言"确实按姿态脚本走满了"）。</summary>
        public int GrabCount
        {
            get { return _grabCount; }
        }

        /// <summary>
        /// 指定下一次取图用第 <paramref name="index"/> 个姿态（见 <see cref="IPoseScriptedCamera"/>）。
        /// ★ 编排器每拍一张都会指定一次，重拍时指定的是同一个序号 → 仿真重画同一张。
        ///   这样"提示的姿态"与"实际渲染的姿态"永远一致；否则重拍会静默吃掉下一个姿态，
        ///   而所有数值指标都还是正常的，只有覆盖度判据会莫名其妙地说"没斜"。
        /// </summary>
        public void SelectPose(int index)
        {
            _index = index < 0 ? 0 : index;
            _posePinned = true;
        }

        /// <summary>
        /// "第几帧用了第几个位姿"的轨迹。
        /// ★ 补它的原因：人工摆板链会<b>重拍</b>（一张没找到板就再按一次快门）。
        ///   仿真相机每被调一次就往前走一格，于是重拍会让"提示的牌号"与"实际渲染的姿态"错位 ——
        ///   表现出来就是"覆盖度说全都不斜"，但图明明不一样。有了这条轨迹一眼就能看出错位。
        /// </summary>
        public readonly List<string> FrameTrace = new List<string>();

        /// <summary>真值相机参数（仿真里"我们塞进去的那个数"，用来当验收基准）。</summary>
        public double[] TrueCameraParam()
        {
            return new double[]
            {
                TrueFocusM, TrueKappa, TruePixelPitchM, TruePixelPitchM, TrueCx, TrueCy, Width, Height
            };
        }

        /// <summary>
        /// 加一个板位姿（平移单位米，<b>旋转单位度</b>）。
        ///
        /// ★★ 这里踩过一个非常贵的坑，务必不要"顺手改回弧度"：
        ///   HALCON 经典 Pose 的文档说旋转是弧度，但在本工程实际使用的这条
        ///   <c>'Rp+T' / 'gba' / 'point'</c> 表示法（位姿元组第 7 位 = 0）里，
        ///   <b>旋转项就是【度】</b>。实测两条对照（见 <c>IntrinsicsLab.PoseUnitProbe</c>）：
        ///     · create_pose(ry = 30)     → pose_to_hom_mat3d 取法向得倾角 <b>30.00°</b>
        ///     · create_pose(ry = 0.5236) → 只有 <b>0.52°</b>（0.5236 被当成 0.5236 度）
        ///   也就是把弧度值填进去等于"几乎没转"。而这类错误<b>残差抓不住</b>：
        ///   板几乎正对，标定照样收敛到 0.008 px，κ 与主点也照样解得很准，
        ///   唯一露出来的地方就是覆盖度判据说"一张都没斜"。
        ///   所以这里<b>对外收度、原样交给 HALCON</b>，中间不做任何换算。
        /// </summary>
        public void AddPose(double tx, double ty, double tz, double rx, double ry, double rz)
        {
            HTuple pose;
            HOperatorSet.CreatePose(
                new HTuple(tx), new HTuple(ty), new HTuple(tz),
                new HTuple(rx), new HTuple(ry), new HTuple(rz),
                new HTuple("Rp+T"), new HTuple("gba"), new HTuple("point"),
                out pose);
            _poses.Add(pose);
        }

        public int PoseCount
        {
            get { return _poses.Count; }
        }

        private int _poseCount
        {
            get { return _poses.Count; }
        }

        /// <summary>
        /// 第 <paramref name="i"/> 个位姿的人话描述（平移 + 旋转都要给）。
        /// ★ 一定要带上旋转：只打平移时，"提示第 3 张、实际渲染第 4 个姿态"这种错位
        ///   从平移上看是 231/206/256 mm 三个数换来换去，肉眼看不出不对；带上旋转（0°/25°/30°）
        ///   一眼就能和提示对上。
        /// </summary>
        public string PoseText(int i)
        {
            if (i < 0 || i >= _poses.Count)
            {
                return "-";
            }

            HTuple p = _poses[i];
            Func<double, string> n1 = v => v.ToString("F1", CultureInfo.InvariantCulture);
            return "位姿#" + i + " T=("
                + n1(-p[0].D * 1000.0) + ", " + n1(-p[1].D * 1000.0) + ", " + n1(p[2].D * 1000.0)
                + ")mm R=(" + n1(p[3].D) + ", " + n1(p[4].D) + ", " + n1(p[5].D) + ")°";
        }

        public OpResult ConfigureSoftwareTrigger()
        {
            return OpResult.Success();
        }

        /// <summary>
        /// 吐下一帧。<paramref name="_timeoutMs"/> 对仿真无意义（合成是同步的）。
        ///
        /// ★ 位姿用完就停在最后一个上，不循环 —— 循环会让"多要了一张"变成"静默重复同一个姿态"，
        ///   而重复姿态对覆盖度没有帮助，只会让标定看起来图更多。
        ///
        /// ★★ 但"停住"这条规则只对<b>盲取自增</b>成立。真实用法是编排器先
        ///   <see cref="SelectPose"/> 指定这一张要拍哪个姿态，再调本方法；重拍时编排器会把
        ///   同一个序号再指定一次，于是仿真<b>重画同一张</b> —— 与"操作员重摆同一个姿态"
        ///   完全等价。少了这一步，重拍就会吃掉下一个姿态，导致提示与实际渲染错位。
        /// </summary>
        public byte[] GrabFrame(int _timeoutMs, out CalibError error)
        {
            error = null;
            if (_poses.Count == 0)
            {
                error = CalibError.Create(CalibFailureKind.Internal, "仿真板相机没有配置任何位姿。");
                return null;
            }

            int idx = _index < _poses.Count ? _index : _poses.Count - 1;
            if (!_posePinned)
            {
                _index++;
            }

            _posePinned = false;      // 一次指定只管一次取图
            _grabCount++;
            FrameTrace.Add("帧" + _grabCount + " → 位姿#" + idx
                + (idx == _poseCount - 1 && _index > _poses.Count ? "(已用尽，重复最后一个)" : string.Empty));

            HObject image = null;
            HObject gray = null;
            try
            {
                HTuple camPar = new HTuple(new object[]
                {
                    "area_scan_division",
                    TrueFocusM,
                    TrueKappa,
                    TruePixelPitchM,
                    TruePixelPitchM,
                    TrueCx,
                    TrueCy,
                    (long)Width,
                    (long)Height
                });

                HOperatorSet.SimCaltab(out image, _board.DescriptionFile, camPar, _poses[idx],
                    GrayBackground, GrayPlate, GrayMarks, 1.0);

                HOperatorSet.ConvertImageType(image, out gray, "byte");

                HTuple ptr, type, w, h;
                HOperatorSet.GetImagePointer1(gray, out ptr, out type, out w, out h);
                int len = w.I * h.I;
                var buf = new byte[len];
                Marshal.Copy(ptr.IP, buf, 0, len);
                return buf;
            }
            catch (Exception ex)
            {
                string msg = ex is HOperatorException
                    ? ((HOperatorException)ex).GetErrorMessage()
                    : ex.Message;
                error = CalibError.Create(CalibFailureKind.Internal,
                    "合成标定板图失败（第 " + (_index) + " 帧）：" + msg);
                return null;
            }
            finally
            {
                SafeDispose(gray);
                SafeDispose(image);
            }
        }

        private static void SafeDispose(HObject obj)
        {
            try
            {
                if (obj != null && obj.IsInitialized())
                {
                    obj.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------ 位姿脚本

        /// <summary>
        /// 按摆板脚本生成一套<b>几何等价</b>的仿真位姿：
        /// 正对 → 远近 → 左右翘 → 前后翘 → 复合斜摆。
        ///
        /// ★ 角度/距离与 <see cref="Algorithm.IntrinsicsPoseScript"/> 里的人话保持一致。
        ///   两边对不上时，"覆盖度够不够"的结论就变成一句空话 —— 所以这里的默认值直接引用
        ///   脚本里的常量，而不是各写一份。
        /// </summary>
        public static List<double[]> DefaultPosePlan(double baseDistanceM)
        {
            double z = baseDistanceM;
            double d = Algorithm.IntrinsicsPoseScript.DepthStepMm / 1000.0;
            double t = Algorithm.IntrinsicsPoseScript.TiltDeg;
            double t2 = t + 5.0;

            var list = new List<double[]>();

            // tx, ty, tz, rx(deg, 绕X), ry(deg, 绕Y), rz(deg, 绕Z)
            list.Add(new double[] { 0, 0, z, 0, 0, 0 });                       // 正对
            list.Add(new double[] { 0, 0, z - d, 0, 0, 0 });                   // 近
            list.Add(new double[] { 0, 0, z + d, 0, 0, 0 });                   // 远
            list.Add(new double[] { 0, 0, z, 0, t, 0 });                       // 左翘（绕 Y）
            list.Add(new double[] { 0, 0, z, 0, -t, 0 });                      // 右翘
            list.Add(new double[] { 0, 0, z, t, 0, 0 });                       // 前翘（绕 X）
            list.Add(new double[] { 0, 0, z, -t, 0, 0 });                      // 后翘
            list.Add(new double[] { 0, 0, z + d, 0, t, 0 });                   // 又斜又远
            list.Add(new double[] { 0, 0, z, -t2, 0, 90 });                    // 换向斜摆 + 绕法向转
            return list;
        }

        public string DescribeTruth()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "仿真真值：f = {0:F4} mm，κ = {1:F1}（1/m²，约合归一化 k1 = {2:F3}），"
                + "像元 {3:F2} µm，主点 ({4:F1}, {5:F1})，图像 {6}×{7}，共 {8} 个位姿",
                TrueFocusM * 1000.0, TrueKappa,
                Algorithm.IntrinsicsGeometry.NormalizedFromKappa(TrueKappa, TrueFocusM),
                TruePixelPitchM * 1e6, TrueCx, TrueCy, Width, Height, _poses.Count);
        }
    }
}
