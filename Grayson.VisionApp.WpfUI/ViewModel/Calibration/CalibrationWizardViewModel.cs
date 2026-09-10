//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationWizardViewModel.cs
// 功能：标定向导ViewModel，支持九点手眼标定、带旋转中心手眼、棋盘格标定、像素当量标定
// 流程：分步向导4个步骤，自动控制运动平台走位、相机采图、特征提取、矩阵计算、保存标定方案
//===================================================================================
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Contracts.Devices.Interfaces;
using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Imaging;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.HalconWrapper.Calibration;
using Grayson.Vision.HalconWrapper.Templates;
using Grayson.Vision.HalconWrapper.Wpf.Imaging;
using Grayson.Vision.HalconWrapper.Wpf.ViewModels;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.ViewModel.Steps;
using Plugins.Robot.Epson; // Epson SCARA：九点走位合并单次 PTP（2026-09-06 修双 MOVE）
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 标定向导 视图模型
    /// 支持类型：九点手眼标定、带旋转中心手眼标定、棋盘格畸变标定、像素当量标定
    /// 硬件依赖：工业相机 + 运动控制卡（XY平台+旋转轴）
    /// </summary>
    public class CalibrationWizardViewModel : ViewModelBase, IDisposable
    {
        /// <summary>平台运动默认速度</summary>
        private const float DefaultMoveSpeed = 50f;

        private readonly Window _owner;
        /// <summary>
        /// 会话是否已"✔ 完成并保存"（2026-09-06 非模态化：取代 DialogResult 语义）。
        /// 宿主（标定中心）在窗口 Closed 时据此决定是否执行关窗回写链；取消/直接关窗保持 false → 不提交。
        /// </summary>
        public bool IsSessionCompleted { get; private set; }
        /// <summary>日志字符串缓存，界面绑定LogText展示全部日志</summary>
        private readonly StringBuilder _log = new StringBuilder();
        private readonly IDevicePool _devicePool;
        /// <summary>Halcon图像渲染服务，负责图像转WPF可显示的上下文</summary>
        private readonly HalconImageRenderService _renderService;
        /// <summary>标定方案仓储，读写数据库/本地配置</summary>
        private readonly ICalibrationProfileRepository _calibrationProfileRepository;
        /// <summary>相机帧同步事件：软触发之后等待相机返回一帧图像，超时控制</summary>
        private readonly AutoResetEvent _frameArrivedEvent = new AutoResetEvent(false);

        /// <summary>标定策略策略模式：不同标定类型实现各自逻辑</summary>
        private ICalibrationStepStrategy _strategy;
        /// <summary>最新一帧图像渲染上下文，用于界面图像显示</summary>
        private WpfImageRenderContext _latestFrameContext;
        /// <summary>图像帧序列号，每收到一帧自增</summary>
        private int _frameSequence;

        /// <summary>参数调节防抖计时器：滑块拖动停止 300ms 后才用新参数重试提取，避免拖动过程高频执行 Halcon 算子</summary>
        private readonly System.Windows.Threading.DispatcherTimer _retryExtractTimer;

        /// <summary>九点采样中被操作员主动跳过的点号集合（失败弹窗选"否"产生）：
        /// 自动采集循环不再反复尝试这些点；该点补采成功后从中移除，保证拟合前校验能拦截未采集点</summary>
        private readonly HashSet<int> _skippedPointIndices = new HashSet<int>();

        /// <summary>九点网格"可达性已通过"的目标坐标缓存（2026-09-06）：
        /// Epson SCARA 可达域是内外半径环带，矩形钳制/静态计算都无法确认某点是否可达，
        /// 唯一可靠判据是试走。每点首次遇到某目标坐标时空走验证一次并记入本表；
        /// 基准/步长/镜像/眼型任一改动 → 目标坐标变化 → 自动失效重验。配置不变时零额外运动。
        /// 手动逐点采集（每点一次按钮）因此只在第一点付一次空走成本。</summary>
        private readonly Dictionary<int, (double X, double Y)> _precheckedTargets = new Dictionary<int, (double X, double Y)>();

        /// <summary>本轮可达性预检失败标记：AutoCollect 收尾据此给"可达域"专属提示，
        /// 避免把 9 点全误报成"特征缺失"（2026-09-06）。</summary>
        private bool _reachPrecheckFailed;

        /// <summary>旋转中心采样中被操作员主动跳过的角度集合（失败弹窗选"否"产生）：
        /// 手动/自动步进不再反复尝试这些角度；角度成功补采后从中移除</summary>
        private readonly HashSet<double> _skippedRotationAngles = new HashSet<double>();

        /// <summary>吸放式标定：工件当前"躺"的机械位置（吸取来源）。首次取吸取位 PickBase，之后跟随每次放料点。</summary>
        private (double X, double Y) _ppLift;
        private bool _ppLiftSet;

        // 旋转采样"观测位就位"确认标记：首次旋转采样前必须人工确认特征已就位（2026-09-04 P1-2）
        private bool _rotationReadyPrompted;
        // 矩阵自动落盘设备目录失败标记（RefreshPublishChecks 展示"仅临时目录"黄项依据）
        private bool _matrixPersistFailed;
        // 最近一次拟合计算的矩阵健康检查报告（BuildHomMatHealthReport 多行文本，体检第④项依据）
        private string _lastHealthReport;
        // 打开向导时的旧标定姿态快照：重标时与本次实测比对（差>2mm/>5° → 发布前体检黄项提醒，2026-09-04）
        private double? _openedCalibZ;
        private double? _openedCalibU0;
        // 本次采样已记录标定姿态标记（九点网格中心 Index=5 成功采集时记一次）
        private bool _poseRecorded;

        // ============ P1b StepDef 会话（2026-09-05，v2 向导 = 任务规格 → 模板步骤 → 数据驱动） ============
        // 有 spec（v2）：CalibrationWizardTemplates.BuildFor(spec) 装配步骤序列，胶囊数/文案/完成判定随任务变；
        // 无 spec（旧兼容路径）：固定 4 步默认模板，视觉与行为与原四步壳等价，Next 不设门禁。
        private CalibrationTaskSpec _sessionSpec;
        /// <summary>特征预览验证成功过（DefineFeature 步完成判定的依据；后台线程写，bool 原子足够）</summary>
        private bool _featureVerifiedOnce;
        /// <summary>拟合计算成功过（ComputeFit 步完成判定的依据）</summary>
        private bool _fitComputedOnce;
        private readonly List<WizardStepItemVm> _stepItems = new List<WizardStepItemVm>();

        /// <summary>v2 任务规格（null=旧兼容四步模式）</summary>
        public CalibrationTaskSpec SessionSpec => _sessionSpec;
        /// <summary>是否为 v2 StepDef 会话（spec 驱动：胶囊动态 + 分步完成门禁）</summary>
        public bool IsSessionV2 => _sessionSpec != null;
        /// <summary>v2 单量会话：工具旋转 e（旋转采样 → 圆拟合偏心；拆自旋转混合档案的 e 段）</summary>
        public bool IsRotationSession => IsSessionV2 && _sessionSpec != null
                                         && _sessionSpec.Quantity == CalibrationQuantity.ToolRotation;
        /// <summary>v2 单量会话：TCP 对针 t（工具中心偏置 TCO；EyeInHand 间接 / EyeToHand 图像，按 spec 布局分发）</summary>
        public bool IsToolOffsetSession => IsSessionV2 && _sessionSpec != null
                                           && _sessionSpec.Quantity == CalibrationQuantity.ToolOffset;
        /// <summary>t 会话是否为 EyeInHand 布局（间接对针：示教 R_n→抬Z拍同点→结算 TCO=H(u)−R_n）。
        /// EyeToHand t 仍由独立对针窗承载（CalibrationToolOffsetWindow），不进本向导。</summary>
        public bool IsEihToolOffsetSession => IsSessionV2 && IsToolOffsetSession && _sessionSpec != null
                                              && _sessionSpec.Layout == EyeMode.EyeInHand;

        // ---- EIH 间接对针执行面板状态（2026-09-08：向导内嵌专用面板，结算时 R_n+p_tip+TCO 三元组一并落库） ----

        /// <summary>Step3 采样槽：非 t 会话 → 类型模板（九点/旋转/棋盘/像素当量）；t 会话 → 对针专用视图</summary>
        public bool ShowTypeSamplingContent => !IsToolOffsetSession;
        /// <summary>Step3 采样槽：t 会话对针执行视图可见（EIH：抬Z抓拍→点选→结算；ETH 向导承载时同视图语义）</summary>
        public bool ShowToolOffsetAlignView => IsToolOffsetSession;
        /// <summary>Step0 槽：EIH t 会话「示教基准位 R_n」卡（DefineOrigin 步，JOG 工具头尖压特征后记机械位）</summary>
        public bool ShowToolOffsetOriginPanel => IsEihToolOffsetSession && CurrentRole == WizardStepRole.DefineOrigin;
        /// <summary>Step3 采样头条（九点进度条）：t 会话无九点网格概念 → 隐藏</summary>
        public bool ShowSamplingProgressStrip => !IsToolOffsetSession;

        /// <summary>R_n 是否已示教（EIH 间接对针第一步门禁）</summary>
        public bool IsToolOffsetOriginSet => TargetProfile != null && TargetProfile.IsNozzleAlignSet;

        /// <summary>R_n 示教卡状态文案（含压住高度 R_nZ：校验台到位目视下压目标）</summary>
        public string ToolOffsetOriginStatusText
        {
            get
            {
                if (!IsEihToolOffsetSession || TargetProfile == null) return string.Empty;
                if (TargetProfile.IsNozzleAlignSet)
                {
                    string uHint = TargetProfile.CalibU0.HasValue
                        ? $"（回转角保持 U0={TargetProfile.CalibU0.Value:F1}°）"
                        : "（回转角保持标定姿态）";
                    string zPart = TargetProfile.NozzleAlignZ.HasValue
                        ? $", Z={TargetProfile.NozzleAlignZ.Value:F1}mm（压住高度 R_nZ——校验台目视下压目标）"
                        : "";
                    string camTxt = CameraMountFollowsZ
                        ? "抓拍自动回标定高度（相机随 Z）"
                        : "成像与 Z 无关、压住时按钮自动抬 Z 露特征（相机固定）";
                    return $"✅ 已记录基准位 R_n=({TargetProfile.NozzleAlignX:F3}, {TargetProfile.NozzleAlignY:F3}){zPart} mm{uHint} —— 下一步进『间接对针』步抓拍（{camTxt}）并点选同一特征。";
                }
                return _lastOriginFail
                    ? $"❌ 记基准位失败：{_lastOriginFailMsg} —— 请确认运动卡在位/已连接后重试。"
                    : "未记录 —— JOG 工具头尖轻压当前工件特征中心(可用塞尺确认刚好接触)，点【📍 记基准位 R_n】。";
            }
        }

        /// <summary>R_n 示教卡状态色（绿=已记；红=记失败；灰=未记）—— 解决"点击无直观反馈"</summary>
        public SolidColorBrush ToolOffsetOriginStatusBrush
        {
            get
            {
                if (TargetProfile != null && TargetProfile.IsNozzleAlignSet) return MakeBrush(0x2E, 0x7D, 0x32); // 绿
                if (_lastOriginFail) return MakeBrush(0xC6, 0x28, 0x28);                                     // 红
                return new SolidColorBrush(Colors.Gray);                                                    // 灰
            }
        }

        private bool _lastOriginFail;
        private string _lastOriginFailMsg = "";

        private (double X, double Y, double Z, double U)? _alignGrabPose; // 抓拍成功那一刻的机位——Z 守护必须用【成图那张】的 Z，不是结算时的当前 Z

        private double _alignPickCol = -1;
        private double _alignPickRow = -1;
        // 点选那一刻的 U 角（2026-09-08：求 e 要按 R(U0−U) 归一，必须用它而不是"结算时"的 U——
        // 用户可能在点选后又转了 U，那样归一角就错了。读不到时退化用结算时的 U。）
        private double _alignPickU = double.NaN;
        private bool _alignPickSet;

        // ---------- 相机安装特性（2026-09-09 重构）----------
        // 同一台物理相机「是否随 Z 升降」决定 HomMat 是否只在标定高度成立：
        //   随 Z → 拍照点位 invocation 必须回 CalibZ（当量纪律）；固定 → 成像与 Z 无关。
        // ⚠ 教训：这个开关此前只存在于单个档案里，同工位各档案互不相关，导致向导(t 档案,null→保守true)
        //   与校验台(H 档案,显式false) 对同一台相机给出相反口径。现在改为：
        //   本档案显式优先 > 继承同工位已声明的档案 > 默认(true 保守)。
        private bool? _cameraMountExplicit;                 // 本会话用户显式选择的口径（未动过为 null）
        private bool ResolveCameraMount(bool emitLog = false)
        {
            // ① 本会话 UI 显式选择
            if (_cameraMountExplicit.HasValue)
            {
                if (emitLog) AppendLog($"[相机安装特性] 按本向导选择：{(ResolveCameraMount() ? "随 Z 升降" : "固定（不随 Z）")}。");
                return _cameraMountExplicit.Value;
            }
            // ② 本档案已声明
            if (TargetProfile?.CameraMovesWithZ.HasValue == true)
            {
                if (emitLog) AppendLog($"[相机安装特性] 沿用本档案声明：{(TargetProfile.CameraMovesWithZ.Value ? "随 Z 升降" : "固定（不随 Z）")}。");
                return TargetProfile.CameraMovesWithZ.Value;
            }
            // ③ 同工位其它档案已声明（2026-09-09：同一台物理相机，只有一种口径）
            var sib = TryInheritCameraMountFromSiblings();
            if (sib.Found)
            {
                if (emitLog)
                    AppendLog($"[相机安装特性] 本档案未声明 → 继承同工位档案《{sib.Name}》：{(sib.Value ? "随 Z 升降" : "固定（不随 Z）")}"
                              + "。Z 守护按此口径生效（改口径请到『图像定格 / 校验台』勾选）。");
                if (TargetProfile != null) TargetProfile.CameraMovesWithZ = sib.Value;
                return sib.Value;
            }
            // ④ 都没声明：保守按"随 Z 升降"
            if (emitLog)
                AppendLog("[相机安装特性] 全工位未声明 → 保守按【随 Z 升降】处理（拍照须回 CalibZ）。"
                          + "若本机相机固定在小臂基座、不随 Z 升降，请在『图像定格』勾选『🚫 相机不随 Z 升降』后重进向导。");
            return true;
        }

        /// <summary>
        /// 只读口径判定（供文案类属性使用，不产生"继承写回档案"的副作用）。
        /// 与 ResolveCameraMount() 同一优先级：本会话显式 &gt; 本档案 &gt; 同工位继承 &gt; 缺省(true 保守)。
        /// ⚠ 2026-09-09：此前文案属性写死"(TargetProfile.CameraMovesWithZ ?? true)"，
        ///   与本向导抓拍逻辑走的 ResolveCameraMount() 继承链不一致 —— 同一个档案能同时
        ///   显示"随 Z 升降"和按"固定"执行，口径自相矛盾。文案一律改用本属性。
        /// </summary>
        private bool CameraMountFollowsZ
        {
            get
            {
                if (_cameraMountExplicit.HasValue) return _cameraMountExplicit.Value;
                if (TargetProfile?.CameraMovesWithZ.HasValue == true) return TargetProfile.CameraMovesWithZ.Value;
                var sib = TryInheritCameraMountFromSiblings();
                return sib.Found ? sib.Value : true;
            }
        }

        /// <summary>相机不随 Z（固定相机工位）——勾上后不再有 CalibZ 高度纪律，也可跳过Helper(#间接对针).</summary>
        public bool CameraMountFixed
        {
            get => _cameraMountExplicit.HasValue && _cameraMountExplicit.Value == false;
            set
            {
                bool? want = value ? false : (bool?)null;
                if (_cameraMountExplicit == want) return;
                _cameraMountExplicit = want;
                if (TargetProfile != null) TargetProfile.CameraMovesWithZ = want;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CameraMountInfoText));
                AppendLog(value
                    ? "[指针] 已勾选『相机不随 Z 升降』：CalibZ 高度纪律关闭，任意 Z 都可吸取/点选/结算。"
                    : "[指针] 已取消『相机不随 Z』：恢复随 Z 口径（拍照需回 CalibZ）。");
            }
        }
        /// <summary>对针 Step3 里显示的单行说明（口径来源 + 当前判定），改口径请先 uncheck 再勾选。</summary>
        public string CameraMountInfoText
        {
            get
            {
                var loader = TryInheritCameraMountFromSiblings();
                string src = _cameraMountExplicit.HasValue ? "本向导勾选"
                       : TargetProfile?.CameraMovesWithZ != null ? "本档案已存"
                       : loader.Found ? "同工位继承《" + loader.Name + "》"
                       : "未声明(缺省=随Z)";
                bool follow = (_cameraMountExplicit ?? TargetProfile?.CameraMovesWithZ) ?? true;
                return $"口径来源：{src}｜{(follow ? "随 Z：拍照须回 CalibZ=" + (TargetProfile?.CalibZ?.ToString("F1") ?? "未设置") : "不随 Z：任意 Z 可用")}";
            }
        }

        // ==================== 九点同工位共享（2026-09-09） ====================
        // 双吸嘴工位这类"一个工位多个标定任务"的场景，九点只跟【相机 + 高度】有关，
        // 与标定哪个物理量（H / e / t）无关 —— 所以九点结果应当是【工位级】共享的，
        // 不该每个任务卡各采一遍。做法：本方案没有自己的九点矩阵时，自动引用同工位
        // 已发布的那一份（含配套标定高度/基准位/基准角/相机安装特性），并在日志里点名来源。
        private bool _ninePointReused;
        private string _ninePointSharedText = "";

        /// <summary>本任务的九点矩阵是否复用自同工位其它方案（true=无需重采九点）</summary>
        public bool NinePointReused => _ninePointReused;

        /// <summary>共享来源说明（空=用本方案自己的九点）</summary>
        public string NinePointSharedText
        {
            get => _ninePointSharedText;
            set { _ninePointSharedText = value; OnPropertyChanged(); OnPropertyChanged(nameof(NinePointReused)); }
        }

        /// <summary>
        /// 尝试复用同工位的九点标定结果。命中条件：本方案还没产出自己的矩阵
        /// （HomMatFilePath 为空/文件不存在），且同工位存在已发布九点的其它方案。
        /// 复用时连带继承 CalibZ / BasePos / CalibU0 / 相机安装特性（同一台相机只有一个口径）。
        /// </summary>
        private void TryShareNinePointMatrix()
        {
            _ninePointReused = false;
            NinePointSharedText = "";
            try
            {
                if (TargetProfile == null) return;
                if (!string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                    && File.Exists(TargetProfile.HomMatFilePath))
                {
                    return; // 本方案已有自己的九点矩阵，不覆盖
                }
                if (string.IsNullOrWhiteSpace(TargetProfile.BoundStationCode)) return;

                var donor = _calibrationProfileRepository.GetAll()
                    .Where(po => po?.Model != null
                                 && po.Model != TargetProfile
                                 && !string.Equals(po.Model.Id, TargetProfile.Id, StringComparison.Ordinal)
                                 && string.Equals(po.BoundStationCode, TargetProfile.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(po.Model.HomMatFilePath)
                                 && File.Exists(po.Model.HomMatFilePath))
                    .OrderByDescending(po => po.Model.UpdatedAt)
                    .FirstOrDefault();
                if (donor == null) return;

                TargetProfile.HomMatFilePath = donor.Model.HomMatFilePath;
                if (TargetProfile.CalibZ == null) TargetProfile.CalibZ = donor.Model.CalibZ;
                if (!TargetProfile.IsBasePosSet && donor.Model.IsBasePosSet)
                {
                    TargetProfile.BasePosX = donor.Model.BasePosX;
                    TargetProfile.BasePosY = donor.Model.BasePosY;
                }
                if (TargetProfile.CalibU0 == null) TargetProfile.CalibU0 = donor.Model.CalibU0;
                if (TargetProfile.CameraMovesWithZ == null) TargetProfile.CameraMovesWithZ = donor.Model.CameraMovesWithZ;

                _ninePointReused = true;
                NinePointSharedText = $"♻ 本任务已复用同工位《{donor.ProfileName}》的九点标定（{donor.Model.HomMatFilePath}），无需重采九点。";
                AppendLog("[九点共享] " + NinePointSharedText);
                if (donor.Model.CalibZ.HasValue)
                {
                    AppendLog($"[九点共享] 沿用九点标定高度 CalibZ={donor.Model.CalibZ.Value:F1}mm"
                              + (donor.Model.CameraMovesWithZ == true ? "（相机随 Z：拍照须回该高度）" : ""));
                }
            }
            catch (Exception ex)
            {
                AppendLog("[九点共享] 复用同工位九点时异常（忽略，按独立标定继续）：" + ex.Message);
            }
        }

        // ==================== 九点同工位共享（2026-09-09） ====================
        // 同一个工位有多张任务卡（如"九点(共享)" / "吸嘴1_九点+旋转" / "吸嘴2_九点"），
        // 九点标定只跟【相机 + 标定高度 + 基准位】有关，与"标定哪个物理量"无关 →
        // 应当做一次、全工位复用。本方案若还没有自己的九点矩阵，就自动引用同工位已发布的那一份，
        // 并连带继承 CalibZ / 相机安装特性 / 基准位 / 基准角（否则同一台相机会出现两种 Z 口径）。
        private bool _ninePointShared;
        private string _sharedNinePointText = "";

        /// <summary>本任务的九点结果是否复用自同工位其它方案（true=无需再采九点）</summary>
        public bool IsNinePointShared
        {
            get => _ninePointShared;
            private set
            {
                if (_ninePointShared == value) return;
                _ninePointShared = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SharedNinePointVisible));
            }
        }
        public bool SharedNinePointVisible => _ninePointShared && !string.IsNullOrEmpty(_sharedNinePointText);
        /// <summary>共享来源说明（UI 横幅 / 日志）</summary>
        public string SharedNinePointText
        {
            get => _sharedNinePointText;
            private set { _sharedNinePointText = value; OnPropertyChanged(); OnPropertyChanged(nameof(SharedNinePointVisible)); }
        }

        /// <summary>在向导初始化时调用：本方案无自有九点矩阵 → 引用同工位已发布的那一份。</summary>
        private void TryInheritStationNinePoint()
        {
            IsNinePointShared = false;
            SharedNinePointText = "";
            try
            {
                if (TargetProfile == null) return;
                // 本方案已有自己的九点矩阵（且文件在）→ 不共享
                if (!string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                    && File.Exists(TargetProfile.HomMatFilePath)) return;
                if (string.IsNullOrWhiteSpace(TargetProfile.BoundStationCode)) return;

                var donor = _calibrationProfileRepository.GetAll()
                    .Where(po => po?.Model != null
                                 && !string.Equals(po.Model.Id, TargetProfile.Id, StringComparison.Ordinal)
                                 && string.Equals(po.BoundStationCode, TargetProfile.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(po.Model.HomMatFilePath)
                                 && File.Exists(po.Model.HomMatFilePath))
                    .OrderByDescending(po => po.Model.UpdatedAt)
                    .FirstOrDefault();
                if (donor == null) return;

                TargetProfile.HomMatFilePath = donor.Model.HomMatFilePath;
                OutputHomMatPath = donor.Model.HomMatFilePath;
                if (TargetProfile.CalibZ == null) TargetProfile.CalibZ = donor.Model.CalibZ;
                if (TargetProfile.CameraMovesWithZ == null) TargetProfile.CameraMovesWithZ = donor.Model.CameraMovesWithZ;
                if (TargetProfile.CalibU0 == null) TargetProfile.CalibU0 = donor.Model.CalibU0;
                if (!TargetProfile.IsBasePosSet && donor.Model.IsBasePosSet)
                {
                    TargetProfile.BasePosX = donor.Model.BasePosX;
                    TargetProfile.BasePosY = donor.Model.BasePosY;
                }
                TargetProfile.IsCalibrated = true;      // 矩阵确实可用（复用自同工位）

                IsNinePointShared = true;
                SharedNinePointText = $"♻ 已复用同工位《{donor.ProfileName}》的九点标定结果 —— 本任务无需重采九点。";
                AppendLog("[九点共享] " + SharedNinePointText);
                AppendLog($"[九点共享] 矩阵文件：{donor.Model.HomMatFilePath}");
                if (donor.Model.CalibZ.HasValue)
                    AppendLog($"[九点共享] 沿用九点标定高度 CalibZ={donor.Model.CalibZ.Value:F1}mm");
            }
            catch (Exception ex)
            {
                AppendLog("[九点共享] 复用同工位九点失败（按独立标定继续）：" + ex.Message);
            }
        }

        /// <summary>本档案未声明时，到同工位其它档案里继承已声明的相机安装特性</summary>
        private (bool Found, bool Value, string Name) TryInheritCameraMountFromSiblings()
        {
            try
            {
                if (TargetProfile == null || string.IsNullOrWhiteSpace(TargetProfile.BoundStationCode)) return (false, true, null);
                var best = _calibrationProfileRepository.GetAll()
                    .Where(po => po?.Model != null
                                 && string.Equals(po.BoundStationCode, TargetProfile.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                 && po.Model.CameraMovesWithZ.HasValue
                                 && !string.Equals(po.Model.Id, TargetProfile.Id, StringComparison.Ordinal))
                    .OrderByDescending(po => po.Model.UpdatedAt)
                    .Select(po => new { po.Model.CameraMovesWithZ, po.ProfileName })
                    .FirstOrDefault();
                return best == null ? (false, true, null) : (true, best.CameraMovesWithZ.Value, best.ProfileName);
            }
            catch { return (false, true, null); }
        }
        /// <summary>AlignTool 步已点选特征像素文案（点选后实时刷新）</summary>
        public string ToolOffsetAlignPickText => !_alignPickSet
            ? "未点选 —— 抓拍后单击画面中【工具头尖刚才压住的那个特征】"
            : $"已点选特征像素 (col={_alignPickCol:F1}, row={_alignPickRow:F1}) —— 可点【🔍 结算 TCO】";
        /// <summary>结算结果文案（TCO/p_tip 落库后显示）</summary>
        public string ToolOffsetSettledStatusText
        {
            get
            {
                if (TargetProfile == null || !TargetProfile.IsToolOffsetCalibrated) return "未结算 —— 完成 抓拍→点选 后点【🔍 结算 TCO】";
                string method = TargetProfile.ToolOffsetMethod == ToolOffsetMethod.EyeInHandIndirect ? "EyeInHand 间接对针"
                    : TargetProfile.ToolOffsetMethod == ToolOffsetMethod.EyeToHandImage ? "EyeToHand 图像对针" : "旧档案未知";
                return $"TCO 已写入档案：({TargetProfile.ToolOffsetWx:F3}, {TargetProfile.ToolOffsetWy:F3}) mm · p_tip=({TargetProfile.ToolAlignPixelX:F1}, {TargetProfile.ToolAlignPixelY:F1}) · 方法={method}";
            }
        }
        /// <summary>AlignTool 步 R_n 一行摘要（EIH t 会话；结算链第 0 环状态）</summary>
        public string ToolOffsetAlignRnLine => TargetProfile == null || !TargetProfile.IsNozzleAlignSet
            ? "R_n = 未记录（请回『示教基准位』步 JOG 工具头尖压住特征后点【📍 记基准位 R_n】）"
            : $"R_n = ({TargetProfile.NozzleAlignX:F3}, {TargetProfile.NozzleAlignY:F3}) mm · 压住高度 Z={TargetProfile.NozzleAlignZ?.ToString("F1") ?? "--"}mm（工具头尖压住特征时的回转中心 XY；目视下压目标）";

        /// <summary>AlignTool 步四步说明（按档案相机安装特性分支，2026-09-08 泛化）</summary>
        public string ToolOffsetAlignStepGuideText => IsEihToolOffsetSession && TargetProfile != null
            ? (CameraMountFollowsZ
                ? "① 已示教基准位 R_n（工具尖压住特征） → ② 抬 Z 回标定高度抓拍（相机随 Z：当量纪律） → ③ 点选同一特征 → ④ 结算 TCO = H(u_feature) − R_n，连同 p_tip 一并写入档案。校验台之后打开即 Ready。"
                : "① 已示教基准位 R_n（工具尖压住特征） → ② 抬 Z 露出特征抓拍（相机固定：成像与 Z 无关，压住时自动抬至最高） → ③ 点选同一特征 → ④ 结算 TCO = H(u_feature) − R_n，连同 p_tip 一并写入档案。校验台之后打开即 Ready。")
            : "";
        /// <summary>AlignTool 步抓拍按钮文案（按相机安装特性分支）</summary>
        public string ToolOffsetAlignCaptureButtonText => IsEihToolOffsetSession && TargetProfile != null
            ? (CameraMountFollowsZ
                ? "📷 抬 Z 回标定高度并抓拍（XY 不动）"
                : "📷 抬 Z 露出特征并抓拍（XY 不动 · 压住时自动抬至最高；成像与 Z 无关）")
            : "📷 抓拍";
        /// <summary>AlignTool 步抓拍按钮下方说明小字</summary>
        public string ToolOffsetAlignCaptureHintText => IsEihToolOffsetSession && TargetProfile != null
            ? (CameraMountFollowsZ
                ? "点击后自动抬 Z 到标定高度（相机随 Z：须回 CalibZ 才当量不失真）并软触发抓拍；画面定格后请单击左侧特征。"
                : "点击后若尖仍压住特征则自动抬 Z 到 0 露出（相机固定、成像与 Z 无关）；画面定格后请单击左侧特征。")
            : "";

        /// <summary>采样模板右侧「平移(吸放)9 点」Tab 可见性（e 会话无平移段 → 隐藏，防误切误采）</summary>
        public bool SampleTranslateTabVisible => !IsRotationSession;
        /// <summary>采样模板右侧 Tab 默认选中（e 会话=旋转 Tab；其余=平移 Tab）</summary>
        public int SampleTabIndex => IsRotationSession ? 1 : 0;
        /// <summary>顶部步骤胶囊数据源（模板全步骤；新旧模式统一渲染）</summary>
        public IReadOnlyList<WizardStepItemVm> StepItems => _stepItems;

        /// <summary>窗口标题（v2 会话显示物理量徽标；旧模式保持原标题）</summary>
        public string WindowTitle => IsSessionV2 ? BuildSessionTitle() : "通用视觉标定向导";
        /// <summary>当前步骤徽标文字："第 i / N 步"</summary>
        public string StepOrdinalText => _stepItems.Count == 0 ? string.Empty : $"第 {Math.Min(CurrentStep + 1, _stepItems.Count)} / {_stepItems.Count} 步";
        /// <summary>当前步骤标题（模板 Title）</summary>
        public string CurrentStepTitle => CurrentStepInRange ? _stepItems[CurrentStep].Title : string.Empty;
        /// <summary>当前步骤引导文案（模板 GuideText）</summary>
        public string CurrentStepGuideText => CurrentStepInRange ? _stepItems[CurrentStep].GuideText : string.Empty;
        /// <summary>当前步骤前置条件人读文案（模板 PreconditionText；e/t 依赖链）</summary>
        public string CurrentStepPrecondition => CurrentStepInRange ? _stepItems[CurrentStep].PreconditionText : string.Empty;
        public bool HasCurrentStepPrecondition => !string.IsNullOrEmpty(CurrentStepPrecondition);
        public bool HasNextStepBlockReason => !string.IsNullOrEmpty(NextStepBlockReason);
        private bool CurrentStepInRange => _stepItems.Count > 0 && CurrentStep >= 0 && CurrentStep < _stepItems.Count;

        // ============ P1c 分步内容面板模型（2026-09-05）============
        // v2 会话：旧四面板按「角色→内容」拆成分节，每步只显示本步该做的事——
        //   面板0 内拆 Bind 节（设备/轴/速览）与 Origin 节（基准示教+网格步长）两节互斥；
        //   面板3 内拆 Compute 节（拟合/残差）与 Verify 节（发布验收）两节互斥。
        //   特征(面板1)与采样(面板2)本身即独立步，原样保留。
        // 旧兼容模式：所有分节恒可见 = 原四面板完整内容（行为与视觉零回归）。

        /// <summary>是否为旧兼容四步模式（非 v2 会话）</summary>
        public bool IsLegacyMode => !IsSessionV2;

        /// <summary>
        /// 「标定轴方向（X/Y 反向）」高级开关可见性（2026-09-06）：
        /// 旧兼容模式恒可见（原行为）；v2 会话仅在「走位网格 H 段」的 DefineOrigin 步放出
        /// ——双吸嘴九点重做走任务卡 H 会话时，发现 Mark 反向/出视野可当场翻轴（翻后旧点需重采）。
        /// 判定口径=含走位网格的采集路径（NozzleTruthWalk/CameraTruthWalk/PickPlaceReturn）；
        /// e(旋转)/像素当量/对针段不显示，防无走位网格时误改脱同步。
        /// </summary>
        public bool AxisDirectionPanelVisible =>
            IsLegacyMode
            || (IsSessionV2
                && CurrentRole == WizardStepRole.DefineOrigin
                && _sessionSpec != null
                && _sessionSpec.Quantity == CalibrationQuantity.HandEye
                && IsWalkGridPath(_sessionSpec.PrimaryPath));

        /// <summary>走位网格采集路径（九点平移型）判定；旋转采样/单轴测距型路径不在此列。</summary>
        private static bool IsWalkGridPath(CalibrationAcquirePath p) =>
            p == CalibrationAcquirePath.NozzleTruthWalk
            || p == CalibrationAcquirePath.CameraTruthWalk
            || p == CalibrationAcquirePath.PickPlaceReturn;

        /// <summary>当前步骤角色（旧模式固定 BindDevices 语义兜底，分节不依赖它）</summary>
        public WizardStepRole CurrentRole
        {
            get
            {
                if (_stepItems.Count == 0 || CurrentStep >= _stepItems.Count) return WizardStepRole.BindDevices;
                return _stepItems[CurrentStep].Role;
            }
        }

        /// <summary>面板0：硬件绑定节是否可见（旧模式恒可见；v2 仅 BindDevices 步）</summary>
        public bool ShowBindSection => !IsSessionV2 || CurrentRole == WizardStepRole.BindDevices;
        /// <summary>面板0：机械基准节是否可见（旧模式恒可见；v2 仅 DefineOrigin 步）</summary>
        public bool ShowOriginSection => !IsSessionV2 || CurrentRole == WizardStepRole.DefineOrigin;
        /// <summary>面板3：拟合计算节是否可见（旧模式恒可见；v2 仅 ComputeFit 步）</summary>
        public bool ShowComputeSection => !IsSessionV2 || CurrentRole == WizardStepRole.ComputeFit;
        /// <summary>面板3：发布验收节是否可见（旧模式恒可见；v2 仅 VerifyAndPublish 步）</summary>
        public bool ShowVerifySection => !IsSessionV2 || CurrentRole == WizardStepRole.VerifyAndPublish;

        // ===== Origin 步内容区会话级显隐（2026-09-06 彻底会话化：e 旋转/偏心向导只留旋转语义） =====

        /// <summary>Origin 步「网格中心基准点 + 走位步长」区：仅平移网格类会话显示（H 段/旧壳）；
        /// e(旋转偏心) 会话无网格走位，隐藏以免误导（用户拍板：偏心向导=延伸杆旋转三点，无基准/步长）</summary>
        public bool ShowOriginGridParams =>
            IsLegacyMode
            || (IsSessionV2 && _sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.HandEye);

        /// <summary>Origin 步「吸放式几何参数（吸取位/拍照位/真空等）」区：仅 H 吸放会话显示；
        /// e 会话（含旧 RotatePickPlace 路径档案）一律隐藏——偏心旋转不吸放放料，仅观测画圆</summary>
        public bool ShowPickPlaceGeometry => IsPickPlaceProfile && !IsRotationSession && !IsToolOffsetSession;

        /// <summary>Origin 步「几何自检清单 + 俯视小地图」卡：H 平移段（基准/步长自检）显示；
        /// e 旋转会话无几何参数可自检，整卡隐藏保持干净</summary>
        public bool ShowOriginSelfCheckSection =>
            ShowOriginSection && (IsLegacyMode || (IsSessionV2 && _sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.HandEye));

        /// <summary>Compute 步「H 平移评估指标（RMS/逐点残差/矩阵路径）」区：仅 H 平移类会话显示；
        /// e 旋转会话无九点矩阵，隐藏避免 0.0000mm / 空残差 / 空矩阵路径 空壳误导</summary>
        public bool ShowHResidualSection =>
            IsLegacyMode
            || (IsSessionV2 && _sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.HandEye);

        /// <summary>分节在步骤序列中的序号（1 起；找不到返回 -1）</summary>
        private int RoleStepNo(WizardStepRole role)
        {
            for (int i = 0; i < _stepItems.Count; i++)
            {
                if (_stepItems[i].Role == role) return i + 1;
            }
            return -1;
        }

        /// <summary>面板0 绑定节大标题（旧=原标题；v2=按模板步骤序）</summary>
        public string BindSectionHeader => !IsSessionV2
            ? "步骤 1：物理场景与硬件绑定"
            : $"第 {RoleStepNo(WizardStepRole.BindDevices)} 步 · 硬件绑定（任务已由档案确定，本页只需绑定设备并核对轴号）";

        /// <summary>面板0 基准节大标题（仅 v2 会话显示；旧模式该节内嵌在步骤1 不另加标题）
        /// 2026-09-06 会话化：e(旋转偏心) 会话无网格基准示教，标题改为旋转观测语义</summary>
        public string OriginSectionHeader
        {
            get
            {
                if (!IsSessionV2) return string.Empty;
                if (_sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.ToolRotation)
                {
                    return $"第 {RoleStepNo(WizardStepRole.DefineOrigin)} 步 · 旋转观测确认（无基准示教——吸嘴吸附延伸杆后直接进入旋转采样，首点由采样步把关）";
                }
                return $"第 {RoleStepNo(WizardStepRole.DefineOrigin)} 步 · 机械基准示教（网格中心基准 + 走位步长）";
            }
        }

        /// <summary>面板3 计算节大标题（旧=原标题；v2=按模板步骤序）</summary>
        public string ComputeSectionHeader => !IsSessionV2
            ? "步骤 4：标定结果拟合计算与报告"
            : $"第 {RoleStepNo(WizardStepRole.ComputeFit)} 步 · 标定拟合计算与残差评估";

        /// <summary>面板3 验收节大标题（仅 v2 会话显示）</summary>
        public string VerifySectionHeader => !IsSessionV2
            ? string.Empty
            : $"第 {RoleStepNo(WizardStepRole.VerifyAndPublish)} 步 · 发布验收（体检全绿或经确认后，点右下角 ✔ 完成并保存即发布）";

        /// <summary>面板1 特征页大标题（旧=原标题；v2=按模板步骤序）</summary>
        public string FeatureSectionHeader => !IsSessionV2
            ? "步骤 2：标定特征与提取算子配置"
            : $"第 {RoleStepNo(WizardStepRole.DefineFeature)} 步 · 特征选择与提取验证";

        /// <summary>v2 会话任务速览（绑定步顶部卡：任务/布局/路径/产物——让操作员知道本会话在标什么）</summary>
        public string SessionSpecSummary
        {
            get
            {
                if (!IsSessionV2 || _sessionSpec == null) return string.Empty;
                string qName;
                switch (_sessionSpec.Quantity)
                {
                    case CalibrationQuantity.HandEye: qName = "手眼矩阵 H"; break;
                    case CalibrationQuantity.ToolRotation: qName = "旋转偏心 e"; break;
                    case CalibrationQuantity.ToolOffset: qName = "对针偏移 t"; break;
                    case CalibrationQuantity.PixelScale: qName = "像素当量 s"; break;
                    default: qName = "镜头畸变"; break;
                }
                string layout = _sessionSpec.Layout == EyeMode.EyeInHand ? "EyeInHand（相机随动）" : "EyeToHand（相机固定）";
                string slot = string.IsNullOrWhiteSpace(_sessionSpec.SlotKey) ? "—" : _sessionSpec.SlotKey;
                string nz = string.IsNullOrWhiteSpace(_sessionSpec.NozzleKey) || _sessionSpec.NozzleKey == "1"
                    ? string.Empty : $"（吸嘴通道 {_sessionSpec.NozzleKey}）";
                string path;
                switch (_sessionSpec.PrimaryPath)
                {
                    case CalibrationAcquirePath.NozzleTruthWalk: path = "吸嘴真值走位：移动吸嘴依次对准 9 个标定位采集"; break;
                    case CalibrationAcquirePath.CameraTruthWalk: path = "相机真值走位：固定相机下移动目标块至视野 9 个位置采集"; break;
                    case CalibrationAcquirePath.PickPlaceReturn: path = "吸放式 H：吸取→放料命令位→回拍照位成像（真值=命令位）"; break;
                    case CalibrationAcquirePath.RotatePickPlace: path = "吸放旋转 e：吸件转 U 各角度放落→回拍测位移→圆拟合偏心"; break;
                    case CalibrationAcquirePath.RotateCameraView: path = "旋转观测 e：吸嘴吸附延伸杆/治具特征，转 U 多角度成像→轨迹圆拟合旋转中心+偏心"; break;
                    case CalibrationAcquirePath.AlignTool: path = "固定相机对针：步进→锁定 M_tool→点选落点→结算 ToolOffset"; break;
                    default: path = "按档案采集路径执行"; break;
                }
                string target;
                if (string.IsNullOrWhiteSpace(_sessionSpec.StationCode))
                {
                    target = "产物：本会话将生成并保存标定 Artifact";
                }
                else
                {
                    string rawSlot = string.IsNullOrWhiteSpace(_sessionSpec.SlotKey) ? "-" : _sessionSpec.SlotKey;
                    string rawNz = string.IsNullOrWhiteSpace(_sessionSpec.NozzleKey) ? "1" : _sessionSpec.NozzleKey;
                    target = $"产物 Artifact：{_sessionSpec.StationCode} | {_sessionSpec.Quantity} | {rawSlot} | n{rawNz}";
                }
                return $"任务：{qName} · {layout} · 相机槽 {slot}{nz}\n采集：{path}\n{target}";
            }
        }

        /// <summary>v2 基准步的实操路径提示（按采集路径给贴合实际的示教步骤）</summary>
        public string OriginPathHintText
        {
            get
            {
                if (!IsSessionV2 || _sessionSpec == null) return string.Empty;
                switch (_sessionSpec.PrimaryPath)
                {
                    case CalibrationAcquirePath.NozzleTruthWalk:
                        // 2026-09-06 勘误：基准=工件取景位，勿要求吸嘴对准工件——相机与吸嘴(回转中心)水平
                        // 偏心可达几十 mm(EIH 实测 e≈40mm)，按"吸嘴对准工件"设基准则工件特征偏出相机画面，
                        // 九点网格走位会拍照不到。吸嘴与工件的对准关系由偏心 e 与换算公式负责，不在本步示教。
                        return "操作：① 手动 Jog 使标定工件特征（如麻将背面图案）在相机画面内清晰成像并尽量居中（取景位）→ ② 点【🎯 设当前轴位置为基准】记录 (X,Y) 为网格中心 → ③ 核对下方步长。后续 9 点网格以此基准为中心向四周推算走位。注意：基准取『工件取景位』而非『吸嘴对准工件位』——相机与吸嘴偏心可达几十 mm，吸嘴对准时工件反而不在画面内，九点走位会拍不到。";
                    case CalibrationAcquirePath.CameraTruthWalk:
                        return "操作（相机固定）：① 把目标特征移动到相机视野中心 → ② 确认执行机构（吸嘴/探针）恰好对准该特征后点【🎯 设当前轴位置为基准】→ ③ 核对下方步长。后续 9 点网格以此为中心扩展。";
                    case CalibrationAcquirePath.PickPlaceReturn:
                    case CalibrationAcquirePath.RotatePickPlace:
                        return "操作：先设【初始吸取位】（吸嘴悬停工件吸点正上方），再设【固定拍照位】（回拍照位并看清网格区），最后做一次首件试放验证再进采样。";
                    case CalibrationAcquirePath.RotateCameraView:
                        // 2026-09-06 彻底会话化：旋转观测式 e 无网格基准/步长示教——只确认观测条件
                        return "操作（旋转观测式·无基准示教）：① 吸嘴吸附延伸杆/治具，把杆端特征移入相机视野并清晰成像 → ② 确认特征在 -45°~+45° 转角内始终不出视野、模板角度范围够 → ③ 直接进入下一步配置旋转观测特征并采样（无网格基准/步长）。";
                    default:
                        return "操作：把执行机构对准标定基准位置后点【🎯 设当前轴位置为基准】，再核对网格步长。";
                }
            }
        }

        /// <summary>图像视窗角标状态（随当前步与数据实时变化：特征分/采样进度/测距状态等）</summary>
        public string ViewChipText
        {
            get
            {
                if (_stepItems.Count == 0) return string.Empty;
                var role = CurrentRole;
                switch (role)
                {
                    case WizardStepRole.DefineFeature:
                        return string.IsNullOrWhiteSpace(MatchScoreText) || MatchScoreText == "尚未提取"
                            ? "尚未提取特征 —— 点【📷 抓图并提取特征】验证"
                            : $"特征质量 {MatchScoreText}";
                    case WizardStepRole.SampleGrid:
                        if (IsSessionV2 && _sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.PixelScale)
                        {
                            return MeasuredPixelDistance > 0
                                ? $"图像距 {MeasuredPixelDistance:F1} px · 已知 {KnownPhysicalDistanceMm:F2} mm"
                                : "先抓图并框选已知距离特征";
                        }
                        if (TargetProfile != null && TargetProfile.Type == CalibrationType.Checkerboard2D)
                        {
                            return "拍摄标定板并提取角点/圆心阵列图像…";
                        }
                        return SamplingProgressText;
                    case WizardStepRole.SampleRotate:
                        int r = RotationPoints.Count(p => p.IsCaptured);
                        return $"旋转采样 {r}/{RotationPoints.Count} · 默认±45°(表内可改/扩角)";
                    case WizardStepRole.AlignTool:
                        return TargetProfile.IsToolOffsetCalibrated ? "对针已写入 ToolOffset" : "对针未完成";
                    case WizardStepRole.ComputeFit:
                        if (IsRotationSession)
                        {
                            return _fitComputedOnce
                                ? $"旋转拟合完成 · 中心=({TargetProfile.ToolCenterWx:F2},{TargetProfile.ToolCenterWy:F2})mm"
                                : "尚未拟合旋转（旋转采样 ≥3 点后点『拟合计算』）";
                        }
                        if (IsToolOffsetSession)
                        {
                            return TargetProfile.IsToolOffsetCalibrated ? "对针结算完成，可保存" : "尚未完成对针";
                        }
                        return CalculatedRms > 0 ? $"标定 RMS = {CalculatedRms:F4} mm" : "尚未拟合计算";
                    case WizardStepRole.VerifyAndPublish:
                        int red = PublishChecks.Count(c => c.Level == 2);
                        int yellow = PublishChecks.Count(c => c.Level == 1);
                        return red == 0 && yellow == 0 ? "体检全部通过，可保存发布"
                            : red > 0 ? $"体检 {red} 项红色阻断" : $"体检 {yellow} 项黄色提醒";
                    default:
                        return string.Empty;
                }
            }
        }

        /// <summary>分步面板模型批量通知（步骤切换/数据刷新后调用）</summary>
        private void NotifyStepPanelUi()
        {
            OnPropertyChanged(nameof(IsLegacyMode));
            OnPropertyChanged(nameof(AxisDirectionPanelVisible));
            OnPropertyChanged(nameof(CurrentRole));
            OnPropertyChanged(nameof(ShowBindSection));
            OnPropertyChanged(nameof(ShowOriginSection));
            OnPropertyChanged(nameof(ShowComputeSection));
            OnPropertyChanged(nameof(ShowVerifySection));
            OnPropertyChanged(nameof(ShowOriginGridParams));
            OnPropertyChanged(nameof(ShowPickPlaceGeometry));
            OnPropertyChanged(nameof(ShowOriginSelfCheckSection));
            OnPropertyChanged(nameof(ShowTypeSamplingContent));
            OnPropertyChanged(nameof(ShowToolOffsetAlignView));
            OnPropertyChanged(nameof(ShowToolOffsetOriginPanel));
            OnPropertyChanged(nameof(ShowSamplingProgressStrip));
            OnPropertyChanged(nameof(IsToolOffsetOriginSet));
            OnPropertyChanged(nameof(ToolOffsetOriginStatusText));
            OnPropertyChanged(nameof(ToolOffsetAlignPickText));
            OnPropertyChanged(nameof(ToolOffsetSettledStatusText));
            OnPropertyChanged(nameof(ToolOffsetAlignRnLine));
            OnPropertyChanged(nameof(ToolOffsetAlignStepGuideText));
            OnPropertyChanged(nameof(ToolOffsetAlignCaptureButtonText));
            OnPropertyChanged(nameof(ToolOffsetAlignCaptureHintText));
            OnPropertyChanged(nameof(ShowHResidualSection));
            OnPropertyChanged(nameof(BindSectionHeader));
            OnPropertyChanged(nameof(OriginSectionHeader));
            OnPropertyChanged(nameof(FeatureSectionHeader));
            OnPropertyChanged(nameof(ComputeSectionHeader));
            OnPropertyChanged(nameof(VerifySectionHeader));
            OnPropertyChanged(nameof(SessionSpecSummary));
            OnPropertyChanged(nameof(OriginPathHintText));
            OnPropertyChanged(nameof(ViewChipText));
        }

        /// <summary>全局模板下拉（模板匹配特征：Step2 选择/创建入口数据源）</summary>
        public ObservableCollection<TemplateInfo> AvailableTemplates { get; } = new ObservableCollection<TemplateInfo>();

        /// <summary>标定算法服务，底层Halcon封装，计算单应矩阵、像素转世界、畸变矫正</summary>
        public ICalibrationService CalibService { get; }

        /// <summary>当前正在编辑/执行的标定方案对象</summary>
        public CalibrationProfile TargetProfile { get; }

        /// <summary>WPF图像显示控件ViewModel，负责显示相机采集的图片、标定标记点</summary>
        public ImageDisplayVm CalibrationImageDisplay { get; }

        /// <summary>九点标定的标定点集合，一共9组：像素坐标 + 平台机械世界坐标</summary>
        public ObservableCollection<CalibrationPointModel> CalibrationPoints { get; } = new ObservableCollection<CalibrationPointModel>();

        /// <summary>旋转中心标定采样点集合：多个不同角度下标记点像素位置，用于求解旋转中心</summary>
        public ObservableCollection<RotationPointModel> RotationPoints { get; } = new ObservableCollection<RotationPointModel>();

        /// <summary>设备池内全部可用相机列表，界面下拉选择</summary>
        public ObservableCollection<ICamera> CameraDeviceList { get; } = new ObservableCollection<ICamera>();

        /// <summary>设备池内全部可用运动控制卡列表，界面下拉选择</summary>
        public ObservableCollection<IMotionCard> MotionDeviceList { get; } = new ObservableCollection<IMotionCard>();

        /// <summary>可用的镜头畸变矫正方案列表，做手眼标定时可以前置套用畸变参数</summary>
        public ObservableCollection<CalibrationProfile> AvailableDistortionProfiles { get; } = new ObservableCollection<CalibrationProfile>();

        private string _templatePickerName;
        /// <summary>模板匹配特征选中的模板名（Step2 下拉双向绑定；写回 Profile + 提取选项，供底层 MatchByName）</summary>
        public string TemplatePickerName
        {
            get => _templatePickerName;
            set
            {
                if (Set(ref _templatePickerName, value))
                {
                    TargetProfile.FeatureTemplateName = value;
                    CalibService.ExtractOptions.TemplateName = value;
                }
            }
        }

        private string _rotationCenterResult = "Cx: --, Cy: --";
        /// <summary>旋转中心结果字符串显示（UI绑定）</summary>
        public string RotationCenterResult
        {
            get => _rotationCenterResult;
            set => Set(ref _rotationCenterResult, value);
        }

        private string _rotationCenterPixelResult = "Px: --, Py: --";
        /// <summary>旋转中心【像素坐标】显示文本</summary>
        public string RotationCenterPixelResult
        {
            get => _rotationCenterPixelResult;
            set => Set(ref _rotationCenterPixelResult, value);
        }

        private string _rotationCenterWorldResult = "Wx: --, Wy: --";
        /// <summary>旋转中心【机械世界坐标】显示文本</summary>
        public string RotationCenterWorldResult
        {
            get => _rotationCenterWorldResult;
            set => Set(ref _rotationCenterWorldResult, value);
        }

        private ICamera _selectedCameraDevice;
        /// <summary>当前选中使用的相机设备</summary>
        public ICamera SelectedCameraDevice
        {
            get => _selectedCameraDevice;
            set
            {
                var oldCamera = _selectedCameraDevice;
                if (Set(ref _selectedCameraDevice, value))
                {
                    // 切换相机：注销旧相机事件，注册新相机帧接收事件
                    OnSelectedCameraChanged(oldCamera, value);
                    UpdateBindingInfo();
                    RaiseNextGate();
                }
            }
        }

        private IMotionCard _selectedMotionDevice;
        /// <summary>当前选中的运动控制卡</summary>
        public IMotionCard SelectedMotionDevice
        {
            get => _selectedMotionDevice;
            set
            {
                var oldMotion = _selectedMotionDevice;
                if (Set(ref _selectedMotionDevice, value))
                {
                    OnSelectedMotionChanged(oldMotion, value);
                    UpdateBindingInfo();
                    RaiseNextGate();
                }
            }
        }

        private bool _isCameraConnected;
        /// <summary>相机是否已经连接成功（UI状态显示）</summary>
        public bool IsCameraConnected
        {
            get => _isCameraConnected;
            private set => Set(ref _isCameraConnected, value);
        }

        private bool _isMotionConnected;
        /// <summary>运动卡是否已经连接成功（UI状态显示）</summary>
        public bool IsMotionConnected
        {
            get => _isMotionConnected;
            private set => Set(ref _isMotionConnected, value);
        }

        private bool _isEpsonMotion;
        /// <summary>
        /// 当前绑定的运动设备是否为 Epson 机械手（UI 显示轴选择指引条）。
        /// Epson 轴槽位 = 笛卡尔 0=X/1=Y/2=Z/3=U，标定 X/Y/旋转轴固定 0/1/3，无需按编号挑选。
        /// </summary>
        public bool IsEpsonMotion
        {
            get => _isEpsonMotion;
            private set => Set(ref _isEpsonMotion, value);
        }

        private string _hardwareBindingSummary = "未绑定硬件";
        /// <summary>硬件绑定信息摘要文本，界面展示：相机名称 | 运动卡名称</summary>
        public string HardwareBindingSummary
        {
            get => _hardwareBindingSummary;
            set => Set(ref _hardwareBindingSummary, value);
        }

        private double _gridStepX = 100.0;
        /// <summary>九点标定网格X步长（毫米），平台每次移动X方向距离；手动输入后即时刷新配置自检与小地图</summary>
        public double GridStepX
        {
            get => _gridStepX;
            set { if (SetGridStep(ref _gridStepX, value, nameof(GridStepX))) RunOnUi(RefreshGeometryChecks); }
        }
        private double _gridStepY = 100.0;
        /// <summary>九点标定网格Y步长（毫米），平台每次移动Y方向距离；手动输入后即时刷新配置自检与小地图</summary>
        public double GridStepY
        {
            get => _gridStepY;
            set { if (SetGridStep(ref _gridStepY, value, nameof(GridStepY))) RunOnUi(RefreshGeometryChecks); }
        }

        /// <summary>网格步长赋值：忽略未变化写入（含 ±1e-9 抖动），变化才发通知并联动自检（2026-09-06）</summary>
        private bool SetGridStep(ref double field, double value, string propName)
        {
            if (Math.Abs(field - value) < 1e-9) return false;
            field = value;
            OnPropertyChanged(propName);
            return true;
        }

        /// <summary>九点标定走位方式下拉选项（步骤1 UI 绑定，仅九点标定显示）</summary>
        public List<TraverseModeOption> TraverseModeOptions { get; } = new List<TraverseModeOption>
        {
            new TraverseModeOption { Mode = NinePointTraverseMode.SpiralCenterFirst, DisplayName = "中心优先螺旋（推荐）" },
            new TraverseModeOption { Mode = NinePointTraverseMode.RowScan, DisplayName = "传统逐行扫描" },
        };

        private NinePointTraverseMode _traverseMode = NinePointTraverseMode.SpiralCenterFirst;
        /// <summary>
        /// 当前选择的九点走位方式（默认中心优先螺旋）。
        /// 切换时重置采集进度：清空跳过点、把已采集点标记回未采集（数据保留，重采覆盖），
        /// 避免按新顺序续采旧进度导致"顺序数组前段的点已采集被跳过"的混乱。
        /// </summary>
        public NinePointTraverseMode TraverseMode
        {
            get => _traverseMode;
            set
            {
                if (!Set(ref _traverseMode, value)) return;
                if (_samplingBusy != 0)
                {
                    AppendLog("[提示] 采样任务执行中，新的走位方式将在本次采样结束后生效，请稍后重新采集。");
                    return;
                }
                _skippedPointIndices.Clear();
                foreach (var p in CalibrationPoints)
                {
                    p.IsCaptured = false;
                    p.IsReliable = true; // 重新采集时重新判断可信度
                }
                CalibService.ResetMarkReference();
                AppendLog($"九点走位方式已切换为【{(value == NinePointTraverseMode.RowScan ? "传统逐行扫描" : "中心优先螺旋")}】，采集进度已重置，请重新采集。");
            }
        }

        private int _checkerboardRows = 7;
        /// <summary>棋盘格标定：内角点行数</summary>
        public int CheckerboardRows
        {
            get => _checkerboardRows;
            set => Set(ref _checkerboardRows, value);
        }

        private int _checkerboardCols = 7;
        /// <summary>棋盘格标定：内角点列数</summary>
        public int CheckerboardCols
        {
            get => _checkerboardCols;
            set => Set(ref _checkerboardCols, value);
        }

        private double _checkerboardSpacingMm = 10.0;
        /// <summary>棋盘格方格实际物理边长 单位mm</summary>
        public double CheckerboardSpacingMm
        {
            get => _checkerboardSpacingMm;
            set => Set(ref _checkerboardSpacingMm, value);
        }

        private double _measuredPixelDistance = 100.0;
        /// <summary>像素当量标定：图像上测量得到像素距离</summary>
        public double MeasuredPixelDistance
        {
            get => _measuredPixelDistance;
            set => Set(ref _measuredPixelDistance, value);
        }

        private double _knownPhysicalDistanceMm = 10.0;
        /// <summary>像素当量标定：物体真实物理距离mm</summary>
        public double KnownPhysicalDistanceMm
        {
            get => _knownPhysicalDistanceMm;
            set => Set(ref _knownPhysicalDistanceMm, value);
        }

        /// <summary>标定输出单应矩阵HomMat文件完整路径</summary>
        public string OutputHomMatPath { get; private set; }

        private double _calculatedRms;
        /// <summary>标定完成RMS重投影误差，单位mm；数值越小标定精度越高</summary>
        public double CalculatedRms
        {
            get => _calculatedRms;
            set => Set(ref _calculatedRms, value);
        }

        /// <summary>
        /// 特征提取算子参数（与 CalibService.ExtractOptions 同一实例）。
        /// 向导"特征配置"步骤滑块直接绑定其属性，调节后通过 ReapplyFeatureExtraction 实时重试。
        /// </summary>
        public FeatureExtractOptions ExtractOptions => CalibService.ExtractOptions;

        #region 匹配质量分数显示（Step2 特征配置实时反馈）

        private double _matchScore;
        /// <summary>最近一次特征提取的匹配分（0~100，无结果时 0）</summary>
        public double MatchScore
        {
            get => _matchScore;
            private set => Set(ref _matchScore, value);
        }

        private string _matchScoreText = "尚未提取";
        /// <summary>匹配分文本（如 "86/100 · 良好"）</summary>
        public string MatchScoreText
        {
            get => _matchScoreText;
            private set => Set(ref _matchScoreText, value);
        }

        private SolidColorBrush _matchScoreBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));
        /// <summary>匹配分等级颜色（≥85 绿 / 70~84 黄 / 55~69 橙 / &lt;55 红 / 失败灰）</summary>
        public SolidColorBrush MatchScoreBrush
        {
            get => _matchScoreBrush;
            private set => Set(ref _matchScoreBrush, value);
        }

        private string _matchScoreDetail = "点击【抓图并提取特征】后，此处将显示当前参数下的识别质量分。分数越高，后续走位采样越不易丢特征点；拖动上方滑块即可实时看到分数变化。";
        /// <summary>匹配质量成分明细（多行文本，UI 直接展示）</summary>
        public string MatchScoreDetail
        {
            get => _matchScoreDetail;
            private set => Set(ref _matchScoreDetail, value);
        }

        private string _matchCandidateSummary = "";
        /// <summary>候选概要（如 "候选 3 个 → 选中 #2"），供界面快速判断是否有干扰候选</summary>
        public string MatchCandidateSummary
        {
            get => _matchCandidateSummary;
            private set => Set(ref _matchCandidateSummary, value);
        }

        /// <summary>候选颜色画刷（选中绿 / 干扰红 / 普通黄）</summary>
        private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 读取 CalibService.LastMatchReport 并刷新 UI 分数属性（线程安全：内部回 UI 线程）。
        /// 供特征预览/调参重试/采样回填后调用，实现"调滑块 → 看分数"所见即所得。
        /// </summary>
        public void RefreshMatchScoreDisplay()
        {
            var report = CalibService?.LastMatchReport;
            RunOnUi(() =>
            {
                if (report == null)
                {
                    MatchScore = 0;
                    MatchScoreText = "尚未提取";
                    MatchScoreBrush = MakeBrush(120, 120, 120);
                    MatchScoreDetail = "暂无提取结果。";
                    MatchCandidateSummary = "";
                    return;
                }

                MatchScore = report.Success ? Math.Max(0, Math.Min(100, report.Score)) : 0;
                if (!report.Success)
                {
                    MatchScoreText = "✗ 未识别 (0/100)";
                    MatchScoreBrush = MakeBrush(200, 60, 60);
                }
                else
                {
                    MatchScoreText = $"{MatchScore:F0}/100 · {report.Verdict}";
                    MatchScoreBrush = MatchScore >= 85 ? MakeBrush(46, 160, 90)
                        : MatchScore >= 70 ? MakeBrush(200, 160, 40)
                        : MatchScore >= 55 ? MakeBrush(230, 120, 40)
                        : MakeBrush(200, 60, 60);
                }
                MatchScoreDetail = string.IsNullOrWhiteSpace(report.Detail)
                    ? (report.Success ? "识别成功。" : "识别失败。")
                    : report.Detail;
                var selected = report.Candidates?.FirstOrDefault(c => c.IsSelected);
                MatchCandidateSummary = report.CandidateCount <= 0
                    ? "无候选"
                    : selected != null
                        ? $"候选 {report.CandidateCount} 个 → 选中 #{selected.Index}"
                        : $"候选 {report.CandidateCount} 个";
                OnPropertyChanged(nameof(ViewChipText));
            });
        }

        #endregion

        /// <summary>日志只读属性，UI文本框绑定</summary>
        public string LogText => _log.ToString();

        private int _currentStep;
        /// <summary>向导当前步骤索引（0..N-1，N=模板步骤数；v2 模板 4~6 步，旧兼容固定 4 步）</summary>
        public int CurrentStep
        {
            get => _currentStep;
            set
            {
                int clamped = _stepItems.Count > 0 ? Math.Max(0, Math.Min(value, _stepItems.Count - 1)) : value;
                if (Set(ref _currentStep, clamped))
                {
                    OnPropertyChanged(nameof(IsNotLastStep));
                    OnPropertyChanged(nameof(IsLastStep));
                    OnPropertyChanged(nameof(StepGuideTip));
                    OnPropertyChanged(nameof(CurrentTabIndex));
                    OnPropertyChanged(nameof(StepOrdinalText));
                    OnPropertyChanged(nameof(CurrentStepTitle));
                    OnPropertyChanged(nameof(CurrentStepGuideText));
                    OnPropertyChanged(nameof(CurrentStepPrecondition));
                    OnPropertyChanged(nameof(HasCurrentStepPrecondition));
                    OnPropertyChanged(nameof(HasNextStepBlockReason));
                    OnPropertyChanged(nameof(ScrollToBottomForCurrentStep));
                    RefreshStepItemStates();
                    // 步骤切换统一刷新：采集进度/黄条/几何自检/发布前体检（末步=验收/发布面板，进入即体检）。
                    NotifySamplingUi();
                    RefreshGeometryChecks();
                    NotifyStep3UiText();
                    if (IsLastStep)
                    {
                        RefreshPublishChecks();
                    }
                    RaiseNextGate();
                    NotifyStepPanelUi();
                }
            }
        }

        /// <summary>还有后续步骤？（非最后一步显示「下一步」按钮）</summary>
        public bool IsNotLastStep => _stepItems.Count == 0 || CurrentStep < _stepItems.Count - 1;
        public bool IsLastStep => !IsNotLastStep;

        // 向导步骤胶囊指示色（当前=蓝 完成=绿 未到=灰）
        private static readonly SolidColorBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        private static readonly SolidColorBrush DoneBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        private static readonly SolidColorBrush InactiveBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));

        /// <summary>主内容区 Tab 槽(0..3)：当前步骤角色映射到既有四面板布局（v2 多步聚槽；旧兼容步骤与槽同号）</summary>
        public int CurrentTabIndex
        {
            get
            {
                if (_stepItems.Count == 0 || CurrentStep >= _stepItems.Count) return 0;
                return IsSessionV2 ? PhaseSlotOf(_stepItems[CurrentStep].Role) : CurrentStep;
            }
        }

        /// <summary>当前步是否需要把所在面板滚到底部（Origin 基准区 / 发布体检区位于面板下部，code-behind 消费）</summary>
        public bool ScrollToBottomForCurrentStep
        {
            get
            {
                if (_stepItems.Count == 0 || !IsSessionV2) return false;
                var role = _stepItems[CurrentStep].Role;
                return role == WizardStepRole.DefineOrigin || role == WizardStepRole.VerifyAndPublish;
            }
        }

        /// <summary>步骤角色 → 内容面板槽（0 硬件/基准、1 特征、2 采样、3 计算/发布）</summary>
        private static int PhaseSlotOf(WizardStepRole role)
        {
            switch (role)
            {
                case WizardStepRole.DefineFeature:
                    return 1;
                case WizardStepRole.SampleGrid:
                case WizardStepRole.SampleRotate:
                case WizardStepRole.AlignTool:
                    return 2;
                case WizardStepRole.ComputeFit:
                case WizardStepRole.VerifyAndPublish:
                    return 3;
                default: // BindDevices / DefineOrigin
                    return 0;
            }
        }

        /// <summary>当前步骤操作提示文案（旧策略按类型给提示；v2 会话由顶部引导条承载 GuideText，此处留空防重复）</summary>
        public string StepGuideTip => IsSessionV2 ? string.Empty : (_strategy != null ? _strategy.GetStepGuideTip(CurrentStep + 1) : string.Empty);

        /// <summary>传统阶段号(1..4)：旧采集策略/采样代码按"固定四步"数值分支（CurrentStep<=1 预览等），
        /// v2 模板步骤数可变(4~6)会破坏该口径——用本属性把 角色 → 阶段号 归一后策略判断不受影响：
        /// 1=绑定/基准(面板0)、2=特征(面板1)、3=采样(网格/旋转/对针)、4=计算/发布(面板3)。</summary>
        public int LegacyPhase
        {
            get
            {
                if (_stepItems.Count == 0) return Math.Min(CurrentStep + 1, 4);
                if (!IsSessionV2) return Math.Min(CurrentStep + 1, 4);
                switch (_stepItems[CurrentStep].Role)
                {
                    case WizardStepRole.BindDevices:
                    case WizardStepRole.DefineOrigin:
                        return 1;
                    case WizardStepRole.DefineFeature:
                        return 2;
                    case WizardStepRole.SampleGrid:
                    case WizardStepRole.SampleRotate:
                    case WizardStepRole.AlignTool:
                        return 3;
                    default:
                        return 4;
                }
            }
        }

        /// <summary>当前步骤完成判定（v2 门禁：数据够了 Next 才亮；旧兼容模式恒可前进）</summary>
        public bool IsCurrentStepReady()
        {
            if (_stepItems.Count == 0) return false;
            return IsSessionV2 ? EvalStepReady(_stepItems[CurrentStep].Role) : true;
        }

        /// <summary>Next 按钮阻断原因（未就绪时的说明文案；就绪/末步为空）</summary>
        public string NextStepBlockReason
        {
            get
            {
                if (!IsSessionV2 || _stepItems.Count == 0 || IsLastStep) return string.Empty;
                if (EvalStepReady(_stepItems[CurrentStep].Role)) return string.Empty;
                return DescribeBlockReason(_stepItems[CurrentStep].Role);
            }
        }

        /// <summary>步骤角色 → 完成判定（数据是否足够进入下一步；仅 v2 会话启用）</summary>
        private bool EvalStepReady(WizardStepRole role)
        {
            switch (role)
            {
                case WizardStepRole.BindDevices:
                    return SelectedCameraDevice != null && SelectedMotionDevice != null;
                case WizardStepRole.DefineOrigin:
                    if (IsToolOffsetSession)
                    {
                        // t 会话：EIH 间接对针的 Origin=示教压住特征记基准位 R_n（门禁=已记）；
                        // ETH 布局无基准位语义（若向导承载）→ 放行
                        return IsEihToolOffsetSession ? TargetProfile.IsNozzleAlignSet : true;
                    }
                    if (IsRotationSession)
                    {
                        // e 会话 Origin：吸放式旋转的吸放动作依赖 Pick/Photo 机械位 → 必须设；
                        // 走位式旋转只需观测就绪（采样首点有 ConfirmRotationReady 把关）→ 不锁
                        return IsPickPlaceProfile
                            ? TargetProfile.IsPickBaseSet && TargetProfile.IsPhotoPoseSet
                            : true;
                    }
                    return IsPickPlaceOriginSpec()
                        ? TargetProfile.IsPickBaseSet && TargetProfile.IsPhotoPoseSet
                        : TargetProfile.IsBasePosSet;
                case WizardStepRole.DefineFeature:
                    // t 会话（两种布局）对针不依赖模板提取/识别分：EIH 间接对针靠"人眼点选同一特征"、
                    // ETH 图像对针靠"点选实际落点" → 特征配置步放行（配置仅作可识别性参考，不设门禁）
                    if (IsToolOffsetSession) return true;
                    return _featureVerifiedOnce;
                case WizardStepRole.SampleGrid:
                    // s（像素当量）：门禁=抓图测距 + 已知距离都已就绪；H 网格：至少 3 个有效采集点
                    if (IsSessionV2 && _sessionSpec.Quantity == CalibrationQuantity.PixelScale)
                    {
                        return MeasuredPixelDistance > 0 && KnownPhysicalDistanceMm > 0;
                    }
                    // 2026-09-09 九点同工位共享：本方案已引用同工位九点矩阵 → 无需重采九点
                    if (NinePointReused) return true;
                    return CalibrationPoints.Count(p => p.IsCaptured) >= 3;
                case WizardStepRole.SampleRotate:
                    return RotationPoints.Count(p => p.IsCaptured) >= 3;
                case WizardStepRole.AlignTool:
                    return TargetProfile.IsToolOffsetCalibrated;
                case WizardStepRole.ComputeFit:
                    // ★ P2 e/t 单量会话：完成=本段结算过（不借 H 已标定放行——那不代表 e/t 完成）
                    if (IsRotationSession) return _fitComputedOnce;
                    if (IsToolOffsetSession) return TargetProfile.IsToolOffsetCalibrated;
                    // 三种判定口径：本会话拟合过 / 旧矩阵已算（重开已标定方案）/ 内联策略写回 RMS
                    return _fitComputedOnce || TargetProfile.IsCalibrated || CalculatedRms > 0;
                default:
                    return true;
            }
        }

        /// <summary>吸放式采集路径判定（H 吸放=PickPlaceReturn；e 吸放旋转=RotatePickPlace）——会话级吸放语义唯一口径</summary>
        private static bool IsPickPlacePath(CalibrationAcquirePath p) =>
            p == CalibrationAcquirePath.PickPlaceReturn
            || p == CalibrationAcquirePath.RotatePickPlace;

        /// <summary>Origin 步骤是否需要吸放几何（吸取位+拍照位），否则用九点基准位语义</summary>
        private bool IsPickPlaceOriginSpec() =>
            _sessionSpec != null && IsPickPlacePath(_sessionSpec.PrimaryPath);

        private string DescribeBlockReason(WizardStepRole role)
        {
            switch (role)
            {
                case WizardStepRole.BindDevices:
                    return "未绑定硬件：请在本页选择 相机 与 运动控制器";
                case WizardStepRole.DefineOrigin:
                    if (IsToolOffsetSession)
                    {
                        return IsEihToolOffsetSession
                            ? "未记基准位 R_n：请 JOG 工具头尖轻压工件特征后点【📍 记基准位 R_n】"
                            : string.Empty;
                    }
                    return IsPickPlaceOriginSpec()
                        ? "未定义几何基准：请示教 初始吸取位 与 固定拍照位"
                        : "未设基准位：请移动机构使特征对准视野中心后点击 🎯 设当前轴位置为基准";
                case WizardStepRole.DefineFeature:
                    return "特征未验证：请点击 📷 抓图并提取特征，直到识别成功";
                case WizardStepRole.SampleGrid:
                    return "采样点不足：至少 3 个有效采集点（不应共线）才能拟合";
                case WizardStepRole.SampleRotate:
                    return "旋转角度不足：至少采集 3 个角度（分布越开，圆心拟合越稳）";
                case WizardStepRole.AlignTool:
                    return "对针未完成：请先执行对针并写入 ToolOffset";
                case WizardStepRole.ComputeFit:
                    return "尚未拟合：请点击 🧮 立即执行标定拟合计算 并确认残差/几何自检";
                default:
                    return string.Empty;
            }
        }

        /// <summary>步骤清单内可否前进（末步禁 / v2 会话需当前步就绪）</summary>
        private bool CanAdvanceFrom(int index)
        {
            if (_stepItems.Count == 0) return false;
            if (index >= _stepItems.Count - 1) return false;
            if (!IsSessionV2) return true;
            return EvalStepReady(_stepItems[index].Role);
        }

        /// <summary>刷新 Next/Prev 按钮态与阻断文案（UI 线程安全；命令可能尚未构造完成，空安全）</summary>
        private void RaiseNextGate()
        {
            RunOnUi(() =>
            {
                (NextStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (PrevStepCommand as RelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(NextStepBlockReason));
                OnPropertyChanged(nameof(IsCurrentStepReady));
                OnPropertyChanged(nameof(HasNextStepBlockReason));
            });
        }

        private void RefreshStepItemStates()
        {
            for (int i = 0; i < _stepItems.Count; i++)
            {
                var it = _stepItems[i];
                it.IsDone = i < CurrentStep;
                it.IsCurrent = i == CurrentStep;
                it.IndicatorBrush = i == CurrentStep ? ActiveBrush : (i < CurrentStep ? DoneBrush : InactiveBrush);
            }
        }

        private void BuildWizardSteps()
        {
            _stepItems.Clear();
            var defs = ResolveStepTemplate();
            for (int i = 0; i < defs.Count; i++)
            {
                var d = defs[i];
                _stepItems.Add(new WizardStepItemVm
                {
                    Index = i,
                    Ordinal = (i + 1).ToString(),
                    Title = d.Title,
                    GuideText = d.GuideText,
                    PreconditionText = d.PreconditionText,
                    Role = d.Role
                });
            }
        }

        /// <summary>模板解析：v2 会话按 spec 装配；空模板(畸变等)/旧模式回退固定 4 步</summary>
        private List<CalibrationStepDef> ResolveStepTemplate()
        {
            if (IsSessionV2)
            {
                var defs = CalibrationWizardTemplates.BuildFor(_sessionSpec);
                if (defs != null && defs.Count > 0) return defs;
            }
            return CreateLegacySteps();
        }

        private static List<CalibrationStepDef> CreateLegacySteps()
        {
            return new List<CalibrationStepDef>
            {
                new CalibrationStepDef(WizardStepRole.BindDevices, "硬件绑定", string.Empty, "bind"),
                new CalibrationStepDef(WizardStepRole.DefineFeature, "特征配置", string.Empty, "feature"),
                new CalibrationStepDef(WizardStepRole.SampleGrid, "数据采集", string.Empty, "grid"),
                new CalibrationStepDef(WizardStepRole.ComputeFit, "计算与发布", string.Empty, "compute")
            };
        }

        /// <summary>v2 物理量/路径 → 旧 CalibrationType 桥（Step3 模板选择器与采集策略按旧 Type 分流，必须对齐）</summary>
        private static CalibrationType? MapQuantityToLegacyType(CalibrationQuantity quantity, CalibrationAcquirePath path)
        {
            switch (quantity)
            {
                case CalibrationQuantity.HandEye:
                    return path == CalibrationAcquirePath.PickPlaceReturn
                        ? CalibrationType.PickPlaceHandEye
                        : CalibrationType.NinePointHandEye;
                case CalibrationQuantity.PixelScale:
                    return CalibrationType.PixelScale;
                case CalibrationQuantity.ToolRotation:
                    // 吸放旋转 e（RotatePickPlace）→ legacy 吸放档案型；走位旋转（RotateCameraView）→ 走位混合档案型
                    return path == CalibrationAcquirePath.RotatePickPlace
                        ? CalibrationType.PickPlaceHandEye
                        : CalibrationType.HandEyeWithRotation;
                default:
                    return null;
            }
        }

        private string BuildSessionTitle()
        {
            if (_sessionSpec == null) return "通用视觉标定向导";
            string qName;
            switch (_sessionSpec.Quantity)
            {
                case CalibrationQuantity.HandEye: qName = "手眼 H"; break;
                case CalibrationQuantity.ToolRotation: qName = "旋转偏心 e"; break;
                case CalibrationQuantity.ToolOffset: qName = "对针 t"; break;
                case CalibrationQuantity.PixelScale: qName = "像素当量 s"; break;
                default: qName = "镜头畸变"; break;
            }
            string slot = string.IsNullOrWhiteSpace(_sessionSpec.SlotKey)
                ? string.Empty : " · 相机槽 " + _sessionSpec.SlotKey;
            string nz = string.IsNullOrWhiteSpace(_sessionSpec.NozzleKey) || _sessionSpec.NozzleKey == "1"
                ? string.Empty : " · 吸嘴 n" + _sessionSpec.NozzleKey;
            string layout = _sessionSpec.Layout == EyeMode.EyeInHand ? "EyeInHand" : "EyeToHand";
            return qName + " 标定" + slot + nz + " · " + layout + " · StepDef 会话";
        }

        // 轴选项数据模型
        public class AxisOption
        {
            public int AxisIndex { get; set; }
            public string DisplayName { get; set; }
            /// <summary>悬停详细说明：Epson 场景给 J 关节联动观察锚点与选轴提示（ZMC 项为 null 不显示）</summary>
            public string Tooltip { get; set; }
        }

        /// <summary>
        /// 向导步骤胶囊展示模型（StepDef 的 UI 包装；属性变更通知供 ItemsControl 高亮/颜色刷新）。
        /// 不进 XAML DataTrigger 判定——颜色/状态由 VM 集中计算后下发。
        /// </summary>
        public class WizardStepItemVm : System.ComponentModel.INotifyPropertyChanged
        {
            public int Index { get; set; }
            public string Ordinal { get; set; }
            public string Title { get; set; }
            public string GuideText { get; set; }
            public string PreconditionText { get; set; }
            public WizardStepRole Role { get; set; }

            private bool _isCurrent;
            public bool IsCurrent
            {
                get => _isCurrent;
                set { if (_isCurrent != value) { _isCurrent = value; Raise(nameof(IsCurrent)); } }
            }

            private bool _isDone;
            public bool IsDone
            {
                get => _isDone;
                set { if (_isDone != value) { _isDone = value; Raise(nameof(IsDone)); } }
            }

            private SolidColorBrush _indicatorBrush = new SolidColorBrush(Color.FromRgb(100, 100, 100));
            public SolidColorBrush IndicatorBrush
            {
                get => _indicatorBrush;
                set { _indicatorBrush = value; Raise(nameof(IndicatorBrush)); }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            private void Raise(string name)
            {
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }

        // 供 Step 0 下拉绑定的轴集合
        public ObservableCollection<AxisOption> AvailableAxes { get; } = new ObservableCollection<AxisOption>
{
    new AxisOption { AxisIndex = 1, DisplayName = "1号轴：X轴（左右移动）" },
    new AxisOption { AxisIndex = 3, DisplayName = "3号轴：左工位Y轴（前后移动）" },
    new AxisOption { AxisIndex = 2, DisplayName = "2号轴：右工位Y轴（前后移动）" },
    new AxisOption { AxisIndex = 0, DisplayName = "0号轴：Z轴（升降/旋转轴）" }
};



        #region UI命令绑定
        /// <summary>设当前机台绝对位置为标定基准中心命令</summary>
        public ICommand SetCurrentAsBasePosCommand { get; }
        /// <summary>手动采集标定点采样命令</summary>
        public ICommand TriggerSampleCommand { get; }
        /// <summary>旋转中心手动采样命令</summary>
        public ICommand TriggerRotationSampleCommand { get; }
        /// <summary>全自动采集所有点位（平台自动走位+自动采图）</summary>
        public ICommand AutoRunAllCommand { get; }
        /// <summary>执行标定算法计算，求解单应矩阵/畸变参数</summary>
        public ICommand RunCalibrationCommand { get; }
        /// <summary>保存标定结果方案，关闭向导窗口</summary>
        public ICommand SaveResultCommand { get; }
        /// <summary>复制排障块（配置快照+最近60行日志）到剪贴板</summary>
        public ICommand CopyTroubleshootCommand { get; }
        /// <summary>吸放式：读当前四轴 → 设为初始吸取位</summary>
        public ICommand SetPickBaseCommand { get; }
        /// <summary>吸放式：读当前四轴 → 设为固定拍照位</summary>
        public ICommand SetPhotoPoseCommand { get; }
        /// <summary>吸放式：首件试放验证（吸→放网格中心→回拍→识别自检）</summary>
        public ICommand TryFirstPlacementCommand { get; }
        /// <summary>上一步向导</summary>
        public ICommand PrevStepCommand { get; }
        /// <summary>下一步向导</summary>
        public ICommand NextStepCommand { get; }
        /// <summary>EIH 间接对针：记当前机械位为基准位 R_n（工具头尖压住工件特征时刻的回转中心 XY）</summary>
        public ICommand RecordToolOffsetOriginCommand { get; }
        /// <summary>EIH 间接对针：抬 Z 回标定高度(XY 不动)并抓拍——特征成像供点选</summary>
        public ICommand AlignEihCaptureCommand { get; }
        /// <summary>EIH 间接对针：结算 TCO=H(u_feature)−R_n，p_tip 一并落库（EyeInHandIndirect）</summary>
        public ICommand SettleToolOffsetEihCommand { get; }
        #endregion

        /// <summary>标定向导构造函数</summary>
        /// <param name="owner">所属弹窗窗口</param>
        /// <param name="profile">传入已有标定方案；传null代表新建标定方案</param>
        /// <param name="spec">v2 任务规格（P1b StepDef 会话）；null=旧兼容四步模式。调用方保证 spec 与 profile 语义一致</param>
        public CalibrationWizardViewModel(Window owner, CalibrationProfile profile = null, CalibrationTaskSpec spec = null)
        {
            _owner = owner;
            _sessionSpec = spec;
            _devicePool = App.StationHostRuntime != null ? App.StationHostRuntime.DevicePool : null;
            _renderService = new HalconImageRenderService();
            _calibrationProfileRepository = StorageFactory.CreateCalibrationProfileRepository();
            CalibService = new CalibrationService();
            CalibrationImageDisplay = new ImageDisplayVm(_renderService);

            // 参数调节防抖：停止拖动 300ms 后用新参数重试提取当前帧
            _retryExtractTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _retryExtractTimer.Tick += (s, e) =>
            {
                _retryExtractTimer.Stop();
                // 后台线程重试提取：调参时同样能看到算子逐步上屏的识别过程
                RunSamplingOnBackground(ReapplyFeatureExtractionCore);
            };

            // 如果没有传入旧方案，创建全新标定方案
            TargetProfile = profile ?? new CalibrationProfile { Id = Guid.NewGuid().ToString("N"), Name = "新建标定方案", Type = CalibrationType.NinePointHandEye };
            // 几何字段写回联动刷新（按钮示教/手动输入均覆盖），Dispose 中退订
            TargetProfile.PropertyChanged += OnTargetProfileGeometryChanged;

            // v2 会话且无旧 profile：按 spec 物理量/路径确定旧 Type（供采集策略/Step3 模板选择器分流）
            if (_sessionSpec != null && profile == null)
            {
                var legacy = MapQuantityToLegacyType(_sessionSpec.Quantity, _sessionSpec.PrimaryPath);
                if (legacy.HasValue)
                {
                    TargetProfile.Type = legacy.Value;
                }
            }
            // 旧标定姿态快照：若重标同一方案，发布前体检会比对"上次 vs 本次"Z/U₀ 差异并提醒（Z 敏感）
            _openedCalibZ = TargetProfile.CalibZ;
            _openedCalibU0 = TargetProfile.CalibU0;

            // 【九点同工位共享】本方案还没有自己的九点矩阵 → 自动引用同工位已发布的那一份（2026-09-09）
            TryShareNinePointMatrix();

            // 根据标定类型选择对应的策略实现
            SelectStrategy(TargetProfile.Type);
            // 策略初始化标定点集合（初始化9个点/旋转采样点）
            _strategy.InitializePoints(this);

            // 绑定所有UI命令
            TriggerSampleCommand = new RelayCommand(_ =>
            {
                // ★ P2 e 单量会话：采样阶段本会话无九点平移段——"采集"动作导向旋转采样，杜绝误走位九点；
                //   特征预览阶段(LegacyPhase<=2)保持原策略预览语义，不拦截
                if (IsRotationSession && LegacyPhase >= 3)
                {
                    DispatchRotationSampleTrigger();
                    return;
                }
                _strategy.TriggerSample(this);
            });
            TriggerRotationSampleCommand = new RelayCommand(_ => DispatchRotationSampleTrigger());
            AutoRunAllCommand = new RelayCommand(_ =>
            {
                // ★ P2 e 单量会话：自动全采=只跑旋转采样（不碰九点列表）
                if (IsRotationSession)
                {
                    AutoRunRotationSession();
                    return;
                }
                _strategy.AutoRunAll(this);
            });
            RunCalibrationCommand = new RelayCommand(_ =>
            {
                // ★ P2 e 单量会话：拟合=旋转圆拟合/偏心结算（跳过九点 H 拟合，避免 0/9 弹窗）
                if (IsRotationSession)
                {
                    ExecuteRotationSessionFit();
                }
                else if (IsToolOffsetSession)
                {
                    // ★ 2026-09-08 硬化：t 会话无矩阵拟合——TCO/p_tip/R_n 三元组已在『间接对针』步结算落库。
                    //   禁止回落旧策略重跑矩阵拟合（按档案 Type 可能误触发九点/吸放 H 拟合，0 点报错或污染数据）。
                    AppendLog("[t 会话] 对针结算已完成（TCO/p_tip/R_n 三元组已写入档案），本会话不产出矩阵——无需拟合，可直接进入下一步并保存发布。");
                }
                else
                {
                    _strategy.ExecuteCalibration(this);
                }
                // P1b：拟合落定后刷新步骤门禁（PixelScale 策略内联计算不经过 ProcessCalibrationResult，须在此兜底）
                RaiseNextGate();
            });
            SaveResultCommand = new RelayCommand(_ => SaveResult());
            CopyTroubleshootCommand = new RelayCommand(_ => CopyTroubleshoot());
            SetPickBaseCommand = new RelayCommand(_ => SetCurrentAsPickBase());
            SetPhotoPoseCommand = new RelayCommand(_ => SetCurrentAsPhotoPose());
            TryFirstPlacementCommand = new RelayCommand(_ => TryFirstPlacement());
            PrevStepCommand = new RelayCommand(_ => PrevStep(), _ => CurrentStep > 0);
            NextStepCommand = new RelayCommand(_ => NextStep(), _ => CanAdvanceFrom(CurrentStep));
            SetCurrentAsBasePosCommand = new RelayCommand(_ => SetCurrentPositionAsBase());
            RecordToolOffsetOriginCommand = new RelayCommand(_ => RecordToolOffsetOrigin());
            AlignEihCaptureCommand = new RelayCommand(_ => CaptureAlignEihFrame());
            SettleToolOffsetEihCommand = new RelayCommand(_ => SettleToolOffsetEih());

            // 加载设备下拉列表、加载已经保存的畸变矫正方案
            LoadDevices();
            LoadAvailableDistortionProfiles();
            LoadTemplates();
            SyncTemplateFeatureDefaults();
            UpdateBindingInfo();
            ApplyTypePresets();
            NotifyStep3UiText();

            // 窗口关闭自动调用Dispose释放相机、运动卡资源
            if (_owner != null)
            {
                _owner.Closed += (s, e) => Dispose();
            }

            string nozzleTag = string.IsNullOrWhiteSpace(TargetProfile.NozzleKey) || TargetProfile.NozzleKey == "1"
                ? string.Empty
                : $" · 吸嘴通道 NozzleKey={TargetProfile.NozzleKey}（工具级标定，按吸嘴独立）";

            // P1b：装配步骤模板（v2=spec 引擎装配；旧=固定 4 步等价模板）→ 顶部胶囊/门禁数据源
            BuildWizardSteps();
            RefreshStepItemStates();
            if (IsSessionV2)
            {
                string stepNames = string.Join(" → ", _stepItems.Select(s => s.Ordinal + ":" + s.Title));
                AppendLog($"[StepDef v2] 任务 {_sessionSpec.SpecId ?? _sessionSpec.DisplayName ?? "?"} 模板装配 {_stepItems.Count} 步：{stepNames}");
                AppendLog($"[StepDef v2] 完成门禁已启用（每步满足条件才能进入下一步）；发布在末步『完成并保存』走 体检-红阻-黄确认 链");
            }
            AppendLog($"标定向导已就绪，标定类型: {TargetProfile.Type}{nozzleTag}");
        }

        /// <summary>
        /// 退出向导销毁资源：停止相机取流、注销事件、释放同步事件与图像上下文。
        /// ⚠ 2026-09-04 不再对相机/运动卡调用 Disconnect()：设备来自全局 DevicePool，
        ///   工位 worker 可能正在用同一相机/运动卡（向导与业务复用同一 worker/设备），
        ///   断开会把业务连接一并踢掉。连接生命周期交由设备池统一管理，向导只负责退订+停流。
        /// </summary>
        public void Dispose()
        {
            TargetProfile.PropertyChanged -= OnTargetProfileGeometryChanged; // 2026-09-06 退订几何字段联动

            // 1. 停止相机取流并注销帧接收/状态事件（保留设备连接，供工位 worker 继续使用）
            if (SelectedCameraDevice != null)
            {
                SelectedCameraDevice.FrameReceived -= OnCameraFrameReceived;
                SelectedCameraDevice.StateChanged -= OnCameraStateChanged;
                try
                {
                    SelectedCameraDevice.StopGrabbing();
                }
                catch (Exception ex)
                {
                    AppendLog($"[警告] 停止相机取流失败: {ex.Message}");
                }
            }

            // 2. 注销运动控制卡状态事件（不断开连接）
            if (SelectedMotionDevice != null)
            {
                SelectedMotionDevice.StateChanged -= OnMotionStateChanged;
            }

            // 3.释放同步等待句柄、图像渲染内存资源
            _frameArrivedEvent?.Dispose();
            _latestFrameContext?.Dispose();
        }

        private void SetCurrentPositionAsBase()
        {
            if (SelectedMotionDevice == null)
            {
                MessageBox.Show("请先连接并选择运动控制卡！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 确保连接
            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                MessageBox.Show("运动卡未连接: " + connectRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // 从底层运动卡读取当前绑定的 X/Y 轴位置
                var posXRes = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindXAxisIndex);
                var posYRes = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindYAxisIndex);

                if (posXRes.Success && posYRes.Success)
                {
                    TargetProfile.BasePosX = posXRes.Data;
                    TargetProfile.BasePosY = posYRes.Data;
                    AppendLog($"[基准设置成功] 绑定轴(X:{TargetProfile.BindXAxisIndex}, Y:{TargetProfile.BindYAxisIndex}) -> 基准坐标: X={TargetProfile.BasePosX:F3}, Y={TargetProfile.BasePosY:F3}");
                    MessageBox.Show($"基准位置设定成功！\nX: {TargetProfile.BasePosX:F3} mm\nY: {TargetProfile.BasePosY:F3} mm", "基准更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    // 写回后必须刷新两处 UI 状态，否则自检仍显示旧值(0,0 红)、Next 按钮门禁不重算：
                    // ① RefreshGeometryChecks：重建"配置自检"红绿清单（IsBasePosSet 刚被置位）；
                    // ② RaiseNextGate：Next 按钮 CanExecute=CanAdvanceFrom→EvalStepReady(DefineOrigin)
                    //    =IsBasePosSet，不 Raise 则 RelayCommand 不重新评估，按钮保持禁用。
                    RefreshGeometryChecks();
                    RaiseNextGate();
                }
                else
                {
                    AppendLog($"[警告] 获取轴当前位置失败: {posXRes.Message} / {posYRes.Message}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[错误] 读取轴位置异常: {ex.Message}");
            }
        }


        /// <summary>吸放式引导①：把当前机械手位置读入「初始吸取位」(X/Y/U)——先 Jog 到吸嘴正对工件吸点</summary>
        private void SetCurrentAsPickBase()
        {
            var vals = ReadCurrentPose(out string msg);
            if (vals == null)
            {
                AppendLog("[引导] 设为吸取位失败: " + msg);
                MessageBox.Show("设为吸取位失败：运动卡未连接或读位置失败（" + msg + "）", "引导", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TargetProfile.PickBaseX = vals.Value.X;
            TargetProfile.PickBaseY = vals.Value.Y;
            TargetProfile.PickBaseU = vals.Value.U;
            AppendLog($"[引导] 已设初始吸取位=({vals.Value.X:F3}, {vals.Value.Y:F3}, U{vals.Value.U:F1})（吸嘴应已正对工件吸点）");
            MessageBox.Show($"初始吸取位已设置：\nX {vals.Value.X:F3}  Y {vals.Value.Y:F3}  U {vals.Value.U:F1}°\n\n下一步：慢降 Z 试吸一次确认 PickZ（或先填下方 Z 高度后点『🧪 首件试放验证』）。",
                "引导", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshGeometryChecks();
            RaiseNextGate(); // 吸放式 Origin 门禁=Pick+Photo 双就绪，设完须重算 Next
        }

        /// <summary>吸放式引导②：把当前机械手位置读入「固定拍照位」(X/Y/Z/U)——回拍照位后应看清网格区</summary>
        private void SetCurrentAsPhotoPose()
        {
            var vals = ReadCurrentPose(out string msg);
            if (vals == null)
            {
                AppendLog("[引导] 设为拍照位失败: " + msg);
                MessageBox.Show("设为拍照位失败：运动卡未连接或读位置失败（" + msg + "）", "引导", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            TargetProfile.PhotoPoseX = vals.Value.X;
            TargetProfile.PhotoPoseY = vals.Value.Y;
            TargetProfile.PhotoPoseZ = vals.Value.Z;
            TargetProfile.PhotoPoseU = vals.Value.U;
            AppendLog($"[引导] 已设固定拍照位=({vals.Value.X:F3}, {vals.Value.Y:F3}, Z{vals.Value.Z:F1}, U{vals.Value.U:F1})");
            MessageBox.Show($"固定拍照位已设置：\nX {vals.Value.X:F3}  Y {vals.Value.Y:F3}  Z {vals.Value.Z:F1}  U {vals.Value.U:F1}°\n\n请回拍照位抓一张图，确认能看清网格区/工件后进入下一步。",
                "引导", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshGeometryChecks();
            RaiseNextGate(); // 吸放式 Origin 门禁=Pick+Photo 双就绪，设完须重算 Next
        }

        /// <summary>读当前四轴位置（X/Y=绑定轴、Z=BindZ、U=旋转轴）；失败返回 null</summary>
        private (double X, double Y, double Z, double U)? ReadCurrentPose(out string failMsg)
        {
            failMsg = "";
            if (SelectedMotionDevice == null)
            {
                failMsg = "未选择运动设备";
                return null;
            }
            var c = EnsureMotionConnected();
            if (!c.Success)
            {
                failMsg = c.Message;
                return null;
            }
            var x = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindXAxisIndex);
            var y = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindYAxisIndex);
            var z = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindZAxisIndex);
            var u = SelectedMotionDevice.GetFeedbackPosition(TargetProfile.BindRotationAxisIndex);
            if (!x.Success || !y.Success)
            {
                failMsg = x.Success ? y.Message : x.Message;
                return null;
            }
            return (x.Success ? x.Data : 0, y.Success ? y.Data : 0,
                    z.Success ? z.Data : TargetProfile.SafeZ, u.Success ? u.Data : 0);
        }

        /// <summary>吸放式引导③：首件试放验证——放一张工件到来料位→点此按钮：吸→放网格中心→回拍→识别自检</summary>
        private void TryFirstPlacement()
        {
            if (!EnsurePickPlaceGeometryReady())
            {
                return;
            }
            RunSamplingOnBackground(() =>
            {
                AppendLog("[引导] 首件试放验证开始：吸(来料位)→放(网格中心)→回拍照位→识别自检...");
                if (SelectedMotionDevice == null)
                {
                    AppendLog("[引导] 无运动设备：仅执行采图演示。");
                    return;
                }
                bool ok = PerformPickPlaceCycle(TargetProfile.BasePosX, TargetProfile.BasePosY,
                    TargetProfile.PickBaseU, "首件试放验证");
                if (!ok)
                {
                    AppendLog("[引导] ❌ 首件试放失败：检查 吸取位/拍照位/Z 高度/真空通道（见上方日志定位）。");
                    return;
                }
                var f = EstimateFeaturePoint(0, TargetProfile.BasePosX, TargetProfile.BasePosY, out bool reliable);
                if (f == null)
                {
                    f = AutoRecoverFeaturePoint(0, TargetProfile.BasePosX, TargetProfile.BasePosY, out reliable);
                }
                if (f != null)
                {
                    AppendLog($"[引导] ✅ 首件试放验证通过：工件已放到网格中心，回拍识别 像素({f.Value.PixelX:F1},{f.Value.PixelY:F1})——可以开始自动采样。");
                    RunOnUi(() => MessageBox.Show("✅ 首件试放验证通过：吸得住、放得正、特征朝上可见。\n可以点『下一步』进入采样（建议先『全自动』一次）。",
                        "引导", MessageBoxButton.OK, MessageBoxImage.Information));
                }
                else
                {
                    AppendLog("[引导] ⚠ 放置成功但回拍未识别到特征：请检查 特征面是否朝上/工件是否在拍照位视野/光源曝光。");
                    RunOnUi(() => MessageBox.Show("放置已成功，但回拍照位未识别到特征。\n请检查：特征面是否朝上、工件是否在网格中心视野内、模板/特征参数。",
                        "引导", MessageBoxButton.OK, MessageBoxImage.Warning));
                }
            });
        }

        #region 对外供策略调用的采集接口

        /// <summary>采样任务防重入标志（0=空闲 1=执行中）：防止按钮连点/自动+手动并发驱动运动轴</summary>
        private int _samplingBusy;

        /// <summary>
        /// 把动作调度到 UI 线程执行（后台采样线程调用时使用）。
        /// ObservableCollection 增删、弹窗等 UI 亲和操作必须回到 UI 线程。
        /// </summary>
        public void RunOnUi(Action action)
        {
            if (action == null) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                dispatcher.Invoke(action);
            }
        }

        /// <summary>
        /// 在后台线程执行采样动作（走位 / 采图 / Halcon 提取）。
        /// 🌟 关键线程模型：算子提取绝不能在 UI 线程同步执行 —— UI 线程被占住时
        /// WPF 不泵消息、合成器不刷新，场景"逐步上屏"每一步都看不见（只有最终结果一闪而出），
        /// 轴到位等待的轮询 Sleep 还会卡死整个界面。
        /// 后台线程跑算子 + 每步绘制同步 Invoke 到 UI 线程（RunOnUiSync），UI 线程保持空闲泵消息：
        /// ① 屏幕上算子每执行一步立刻可见（HDevelop 体验）；② VS 断点单步时同样逐步可见；
        /// ③ 走位等待期间界面不卡、拖动缩放正常。
        /// </summary>
        public void RunSamplingOnBackground(Action samplingAction)
        {
            if (samplingAction == null) return;
            if (Interlocked.CompareExchange(ref _samplingBusy, 1, 0) != 0)
            {
                AppendLog("[提示] 已有采样任务正在执行，请等待其完成或中止后再操作。");
                return;
            }
            Task.Run(() =>
            {
                try
                {
                    samplingAction();
                }
                catch (Exception ex)
                {
                    AppendLog($"[错误] 采样流程异常: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _samplingBusy, 0);
                }
            });
        }

        /// <summary>
        /// 采集特征图像（通用）：抓图显示 + 按第二步"特征配置"选择的特征类型
        /// （圆形 Mark 提取圆心 / 十字 Mark 形状匹配）实时提取特征，
        /// 并利用 CalibrationService 注入的视窗句柄把特征标记叠加绘制到图像上。
        /// </summary>
        public bool CaptureFeatureFrame(string actionName)
        {
            bool captured = CaptureAndDisplayFrame(actionName, true);
            if (captured)
            {
                // 后台线程提取：特征预览时算子每步绘制即时上屏（与采样共用防重入锁，避免场景互相覆盖）
                RunSamplingOnBackground(ExtractFeatureAndAnnotate);
            }
            return captured;
        }

        /// <summary>
        /// 按用户选择的特征类型，在最新一帧图像上提取特征，
        /// 由 CalibrationService 通过注入的视窗句柄（DisplayContext）把识别过程与结果绘制到视图窗口。
        /// </summary>
        private void ExtractFeatureAndAnnotate()
        {
            // 读 _latestFrameContext（相机回调线程已登记的最新帧），而非 ActiveImageContext：
            // ActiveImageContext 由 Dispatcher 队列异步刷新，抓图流程在 UI 线程同步 WaitOne 返回后
            // 队列尚未消费，此时读 ActiveImageContext 拿到的是旧帧，特征标记会画错位置。
            var renderImage = _latestFrameContext?.Image;
            if (renderImage == null || renderImage.NativeHandle == null)
            {
                AppendLog("[特征提取] 当前无可用图像帧，跳过特征提取。");
                return;
            }

            string featureName = TargetProfile.FeatureType == CalibrationFeatureType.CrossMark
                ? "十字 Mark (形状匹配)"
                : TargetProfile.FeatureType == CalibrationFeatureType.TemplateMatch
                    ? "模板匹配 (" + (CalibService.ExtractOptions.TemplateName ?? "未选模板") + ")"
                    : "圆形 Mark (提取圆心)";

            try
            {
                // CalibrationService 内部解包 IRenderImage.NativeHandle 为 HObject 并提取特征、叠加绘制到视窗
                EnsureTemplateSyncForExtraction();
                var res = CalibService.ExtractFeaturePreview(renderImage, TargetProfile.FeatureType);
                // 分数实时刷新（含失败诊断），让操作员调参时直观看到匹配分变化
                RefreshMatchScoreDisplay();
                if (res.Success)
                {
                    _featureVerifiedOnce = true;
                    RaiseNextGate();
                    AppendLog($"[特征提取] {featureName} 识别成功 → 中心 ({res.Data.PixelX:F1}, {res.Data.PixelY:F1})，已在视窗叠加标记。");
                    // 预览阶段（特征未进入采样前）只确认特征、不采集：明确提示，避免操作员误以为坐标已写入列表
                    if (LegacyPhase <= 2)
                    {
                        AppendLog("【提示】当前为特征预览模式，识别结果仅用于确认特征，不会写入标定点列表；请点击『下一步』进入数据采集步骤后采样。");
                    }
                }
                else
                {
                    AppendLog($"[特征提取] {featureName} 未识别: {res.Message}");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[特征提取] {featureName} 执行异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 特征提取参数调节后的实时重试入口（滑块 ValueChanged 调用）。
        /// 防抖 300ms：连续拖动只触发一次真正的重试，避免高频执行 Halcon 算子卡 UI。
        /// </summary>
        public void ReapplyFeatureExtraction()
        {
            if (_retryExtractTimer == null) return;
            // 无帧时不启动防抖：避免页面初始加载时滑块绑定赋值触发一次无意义的空重试
            if (_latestFrameContext?.Image == null) return;
            _retryExtractTimer.Stop();
            _retryExtractTimer.Start();
        }

        /// <summary>
        /// 用最新参数在当前帧上重新提取特征并叠加标注（所见即所得调参）。
        /// 只做预览验证，不写入标定点数据。
        /// </summary>
        private void ReapplyFeatureExtractionCore()
        {
            var renderImage = _latestFrameContext?.Image;
            if (renderImage == null || renderImage.NativeHandle == null)
            {
                AppendLog("[参数重试] 当前无图像帧，请先在特征配置步骤点击『抓图并提取特征』。");
                return;
            }

            string featureName = TargetProfile.FeatureType == CalibrationFeatureType.CrossMark
                ? "十字 Mark (形状匹配)"
                : TargetProfile.FeatureType == CalibrationFeatureType.TemplateMatch
                    ? "模板匹配 (" + (CalibService.ExtractOptions.TemplateName ?? "未选模板") + ")"
                    : "圆形 Mark (提取圆心)";

            try
            {
                EnsureTemplateSyncForExtraction();
                var res = CalibService.ExtractFeaturePreview(renderImage, TargetProfile.FeatureType);
                RefreshMatchScoreDisplay(); // 调参后实时刷新匹配分（所见即所得）
                if (res.Success)
                {
                    _featureVerifiedOnce = true;
                    RaiseNextGate();
                }
                AppendLog(res.Success
                    ? $"[参数重试] 新参数识别成功 → 中心 ({res.Data.PixelX:F1}, {res.Data.PixelY:F1})，已更新视窗叠加标记。"
                    : $"[参数重试] 新参数下仍未识别: {res.Message}");
            }
            catch (Exception ex)
            {
                AppendLog($"[参数重试] 执行异常: {ex.Message}");
            }
        }

        /// <summary>棋盘格标定采集一张棋盘格图片</summary>
        public bool CaptureCheckerboardSample()
        {
            return CaptureAndDisplayFrame("棋盘格采样", true);
        }

        /// <summary>像素当量标定采集图像</summary>
        public bool CapturePixelScaleSample()
        {
            return CaptureAndDisplayFrame("像素当量采样", true);
        }

        /// <summary>
        /// 采集下一个九点标定点（支持断点续采 / 失败重试 / 失败跳过）：
        /// 1.计算平台XY目标机械坐标
        /// 2.控制平台移动到目标位置（内部等待轴到位）
        /// 3.相机软触发采图
        /// 4.Halcon 提取特征点（参考位置 ROI 失败自动降级全图搜索）——提取过程中
        ///   每个算子的结果即时上屏呈现：绿 ROI 框 → 蓝阈值层 → 紫连通域 → 橙候选 →
        ///   青 XLD 轮廓 → 红拟合圆 → 黑描边绿十字醒目标记（识别中心）
        /// 5.填充 CalibrationPoints 的世界坐标、像素坐标，并把所有已采集点以小绿十字
        ///   阵列叠加到视图窗口——识别正确时 9 个十字应构成与走位网格一致的规则 3x3
        ///   阵列，误检点表现为十字重叠/缺失/阵列畸变，操作员目视即可发现哪个像素点取错
        /// 失败交互：此时视图窗口已绘制失败诊断场景（绿 ROI 框 / 蓝阈值层 / 红色提示文字），
        /// 弹窗三选 —— 是=原地重试当前点（调光源/特征参数后）、否=跳过该点继续后面的点（稍后可补采）、
        /// 取消=中止采集。跳过的点不写数据，拟合前校验会拦截；调整后再次点击采集可从断点补采。
        /// autoResolveFailures=true（全自动模式）：识别失败不弹窗，自动跳过并继续后面的点，
        /// 由 AutoCollect 流程在整轮结束后统一对缺失点做第二轮补采并汇总提示——单点失败
        /// 不再中断整个自动流程（2026-09-03 失败宽容化）。
        /// </summary>
        public bool CaptureNextCalibrationPoint(bool autoResolveFailures = false)
        {
            // 0. 校验基准位置已设置：基准坐标可能恰为 0（机台原点），不能以 BasePosX==0 判断。
            //    未设基准时第一点目标 = (-GridStepX, -GridStepY)，可能走出视野导致首点提取失败、坐标不回填。
            if (!TargetProfile.IsBasePosSet)
            {
                RunOnUi(() => MessageBox.Show("尚未设置标定基准位置！请返回步骤 1，点击『设当前轴位置为基准』，或在基准坐标框中手动输入 X/Y。",
                    "基准未设置", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }

            // 0.5 九点阶段参考半径隔离（与旋转阶段互不污染，2026-09-03 延伸杆适配）：
            //    本阶段尚无已采点时，若此前旋转阶段记录了杆端 Mark 参考半径，先重置，
            //    让九点特征（麻将 Mark / 标定板 Mark）重建自己的参考。已有已采点（断点续采）
            //    说明参考已属于本阶段特征，不重置。
            if (!CalibrationPoints.Any(p => p.IsCaptured))
            {
                CalibService.ResetMarkReference();
                AppendLog("[H九点·重标核对] 首点开始前请确认：① 标定工件(麻将)固定在生产实际取料位/治具位；② 步骤1『设当前轴位置为基准』= JOG 使工件特征成像在画面中央时的机械坐标（取景位）——勿要求吸嘴对准工件：相机与吸嘴偏心可达几十 mm，吸嘴对准时工件偏出画面、九点走位会拍照不到（基准=取景位，吸嘴对准关系由偏心 e/换算公式负责）；③ 网格步长内工件成像全程在视野内。本次起逐点回放 世界位↔像素，供换算语义核验。");
            }

            // 走位顺序数组（元素为点 Index 编号），由步骤1选择的走位方式决定：
            // 默认中心优先螺旋 {5,6,3,2,1,4,7,8,9}，可切换传统逐行扫描 {1..9}
            int[] order = NinePointTraverseOrder.GetOrder(TraverseMode);
            // 游标：记录顺序数组中已处理到的位置，天然支持断点续采
            int cursor = 0;
            // 0.6 九点网格可达性空走预检（2026-09-06，Epson SCARA 环带可达域适配）：
            //    背景：SCARA 可达域是内外半径环带——矩形 0~600 钳制与上位机静态计算都无法
            //    预判某点是否可达，控制器才是一锤定音的裁决者（4007 外圈够不着 / 4001 内圈
            //    收不拢或 U 姿态不可达 / 2997 Z 超软限）。若基准/步长组合把边角点推出环带，
            //    旧逻辑会在采图进行中弹"走位被拒"打断流程。
            //    本预检把"走位被拒"整体前置：凡目标坐标未验证过的待采点，先空走验证一遍；
            //    全部可达才进入采图循环（循环内 0 拒绝）。验证通过的坐标记入 _precheckedTargets，
            //    基准/步长/镜像/眼型改动会改变坐标 → 自动失效重验；配置不变时零额外运动
            //    （手动逐点采集只有第一点付一次空走成本）。
            var pendingPrecheck = new List<(int Index, double X, double Y)>();
            foreach (int idx in order)
            {
                if (_skippedPointIndices.Contains(idx)) continue;
                if (CalibrationPoints.Any(p => p.Index == idx && p.IsCaptured)) continue;
                if (!TryGetNinePointTarget(idx, out double px, out double py)) continue;
                if (_precheckedTargets.TryGetValue(idx, out var prev) &&
                    Math.Abs(prev.X - px) < 1e-6 && Math.Abs(prev.Y - py) < 1e-6)
                {
                    continue; // 同配置已试走通过，跳过
                }
                pendingPrecheck.Add((idx, px, py));
            }
            if (pendingPrecheck.Count > 0)
            {
                var failed = new List<string>();
                foreach (var (pidx, px, py) in pendingPrecheck)
                {
                    if (!MovePlatformTo(px, py, out string rejectReason))
                    {
                        failed.Add($"· 点{pidx} 目标 (X:{px:F1}, Y:{py:F1})：{DecodeEpsonRejectReason(rejectReason)}");
                        continue;
                    }
                    _precheckedTargets[pidx] = (px, py); // 该目标坐标试走通过 → 后续同坐标直接放行
                }
                if (failed.Count > 0)
                {
                    _reachPrecheckFailed = true;
                    string hint =
                        "九点网格可达性预检未通过——以下网格点被控制器拒绝走位，已在本轮采图前中止（已采数据保留）：\n\n" +
                        string.Join("\n", failed) + "\n\n" +
                        "【原因】Epson SCARA 可达域是内外半径环带而非矩形：\n" +
                        "· code 4007 超动作区域 → 多在外圈之外（机械臂够不着）；\n" +
                        "· code 4001 关节超脉冲范围 → 多进内圈空洞（两臂收不拢）或当前 U 姿态不可达；\n" +
                        "· code 2997 → Z 超出 RC+ 软限（现场实测约 -145.5mm）。\n\n" +
                        "【调整建议】\n" +
                        "· 基准位置尽量选在环带中腰——目视两臂夹角舒展（约 90°），远离折叠/伸直极限；\n" +
                        "· 或减小网格步长，让 9 点整体收拢在可达区内；\n" +
                        "· 基准请用『设当前轴位置为基准』（JOG 到达过的点必然可达），勿手输理论坐标。\n\n" +
                        "调整后重新点击采集即可——坐标变化的点会自动重验，已验证的点直接跳过。";
                    if (!autoResolveFailures)
                    {
                        RunOnUi(() => MessageBox.Show(hint, "走位可达性预检未通过", MessageBoxButton.OK, MessageBoxImage.Warning));
                    }
                    else
                    {
                        AppendLog("[可达性预检未通过] " + hint.Replace("\n", " "));
                    }
                    return false;
                }
                AppendLog($"[可达性预检] {pendingPrecheck.Count} 个网格点空走验证全部可达，进入采图循环（同配置后续不再重复验证）。");
            }

            // 手动模式补采解除开关（2026-09-03）：当剩余未采集点全部被跳过时，自动解除跳过
            // 标记进入补采（操作员回步骤2调参后回来点采集，期望直接补采失败点而不是弹『均已跳过』）。
            // 同一次调用只解除一次：解除后再失败且操作员再次选"跳过"，则正常收尾提示，避免死循环。
            bool manualRetryUnlocked = false;
            while (true)
            {
                // 从游标起按走位顺序找第一个"未采集且未被跳过"的点
                CalibrationPointModel targetPoint = null;
                int foundPos = -1;
                for (int k = cursor; k < order.Length; k++)
                {
                    if (_skippedPointIndices.Contains(order[k])) continue;
                    var p = CalibrationPoints.FirstOrDefault(x => x.Index == order[k] && !x.IsCaptured);
                    if (p != null)
                    {
                        targetPoint = p;
                        foundPos = k;
                        break;
                    }
                }
                if (targetPoint == null)
                {
                    bool allDone = CalibrationPoints.All(p => p.IsCaptured);
                    if (!allDone && !autoResolveFailures && !manualRetryUnlocked)
                    {
                        // 剩余未采集点全被跳过 → 手动模式自动解除跳过进入补采（无需重新弹窗拒绝）
                        var stuck = _skippedPointIndices
                            .Where(i => CalibrationPoints.Any(p => p.Index == i && !p.IsCaptured))
                            .ToList();
                        if (stuck.Count > 0)
                        {
                            manualRetryUnlocked = true;
                            foreach (var idx in stuck)
                            {
                                _skippedPointIndices.Remove(idx);
                            }
                            AppendLog($"[补采] 剩余未采集点均为此前跳过：解除 {stuck.Count} 个点（#{string.Join(", #", stuck)}），本次自动补采。");
                            cursor = 0; // 从头按走位顺序找第一个缺失点
                            continue;
                        }
                    }
                    if (autoResolveFailures)
                    {
                        // 自动模式：不弹窗，让 AutoCollect 流程统一收尾（已采点数校验/补采提示）
                        return false;
                    }
                    RunOnUi(() => MessageBox.Show(allDone
                        ? "所有标定点位均已完成采集！"
                        : "剩余未采集的点均已跳过，请调整光源 / 特征参数 / 步长后重新点击采集以补采跳过的点。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                    return false;
                }

                int index = targetPoint.Index;

                // 目标绝对坐标计算（含网格偏移/轴镜像/眼型符号，公式与可达性预检共用同一出处，
                // 详见 TryGetNinePointTarget 的注释）——2026-09-06 起抽取，防两处公式漂移。
                if (!TryGetNinePointTarget(index, out double posX, out double posY))
                {
                    RunOnUi(() => MessageBox.Show("尚未设置标定基准位置！请返回步骤 1，点击『设当前轴位置为基准』，或在基准坐标框中手动输入 X/Y。",
                        "基准未设置", MessageBoxButton.OK, MessageBoxImage.Warning));
                    return false;
                }

                // 1. 平台驱动指定工位轴就地九点走位（内部等待两轴到位后再返回）
                //    ★ 2026-09-03：走位被拒必须中止本轮采样并提示调整基准/步长——旧逻辑失败仍采图，
                //    采的是错误位置的图。2026-09-06：入口可达性预检已把"不可达"挡在采图循环之前，
                //    此处为配置在预检后被改动等异常情形的最后兜底——失败原因按控制器错误码解码展示。
                bool moved = MovePlatformTo(posX, posY, out string rejectReason);
                if (!moved)
                {
                    RunOnUi(() => MessageBox.Show(
                        $"第 {index} 点走位被控制器拒绝（目标 X:{posX:F1} Y:{posY:F1}）。\n\n" +
                        $"控制器反馈：{DecodeEpsonRejectReason(rejectReason)}\n\n" +
                        "（正常情况下入口可达性预检已拦截该情况；若仍出现，多为预检后基准/步长被改动，或机械限位发生变化。）\n\n" +
                        "请调整：\n· 基准位置（设到环带中腰的可达区，见上一条提示）；\n· 网格步长（过大会把边角点推出可达域或视野）。\n\n" +
                        "本次采集已中止，已采集的点保留；调整后重新点击采集即可续采。",
                        "走位失败", MessageBoxButton.OK, MessageBoxImage.Warning));
                    return false;
                }

                // 2. 相机抓图
                bool captured = CaptureAndDisplayFrame("九点采样", true);
                if (!captured)
                {
                    RunOnUi(() => MessageBox.Show($"第 {index} 点相机采图超时或失败！", "采图异常", MessageBoxButton.OK, MessageBoxImage.Error));
                    return false;
                }

                // 3. Halcon 提取特征点（失败时 EstimateFeaturePoint 内部已自动全图重试过一次）
                //    本方法在后台线程执行：UI 线程保持泵消息，提取过程中每个算子的绘制即时上屏可见
                //    期望位置由已采集点线性外推（传走位目标世界坐标），不再使用上一采集点的过期像素
                //    reliable=false：识别位置偏离预测过大（疑似取错特征），该点不参与后续预测
                var feature = EstimateFeaturePoint(index, posX, posY, out bool featureReliable);
                // 3.x 自动自愈（2026-09-03）：识别失败不立即弹窗——
                // 先做 ① 原曝光连拍重试 ② 曝光 ±30% 微调重拍（救"角落过暗/过曝→Mark 不在阈值层"的成像类失败）
                if (feature == null)
                {
                    feature = AutoRecoverFeaturePoint(index, posX, posY, out featureReliable);
                }
                if (feature == null)
                {
                    if (autoResolveFailures)
                    {
                        // 自动模式：不打断流程，记入跳过集合由 AutoCollect 收尾统一补采/提示
                        AppendLog($"[第 {index} 点] 自动模式：该点未识别到特征（已自动自愈重试），先跳过继续后续点（结束后自动补采）。");
                        _skippedPointIndices.Add(index);
                        cursor = foundPos + 1;
                        continue;
                    }
                    // 视图窗口此刻就是失败诊断画面：绿色 ROI 框、蓝色阈值层、橙色候选、红色提示文字。
                    // 操作员可对照画面判断是 Mark 出视野、ROI 截断还是成像质量问题，再选择处理方式。
                    MessageBoxResult choice = MessageBoxResult.Cancel;
                    RunOnUi(() => choice = MessageBox.Show(
                        $"第 {index} 点未识别到有效 Mark 特征（已自动全图重试仍失败）。\n\n" +
                        "请对照视图窗口的失败诊断画面检查：\n" +
                        "· Mark 是否移出视野 / 半出视野（减小步长，或将基准位置设到 Mark 视野居中处）\n" +
                        "· 绿色 ROI 框是否截断了 Mark（增大特征配置里的搜索半径）\n" +
                        "· 光源 / 曝光 / 对焦是否在视野边缘劣化\n\n" +
                        "【是】原地重试当前点（调整光源 / 特征参数后）\n" +
                        "【否】跳过该点，继续采集后面的点（稍后可补采）\n" +
                        "【取消】中止本次采集",
                        "Mark 未检出", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                    if (choice == MessageBoxResult.Yes)
                    {
                        AppendLog($"[第 {index} 点] 操作员选择原地重试。");
                        continue; // cursor 不变 → 重新走位采图提取当前点
                    }
                    if (choice == MessageBoxResult.No)
                    {
                        AppendLog($"[第 {index} 点] 操作员选择跳过该点，继续后续点（拟合前需补采该点）。");
                        _skippedPointIndices.Add(index);
                        cursor = foundPos + 1; // 推进游标，按走位顺序继续后面的点
                        continue;
                    }
                    AppendLog($"[第 {index} 点] 操作员中止采集流程，已完成 {CalibrationPoints.Count(p => p.IsCaptured)}/9 点。");
                    return false;
                }

                // 4. 识别结果呈现（不打断流程）：提取过程中每个算子的结果已即时上屏
                //    （绿 ROI → 蓝阈值 → 紫连通域 → 橙候选 → 青 XLD → 红拟合圆 →
                //    黑描边绿十字醒目标记），操作员对照视图窗口即可判断识别是否正确，
                //    无需弹框确认；发现误检可在采集完成后对该点重新采样覆盖。
                var capturedPixel = feature.Value;

                // 5. 回填真实机械绝对坐标与图像像素坐标，并标记该点已采集（UI 线程变更绑定属性）
                double capturedScore = CalibService?.LastMatchReport != null ? CalibService.LastMatchReport.Score : 0;
                RunOnUi(() =>
                {
                    targetPoint.WorldX = posX;
                    targetPoint.WorldY = posY;
                    targetPoint.PixelX = capturedPixel.PixelX;
                    targetPoint.PixelY = capturedPixel.PixelY;
                    targetPoint.IsCaptured = true;
                    targetPoint.IsReliable = featureReliable; // 疑似取错 → 不参与后续预测
                    targetPoint.MatchScore = capturedScore;   // 记录该点匹配质量分（表格展示）
                });
                _skippedPointIndices.Remove(index); // 若该点此前被跳过过，补采成功即销账

                // 5.x 标定姿态记录（2026-09-04）：网格中心(Index=5)成功采集时读取此刻轴位姿，
                //    记录拍照高度 Z 与旋转基准 U₀。HomMat 对拍照 Z 敏感，落档供业务端"姿态≠标定姿态→重标提醒"；
                //    平移采样阶段 U 轴不动，读到的 U 即标定基准（旋转补偿 R(U−U₀) 的 U₀ 参考）。
                if (!_poseRecorded && index == 5)
                {
                    var poseCenter = ReadCurrentPose(out string poseFail);
                    if (poseCenter.HasValue)
                    {
                        TargetProfile.CalibZ = poseCenter.Value.Z;
                        TargetProfile.CalibU0 = poseCenter.Value.U;
                        _poseRecorded = true;
                        AppendLog($"[姿态] 已记录本次标定姿态：拍照 Z={poseCenter.Value.Z:F2}mm，U 基准={poseCenter.Value.U:F2}°（网格中心#5 采样时实测）");
                    }
                    else
                    {
                        AppendLog($"[姿态] 记录标定姿态失败（读轴位姿：{poseFail}）——本次保存将不携带 Z/U₀。");
                    }
                }

                // 6. 已采集点标记叠加：把目前所有已成功采集的点以小绿十字阵列追加到
                //    当前场景（不清屏，叠加在刚完成的算子结果之上）。识别正确时十字
                //    阵列与走位网格一致（规则 3x3）；误检点表现为十字重叠（如两个不同
                //    世界坐标的点识别出几乎相同的像素位置）/ 缺失 / 阵列畸变，
                //    操作员目视即可发现哪个像素点取错。
                var capturedMarks = CalibrationPoints.Where(p => p.IsCaptured).ToList();
                CalibService.AppendCapturedMarks(
                    capturedMarks.Select(p => p.Index).ToArray(),
                    capturedMarks.Select(p => p.PixelX).ToArray(),
                    capturedMarks.Select(p => p.PixelY).ToArray());

                AppendLog($"[第 {index} 点] 轴(X:{TargetProfile.BindXAxisIndex}, Y:{TargetProfile.BindYAxisIndex}) 走位到 (X:{posX:F3}, Y:{posY:F3}) -> 识别像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
                // 换算语义核验明细（2026-09-06）：世界=走位命令位（麻将固定、机械手带相机走的网格），
                // 像素=麻将成像。若重标后此表仍是"世界≈BasePos±10 网格、麻将真身却远在网格外"，
                // 则矩阵输出 H(u) 语义=“成像 u 时机械手位”而非“工件真位”——校验台直走 H(u) 必然不到麻将。
                AppendLog($"[H点 #{index}] World=({posX:F3},{posY:F3}) Pixel=({targetPoint.PixelX:F1},{targetPoint.PixelY:F1}) Score={capturedScore:F3} 可靠={featureReliable} | Base=({TargetProfile.BasePosX:F3},{TargetProfile.BasePosY:F3}) 步长=({GridStepX:F1},{GridStepY:F1})");
                NotifySamplingUi();
                return true;
            }
        }

        /// <summary>
        /// 识别失败自动自愈（2026-09-03，九点采样稳定性兜底）：
        /// 尝试序列 = 原曝光连拍 2 帧 → 曝光 -30% 1 帧 → 曝光 +30% 1 帧（最多 4 次补采）。
        /// 全程保持当前机械位置不动，仅重拍+重提；任一次成功即采用并还原曝光。
        /// 适用：
        /// · 连拍重试 → 吸收软触发偶发丢帧 / 闪光灯未同步 / 瞬时反光 / 对焦微颤；
        /// · 曝光微调 → 救"视野边缘过暗/过曝 → Mark 灰度不在固定阈值层"的成像类失败
        ///   （多帧重拍解决不了同帧同结果，曝光变化才真正改变阈值分割输入）。
        /// 相机不支持曝光读写 / 设置失败时静默跳过曝光档，仅做多帧重试，绝不抛异常中断流程。
        /// </summary>
        private (double PixelX, double PixelY)? AutoRecoverFeaturePoint(int index, double worldX, double worldY, out bool reliable)
        {
            reliable = false;
            AppendLog($"[第 {index} 点] 首次识别失败 → 自动自愈（连拍重试 + 曝光 ±30% 微调）...");

            var cam = SelectedCameraDevice;
            double? baseExposure = null;
            if (cam != null)
            {
                try
                {
                    var expRes = cam.GetExposureTime() as Result<object>;
                    if (expRes != null && expRes.Success && expRes.Data != null)
                    {
                        baseExposure = Convert.ToDouble(expRes.Data);
                    }
                }
                catch { baseExposure = null; }
            }
            if (baseExposure.HasValue)
            {
                AppendLog($"[第 {index} 点] 当前曝光 {baseExposure.Value:F0}us，微调档可用。");
            }

            // 尝试序列：原曝光 ×2 → 0.7× → 1.3×
            double[] factors = { 1.0, 1.0, 0.7, 1.3 };
            try
            {
                for (int i = 0; i < factors.Length; i++)
                {
                    double factor = factors[i];
                    if (i >= 2)
                    {
                        if (!baseExposure.HasValue || cam == null)
                        {
                            continue; // 无曝光能力 → 跳过微调档，仅完成 2 次原曝光连拍
                        }
                        try
                        {
                            double target = Math.Max(10.0, Math.Min(baseExposure.Value * factor, 1000000.0));
                            var sr = cam.SetExposureTime(target);
                            if (sr == null || !sr.Success)
                            {
                                AppendLog($"[第 {index} 点] 曝光设 {(int)target}us 失败，跳过该档。");
                                continue;
                            }
                            AppendLog($"[第 {index} 点] 曝光微调 → {(int)target}us（系数 {factor:F0%}）。");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[第 {index} 点] 曝光设置异常，跳过该档: {ex.Message}");
                            continue;
                        }
                    }

                    bool shot = CaptureAndDisplayFrame($"九点采样-自愈{i + 1}", true);
                    if (!shot)
                    {
                        AppendLog($"[第 {index} 点] 自愈第 {i + 1} 次采图失败，继续尝试...");
                        continue;
                    }
                    var f = EstimateFeaturePoint(index, worldX, worldY, out bool r);
                    if (f.HasValue)
                    {
                        reliable = r;
                        AppendLog($"[第 {index} 点] 自愈成功（第 {i + 1} 次尝试，曝光系数 {factor:F0%}）。");
                        return f;
                    }
                }
                return null;
            }
            finally
            {
                // 还原曝光到进入自愈前的值
                if (baseExposure.HasValue && cam != null)
                {
                    try { cam.SetExposureTime(baseExposure.Value); }
                    catch { /* 还原失败不阻塞：下次采图由 Step2 曝光/增益重新设定 */ }
                }
            }
        }

        /// <summary>
        /// 旋转中心采样：移动旋转轴到指定角度，拍照采样标记点像素位置，
        /// 多个不同角度的像素点用来拟合求解旋转中心（偏心吸嘴绕 U 轴扫圆的圆心）。
        /// 2026-09-03 强化：
        /// · 识别失败先自动自愈（连拍重试 + 曝光 ±30% 微调，同九点策略）；
        /// · 仍失败 → autoResolveFailures=false（手动模式）弹窗三选：
        ///   重试该角度 / 跳过该角度（记入 _skippedRotationAngles） / 中止，
        ///   不再出现"点不采到、点击也没反应"的卡死式静默重试；
        /// · autoResolveFailures=true（自动模式）不弹窗，自动跳过由 AutoCollect 收尾补采。
        /// ★ 延伸杆方式适配（2026-09-03）：旋转标定要求"被观测 Mark 随 U 轴转动"。
        ///   若麻将是固定工台件（不会随 U 转），旋转阶段须用延伸杆端 Mark（装在 U 轴末端）。
        ///   此时旋转特征与九点特征（麻将 Mark）尺寸/半径不同——九点采样记录的参考 Mark 半径
        ///   会按 ±40% 过滤误杀杆端 Mark，故本阶段首个旋转点采样前强制重置参考半径，
        ///   以杆端 Mark 重建参考；并日志引导确认"视野内仅杆端 Mark 且随 U 轴转动"。
        /// </summary>
        /// <summary>
        /// 旋转采样阶段开始前的"观测位就位"确认（P1-2，2026-09-04）。
        /// 行业操作规范：旋转采样只转 R/U 轴、不移动 XY，因此开始前必须人工把『旋转特征』
        /// （延伸杆端 Mark / 吸嘴自身特征）移入相机视野——否则每个角度都在空视野漏识别，误以为标定失败。
        /// 吸放式场景特征随吸放移动、无需换杆，改为确认"网格中心放料区就绪"。
        /// 确认成功一次后本 profile 不再询问（补采/续采不重复打断）；取消则中止本次采样。
        /// 可能被后台采样线程调用 → 弹窗经 RunOnUi 同步转回 UI 线程并阻塞后台直到用户响应。
        /// </summary>
        private bool ConfirmRotationReady()
        {
            if (_rotationReadyPrompted)
            {
                return true;
            }

            string tip;
            if (IsPickPlaceProfile)
            {
                tip = "【吸放式旋转采样 · 开始前确认】\n\n"
                    + "旋转标定动作序列 = 吸住工件 → 转 U 角 → 放回网格中心 → 回拍照位 → 识别。\n\n"
                    + "开始前请确认：\n"
                    + " · 拍照位正下方『网格中心（基准点）』放料区已清空、可接收工件；\n"
                    + " · 已在步骤1 完成【🧪 首件试放验证】（吸取/放料/Z 序/真空链路可靠，工件特征清晰）。\n\n"
                    + "确认后才开始逐角度吸放采样（仅首个旋转点询问一次，后续角度/补采不再打断）。";
            }
            else
            {
                tip = "【旋转采样 · 观测位就位确认】\n\n"
                    + "九点平移已采完，旋转阶段将只转 R/U 轴、不再移动 XY。\n"
                    + "请现在用 Jog 把『旋转特征』移到相机视野中央附近——\n"
                    + " · 旋转特征 = 随轴转动的那一个（延伸杆端 Mark 或吸嘴自身特征）；\n"
                    + " · 若视野里只有固定工件/麻将，旋转标定无法成立（需要先装延伸杆并把麻将移开）。\n\n"
                    + "确认特征已就位后才开始逐角度采样（否则每个角度都在空视野漏识别）。";
            }
            if (TargetProfile.FeatureType == CalibrationFeatureType.TemplateMatch)
            {
                tip += $"\n\n· 模板匹配角度范围：{TargetProfile.TemplateAngleStart:F0}° ~ {TargetProfile.TemplateAngleEnd:F0}°\n"
                    + "  须覆盖旋转采样角度（默认 -45°/0°/+45° 3 点、跨度 90°——偏心大时 Mark 转大角度会出视野或超出模板范围失配，故默认收窄；现场若更大跨度仍清晰，可在表内扩角并同步确认模板角度范围够覆盖）。";
            }

            MessageBoxResult result = MessageBoxResult.Cancel;
            // 默认按钮取"否"：防止现场误按回车直接开转（设备动作不可轻易触发）
            RunOnUi(() => result = MessageBox.Show(tip, "旋转采样就位确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No));
            if (result == MessageBoxResult.Yes)
            {
                _rotationReadyPrompted = true;
                AppendLog("[旋转采样] 操作员已确认观测位就位，开始旋转采样。");
                return true;
            }
            AppendLog("[旋转采样] 操作员未确认观测位就位——本次旋转采样已取消（可随时重新点击采样，就位后再确认）。");
            return false;
        }

        /// <summary>
        /// 旋转采样（单步）：把旋转轴转到下一个未采集角度 → 拍照 → 提取旋转特征（延伸杆/吸嘴特征）。
        /// 失败自动自愈（连拍+曝光±30%）；手动模式弹三选（重试/跳过/中止）；自动模式跳过由 AutoCollect 收尾补采。
        /// </summary>
        public bool CaptureNextRotationPoint(bool autoResolveFailures = false)
        {
            // 旋转阶段参考半径隔离：尚未采到任何旋转点时，九点阶段记录的参考 Mark 半径
            // （麻将 Mark）不适用于延伸杆/吸嘴旋转特征——首点前重置，让本阶段重新记录。
            // 已有旋转点（补采续采）不重置，保持本阶段已建立的杆端 Mark 参考。
            if (!RotationPoints.Any(p => p.IsCaptured))
            {
                // P1-2 观测位就位确认：旋转首点前必须人工确认旋转特征已在视野内（只问一次）
                if (!ConfirmRotationReady())
                {
                    return false;
                }
                CalibService.ResetMarkReference();
                AppendLog("[旋转采样] 本阶段首个旋转点前已重置参考 Mark 半径——若使用延伸杆端 Mark（与麻将 Mark 不同尺寸），本阶段将以其重建参考。");
                AppendLog("[旋转采样] 前提核对：旋转特征（延伸杆 Mark / 吸嘴特征）必须随 U 轴转动。若视野内只有固定麻将等不动件，旋转标定无法成立，请先安装延伸杆并将杆端 Mark 移入视野、移开麻将。");
            }

            // 手动模式补采解锁开关（2026-09-03）：当剩余未采集角度全部被跳过时自动解除跳过
            // 进入补采（同九点 CaptureNextCalibrationPoint 策略）。同一次调用只解除一次。
            bool manualRetryUnlocked = false;

            // 目标 = 第一个未采集且未被跳过的角度
            var targetPoint = RotationPoints.FirstOrDefault(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg));
            if (targetPoint == null)
            {
                bool allDone = RotationPoints.All(p => p.IsCaptured);
                if (!allDone && !autoResolveFailures && !manualRetryUnlocked)
                {
                    var stuck = _skippedRotationAngles
                        .Where(a => RotationPoints.Any(p => p.AngleDeg == a && !p.IsCaptured))
                        .ToList();
                    if (stuck.Count > 0)
                    {
                        manualRetryUnlocked = true;
                        foreach (var a in stuck)
                        {
                            _skippedRotationAngles.Remove(a);
                        }
                        AppendLog($"[补采] 剩余旋转角度均为此前跳过：解除 {stuck.Count} 个角度（{string.Join("°, ", stuck)}°），本次自动补采。");
                        targetPoint = RotationPoints.FirstOrDefault(p => !p.IsCaptured);
                    }
                }
            }
            if (targetPoint == null)
            {
                bool allDone = RotationPoints.All(p => p.IsCaptured);
                if (autoResolveFailures)
                {
                    return false; // 自动模式：交给 AutoCollect 收尾
                }
                RunOnUi(() => MessageBox.Show(allDone
                    ? "旋转采样点已全部完成。"
                    : "剩余角度均已跳过。请调整光源 / 曝光 / 特征参数后重新点击【步进旋转采样】补采，或接受当前已采点数进行拟合（≥3 点）。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                return false;
            }

            double angle = targetPoint.AngleDeg;
            while (true)
            {
                // 旋转轴转到设定角度 → 拍帧 → 提取
                MoveRotationTo(angle);
                bool shot = CaptureAndDisplayFrame("旋转采样", true);
                var feature = shot ? EstimateRotationFeaturePoint(angle) : null;
                // 识别失败 → 自动自愈（连拍 + 曝光微调）
                if (feature == null)
                {
                    feature = AutoRecoverRotationFeature(angle);
                }

                if (feature.HasValue)
                {
                    var rot = feature.Value;
                    double rotScore = CalibService?.LastMatchReport != null ? CalibService.LastMatchReport.Score : 0;
                    // 实读 U（到位后回读，防指令角与实际角偏差——偏心结算按实转角才准）
                    double actU = angle;
                    var rotPose = ReadCurrentPose(out _);
                    if (rotPose.HasValue) { actU = rotPose.Value.U; }
                    RunOnUi(() =>
                    {
                        targetPoint.PixelX = rot.PixelX;
                        targetPoint.PixelY = rot.PixelY;
                        targetPoint.IsCaptured = true;
                        targetPoint.MatchScore = rotScore; // 记录该角度匹配质量分（表格展示）
                    });
                    _skippedRotationAngles.Remove(angle); // 若此前被跳过，补采成功即销账
                    AppendLog($"[e点 {angle:F1}°] 指令U={angle:F1} 实读U={actU:F2} Pixel=({targetPoint.PixelX:F1},{targetPoint.PixelY:F1}) Score={rotScore:F3}");
                    break;
                }

                // 自愈全部失败 → 弹窗三选（不再静默重复同一角度）；自动模式跳过收尾
                if (autoResolveFailures)
                {
                    AppendLog($"[旋转 {angle:F1}°] 自动模式：未识别到偏心 Mark（已自动自愈），先跳过该角度（结束后自动补采）。");
                    _skippedRotationAngles.Add(angle);
                    targetPoint = RotationPoints.FirstOrDefault(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg));
                    if (targetPoint == null)
                    {
                        return false; // 交给 AutoCollect 收尾
                    }
                    angle = targetPoint.AngleDeg;
                    continue;
                }

                MessageBoxResult choice = MessageBoxResult.Cancel;
                RunOnUi(() => choice = MessageBox.Show(
                    $"旋转 {angle:F1}° 采样未识别到偏心 Mark（已自动连拍 + 曝光 ±30% 重试）。\n\n" +
                    "请对照视图窗口检查：\n" +
                    "· 【物理前提】旋转特征（延伸杆端 Mark / 吸嘴特征）必须随 U 轴转动——麻将等固定件不动，无法标定旋转中心；\n" +
                    "· 偏心 Mark 是否随 U 轴转到相机视野内（偏心大时可先转回 0° 校核）；\n" +
                    "· 该角度下光照/反光变化是否让 Mark 对比度丢失；\n" +
                    "· 特征参数（阈值/圆度/面积）是否匹配当前画面。\n\n" +
                    "【是】原地重试该角度  【否】跳过该角度继续后续  【取消】中止本次采集",
                    "旋转采样失败", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                if (choice == MessageBoxResult.Yes)
                {
                    AppendLog($"[旋转 {angle:F1}°] 操作员选择原地重试。");
                    continue; // 同角度再来一轮（走位 → 拍 → 提取 → 自愈）
                }
                if (choice == MessageBoxResult.No)
                {
                    AppendLog($"[旋转 {angle:F1}°] 操作员选择跳过该角度（稍后可补采）。");
                    _skippedRotationAngles.Add(angle);
                    targetPoint = RotationPoints.FirstOrDefault(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg));
                    if (targetPoint == null)
                    {
                        RunOnUi(() => MessageBox.Show(
                            "剩余旋转角度均已跳过。可接受当前已采点数拟合（≥3 点），或调整参数后重新点击采样补采。",
                            "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                        return false;
                    }
                    angle = targetPoint.AngleDeg;
                    continue;
                }
                AppendLog($"[旋转 {angle:F1}°] 操作员中止旋转采样，已采 {RotationPoints.Count(p => p.IsCaptured)} 点。");
                return false;
            }

            AppendLog($"[旋转 {angle:F1}°] 采样像素 Px:{targetPoint.PixelX:F1}, Py:{targetPoint.PixelY:F1}");
            // 旋转 0° 采样点兜底：若网格中心点未记录到 U₀（如九点后手动转轴再进旋转段），补记此刻轴 U 读数作基准
            if (Math.Abs(angle) < 0.01 && !TargetProfile.CalibU0.HasValue)
            {
                var pose0 = ReadCurrentPose(out _);
                if (pose0.HasValue)
                {
                    TargetProfile.CalibU0 = pose0.Value.U;
                    AppendLog($"[姿态] 已补记旋转 U 基准 = {pose0.Value.U:F2}°（0° 旋转采样点实测）");
                }
            }
            // 采样完更新旋转中心计算结果UI（回 UI 线程）
            RunOnUi(UpdateRotationCenterResults);
            return true;
        }

        /// <summary>
        /// 旋转采样识别失败自动自愈（与九点 AutoRecoverFeaturePoint 同策略）：
        /// 原曝光连拍 2 帧 → 曝光 -30% / +30% 各 1 帧；任一次成功即还原曝光并返回。
        /// 相机不支持曝光读写时退化为纯多帧重试，绝不抛异常中断流程。
        /// </summary>
        private (double PixelX, double PixelY)? AutoRecoverRotationFeature(double angleDeg)
        {
            AppendLog($"[旋转 {angleDeg:F1}°] 首次识别失败 → 自动自愈（连拍重试 + 曝光 ±30% 微调）...");

            var cam = SelectedCameraDevice;
            double? baseExposure = null;
            if (cam != null)
            {
                try
                {
                    var expRes = cam.GetExposureTime() as Result<object>;
                    if (expRes != null && expRes.Success && expRes.Data != null)
                    {
                        baseExposure = Convert.ToDouble(expRes.Data);
                    }
                }
                catch { baseExposure = null; }
            }

            double[] factors = { 1.0, 1.0, 0.7, 1.3 };
            try
            {
                for (int i = 0; i < factors.Length; i++)
                {
                    double factor = factors[i];
                    if (i >= 2)
                    {
                        if (!baseExposure.HasValue || cam == null) continue;
                        try
                        {
                            double target = Math.Max(10.0, Math.Min(baseExposure.Value * factor, 1000000.0));
                            var sr = cam.SetExposureTime(target);
                            if (sr == null || !sr.Success)
                            {
                                AppendLog($"[旋转 {angleDeg:F1}°] 曝光设 {(int)target}us 失败，跳过该档。");
                                continue;
                            }
                            AppendLog($"[旋转 {angleDeg:F1}°] 曝光微调 → {(int)target}us（系数 {factor:F0%}）。");
                        }
                        catch (Exception ex)
                        {
                            AppendLog($"[旋转 {angleDeg:F1}°] 曝光设置异常，跳过该档: {ex.Message}");
                            continue;
                        }
                    }

                    bool shot = CaptureAndDisplayFrame($"旋转采样-自愈{i + 1}", true);
                    if (!shot)
                    {
                        AppendLog($"[旋转 {angleDeg:F1}°] 自愈第 {i + 1} 次采图失败，继续尝试...");
                        continue;
                    }
                    var f = EstimateRotationFeaturePoint(angleDeg);
                    if (f.HasValue)
                    {
                        AppendLog($"[旋转 {angleDeg:F1}°] 自愈成功（第 {i + 1} 次尝试，曝光系数 {factor:F0%}）。");
                        return f;
                    }
                }
                return null;
            }
            finally
            {
                if (baseExposure.HasValue && cam != null)
                {
                    try { cam.SetExposureTime(baseExposure.Value); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// 全自动采集全部九点标定点（2026-09-03 失败宽容化重构）：
        /// 第一轮：autoResolveFailures=true 顺序走完全部未采集点——识别失败自动跳过并记录，
        ///   不再弹窗打断整个流程（此前失败弹窗会让"一点失败 → 整局中止 → 重来"）。
        /// 第二轮：清空跳过集合，仅对真正缺失的点再自动补采一轮（吸收光源/曝光瞬态变化）。
        /// 收尾：仍有缺失 → 一次性汇总弹窗列出缺失点编号与操作建议（调整参数后重新点击采集补采，
        ///   或已采 ≥3 点时直接进入拟合计算——ExecuteCalibration 已放宽允许）。
        /// </summary>
        public void AutoCollectNinePointSamples()
        {
            _reachPrecheckFailed = false; // 每轮自动采集前重置预检失败标记
            // 第一轮：顺序采集（失败自动跳过，不中断）
            while (CalibrationPoints.Any(p => !p.IsCaptured))
            {
                if (!CaptureNextCalibrationPoint(autoResolveFailures: true))
                {
                    break; // 无点可采（全部完成 / 剩余均已跳过 / 可达性预检未通过）
                }
            }

            // ★ 可达性预检未通过：网格点被控制器拒绝走位（不可达）属配置问题，不是特征缺失——
            //    给专属提示并中止，避免收尾把 9 点全误报成"未取到特征"误导操作员调光源/曝光。
            if (_reachPrecheckFailed)
            {
                _reachPrecheckFailed = false;
                AppendLog("[自动采集] 因九点网格可达性预检未通过而中止（走位被控制器拒绝，非特征识别问题）。");
                RunOnUi(() => MessageBox.Show(
                    "自动采集中止：九点网格可达性预检未通过——有网格点被控制器拒绝走位（不在 SCARA 可达环带内）。\n\n" +
                    "调整建议：\n" +
                    "· 基准位置选到环带中腰（两臂夹角约 90° 舒展处，勿贴内外边界）；\n" +
                    "· 或减小网格步长让 9 点整体收拢在可达区内；\n" +
                    "· 基准请用『设当前轴位置为基准』（JOG 到达过的点必然可达）。\n\n" +
                    "调整后重新运行自动采集即可；已采点保留，会自动补采缺失点。",
                    "走位可达性预检未通过", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            // 第二轮：仅对缺失点补采（解除跳过标记；成功点不动）
            if (CalibrationPoints.Any(p => !p.IsCaptured))
            {
                int before = CalibrationPoints.Count(p => p.IsCaptured);
                AppendLog($"[自动采集] 第一轮完成，已采 {before}/9；对缺失点执行第二轮补采...");
                _skippedPointIndices.Clear();
                while (CalibrationPoints.Any(p => !p.IsCaptured))
                {
                    if (!CaptureNextCalibrationPoint(autoResolveFailures: true))
                    {
                        break;
                    }
                }
                int after = CalibrationPoints.Count(p => p.IsCaptured);
                if (after > before)
                {
                    AppendLog($"[自动采集] 第二轮补采成功 {after - before} 点，累计 {after}/9。");
                }
            }

            // 收尾汇总：仍缺失的点一次列出（不强制重来）
            var missing = CalibrationPoints.Where(p => !p.IsCaptured).Select(p => p.Index).ToList();
            if (missing.Count > 0)
            {
                int got = CalibrationPoints.Count(p => p.IsCaptured);
                string list = string.Join(", #", missing);
                AppendLog($"[自动采集] 结束：已采 {got}/9，缺失点 #{list}。");
                RunOnUi(() => MessageBox.Show(
                    $"自动采集结束：已采 {got}/9 点，仍有 {missing.Count} 点未取到特征（#{list}）。\n\n" +
                    "可能原因：该点 Mark 成像差 / 反光 / 半出视野。建议：\n" +
                    "· 点击【⚡ 轴步进并采单个点】逐点补采（可先调光源或特征参数）\n" +
                    "· 或直接点击【🧮 立即执行标定拟合计算】——已采点数 ≥3 时允许拟合（误差会反映在 RMS 中）",
                    "部分点位缺失", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else
            {
                AppendLog("[自动采集] 全部 9 点采集完成！");
            }
        }

        /// <summary>全自动采集全部旋转角度采样点（失败自动跳过 + 第二轮补采，同九点策略）</summary>
        public void AutoCollectRotationSamples()
        {
            // 第一轮：顺序采集（失败自动跳过，不中断）
            while (RotationPoints.Any(p => !p.IsCaptured))
            {
                if (!CaptureNextRotationPoint(autoResolveFailures: true))
                {
                    break;
                }
            }

            // 第二轮：仅对缺失角度补采
            if (RotationPoints.Any(p => !p.IsCaptured))
            {
                int before = RotationPoints.Count(p => p.IsCaptured);
                AppendLog($"[自动采集] 旋转采样第一轮完成，已采 {before} 点；对缺失角度执行第二轮补采...");
                _skippedRotationAngles.Clear();
                while (RotationPoints.Any(p => !p.IsCaptured))
                {
                    if (!CaptureNextRotationPoint(autoResolveFailures: true))
                    {
                        break;
                    }
                }
                int after = RotationPoints.Count(p => p.IsCaptured);
                if (after > before)
                {
                    AppendLog($"[自动采集] 旋转采样第二轮补采成功 {after - before} 点，累计 {after} 点。");
                }
            }

            // 收尾汇总（≥3 点即可拟合圆，缺失角度只是降低覆盖弧长）
            var missing = RotationPoints.Where(p => !p.IsCaptured).Select(p => p.AngleDeg).ToList();
            if (missing.Count > 0)
            {
                int got = RotationPoints.Count(p => p.IsCaptured);
                string list = string.Join("°, ", missing) + "°";
                AppendLog($"[自动采集] 旋转采样结束：已采 {got} 点，缺失角度 {list}。");
                RunOnUi(() => MessageBox.Show(
                    $"旋转采样结束：已采 {got} 点，{missing.Count} 个角度未取到特征（{list}）。\n\n" +
                    (got >= 3
                        ? "已采点数 ≥3，可进行旋转中心圆拟合；缺失角度越多覆盖弧长越短、圆心误差越大（拟合后看 RMS）。\n可调整光源/曝光后逐点补采，或直接拟合计算。"
                        : "已采点数 &lt;3，不足以拟合旋转中心圆，请调整参数后补采。"),
                    "旋转采样部分缺失", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else
            {
                AppendLog("[自动采集] 旋转采样全部角度采集完成！");
            }
        }

        /// <summary>
        /// 已采旋转角度的环向覆盖弧长（度）。把角度视为圆上点，覆盖弧长 = 360° − 最大空隙：
        ///   0/90/180/270 → 空隙 90°×4 → 覆盖 270°；0/30/60 → 空隙 30/30/300 → 覆盖 60°。
        /// 覆盖越接近 360° 圆心拟合越稳；&lt;90° 时圆心沿缺弧方向误差被放大数倍（2026-09-04 门控依据）。
        /// </summary>
        private static double RotationArcCoverageDeg(IReadOnlyList<RotationPointModel> sampled)
        {
            if (sampled == null || sampled.Count < 2)
            {
                return 0;
            }
            var ang = sampled.Select(p => ((p.AngleDeg % 360) + 360) % 360).OrderBy(a => a).ToList();
            double maxGap = ang[0] + 360.0 - ang[ang.Count - 1]; // 首尾跨越 0° 的空隙
            for (int i = 1; i < ang.Count; i++)
            {
                double gap = ang[i] - ang[i - 1];
                if (gap > maxGap) maxGap = gap;
            }
            return 360.0 - maxGap;
        }

        /// <summary>
        /// 更新旋转中心结果
        /// 逻辑：优先用最小二乘圆拟合（Kåsa 代数法）从多个角度采样点求圆心；
        /// 采样点不足 3 个或拟合失败时回退像素平均（简易版，仅作兜底提示）。
        /// 圆心 = 旋转轴（U/R）在相机图像平面的投影，即偏心吸嘴绕轴扫圆的中心。
        /// 若已有标定矩阵，则把像素圆心映射为机械世界坐标（供业务流旋转补偿）。
        /// ⚠ 注意：圆拟合对采样角度的覆盖范围有要求（≥3 个不共线角度、最好覆盖 >90°），
        ///   角度采样太窄时残差大、圆心不稳定——采样后看 RMS 残差可判断。
        /// </summary>
        public void UpdateRotationCenterResults()
        {
            var sampled = RotationPoints.Where(p => p.IsCaptured).ToList();
            if (sampled.Count == 0)
            {
                return;
            }

            // ⚠ 2026-09-04 角度覆盖门控：仅"点数≥3"不够——0/30/60 三点覆盖 60° 弧时，
            //   圆心沿缺弧方向误差被放大数倍。覆盖 <90° 时不写最终结果，明确提示补采。
            if (sampled.Count >= 3)
            {
                double coverage = RotationArcCoverageDeg(sampled);
                if (coverage < 90.0)
                {
                    string tip = $"⚠ 旋转角度覆盖仅 {coverage:F0}°（<90°）：圆心沿缺弧方向误差大，结果不可信。请把采样角扩到 ≥90°（默认 ±45° 即达标；受视野/模板限制扩不动时需更换观测特征或调整视野基准）后重算。";
                    AppendLog("[旋转结果] " + tip);
                    RotationCenterResult = tip;
                    RotationCenterPixelResult = "（未拟合：角度覆盖不足）";
                    RotationFitSummaryText = tip;
                    UpdateRotationPlot();
                    return;
                }
            }

            double centerPx;
            double centerPy;
            string methodInfo;

            // e 拟合输入回放（2026-09-06）：三点像素应绕同一圆心分布——若圆心距任意点很近
            // （≈0）说明三点近似共线/特征没随 U 转或转角没生效，e 值无意义。
            var ptTrace = string.Join("  ", sampled.Select(s => $"{s.AngleDeg:F1}°→({s.PixelX:F1},{s.PixelY:F1})"));
            AppendLog($"[e拟合] 输入点回放: {ptTrace}");

            if (sampled.Count >= 3)
            {
                var fitRes = Calib2DTool.FitCircleKasa(
                    sampled.Select(p => p.PixelX).ToArray(),
                    sampled.Select(p => p.PixelY).ToArray());
                if (fitRes.Success)
                {
                    centerPx = fitRes.Data.Cx;
                    centerPy = fitRes.Data.Cy;
                    methodInfo = $"最小二乘圆拟合 (半径 {fitRes.Data.Radius:F2}px, RMS {fitRes.Data.Rms:F2}px)";
                }
                else
                {
                    // 圆拟合失败（采样点近似共线/重合）→ 回退平均并提示
                    centerPx = sampled.Average(p => p.PixelX);
                    centerPy = sampled.Average(p => p.PixelY);
                    methodInfo = $"⚠ 圆拟合失败({fitRes.Message})，已回退像素平均";
                }
            }
            else
            {
                centerPx = sampled.Average(p => p.PixelX);
                centerPy = sampled.Average(p => p.PixelY);
                methodInfo = "像素平均（采样点不足 3 个）";
            }

            // 保存到标定方案模型
            TargetProfile.ToolCenterPx = centerPx;
            TargetProfile.ToolCenterPy = centerPy;

            // 偏心矢量（像素系，θ=0 方向）：R(-θ) 平均——特征(延伸杆/治具标记)随 U 刚体旋转，
            // p_θ − 圆心 = R(θ)·e，故 e = mean(R(-θ)·(p_θ − 圆心))。与吸放式同数学，观测式同样导出
            // ToolEcc（2026-09-06：e 会话收敛为观测式后放料补偿字段不缺失）
            double ex = 0, ey = 0;
            if (sampled.Count >= 2)
            {
                foreach (var s in sampled)
                {
                    double rad = s.AngleDeg * Math.PI / 180.0;
                    double dx = s.PixelX - centerPx;
                    double dy = s.PixelY - centerPy;
                    double c = Math.Cos(rad), sn = Math.Sin(rad);
                    ex += dx * c + dy * sn;
                    ey += -dx * sn + dy * c;
                }
                ex /= sampled.Count;
                ey /= sampled.Count;
            }
            TargetProfile.ToolEccPx = ex;
            TargetProfile.ToolEccPy = ey;
            TargetProfile.ToolEccAngleDeg = Math.Atan2(ey, ex) * 180.0 / Math.PI;
            AppendLog($"[e拟合] 圆心=({centerPx:F2},{centerPy:F2})px 偏心矢e=({ex:F2},{ey:F2})px 方向角={TargetProfile.ToolEccAngleDeg:F1}° ({methodInfo})");

            RotationCenterResult = $"Cx: {centerPx:F2}, Cy: {centerPy:F2}  [{methodInfo}]";
            RotationCenterPixelResult = $"Px: {centerPx:F2}, Py: {centerPy:F2}";
            RotationFitSummaryText = methodInfo + " → " + RotationCenterWorldResult;

            // 如果已经生成单应矩阵，把像素中心/偏心端点映射为平台机械世界坐标
            if (!string.IsNullOrWhiteSpace(OutputHomMatPath))
            {
                // ★ 2026-09-08 定案：O 必须【先把各采样点 H 映射 → 再在映射域定圆】。
                // Mark 在映射域才精确共圆（H(u_θ)=F0−r−R(θ)·m，半径=|m| 被丢弃）；像素域这些点落在
                // 【椭圆】上（H 非相似：x/y 当量差 / 安装剪切），用圆拟合椭圆点圆心必然偏。
                // 仿真实测（x/y 当量差 8%+剪切）：像素定圆再映射误差 8.46mm，先映射再定圆 0.000mm。
                bool worldFitOk = false;
                double wcx = 0, wcy = 0;
                if (sampled.Count >= 3)
                {
                    var wxs = new System.Collections.Generic.List<double>();
                    var wys = new System.Collections.Generic.List<double>();
                    bool mapAllOk = true;
                    foreach (var sp in sampled)
                    {
                        var mr = CalibService.MapPixelToWorld(OutputHomMatPath, sp.PixelX, sp.PixelY);
                        if (!mr.Success) { mapAllOk = false; break; }
                        wxs.Add(mr.Data.WorldX);
                        wys.Add(mr.Data.WorldY);
                    }
                    if (mapAllOk)
                    {
                        var wf = Calib2DTool.FitCircleKasa(wxs.ToArray(), wys.ToArray());
                        if (wf.Success)
                        {
                            wcx = wf.Data.Cx; wcy = wf.Data.Cy; worldFitOk = true;
                            AppendLog($"[e拟合] O=映射域定圆(半径{wf.Data.Radius:F3}mm 被丢弃) 圆心=({wcx:F3},{wcy:F3}) —— 严格法：先 H 映射各点再定圆");
                        }
                    }
                }
                var mapRes = CalibService.MapPixelToWorld(OutputHomMatPath, centerPx, centerPy);
                if (mapRes.Success)
                {
                    if (worldFitOk)
                    {
                        TargetProfile.ToolCenterWx = wcx;
                        TargetProfile.ToolCenterWy = wcy;
                    }
                    else
                    {
                        TargetProfile.ToolCenterWx = mapRes.Data.WorldX;
                        TargetProfile.ToolCenterWy = mapRes.Data.WorldY;
                        AppendLog($"[e拟合] ⚠ 映射域定圆不可用，已回退【像素定圆再映射】=({mapRes.Data.WorldX:F3},{mapRes.Data.WorldY:F3})：H 有各向异性时该值不可信，请检查矩阵与采样点。");
                    }
                    TargetProfile.HasRotationCenter = true; // 2026-09-08：消费式与对针求 e 都依赖 O
                    AppendLog($"[e拟合] 旋转中心 O(H域)=({mapRes.Data.WorldX:F3},{mapRes.Data.WorldY:F3}) —— 已落库（半径被丢弃，延伸杆长度/偏心不影响）");
                    RotationCenterWorldResult = $"Wx: {mapRes.Data.WorldX:F3}, Wy: {mapRes.Data.WorldY:F3}";
                    if (Math.Abs(ex) > 1e-9 || Math.Abs(ey) > 1e-9)
                    {
                        var eccTip = CalibService.MapPixelToWorld(OutputHomMatPath, centerPx + ex, centerPy + ey);
                        if (eccTip.Success)
                        {
                            TargetProfile.ToolEccWx = eccTip.Data.WorldX - mapRes.Data.WorldX;
                            TargetProfile.ToolEccWy = eccTip.Data.WorldY - mapRes.Data.WorldY;
                            RotationFitSummaryText = $"圆心({centerPx:F1},{centerPy:F1}) · 偏心 Px={ex:F1},Py={ey:F1} ({TargetProfile.ToolEccAngleDeg:F0}°) → " + RotationCenterWorldResult;
                            AppendLog($"[e拟合·世界系] 旋转中心World=({mapRes.Data.WorldX:F3},{mapRes.Data.WorldY:F3}) 偏心端World=({eccTip.Data.WorldX:F3},{eccTip.Data.WorldY:F3}) → ToolEccW=({TargetProfile.ToolEccWx:F3},{TargetProfile.ToolEccWy:F3})mm (经 {OutputHomMatPath} 映射)");
                        }
                    }
                }
            }
            UpdateRotationPlot();
        }
        #endregion

        #region 吸放式标定（PickPlaceHandEye，行业标准：吸住工件→放网格点→回拍照位拍照；旋转段吸住转 U→放料→回拍→圆拟合求偏心）

        /// <summary>加载全局模板列表（模板匹配特征 Step2 下拉数据源）</summary>
        private void LoadTemplates()
        {
            AvailableTemplates.Clear();
            try
            {
                var res = new TemplateManager().GetAll();
                if (res.Success)
                {
                    foreach (var t in res.Data)
                    {
                        AvailableTemplates.Add(t);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 加载模板列表失败: {ex.Message}");
            }
        }

        /// <summary>模板特征参数同步：Profile(持久化) ↔ ExtractOptions(提取算子) ↔ 下拉框</summary>
        private void SyncTemplateFeatureDefaults()
        {
            string name = TargetProfile.FeatureTemplateName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = CalibService.ExtractOptions.TemplateName;
            }
            if (!string.IsNullOrWhiteSpace(name))
            {
                TargetProfile.FeatureTemplateName = name;
                CalibService.ExtractOptions.TemplateName = name;
            }
            // 同步其它模板参数（Profile 为准，用户可再调 ExtractOptions）
            CalibService.ExtractOptions.TemplateMinScore = TargetProfile.TemplateMinScore;
            CalibService.ExtractOptions.TemplateAngleStart = TargetProfile.TemplateAngleStart;
            CalibService.ExtractOptions.TemplateAngleEnd = TargetProfile.TemplateAngleEnd;
            _templatePickerName = name;
            OnPropertyChanged(nameof(TemplatePickerName));
        }

        /// <summary>每次提取前把 Profile 模板选择同步进 ExtractOptions（模板特征安全守卫）</summary>
        private void EnsureTemplateSyncForExtraction()
        {
            if (TargetProfile.FeatureType == CalibrationFeatureType.TemplateMatch)
            {
                string name = TargetProfile.FeatureTemplateName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = TemplatePickerName;
                }
                if (!string.IsNullOrWhiteSpace(name))
                {
                    CalibService.ExtractOptions.TemplateName = name;
                }
            }
        }

        /// <summary>
        /// 小白化预设（2026-09-03）：凡能由「标定类型 + 设备品牌」推导的配置，一律代码自动带出，
        /// 不要求操作员理解。所有推导打印到日志，出问题时按日志核对即可。
        /// </summary>
        private void ApplyTypePresets()
        {
            // ★ 2026-09-06 会话化：旋转工具预设只属于真正执行旋转的会话（v2 e 段；旧壳混合档案）。
            //   v2 H 段会话即便档案为旧混合型也不自动开 HasToolOffset（本会话不转旋转轴；
            //   档案若原本已开则原样保留，此预设只加不开）。
            bool rotationType = IsSessionV2
                ? IsRotationSession
                : TargetProfile.Type == CalibrationType.HandEyeWithRotation
                  || TargetProfile.Type == CalibrationType.PickPlaceHandEye;

            // 旋转中心/偏心类标定必然带末端旋转工具 → 自动开启 TCP（无需手动勾选）
            if (rotationType && !TargetProfile.HasToolOffset)
            {
                TargetProfile.HasToolOffset = true;
                AppendLog("[预设] 标定类型含旋转中心/偏心（末端旋转工具）→ HasToolOffset 自动开启（无需手动勾选）。");
            }

            // 特征默认：吸放式/旋转采样对光照敏感优先模板；无模板名时提示但保留原选择
            if (IsPickPlaceProfile
                && TargetProfile.FeatureType == CalibrationFeatureType.CircleMark
                && string.IsNullOrEmpty(TargetProfile.FeatureTemplateName))
            {
                AppendLog("[预设] 吸放式标定默认仍用圆形 Mark（无需模板）；如工件无圆形特征可在步骤2切换 十字/模板匹配。");
            }

            // 每次进入向导打印一份"生效配置快照"，出问题直接看日志对照
            AppendLog($"[预设] 生效配置快照：类型={TargetProfile.Type} | 特征={TargetProfile.FeatureType}" +
                      (TargetProfile.FeatureType == CalibrationFeatureType.TemplateMatch ? "(" + (TargetProfile.FeatureTemplateName ?? "未选") + ")" : "") +
                      $" | EyeMode={TargetProfile.EyeMode} | 轴 X/Y/Rot/Z={TargetProfile.BindXAxisIndex}/{TargetProfile.BindYAxisIndex}/{TargetProfile.BindRotationAxisIndex}/{TargetProfile.BindZAxisIndex}" +
                      $" | HasToolOffset={TargetProfile.HasToolOffset} | 基准=({TargetProfile.BasePosX:F3},{TargetProfile.BasePosY:F3}) 步长=({GridStepX:F1},{GridStepY:F1}) | 方向反转 X/Y={TargetProfile.InvertXAxis}/{TargetProfile.InvertYAxis}");
            if (IsPickPlaceProfile)
            {
                AppendLog($"[预设] 吸放式几何：吸取位=({TargetProfile.PickBaseX:F3},{TargetProfile.PickBaseY:F3},U{TargetProfile.PickBaseU:F1}) 已设={TargetProfile.IsPickBaseSet}" +
                          $" | 拍照位=({TargetProfile.PhotoPoseX:F3},{TargetProfile.PhotoPoseY:F3},U{TargetProfile.PhotoPoseU:F1},Z{TargetProfile.PhotoPoseZ:F1}) 已设={TargetProfile.IsPhotoPoseSet}" +
                          $" | Z 高 Safe/Pick/Place={TargetProfile.SafeZ:F1}/{TargetProfile.PickZ:F1}/{TargetProfile.PlaceZ:F1} | 真空通道={TargetProfile.PickVacuumIoIndex} 检知={TargetProfile.PickDetectIoIndex}");
            }
            NotifySamplingUi();
            RefreshGeometryChecks();
        }

        /// <summary>旋转采样按钮分发：吸放式（会话级判定，e/H 均按 spec 路径）走吸放旋转采样，其余沿用原旋转采样</summary>
        private void DispatchRotationSampleTrigger()
        {
            if (IsPickPlaceProfile)
            {
                RunSamplingOnBackground(() => CaptureNextPickPlaceRotationPoint());
                return;
            }
            CaptureNextRotationPoint();
        }

        // ================= P2：e 单量会话执行（旋转混合档案拆出的 e 段） =================

        /// <summary>
        /// e 会话"自动全采"= 只跑旋转采样（不碰九点列表）。
        /// 吸放式路径（spec 判定，RotatePickPlace）走吸放旋转采样；走位式（RotateCameraView）走延伸杆旋转采样。
        /// </summary>
        private void AutoRunRotationSession()
        {
            AppendLog("[e 会话] 开始自动旋转采样（单量会话：不执行九点平移段）...");
            RunSamplingOnBackground(() =>
            {
                if (IsPickPlaceProfile)
                {
                    AutoCollectPickPlaceRotationSamples();
                }
                else
                {
                    AutoCollectRotationSamples();
                }
                RunOnUi(() =>
                {
                    AppendLog("[e 会话] 旋转采样结束，进入拟合计算...");
                    GoToComputeStep();
                });
            });
        }

        /// <summary>
        /// e 会话"拟合计算"= 旋转圆拟合/偏心结算（跳过九点 H 拟合，避免 e 会话 0/9 弹窗）。
        /// 复用既有结算：走位式 UpdateRotationCenterResults（圆心 + 有 H 时映射机械域中心）；
        /// 吸放式 UpdatePickPlaceRotationResults（圆心 + 偏心矢量 ToolEcc 一并导出）。
        /// e 会话不产新 HomMat：若档案已有矩阵文件则复用作像素→机械映射，否则结果仅落像素域并提示。
        /// </summary>
        private void ExecuteRotationSessionFit()
        {
            int nRot = RotationPoints.Count(p => p.IsCaptured);
            if (nRot < 3)
            {
                AppendLog($"[e 会话] 旋转采样仅 {nRot} 点，不足以拟合旋转中心圆（需 ≥3，建议覆盖 ≥90°）。");
                RunOnUi(() => MessageBox.Show($"旋转采样已采 {nRot} 点，至少需 3 点才能拟合旋转中心圆。\n请先补采更分散的角度（建议覆盖 ≥90°/180°）。",
                    "数据不足", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            EnsureRotationWorldMappingSource();
            if (IsPickPlaceProfile) // 会话级：e 吸放（RotatePickPlace）→ 吸放结算含 ToolEcc 导出；走位旋转 → 圆心结算
            {
                UpdatePickPlaceRotationResults();
                AppendLog($"[e 会话] 吸放式旋转结算完成：中心=({TargetProfile.ToolCenterWx:F3},{TargetProfile.ToolCenterWy:F3})mm " +
                          $"偏心 ToolEcc=({TargetProfile.ToolEccWx:F3},{TargetProfile.ToolEccWy:F3})mm 角={TargetProfile.ToolEccAngleDeg:F1}°");
            }
            else
            {
                UpdateRotationCenterResults();
                AppendLog($"[e 会话] 走位式旋转结算完成：中心=({TargetProfile.ToolCenterWx:F3},{TargetProfile.ToolCenterWy:F3})mm");
            }

            _fitComputedOnce = true;
            NotifySamplingUi();
            // P3：e 会话残差明细（逐角度偏差 + 圆拟合 RMS）随拟合刷新
            RefreshRotationResiduals();
            RaiseNextGate();
        }

        /// <summary>
        /// e 会话拟合前的世界映射源准备：OutputHomMatPath 为空时复用档案已发布矩阵文件，
        /// 使旋转中心/偏心从像素域映射到机械域（依赖 H 提供坐标系）。
        /// </summary>
        private void EnsureRotationWorldMappingSource()
        {
            if (!string.IsNullOrWhiteSpace(OutputHomMatPath)) return;
            if (TargetProfile != null
                && !string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                && File.Exists(TargetProfile.HomMatFilePath))
            {
                OutputHomMatPath = TargetProfile.HomMatFilePath;
                AppendLog($"[e 会话] 复用档案 H 矩阵做像素→机械映射：{TargetProfile.HomMatFilePath}");
            }
            else
            {
                AppendLog("[e 会话] 无可用 H 矩阵：旋转结果仅落像素域（ToolCenterPx/Py）。请先完成 H 段会话并发布矩阵后重跑本段，可得机械域旋转中心/偏心。");
            }
        }

        // ================= 吸放动作原语 =================

        /// <summary>XY 平动（不清 U、不做归零——吸放式 U 由调用方显式控制）</summary>
        private bool MoveXyRaw(double x, double y)
        {
            if (SelectedMotionDevice == null)
            {
                return true; // 无运动设备 = 纯抓图演示
            }
            var c = EnsureMotionConnected();
            if (!c.Success)
            {
                AppendLog("[吸放] 运动设备连接失败: " + c.Message);
                return false;
            }
            var rx = SelectedMotionDevice.MoveAbsolute(TargetProfile.BindXAxisIndex, (float)x, DefaultMoveSpeed);
            var ry = SelectedMotionDevice.MoveAbsolute(TargetProfile.BindYAxisIndex, (float)y, DefaultMoveSpeed);
            if ((rx != null && !rx.Success) || (ry != null && !ry.Success))
            {
                AppendLog($"[吸放走位被拒] (X:{x:F3}, Y:{y:F3})：{(rx != null && !rx.Success ? rx.Message : "")}{(ry != null && !ry.Success ? " / " + ry.Message : "")}");
                return false;
            }
            WaitAxesIdle(TargetProfile.BindXAxisIndex, TargetProfile.BindYAxisIndex);
            return true;
        }

        /// <summary>Z 轴升降到绝对高度</summary>
        private bool MoveZTo(double z)
        {
            if (SelectedMotionDevice == null)
            {
                return true;
            }
            var c = EnsureMotionConnected();
            if (!c.Success)
            {
                AppendLog("[吸放] 运动设备连接失败: " + c.Message);
                return false;
            }
            var rz = SelectedMotionDevice.MoveAbsolute(TargetProfile.BindZAxisIndex, (float)z, DefaultMoveSpeed);
            if (rz != null && !rz.Success)
            {
                AppendLog($"[吸放] Z 轴升降到 {z:F1} 被拒: {rz.Message}");
                return false;
            }
            WaitAxesIdle(TargetProfile.BindZAxisIndex);
            return true;
        }

        /// <summary>真空通断 + 稳定延时 +（可选）检知校验。无 IO 能力时仅模拟（纯视觉演示可用）。</summary>
        private bool ApplyVacuum(bool on)
        {
            var io = SelectedMotionDevice as IIoDevice;
            if (io == null)
            {
                AppendLog(on
                    ? "[吸放] 运动设备不具备 IIoDevice（无真空控制），仅模拟吸住（演示模式）。"
                    : "[吸放] 无真空控制，模拟释放（演示模式）。");
                return true;
            }
            var r = io.WriteDo(TargetProfile.PickVacuumIoIndex, on);
            if (!r.Success)
            {
                AppendLog($"[吸放] 真空{(on ? "开" : "关")} 通道{TargetProfile.PickVacuumIoIndex} 失败: {r.Message}");
                return false;
            }
            int delay = on ? TargetProfile.PickVacuumOnDelayMs : TargetProfile.PickVacuumOffDelayMs;
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }
            if (on && TargetProfile.PickDetectIoIndex >= 0)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1500)
                {
                    var d = io.ReadDi(TargetProfile.PickDetectIoIndex);
                    if (d.Success && d.Data)
                    {
                        AppendLog($"[吸放] 真空检知 IO{PickDetectIoIndexText()} 确认吸住工件。");
                        return true;
                    }
                    Thread.Sleep(60);
                }
                AppendLog($"[吸放] ⚠ 开启真空后 1.5s 未检知到工件（检知 IO{PickDetectIoIndexText()} off）——工件可能不在吸取位或检知配置有误，本次中止。");
                return false;
            }
            return true;
        }

        private string PickDetectIoIndexText() => TargetProfile.PickDetectIoIndex >= 0 ? TargetProfile.PickDetectIoIndex.ToString() : "未配置";

        /// <summary>校验吸放式几何输入（吸取位/拍照位）是否已设置</summary>
        private bool EnsurePickPlaceGeometryReady()
        {
            RefreshGeometryChecks();
            if (!TargetProfile.IsPickBaseSet || !TargetProfile.IsPhotoPoseSet)
            {
                string msg = "吸放式标定需要先在步骤1填写几何参数：\n"
                    + (TargetProfile.IsPickBaseSet ? "" : "· 初始吸取位 X/Y（吸嘴吸住工件的位置，可先手动走位对准工件后填入/设当前位置）\n")
                    + (TargetProfile.IsPhotoPoseSet ? "" : "· 固定拍照位 X/Y（每次回位拍照的姿态；需与吸取位同一可达区、Z 保证在焦）\n");
                AppendLog("[吸放] 几何参数未设置完整：" + msg.Replace("\n", " "));
                RunOnUi(() => MessageBox.Show(msg, "吸放式几何参数缺失", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }
            return true;
        }

        /// <summary>取工件当前位置（首轮=吸取位），并保证机器人先飞到该点上方再下探</summary>
        private (double X, double Y) GetPpLiftSpot()
        {
            if (!_ppLiftSet)
            {
                _ppLift = (TargetProfile.PickBaseX, TargetProfile.PickBaseY);
                _ppLiftSet = true;
            }
            return _ppLift;
        }

        /// <summary>一次完整吸放采样（九点的一个网格点）：吸→放→回拍照位→拍→提取→回填。</summary>
        public bool CaptureNextPickPlacePoint(bool autoResolveFailures = false)
        {
            if (!EnsurePickPlaceGeometryReady())
            {
                return false;
            }

            int[] order = NinePointTraverseOrder.GetOrder(TraverseMode);
            int cursor = 0;
            while (true)
            {
                CalibrationPointModel targetPoint = null;
                int foundPos = -1;
                for (int k = cursor; k < order.Length; k++)
                {
                    if (_skippedPointIndices.Contains(order[k]))
                    {
                        continue;
                    }
                    var p = CalibrationPoints.FirstOrDefault(x => x.Index == order[k] && !x.IsCaptured);
                    if (p != null)
                    {
                        targetPoint = p;
                        foundPos = k;
                        break;
                    }
                }
                if (targetPoint == null)
                {
                    if (autoResolveFailures)
                    {
                        return false;
                    }
                    bool allDone = CalibrationPoints.All(p => p.IsCaptured);
                    RunOnUi(() => MessageBox.Show(allDone
                        ? "所有标定点位均已完成采集！"
                        : "剩余未采集的点均已跳过，请调整光源 / 特征参数 / 几何参数后重新点击采集以补采。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                    return false;
                }

                int index = targetPoint.Index;
                int row = (index - 1) / 3;
                int col = (index - 1) % 3;
                double offsetX = (col - 1) * GridStepX;
                double offsetY = (row - 1) * GridStepY;
                if (TargetProfile.InvertXAxis)
                {
                    offsetX = -offsetX;
                }
                if (TargetProfile.InvertYAxis)
                {
                    offsetY = -offsetY;
                }
                // ⚠ 2026-09-04 吸放式放料点 = 放料命令坐标，与相机装法/EyeMode 无关：
                //   工件被吸嘴搬去 (posX, posY) 放下，世界坐标记录的就是这个工作台落点；
                //   此前沿用普通九点的 EyeInHand 反向偏移（Base−offset）——3×3 镜像网格
                //   的点集虽不变，但 index↔像素 的配对被反向（最左像素配到最右世界点），
                //   HomMat 符号整体翻转、业务落点全镜像。吸放式一律 Base + offset。
                //   若现场轴方向相反，用步骤1的 X/Y 方向反转开关（InvertX/YAxis）纠正。
                double posX = TargetProfile.BasePosX + offsetX;
                double posY = TargetProfile.BasePosY + offsetY;

                AppendLog($"[吸放 #{index}] 目标网格 (X:{posX:F3}, Y:{posY:F3}) —— 执行 吸住→放料→回拍照位→采图 序列...");
                if (!PerformPickPlaceCycle(posX, posY, TargetProfile.PickBaseU, "九点吸放采样"))
                {
                    if (autoResolveFailures)
                    {
                        AppendLog($"[吸放 #{index}] 吸放动作序列失败，自动模式先跳过该点。");
                        _skippedPointIndices.Add(index);
                        cursor = foundPos + 1;
                        continue;
                    }
                    RunOnUi(() => MessageBox.Show($"第 {index} 点吸放动作序列失败（检查几何参数/Z 高度/真空 IO 后重试）。", "吸放失败", MessageBoxButton.OK, MessageBoxImage.Warning));
                    return false;
                }

                var feature = EstimateFeaturePoint(index, posX, posY, out bool featureReliable);
                if (feature == null)
                {
                    feature = AutoRecoverFeaturePoint(index, posX, posY, out featureReliable);
                }
                if (feature == null)
                {
                    if (autoResolveFailures)
                    {
                        AppendLog($"[吸放 #{index}] 自动模式：该点未识别到特征（已自愈重试），先跳过。");
                        _skippedPointIndices.Add(index);
                        cursor = foundPos + 1;
                        continue;
                    }
                    MessageBoxResult choice = MessageBoxResult.Cancel;
                    RunOnUi(() => choice = MessageBox.Show(
                        $"第 {index} 点（网格 {posX:F1},{posY:F1}）拍照位未识别到特征（已自动重试）。\n\n" +
                        "请检查：\n· 工件是否确实放到了该网格点（真空是否释放干净/工件未滑移）；\n· 回拍照位后工件是否在相机视野内、特征朝上；\n· 光源/曝光/特征参数（模板是否匹配当前成像）。\n\n" +
                        "【是】重试该点  【否】跳过  【取消】中止",
                        "吸放采样失败", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                    if (choice == MessageBoxResult.Yes)
                    {
                        continue;
                    }
                    if (choice == MessageBoxResult.No)
                    {
                        _skippedPointIndices.Add(index);
                        cursor = foundPos + 1;
                        continue;
                    }
                    return false;
                }

                var pixel = feature.Value;
                double score = CalibService?.LastMatchReport != null ? CalibService.LastMatchReport.Score : 0;
                RunOnUi(() =>
                {
                    targetPoint.WorldX = posX;
                    targetPoint.WorldY = posY;
                    targetPoint.PixelX = pixel.PixelX;
                    targetPoint.PixelY = pixel.PixelY;
                    targetPoint.IsCaptured = true;
                    targetPoint.IsReliable = featureReliable;
                    targetPoint.MatchScore = score;
                });
                _skippedPointIndices.Remove(index);
                var marks = CalibrationPoints.Where(p => p.IsCaptured).ToList();
                CalibService.AppendCapturedMarks(marks.Select(p => p.Index).ToArray(), marks.Select(p => p.PixelX).ToArray(), marks.Select(p => p.PixelY).ToArray());
                AppendLog($"[吸放 #{index}] 放置({posX:F3},{posY:F3}) 回拍照位识别 像素({pixel.PixelX:F1},{pixel.PixelY:F1}) 分:{score:F0}");
                NotifySamplingUi();
                return true;
            }
        }

        /// <summary>吸放式旋转采样一次：吸住当前工件→转 U 到目标角→放到网格中心→回拍照位→拍→提取→回填。</summary>
        public bool CaptureNextPickPlaceRotationPoint(bool autoResolveFailures = false)
        {
            if (!EnsurePickPlaceGeometryReady())
            {
                return false;
            }
            // P1-2 观测就位确认：吸放式旋转首点前确认"网格中心放料区就绪"（只问一次）
            if (!RotationPoints.Any(p => p.IsCaptured) && !ConfirmRotationReady())
            {
                return false;
            }
            var target = RotationPoints.FirstOrDefault(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg));
            if (target == null)
            {
                if (autoResolveFailures)
                {
                    return false;
                }
                bool allDone = RotationPoints.All(p => p.IsCaptured);
                RunOnUi(() => MessageBox.Show(allDone
                    ? "旋转采样点已全部完成。"
                    : "剩余角度均已跳过：请调整参数后点击【步进旋转采样】补采，或已采 ≥3 点直接拟合。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information));
                return false;
            }

            double angle = target.AngleDeg;
            // 旋转采样统一放到网格中心（基准点），保证各角度成像区域一致
            double spotX = TargetProfile.BasePosX;
            double spotY = TargetProfile.BasePosY;
            AppendLog($"[吸放旋转 {angle:F1}°] 吸住→转U→放({spotX:F1},{spotY:F1})→回拍照位→采图...");
            if (!PerformPickPlaceCycle(spotX, spotY, angle, "吸放旋转采样"))
            {
                if (autoResolveFailures)
                {
                    AppendLog($"[吸放旋转 {angle:F1}°] 动作序列失败，跳过。");
                    _skippedRotationAngles.Add(angle);
                    return false;
                }
                RunOnUi(() => MessageBox.Show($"旋转 {angle:F1}° 吸放动作序列失败（检查几何/Z/真空后重试）。", "旋转采样失败", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }

            var feature = EstimateFeaturePoint(0, spotX, spotY, out bool reliable);
            if (feature == null)
            {
                feature = AutoRecoverFeaturePoint(0, spotX, spotY, out reliable);
            }
            if (feature == null)
            {
                if (autoResolveFailures)
                {
                    AppendLog($"[吸放旋转 {angle:F1}°] 未识别到特征，跳过（稍后补采或 ≥3 点直接拟合）。");
                    _skippedRotationAngles.Add(angle);
                    return false;
                }
                MessageBoxResult choice = MessageBoxResult.Cancel;
                RunOnUi(() => choice = MessageBox.Show(
                    $"旋转 {angle:F1}° 放置后拍照位未识别到特征。\n\n请检查工件是否随 U 转动后仍可见（特征面朝上/模板角度范围），或光照变化。\n\n" +
                    "【是】重试  【否】跳过  【取消】中止",
                    "旋转采样失败", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning));
                if (choice == MessageBoxResult.Yes)
                {
                    return CaptureNextPickPlaceRotationPoint(autoResolveFailures);
                }
                if (choice == MessageBoxResult.No)
                {
                    _skippedRotationAngles.Add(angle);
                    return false;
                }
                return false;
            }

            var px = feature.Value;
            double score = CalibService?.LastMatchReport != null ? CalibService.LastMatchReport.Score : 0;
            RunOnUi(() =>
            {
                target.PixelX = px.PixelX;
                target.PixelY = px.PixelY;
                target.IsCaptured = true;
                target.MatchScore = score;
            });
            _skippedRotationAngles.Remove(angle);
            AppendLog($"[吸放旋转 {angle:F1}°] 像素({px.PixelX:F1},{px.PixelY:F1}) 分:{score:F0}");
            NotifySamplingUi();
            return true;
        }

        /// <summary>
        /// 吸放动作核心序列：吸取当前位置工件 → 平移到 (x,y) → 放到 PlaceZ → 抬 SafeZ →
        /// 回拍照位（XY/U/Z）→ 采一帧图（供后续 EstimateFeaturePoint 从最新帧提取）。
        /// 成功后工件位于 (x,y)，_ppLift 同步更新。
        /// </summary>
        private bool PerformPickPlaceCycle(double placeX, double placeY, double holdU, string actionName)
        {
            var spot = GetPpLiftSpot();

            // 1. 飞到工件处吸取（U 先转到 PickBaseU，保证吸持方向一致）
            if (SelectedMotionDevice != null)
            {
                if (!MoveXyRaw(spot.X, spot.Y))
                {
                    return false;
                }
                MoveRotationTo(TargetProfile.PickBaseU);
                if (!MoveZTo(TargetProfile.PickZ) || !ApplyVacuum(true))
                {
                    return false;
                }
                if (!MoveZTo(TargetProfile.SafeZ))
                {
                    return false;
                }

                // 2. 平移到目标网格点，放料
                if (!MoveXyRaw(placeX, placeY))
                {
                    return false;
                }
                if (SelectedMotionDevice != null && Math.Abs(holdU - TargetProfile.PickBaseU) > 0.01)
                {
                    MoveRotationTo(holdU); // 放料前转到位（旋转采样段使用）
                }
                if (!MoveZTo(TargetProfile.PlaceZ) || !ApplyVacuum(false))
                {
                    return false;
                }
                if (!MoveZTo(TargetProfile.SafeZ))
                {
                    return false;
                }
                _ppLift = (placeX, placeY);

                // 3. 回固定拍照位（XY → U(PhotoPoseU) → Z(PhotoPoseZ)）
                if (!MoveXyRaw(TargetProfile.PhotoPoseX, TargetProfile.PhotoPoseY))
                {
                    return false;
                }
                MoveRotationTo(TargetProfile.PhotoPoseU);
                if (!MoveZTo(TargetProfile.PhotoPoseZ))
                {
                    return false;
                }
            }

            bool cap = CaptureAndDisplayFrame(actionName, true);
            if (!cap)
            {
                AppendLog($"[吸放] {actionName} 采图超时/失败。");
                return false;
            }
            return true;
        }

        /// <summary>吸放式九点全自动（保留已采点；首轮+第二轮补采+汇总）</summary>
        public void AutoCollectPickPlaceNinePointSamples()
        {
            while (CalibrationPoints.Any(p => !p.IsCaptured && !_skippedPointIndices.Contains(p.Index)))
            {
                if (!CaptureNextPickPlacePoint(true))
                {
                    break;
                }
            }
            if (CalibrationPoints.Any(p => !p.IsCaptured) && _skippedPointIndices.Count > 0)
            {
                AppendLog("[自动采集·吸放] 第二轮补采：重试此前跳过的点…");
                _skippedPointIndices.Clear();
                while (CalibrationPoints.Any(p => !p.IsCaptured && !_skippedPointIndices.Contains(p.Index)))
                {
                    if (!CaptureNextPickPlacePoint(true))
                    {
                        break;
                    }
                }
            }
            var missing = CalibrationPoints.Where(p => !p.IsCaptured).Select(p => p.Index).ToList();
            int got = CalibrationPoints.Count(p => p.IsCaptured);
            if (missing.Count > 0)
            {
                AppendLog($"[自动采集·吸放] 九点结束：已采 {got}/9，缺失 #{string.Join(", #", missing)}。");
                RunOnUi(() => MessageBox.Show(
                    $"九点吸放采样结束：已采 {got}/9 点。\n缺失: #{string.Join(", #", missing)}\n\n" +
                    (got >= 6 ? "已采 ≥6，可继续拟合（缺失点多会降精度）。" : "不足 6 点无法可靠拟合，请调整后补采。"),
                    "九点采样部分缺失", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else
            {
                AppendLog("[自动采集·吸放] 九点全部采集完成！");
            }
        }

        /// <summary>吸放式旋转全自动（首轮+第二轮补采）</summary>
        public void AutoCollectPickPlaceRotationSamples()
        {
            while (RotationPoints.Any(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg)))
            {
                if (!CaptureNextPickPlaceRotationPoint(true))
                {
                    break;
                }
            }
            if (RotationPoints.Any(p => !p.IsCaptured) && _skippedRotationAngles.Count > 0)
            {
                AppendLog("[自动采集·吸放] 旋转第二轮补采…");
                _skippedRotationAngles.Clear();
                while (RotationPoints.Any(p => !p.IsCaptured && !_skippedRotationAngles.Contains(p.AngleDeg)))
                {
                    if (!CaptureNextPickPlaceRotationPoint(true))
                    {
                        break;
                    }
                }
            }
            var missing = RotationPoints.Where(p => !p.IsCaptured).Select(p => p.AngleDeg).ToList();
            int got = RotationPoints.Count(p => p.IsCaptured);
            if (missing.Count > 0)
            {
                AppendLog($"[自动采集·吸放] 旋转结束：已采 {got} 点，缺失 {string.Join("°, ", missing)}°。");
                RunOnUi(() => MessageBox.Show(
                    $"旋转采样结束：已采 {got} 点。\n缺失角度: {string.Join("°, ", missing)}°\n\n" +
                    (got >= 3 ? "已采 ≥3，可拟合旋转中心与偏心。" : "不足 3 点，请补采。"),
                    "旋转采样部分缺失", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else
            {
                AppendLog("[自动采集·吸放] 旋转全部角度采集完成！");
            }
        }

        /// <summary>
        /// 吸放式旋转结果回填：圆拟合旋转中心（像素+世界）+ 导出工具偏心矢量 ToolEcc。
        /// 偏心矢量数学：工件特征绕放置点（网格中心 U 轴投影）扫圆，第 θ 个采样点相对圆心
        /// 的矢量 = R(θ)·ToolEcc；故 ToolEcc(像素) = R(-θ)·(p_θ − 圆心)，对全部角度平均抵消噪声。
        /// </summary>
        public void UpdatePickPlaceRotationResults()
        {
            var sampled = RotationPoints.Where(p => p.IsCaptured).ToList();
            if (sampled.Count == 0)
            {
                return;
            }

            // ⚠ 2026-09-04 角度覆盖门控（同 UpdateRotationCenterResults）：<90° 时不写最终圆心/偏心
            if (sampled.Count >= 3)
            {
                double coverage = RotationArcCoverageDeg(sampled);
                if (coverage < 90.0)
                {
                    string tip = $"⚠ 旋转角度覆盖仅 {coverage:F0}°（<90°）：圆心/偏心沿缺弧方向误差大，结果不可信。请把采样角扩到 ≥90°（默认 ±45° 即达标；受视野/模板限制扩不动时需更换观测特征或调整视野基准）后重算。";
                    AppendLog("[吸放旋转] " + tip);
                    RotationCenterResult = tip;
                    RotationCenterPixelResult = "（未拟合：角度覆盖不足）";
                    RotationFitSummaryText = tip;
                    NotifySamplingUi();
                    UpdateRotationPlot();
                    return;
                }
            }

            double centerPx;
            double centerPy;
            if (sampled.Count >= 3)
            {
                var fit = Calib2DTool.FitCircleKasa(sampled.Select(p => p.PixelX).ToArray(), sampled.Select(p => p.PixelY).ToArray());
                if (fit.Success)
                {
                    centerPx = fit.Data.Cx;
                    centerPy = fit.Data.Cy;
                    AppendLog($"[吸放旋转] 圆拟合 圆心({centerPx:F2},{centerPy:F2}) 半径 {fit.Data.Radius:F2}px RMS {fit.Data.Rms:F2}px");
                }
                else
                {
                    centerPx = sampled.Average(p => p.PixelX);
                    centerPy = sampled.Average(p => p.PixelY);
                    AppendLog($"[吸放旋转] 圆拟合失败({fit.Message})，回退像素平均。");
                }
            }
            else
            {
                centerPx = sampled.Average(p => p.PixelX);
                centerPy = sampled.Average(p => p.PixelY);
            }

            TargetProfile.ToolCenterPx = centerPx;
            TargetProfile.ToolCenterPy = centerPy;
            RotationCenterResult = $"Cx: {centerPx:F2}, Cy: {centerPy:F2}（吸放式·放置点圆心）";
            RotationCenterPixelResult = $"Px: {centerPx:F2}, Py: {centerPy:F2}";

            // 偏心矢量（像素系，θ=0 方向）：R(-θ) 平均
            double ex = 0, ey = 0;
            foreach (var s in sampled)
            {
                double rad = s.AngleDeg * Math.PI / 180.0;
                double dx = s.PixelX - centerPx;
                double dy = s.PixelY - centerPy;
                double c = Math.Cos(rad), sn = Math.Sin(rad);
                ex += dx * c + dy * sn;
                ey += -dx * sn + dy * c;
            }
            ex /= sampled.Count;
            ey /= sampled.Count;
            TargetProfile.ToolEccPx = ex;
            TargetProfile.ToolEccPy = ey;
            TargetProfile.ToolEccAngleDeg = Math.Atan2(ey, ex) * 180.0 / Math.PI;

            if (!string.IsNullOrWhiteSpace(OutputHomMatPath))
            {
                var centerMap = CalibService.MapPixelToWorld(OutputHomMatPath, centerPx, centerPy);
                var eccTipMap = CalibService.MapPixelToWorld(OutputHomMatPath, centerPx + ex, centerPy + ey);
                if (centerMap.Success && eccTipMap.Success)
                {
                    TargetProfile.ToolCenterWx = centerMap.Data.WorldX;
                    TargetProfile.ToolCenterWy = centerMap.Data.WorldY;
                    TargetProfile.ToolEccWx = eccTipMap.Data.WorldX - centerMap.Data.WorldX;
                    TargetProfile.ToolEccWy = eccTipMap.Data.WorldY - centerMap.Data.WorldY;
                    RotationCenterWorldResult = $"Wx: {centerMap.Data.WorldX:F3}, Wy: {centerMap.Data.WorldY:F3}";
                    AppendLog($"[吸放旋转] 旋转中心世界 ({centerMap.Data.WorldX:F3},{centerMap.Data.WorldY:F3})；工具偏心 ToolEcc(Wx={TargetProfile.ToolEccWx:F3}, Wy={TargetProfile.ToolEccWy:F3}, {TargetProfile.ToolEccAngleDeg:F1}°) | Px={ex:F2}, Py={ey:F2}");
                }
            }
            else
            {
                AppendLog($"[吸放旋转] 工具偏心(像素 θ=0 方向): Px={ex:F2}, Py={ey:F2}，方向角 {TargetProfile.ToolEccAngleDeg:F1}°（待矩阵生成后映射世界）。");
            }
            RotationFitSummaryText = $"圆心({centerPx:F1},{centerPy:F1}) · 偏心 Px={ex:F1},Py={ey:F1} ({TargetProfile.ToolEccAngleDeg:F0}°)";
            NotifySamplingUi();
            UpdateRotationPlot();
        }

        #endregion

        #region 硬件设备加载、切换设备事件

        /// <summary>从全局设备池加载相机、运动卡，填充下拉列表；自动匹配上次绑定的设备</summary>
        private void LoadDevices()
        {
            CameraDeviceList.Clear();
            MotionDeviceList.Clear();
            if (_devicePool == null)
            {
                AppendLog("[警告] DevicePool 尚未初始化，无法加载硬件列表。");
                return;
            }

            // 筛选全部相机、运动卡设备
            foreach (var camera in _devicePool.GetAllDevices().OfType<ICamera>())
            {
                CameraDeviceList.Add(camera);
            }
            foreach (var motion in _devicePool.GetAllDevices().OfType<IMotionCard>())
            {
                MotionDeviceList.Add(motion);
            }

            // 优先选中标定方案历史绑定的相机、运动卡；没有就取列表第一个
            SelectedCameraDevice = CameraDeviceList.FirstOrDefault(c => IsBoundDevice(c, TargetProfile.CameraId)) ?? CameraDeviceList.FirstOrDefault();
            SelectedMotionDevice = MotionDeviceList.FirstOrDefault(m => IsBoundDevice(m, TargetProfile.AxisId)) ?? MotionDeviceList.FirstOrDefault();
        }

        /// <summary>判断设备是否匹配标定方案保存的设备ID；兼容DeviceKey / DeviceId / DeviceName三种匹配</summary>
        private static bool IsBoundDevice(IDevice device, string profileId)
        {
            if (device == null || string.IsNullOrWhiteSpace(profileId))
            {
                return false;
            }
            return string.Equals(device.DeviceKey, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceId, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceName, profileId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>加载已经保存的镜头畸变标定方案，手眼标定可以前置套用畸变矫正</summary>
        private void LoadAvailableDistortionProfiles()
        {
            AvailableDistortionProfiles.Clear();
            AvailableDistortionProfiles.Add(new CalibrationProfile
            {
                Id = string.Empty,
                Name = "-- 无 / 不绑定畸变矫正 --"
            });
            try
            {
                foreach (var po in _calibrationProfileRepository.GetAll())
                {
                    // 只加载镜头畸变类型方案，排除当前正在编辑的方案
                    if (po.Model != null && po.Model.Type == CalibrationType.CameraLensDistortion && po.Model.Id != TargetProfile.Id)
                    {
                        AvailableDistortionProfiles.Add(po.Model);
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 加载前置畸变方案失败: {ex.Message}");
            }
            OnPropertyChanged(nameof(HasDistortionProfiles));
            OnPropertyChanged(nameof(DistortionHintText));
        }

        /// <summary>根据标定类型选择对应的策略实现（策略模式）</summary>
        private void SelectStrategy(CalibrationType type)
        {
            // ★ 2026-09-06 彻底会话化：v2 单量会话由 spec（物理量 × 采集路径）唯一裁决——
            //   旧混合档案(九点+旋转/吸放)的 H 段会话必须落纯九点/吸放九点策略，
            //   否则混合策略 ExecuteCalibration 会把旋转硬门槛带进只采九点的 H 会话。
            //   旧兼容壳（无 spec）仍按档案 Type 分流，行为不变。
            if (_sessionSpec != null)
            {
                switch (_sessionSpec.Quantity)
                {
                    case CalibrationQuantity.HandEye:
                        // H 段：吸放式(放料命令位真值) / 走位式(吸嘴或相机真值走位)——均无旋转段
                        if (IsPickPlacePath(_sessionSpec.PrimaryPath))
                        {
                            _strategy = new PickPlaceCalibrationStrategy();
                        }
                        else
                        {
                            _strategy = new NinePointCalibrationStrategy();
                        }
                        return;
                    case CalibrationQuantity.ToolRotation:
                        // e 段：采样/拟合动作已由 VM 按 IsRotationSession 直接接管（不经策略），
                        // 此处仅承担 InitializePoints 种旋转点表 → 按吸放/走位路径选同族策略即可
                        if (IsPickPlacePath(_sessionSpec.PrimaryPath))
                        {
                            _strategy = new PickPlaceCalibrationStrategy();
                        }
                        else
                        {
                            _strategy = new HandEyeWithRotationCalibrationStrategy();
                        }
                        return;
                    case CalibrationQuantity.PixelScale:
                        _strategy = new PixelScaleCalibrationStrategy();
                        return;
                    default:
                        // t(对针)/畸变等：不经策略采样/拟合，仅兜底种默认点表（UI 不消费）
                        _strategy = new NinePointCalibrationStrategy();
                        return;
                }
            }

            switch (type)
            {
                case CalibrationType.HandEyeWithRotation:
                    _strategy = new HandEyeWithRotationCalibrationStrategy();
                    break;
                case CalibrationType.PickPlaceHandEye:
                    _strategy = new PickPlaceCalibrationStrategy();
                    break;
                case CalibrationType.Checkerboard2D:
                case CalibrationType.CameraLensDistortion:
                    // ⚠ 2026-09-04 两个"标定板/相机内参"类型均未实现真实角点检测，共用占位策略：
                    //   拟合计算阶段会明确拒绝（绝不产出硬编码假矩阵/静默跑九点）。正常入口（标定管理页）
                    //   已把这两种类型从可选列表移除并拦截，此处分流仅为兜底历史遗留方案。
                    _strategy = new CheckerboardCalibrationStrategy();
                    break;
                case CalibrationType.PixelScale:
                    _strategy = new PixelScaleCalibrationStrategy();
                    break;
                default:
                    _strategy = new NinePointCalibrationStrategy();
                    break;
            }
        }

        /// <summary>切换选中相机：注销旧相机事件订阅，注册新相机帧、状态事件</summary>
        private void OnSelectedCameraChanged(ICamera oldCamera, ICamera newCamera)
        {
            if (oldCamera != null)
            {
                oldCamera.FrameReceived -= OnCameraFrameReceived;
                oldCamera.StateChanged -= OnCameraStateChanged;
                try
                {
                    oldCamera.StopGrabbing();
                    oldCamera.Disconnect();
                }
                catch { }
            }
            if (newCamera == null)
            {
                IsCameraConnected = false;
                return;
            }
            newCamera.FrameReceived += OnCameraFrameReceived;
            newCamera.StateChanged += OnCameraStateChanged;
            OnCameraStateChanged(newCamera, newCamera.State);
        }

        /// <summary>切换运动控制卡，注册状态变更事件</summary>
        private void OnSelectedMotionChanged(IMotionCard oldMotion, IMotionCard newMotion)
        {
            if (oldMotion != null)
            {
                oldMotion.StateChanged -= OnMotionStateChanged;
            }
            if (newMotion == null)
            {
                IsMotionConnected = false;
                IsEpsonMotion = false;
                return;
            }
            newMotion.StateChanged += OnMotionStateChanged;
            OnMotionStateChanged(newMotion, newMotion.State);

            // 按设备品牌适配轴下拉显示名（轴号不变，只换"物理含义/推荐选择"描述）：
            // - ZMC 双滑台：1=X 2=右Y 3=左Y 0=Z（保留历史命名，行为不变）；
            // - Epson SCARA：轴槽位=笛卡尔 0=X/1=Y/2=Z/3=U（MoveAbsolute/MOVE 均笛卡尔整点），
            //   故标定 X/Y/旋转轴 = 0/1/3，无编号歧义（J1~J4 是关节号，勿与槽位混淆）。
            IsEpsonMotion = newMotion is IDevice dev && string.Equals(dev.BrandName, "Epson", StringComparison.OrdinalIgnoreCase);
            UpdateAxisDisplayNames(newMotion);

            // ★ Epson 机械手轴默认值自动适配（2026-09-02 防选错撞机）：
            //   旋转轴若仍是 ZMC 默认 0，旋转采样会拿 X 槽位当旋转轴——把角度当毫米位移发车，
            //   有撞机风险，故【强制】设为 3(U)；标定 X/Y 若仍是 ZMC 默认(1/3)则纠正为笛卡尔(0/1)。
            if (IsEpsonMotion)
            {
                if (TargetProfile.BindRotationAxisIndex != 3)
                {
                    AppendLog("[轴自动适配] Epson 机械手：旋转轴强制设为「U 轴(3)」—否则旋转采样会误动 X 轴（撞机风险）");
                    TargetProfile.BindRotationAxisIndex = 3;
                }
                if (TargetProfile.BindXAxisIndex == 1 && TargetProfile.BindYAxisIndex == 3)
                {
                    AppendLog("[轴自动适配] Epson 机械手：标定 X/Y 轴已设为 0=X / 1=Y（笛卡尔，与走位框同名）");
                    TargetProfile.BindXAxisIndex = 0;
                    TargetProfile.BindYAxisIndex = 1;
                }
            }
        }

        /// <summary>
        /// 根据运动设备品牌刷新 AvailableAxes 的显示名。
        /// 设备类型判断用 BrandName（Epson 插件的 BrandName = "Epson"）。
        /// Epson 场景说明（2026-09-02 现场）：EpsonRobot 的轴槽位是【笛卡尔坐标】
        /// 0=X/1=Y/2=Z/3=U（MoveAbsolute/MOVE 均按四轴笛卡尔整点解释），与走位框、
        /// MOVE 指令参数完全同名，因此下拉直接显示 X/Y/Z/U，不出现编号。
        /// 控制器 Joint 监视器的 J1~J4 是【关节号】，与槽位不是 1:1：
        ///   - J3(升降)=Z、J4(旋转)=U 与坐标同名可直接对应；
        ///   - SCARA 的 X/Y 由 J1+J2 联动逆解——"改 X 值 J2 动得多、改 Y 值 J1 动得多"
        ///     是正常运动学现象（见选项 Tooltip），千万不要据此把 X 当"J2 轴"去选。
        /// </summary>
        private void UpdateAxisDisplayNames(IMotionCard motion)
        {
            bool isEpson = motion is IDevice dev &&
                           string.Equals(dev.BrandName, "Epson", StringComparison.OrdinalIgnoreCase);

            AvailableAxes.Clear();
            if (isEpson)
            {
                AvailableAxes.Add(new AxisOption
                {
                    AxisIndex = 0,
                    DisplayName = "X 轴（笛卡尔·走位框 X 同值）",
                    Tooltip = "笛卡尔 X（MOVE 第 1 参）。改 X 值时 Joint 监视器里 J2(小臂) 动得明显——SCARA 正常联动，不是轴号对调。标定 X 轴选此项。"
                });
                AvailableAxes.Add(new AxisOption
                {
                    AxisIndex = 1,
                    DisplayName = "Y 轴（笛卡尔·走位框 Y 同值）",
                    Tooltip = "笛卡尔 Y（MOVE 第 2 参）。改 Y 值时 Joint 里 J1(大臂) 动得明显——SCARA 正常联动。标定 Y 轴选此项。"
                });
                AvailableAxes.Add(new AxisOption
                {
                    AxisIndex = 2,
                    DisplayName = "Z 轴（吸嘴升降·对应 J3）",
                    Tooltip = "吸嘴升降（Joint 的 J3），行程 -150~0mm、向下为负。九点平移/旋转标定都用不到。"
                });
                AvailableAxes.Add(new AxisOption
                {
                    AxisIndex = 3,
                    DisplayName = "U 轴（末端旋转·对应 J4）★旋转标定选此",
                    Tooltip = "末端旋转（Joint 的 J4），单位角度（如 90 = 90°）。旋转中心标定【必须】选此项——若误选 X/Y/Z，旋转采样会把角度当毫米位移发车（撞机风险）。切到 Epson 设备时向导已自动设为 3(U)。"
                });
            }
            else
            {
                // ZMC 双滑台默认命名（与历史版本一致）：轴号即实际轴编号，需按工位挑选
                AvailableAxes.Add(new AxisOption { AxisIndex = 1, DisplayName = "1号轴：X轴（左右移动）" });
                AvailableAxes.Add(new AxisOption { AxisIndex = 3, DisplayName = "3号轴：左工位Y轴（前后移动）" });
                AvailableAxes.Add(new AxisOption { AxisIndex = 2, DisplayName = "2号轴：右工位Y轴（前后移动）" });
                AvailableAxes.Add(new AxisOption { AxisIndex = 0, DisplayName = "0号轴：Z轴（升降/旋转轴）" });
            }
        }

        /// <summary>相机设备状态变更回调；切Dispatcher到UI线程更新绑定属性</summary>
        private void OnCameraStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsCameraConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        /// <summary>运动卡状态变更回调，UI线程更新状态</summary>
        private void OnMotionStateChanged(object sender, DeviceState state)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsMotionConnected = state == DeviceState.Connected;
                UpdateBindingInfo();
            }));
        }

        /// <summary>更新标定方案绑定硬件信息：记录当前相机、运动卡ID，更新界面显示字符串</summary>
        private void UpdateBindingInfo()
        {
            TargetProfile.CameraId = SelectedCameraDevice != null ? (string.IsNullOrWhiteSpace(SelectedCameraDevice.DeviceKey) ? SelectedCameraDevice.DeviceId : SelectedCameraDevice.DeviceKey) : null;
            TargetProfile.AxisId = SelectedMotionDevice != null ? (string.IsNullOrWhiteSpace(SelectedMotionDevice.DeviceKey) ? SelectedMotionDevice.DeviceId : SelectedMotionDevice.DeviceKey) : null;
            TargetProfile.BoundDeviceId = TargetProfile.CameraId;
            TargetProfile.BindingInfo = string.Format("相机: {0} | 运动卡: {1}", GetDeviceDisplayName(SelectedCameraDevice), GetDeviceDisplayName(SelectedMotionDevice));
            HardwareBindingSummary = TargetProfile.BindingInfo;
        }

        /// <summary>获取设备友好显示名称，优先DeviceName，其次DeviceKey，最后DeviceId</summary>
        private static string GetDeviceDisplayName(IDevice device)
        {
            if (device == null)
            {
                return "未选择";
            }
            if (!string.IsNullOrWhiteSpace(device.DeviceName))
            {
                return device.DeviceName;
            }
            if (!string.IsNullOrWhiteSpace(device.DeviceKey))
            {
                return device.DeviceKey;
            }
            return device.DeviceId;
        }
        #endregion

        #region 相机采集逻辑

        /// <summary>
        /// 相机采集一张图片，软触发，等待图像帧，渲染到UI图像控件
        /// waitForFrame=true 会等待AutoResetEvent收到帧信号，1200ms超时
        /// ★ 2026-09-06 修复：软触发失败 / 超时不再静默沿用旧画面（ActiveImageContext 兜底）。
        ///   九点/旋转每个采样点都必须拿到"本点位的新帧"——走位后旧图 = 上一位置坐标，
        ///   会导致该点像素坐标错乱却"看似成功"。现在自动重触发至多 3 次，仍无新帧返回 false，
        ///   由外层自愈/自动采集流程重采或中止（自愈连拍本身就会再次调本方法）。
        /// </summary>
        private bool CaptureAndDisplayFrame(string actionName, bool waitForFrame)
        {
            if (SelectedCameraDevice == null)
            {
                RunOnUi(() => MessageBox.Show("请先在 Step 0 绑定相机设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning));
                return false;
            }

            var connectRes = EnsureCameraConnected();
            if (!connectRes.Success)
            {
                RunOnUi(() => MessageBox.Show("相机连接失败：" + connectRes.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                return false;
            }

            // 设置相机软触发模式，开始取流
            SelectedCameraDevice.SetTriggerMode(1);
            var startRes = SelectedCameraDevice.StartGrabbing();
            if (!startRes.Success)
            {
                AppendLog($"[警告] {actionName} 启动采集流失败: {startRes.Message}");
            }

            const int maxTriggerAttempts = 3;
            for (int attempt = 1; attempt <= maxTriggerAttempts; attempt++)
            {
                _frameArrivedEvent.Reset();
                // 软触发拍照，兼容两种API方法名 SoftwareTrigger / SoftTrigger
                var triggerRes = SelectedCameraDevice.SoftwareTrigger();
                if (!triggerRes.Success)
                {
                    triggerRes = SelectedCameraDevice.SoftTrigger();
                }
                if (!triggerRes.Success)
                {
                    AppendLog($"[警告] {actionName} 软触发失败(第{attempt}/{maxTriggerAttempts}次): {triggerRes.Message}");
                    if (attempt < maxTriggerAttempts)
                    {
                        Thread.Sleep(200);
                        continue;
                    }
                    return false; // 触发不了 = 拿不到新帧：宁可中止，也不吃上一位置的旧图
                }

                if (!waitForFrame)
                {
                    return true;
                }

                // 等待图像到达，最多等待1200ms
                bool signaled = _frameArrivedEvent.WaitOne(1200);
                if (signaled)
                {
                    AppendLog($"[{actionName}] 已完成抓图并刷新显示（第{attempt}次触发命中新帧）。");
                    return true;
                }

                AppendLog($"[警告] {actionName} 第{attempt}/{maxTriggerAttempts}次触发后 1200ms 内未收到新帧，自动重触发…");
                if (attempt < maxTriggerAttempts)
                {
                    Thread.Sleep(150);
                }
            }

            AppendLog($"[{actionName}] {maxTriggerAttempts} 次触发均未收到新帧 —— 中止本次采样（不沿用旧画面，避免坐标错乱）。");
            return false;
        }

        /// <summary>确保相机处于已连接状态；未连接则执行Connect()</summary>
        private Result EnsureCameraConnected()
        {
            if (SelectedCameraDevice == null)
            {
                return Result.Fail("未选择相机。");
            }
            if (SelectedCameraDevice.State == DeviceState.Connected)
            {
                return Result.Ok();
            }
            return SelectedCameraDevice.Connect();
        }

        /// <summary>确保运动卡处于已连接状态</summary>
        private Result EnsureMotionConnected()
        {
            if (SelectedMotionDevice == null)
            {
                return Result.Fail("未选择运动控制卡。");
            }
            if (SelectedMotionDevice.State == DeviceState.Connected)
            {
                return Result.Ok();
            }
            return SelectedMotionDevice.Connect();
        }
        #endregion

        #region 平台运动控制

        /// <summary>
        /// 平台XY轴绝对移动
        /// 轴定义注释：
        //AxisIndex = 控制器内部轴编号，AxisName = 轴实际物理含义/安装位置
        //AxisList.Add(new AxisInfoModel { AxisIndex = 1, AxisName = "1号轴：X轴（左右运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 2, AxisName = "2号轴：右侧Y轴（前后运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 3, AxisName = "3号轴：左侧Y轴（前后运动）" });
        //AxisList.Add(new AxisInfoModel { AxisIndex = 0, AxisName = "0号轴：Z轴（升降/旋转轴）" });
        /// 当前标定使用：轴1 = X方向；轴3 = Y方向
        /// </summary>
        /// <summary>
        /// 平台驱动指定轴绝对移动
        /// </summary>
        /// <summary>
        /// 驱动平台/机械手走到指定 XY，等待到位。
        /// Epson SCARA：合并为一次四轴 PTP（X/Y 同时走，Z/U 保持当前值）——
        /// 旧实现连续两次单轴 MoveAbsolute 会各触发一次整点位 MOVE，同目标重复走位（2026-09-06 修）。
        /// 其余控制器（ZMC 等）：沿用两轴 MoveAbsolute。
        /// 返回是否真的到位：控制器拒绝（Epson 超动作区域 4007 / 关节超脉冲 4001 / Z 软限 2997）
        /// 或连接失败均返回 false，并以 rejectReason 透传原始失败信息——调用方必须中止本次采样，
        /// 否则会在错误位置采图，导致该点识别错乱（2026-09-03 修复；2026-09-06 起带原因输出）。
        /// </summary>
        private bool MovePlatformTo(double worldX, double worldY, out string rejectReason)
        {
            rejectReason = null;
            if (SelectedMotionDevice == null)
            {
                AppendLog("[提示] 当前未绑定运动控制卡，本次仅执行抓图采样。");
                return true; // 无运动设备 = 纯抓图模式，视为"无需走位"继续
            }
            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                AppendLog("[警告] 运动控制卡连接失败: " + connectRes.Message);
                rejectReason = connectRes.Message;
                return false;
            }

            // ★ Epson 机械手九点平移前强制 U 轴归零（2026-09-03 现场 4001 修复）：
            //   旋转采样若中止/残留会停在 ±60° 等非零角度；此时再做 XY 平移，MOVE 整点
            //   携带 U=±60° 走位——SCARA 在该姿态下部分 XY 坐标不可达 → 控制器 4001
            //   拒绝（日志实锤：MOVE 241.09,47.03,-75,-60.008 → ERR motion rejected 4001）。
            //   且手眼标定要求在固定 U 姿态下平移采样（U≠0 相当于换了相机视角，矩阵会错）。
            if (IsEpsonMotion)
            {
                try
                {
                    int rotAxis = TargetProfile.BindRotationAxisIndex;
                    var uRes = SelectedMotionDevice.GetFeedbackPosition(rotAxis);
                    if (uRes.Success && Math.Abs(uRes.Data) > 0.5)
                    {
                        AppendLog($"[走位前] 检测到 U 轴残留 {uRes.Data:F1}°，九点平移前先归零（防 4001 与视角错乱）。");
                        var rz = SelectedMotionDevice.MoveAbsolute(rotAxis, 0, DefaultMoveSpeed);
                        if (rz != null && !rz.Success)
                        {
                            AppendLog($"[走位前] U 轴归零失败: {rz.Message} —— 中止本次平移，请先手动回 U 到 0°。");
                            rejectReason = rz.Message;
                            return false;
                        }
                        WaitAxesIdle(TargetProfile.BindRotationAxisIndex);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog($"[走位前] U 轴归零检查异常（忽略继续平移）: {ex.Message}");
                }
            }

            // 动态驱动方案绑定的 X 轴与 Y 轴（检查拒绝结果：超行程/范围钳制）
            // ★ Epson SCARA 合并单次 PTP（2026-09-06）：EpsonRobot.MoveAbsolute(单轴) 内部
            //   也是"读四轴当前值补全后整点 MOVE"，连发两次 = 同目标两次整点位走位（先 X 后 Y），
            //   浪费一次运动且中间姿态不必要。改为直接四轴 PTP，Z/U 取当前位置（U 前置已归零）。
            if (SelectedMotionDevice is EpsonRobot epsonRobot)
            {
                var cur = epsonRobot.GetPositionsAll();
                if (!cur.Success)
                {
                    AppendLog($"[走位被拒] Epson 读取当前 Z/U 失败: {cur.Message} —— 中止本次平移。");
                    rejectReason = cur.Message;
                    return false;
                }
                var ptp = epsonRobot.MoveToPtp((float)worldX, (float)worldY,
                                               cur.Data[EpsonRobot.AxisZ], cur.Data[EpsonRobot.AxisU],
                                               DefaultMoveSpeed);
                if (ptp != null && !ptp.Success)
                {
                    // 原始消息形如 "PTP 定位失败: 指令 [MOVE ...] 失败，控制器应答: ERR motion rejected(code 4007): ..."
                    // —— 完整透传，调用方可按 (code NNN) 解码成可读原因
                    AppendLog($"[走位被拒] 目标 (X:{worldX:F3}, Y:{worldY:F3}) PTP 运动失败：{ptp.Message}");
                    rejectReason = ptp.Message;
                    return false;
                }
            }
            else
            {
                var rx = SelectedMotionDevice.MoveAbsolute(TargetProfile.BindXAxisIndex, (float)worldX, DefaultMoveSpeed);
                var ry = SelectedMotionDevice.MoveAbsolute(TargetProfile.BindYAxisIndex, (float)worldY, DefaultMoveSpeed);
                if ((rx != null && !rx.Success) || (ry != null && !ry.Success))
                {
                    rejectReason = (rx != null && !rx.Success ? rx.Message : "") +
                                  (ry != null && !ry.Success ? " / " + ry.Message : "");
                    AppendLog($"[走位被拒] 目标 (X:{worldX:F3}, Y:{worldY:F3}) 运动失败：{rejectReason}");
                    return false;
                }
            }

            // 等待两轴实际到位后再采图（替代原先固定 Thread.Sleep(200)）：
            // MoveAbsolute 是异步下发指令，步长大 / 速度低时 200ms 内轴仍在运动或减速震荡，
            // 拍到的 Mark 处于拖尾 / 模糊状态 → 圆度骤减被几何筛掉，表现为"后面几个点识别不了"。
            WaitAxesIdle(TargetProfile.BindXAxisIndex, TargetProfile.BindYAxisIndex);
            return true;
        }

        /// <summary>
        /// 计算九点网格第 index 个点的目标机械坐标（Index 1~9 固定对应 3x3 网格，
        /// 1=(0,0)左上 … 5=(1,1)中心 … 9=(2,2)右下，row/col 从 0 起）。
        /// 与走位顺序(TraverseMode)无关，只由基准/步长/镜像/眼型决定——
        /// 采集循环与可达性预检共用本方法，保证两处公式永不漂移（2026-09-06 抽取）。
        /// 公式说明：
        /// · 网格偏移 = (col-1)*StepX / (row-1)*StepY（相对基准的 (-1,0,1) 倍步长）；
        /// · 轴镜像开关（InvertXAxis/InvertYAxis）：现场轴实际运动方向与软件假设相反时翻转；
        /// · EyeInHand(眼在手上): 相机动标定板静止，向右看 Mark 需相机向左走 (X 反向偏移)，
        ///   Y 同理（2026-09-02 修正：只反 X 不反 Y 会让 Y 网格镜像错乱/走出视野）；
        ///   EyeToHand(眼在手外): 相机静止工作台动，正向偏移。
        /// 返回 false 表示基准未设置。
        /// </summary>
        private bool TryGetNinePointTarget(int index, out double posX, out double posY)
        {
            posX = 0;
            posY = 0;
            if (!TargetProfile.IsBasePosSet) return false;

            int row = (index - 1) / 3;
            int col = (index - 1) % 3;
            double offsetX = (col - 1) * GridStepX;
            double offsetY = (row - 1) * GridStepY;
            if (TargetProfile.InvertXAxis) offsetX = -offsetX;
            if (TargetProfile.InvertYAxis) offsetY = -offsetY;

            bool eih = TargetProfile.EyeMode == EyeMode.EyeInHand;
            posX = eih ? (TargetProfile.BasePosX - offsetX) : (TargetProfile.BasePosX + offsetX);
            posY = eih ? (TargetProfile.BasePosY - offsetY) : (TargetProfile.BasePosY + offsetY);
            return true;
        }

        /// <summary>
        /// 把控制器/脚本返回的走位拒绝原始消息解码成操作员可读原因。
        /// 错误码映射为现场日志实锤（2026-09-06 Epson RC70）：
        /// · 4007 = 超动作区域（多在可达环带外圈之外，机械臂够不着）；
        /// · 4001 = 关节超脉冲范围（多进内圈空洞两臂收不拢，或当前 U 姿态下该 XY 不可达）；
        /// · 2997 = Z 轴超出 RC+ 软限（现场实测下限约 -145.5mm）。
        /// 未识别的原始消息原样返回（不含建议，避免误导）。
        /// </summary>
        private static string DecodeEpsonRejectReason(string rawMessage)
        {
            if (string.IsNullOrEmpty(rawMessage)) return "控制器拒绝走位（无详细原因，详见日志）";
            string m = rawMessage;
            if (m.Contains("code 4007"))
                return "控制器判定目标超出动作区域(code 4007)——SCARA 可达域是环形带，此点多半落在外圈之外(机械臂够不着)";
            if (m.Contains("code 4001"))
                return "控制器判定关节超脉冲范围(code 4001)——多为目标落进内圈空洞(两臂收不拢)或当前 U 姿态下该 XY 不可达";
            if (m.Contains("code 2997"))
                return "控制器判定 Z 轴超出软限(code 2997)——目标 Z 低于 RC+ 设置的 Z 下限(现场实测约 -145.5mm)";
            if (m.Contains("out of range"))
                return "坐标被 Epson 脚本粗筛拦截(数量级明显非法)——真实可达性请以控制器裁决为准";
            return m;
        }

        /// <summary>
        /// 轮询等待指定轴全部空闲（到位），到位后再留 100ms 稳定时间消除残余震荡。
        /// 运动卡不支持 IsAxisIdle 查询（返回失败）或超时（默认 8s）时降级为固定等待并记日志。
        /// </summary>
        private void WaitAxesIdle(params int[] axes)
        {
            const int timeoutMs = 8000;
            if (SelectedMotionDevice == null || axes == null || axes.Length == 0)
            {
                return;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    bool allIdle = true;
                    foreach (var axis in axes)
                    {
                        var idleRes = SelectedMotionDevice.IsAxisIdle(axis);
                        if (!idleRes.Success)
                        {
                            // 查询接口不可用：降级固定等待，不再空转超时
                            AppendLog($"[提示] 运动卡不支持轴 [{axis}] 到位查询，降级固定等待 500ms。");
                            Thread.Sleep(500);
                            return;
                        }
                        if (!idleRes.Data)
                        {
                            allIdle = false;
                            break;
                        }
                    }
                    if (allIdle)
                    {
                        Thread.Sleep(100); // 到位后稳定片刻（消除减速末端残余震荡）
                        return;
                    }
                    Thread.Sleep(20);
                }
                AppendLog($"[警告] 等待轴({string.Join("/", axes)})到位超时({timeoutMs}ms)，继续执行采图。");
            }
            catch (Exception ex)
            {
                AppendLog($"[警告] 轴到位查询异常({ex.Message})，降级固定等待 500ms。");
                Thread.Sleep(500);
            }
        }


        /// <summary>
        /// 旋转轴绝对运动，角度单位度。
        /// 旋转轴号取 TargetProfile.BindRotationAxisIndex（可配）：
        /// - ZMC 双滑台工位默认 0（0号轴 = Z/旋转轴，与旧行为一致）；
        /// - Epson SCARA 工位须在向导步骤1选 3（U 轴）——
        ///   否则会把 X 轴转到"角度值"的毫米位置，实机上有撞机风险。
        /// </summary>
        private void MoveRotationTo(double angleDeg)
        {
            if (SelectedMotionDevice == null)
            {
                AppendLog("[提示] 当前未绑定运动控制卡，旋转采样仅抓图不转轴。");
                return;
            }
            var connectRes = EnsureMotionConnected();
            if (!connectRes.Success)
            {
                AppendLog("[警告] 旋转轴连接失败: " + connectRes.Message);
                return;
            }
            int rotAxis = TargetProfile.BindRotationAxisIndex;
            AppendLog($"[旋转采样] 旋转轴 {rotAxis} → {angleDeg:F1}°");
            SelectedMotionDevice.MoveAbsolute(rotAxis, (float)angleDeg, DefaultMoveSpeed);
            // 等待旋转轴到位后再采图，避免拍到运动中的 Mark（等待轴号与运动轴号保持一致）
            WaitAxesIdle(TargetProfile.BindRotationAxisIndex);
        }
        #endregion


        #region 特征点提取（委托 HalconWrapper.Calibration 服务）

        /// <summary>
        /// 提取九点标定特征标记点像素坐标（严谨 Halcon 真实提取，不使用假数据）
        /// </summary>
        /// <param name="index">当前采样点编号（1~9）</param>
        /// <param name="worldX">当前采样点目标世界坐标 X（走位目标）</param>
        /// <param name="worldY">当前采样点目标世界坐标 Y（走位目标）</param>
        private (double PixelX, double PixelY)? EstimateFeaturePoint(int index, double worldX, double worldY, out bool reliable)
        {
            reliable = true;
            // 用 _latestFrameContext（最新帧），理由同 ExtractFeatureAndAnnotate
            var imageObj = _latestFrameContext?.Image;
            if (imageObj == null)
            {
                AppendLog($"[错误] 第 {index} 点提取失败：当前图像上下文为空。");
                return null;
            }

            // 期望位置 = 由"已采集点"按轴位移率线性外推当前点的像素位置。
            // ⚠ 不能用"上一个采集点的像素"——走位后 Mark 已位移 步长/像素当量 个像素
            // （通常数百 px，远超 SearchRadius），旧位置 ± SearchRadius 的 ROI 根本不含
            // 真实 Mark，"离旧位置最近"反而会把反光点等伪特征选回来（此前 8 点全偏的根因）。
            // 外推依据：像素≈世界的线性关系正是待标定量，但用已采集点在线估计位移率即可
            // 预测后续点（3,5~9 点均有预测；1/2/4 点缺样本自动全图搜索）。
            var predicted = PredictExpectedPixel(worldX, worldY);
            double seedPx = predicted?.Px ?? -1;
            double seedPy = predicted?.Py ?? -1;
            // 部分预测（某轴缺样本，seed 方向不完整）：跳过 ROI 裁剪，直接全图搜索，
            // 仅用 seed 做多候选取近——不精确的 seed 若作 ROI 中心，可能把 ROI 框到
            // 伪特征附近，且"ROI 内成功命中伪特征"不会触发降级全图重试。
            // 典型场景：螺旋顺序第 3 点(0,2) 首次需 Y 位移但无 Y 样本 → X 轴可外推、Y 轴靠 0 位移占位。
            bool skipRoi = predicted?.Partial ?? false;
            if (!predicted.HasValue)
            {
                AppendLog($"[第 {index} 点] 已采集点样本不足，无预测引导，采用全图搜索提取（画面边缘有干扰物时留意候选标记）。");
            }

            // 调用底层 Halcon 服务真实提取（按第二步配置的特征类型路由：圆 Mark / 十字 Mark，
            // 保证采样与预览使用同一套算法——此前这里写死圆算法，选十字 Mark 时采样必然失败）
            var extractRes = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, index, seedPx, seedPy, forceFullImage: skipRoi);
            if (!extractRes.Success && (seedPx > 0 || seedPy > 0) && !skipRoi)
            {
                // 参考位置 ROI 搜索失败 → 自动降级全图搜索重试一次（仍携带 seed）：
                // seed 只是"缩小搜索范围"的引导，不应成为失败原因：局部 ROI 常因
                // 走位步长的像素位移大于 SearchRadius、或 Mark 靠近视野边缘被 ROI 截断而错过目标。
                // 全图搜索跳过 ROI 裁剪但保留 seed 做多候选择近——若不带 seed，
                // 全图多候选时取第一个极易选中伪特征（此前 RMS 过大的直接根因之一）。
                AppendLog($"[第 {index} 点] 参考位置 ROI 搜索未命中，自动降级全图搜索重试（建议检查 SearchRadius 是否覆盖走位步长的像素位移）...");
                extractRes = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, index, seedPx, seedPy, forceFullImage: true);
                if (extractRes.Success)
                {
                    AppendLog($"[第 {index} 点] 全图搜索重试识别成功。");
                }
            }
            if (extractRes.Success)
            {
                // 预测可用时打印"预测 vs 识别"偏差：偏差小说明外推与识别互相印证；
                // 偏差大（超阈值）说明该点很可能选中伪特征/取错特征点：
                // ① 自动全图重试一次（保留预测 seed 择近）排除"ROI 框错/ROI 内误选"；
                // ② 重试后仍偏差大 → 弹窗提醒操作员目视确认，并标记该点不可靠、
                //    不参与后续点位预测（阻断"一个点取错导致后续预测连环偏移"的误差累积）。
                if (predicted.HasValue)
                {
                    double dev = Math.Sqrt(
                        DistanceSq(extractRes.Data.PixelX - predicted.Value.Px, extractRes.Data.PixelY - predicted.Value.Py));
                    // 仅"全预测"（seed 两轴都可信）才做可信度校验；部分预测 seed 本身不完整，
                    // 偏差大是预期的，不误报
                    bool fullPredict = !skipRoi;
                    // ★ 阈值放宽（2026-09-03 误报修复）：旧阈值 max(100, SearchRadius=150) 对
                    //   20mm 步长（单步像素位移常 200~400px）太紧——网格早期外推误差、参考点
                    //   微偏都会让"取对的点"偏离预测 >150px。改 max(250, SearchRadius×1.5)。
                    double warnThreshold = Math.Max(250, ExtractOptions.SearchRadius * 1.5);
                    if (fullPredict && dev > warnThreshold)
                    {
                        AppendLog($"[第 {index} 点] 识别位置偏离预测 {dev:F0}px（阈值 {warnThreshold:F0}px），自动全图重试校验是否取错特征...");
                        var retry = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, index, predicted.Value.Px, predicted.Value.Py, forceFullImage: true);
                        if (retry.Success)
                        {
                            double retryDev = Math.Sqrt(
                                DistanceSq(retry.Data.PixelX - predicted.Value.Px, retry.Data.PixelY - predicted.Value.Py));
                            if (retryDev < dev - 50)
                            {
                                AppendLog($"[第 {index} 点] 全图重试命中更优候选（偏差 {retryDev:F0}px），采用重试结果——首轮确为取错。");
                                extractRes = retry;
                                dev = retryDev;
                            }
                        }
                        if (dev > warnThreshold)
                        {
                            // ★ 2026-09-03 误报修复：走到这里说明"全图重试没有找到比当前识别更接近
                            // 预测的候选"——该区域唯一符合特征的就是它 → 大概率是【预测外推偏了】
                            // （网格早期样本少/非线性/参考点误差），而非取错。旧逻辑此时直接
                            // 标不可靠+弹窗打断，现场"明明取对却报偏离"即此误报。
                            // 新逻辑：中等偏差（≤2×阈值）只记日志、保留可靠点，由操作员经已采集点
                            // 十字阵列目视把关；仅超 2×阈值（识别点离预测异常远）才弹窗一次确认。
                            if (dev > warnThreshold * 2)
                            {
                                reliable = false; // 疑似取错 → 不参与后续预测
                                AppendLog($"[第 {index} 点] ❌ 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 偏离预测({predicted.Value.Px:F0},{predicted.Value.Py:F0}) {dev:F0}px（超 2×阈值），已标记为不可靠（不参与后续点位预测）。");
                                RunOnUi(() => MessageBox.Show(
                                    $"第 {index} 点识别位置偏离预测 {dev:F0}px（阈值 {warnThreshold:F0}px），很可能取错了特征点。\n\n" +
                                    "请对照视图窗口检查：识别十字是否压在真实 Mark 中心（而非反光/螺钉/字符等干扰）。\n\n" +
                                    "该点已标记为不可靠，不参与后续点位预测（避免连环偏移）；可稍后对该点重新采样覆盖。",
                                    "特征点可疑", MessageBoxButton.OK, MessageBoxImage.Warning));
                            }
                            else
                            {
                                AppendLog($"[第 {index} 点] 识别与预测偏差 {dev:F0}px（阈值 {warnThreshold:F0}px）：全图重试无更近候选 → 判定为预测外推偏差（非取错），该点保留可靠。请结合已采集点十字阵列目视确认。");
                            }
                        }
                        else
                        {
                            AppendLog($"[第 {index} 点] 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 与预测偏差 {dev:F1}px");
                        }
                    }
                    else
                    {
                        AppendLog(dev > warnThreshold
                            ? $"[第 {index} 点] 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 偏离预测({predicted.Value.Px:F0},{predicted.Value.Py:F0}) {dev:F0}px（部分预测，属预期）"
                            : $"[第 {index} 点] 识别像素({extractRes.Data.PixelX:F0},{extractRes.Data.PixelY:F0}) 与预测偏差 {dev:F1}px");
                    }
                }
                return (extractRes.Data.PixelX, extractRes.Data.PixelY);
            }

            AppendLog($"[警告] 第 {index} 点特征提取失败: {extractRes.Message}");
            return null;
        }

        /// <summary>平方距离（避免开方）</summary>
        private static double DistanceSq(double dx, double dy)
        {
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 用已采集点（世界坐标→像素坐标）线性外推目标点的期望像素位置。
        /// 逐轴估计位移率：取世界坐标差最大的已采集点对（最稳定），
        /// dPixel/dWorld 各 4 个分量（Px/Wx、Py/Wx、Px/Wy、Py/Wy）。
        /// 支持"部分预测"：某轴缺样本时该轴按 0 位移占位（seed 至少带对一个轴方向），
        /// 返回的 Partial=true 由调用方决定跳过 ROI 裁剪、仅用 seed 做多候选取近。
        /// 所有轴都没有任何位移样本时返回 null（无 seed 全图搜索）。
        /// 预测值明显出视野（负坐标）也返回 null。
        /// </summary>
        private (double Px, double Py, bool Partial)? PredictExpectedPixel(double worldX, double worldY)
        {
            // 只用"可靠"样本做外推：识别时与预测偏差过大被标记不可靠的点若参与预测，
            // 会把取错的特征位置带进位移率估计，导致后续点预测连环偏移（一个点偏、后面全偏）。
            var captured = CalibrationPoints.Where(p => p.IsCaptured && p.IsReliable).ToList();
            if (captured.Count == 0)
            {
                return null;
            }

            var refPoint = captured[0];
            double dPxPerWx = EstimateAxisRate(captured, p => p.WorldX, p => p.PixelX);
            double dPyPerWx = EstimateAxisRate(captured, p => p.WorldX, p => p.PixelY);
            double dPxPerWy = EstimateAxisRate(captured, p => p.WorldY, p => p.PixelX);
            double dPyPerWy = EstimateAxisRate(captured, p => p.WorldY, p => p.PixelY);

            double dx = worldX - refPoint.WorldX;
            double dy = worldY - refPoint.WorldY;
            const double Eps = 1e-6;
            bool needX = Math.Abs(dx) > Eps;
            bool needY = Math.Abs(dy) > Eps;

            // 该轴是否已有位移率样本（任一像素分量可算即算有）
            bool hasXRate = !double.IsNaN(dPxPerWx) || !double.IsNaN(dPyPerWx);
            bool hasYRate = !double.IsNaN(dPxPerWy) || !double.IsNaN(dPyPerWy);

            // 两轴均无样本 → 无法外推，无 seed 全图搜索。
            // 例：螺旋顺序第 2 点（仅采了中心 1 个点）；第 3 点(0,2) 首次需 Y 位移，
            // 但已采集点(5,6) 都在第二行 → 仅 X 有样本 → 走部分预测而非整体放弃。
            if (!hasXRate && !hasYRate)
            {
                return null;
            }

            // 部分预测：能预测的轴就预测，缺样本的轴用参考点轴值占位（0 位移）
            double px = refPoint.PixelX
                + (needX && !double.IsNaN(dPxPerWx) ? dx * dPxPerWx : 0)
                + (needY && !double.IsNaN(dPxPerWy) ? dy * dPxPerWy : 0);
            double py = refPoint.PixelY
                + (needX && !double.IsNaN(dPyPerWx) ? dx * dPyPerWx : 0)
                + (needY && !double.IsNaN(dPyPerWy) ? dy * dPyPerWy : 0);

            // 部分预测标记：目标点在某轴有位移、但该轴没有位移率样本 → seed 该轴方向不可信
            bool partial = (needX && !hasXRate) || (needY && !hasYRate);

            // 预测位置明显出视野 → 外推不可信，退回全图搜索
            if (px < 0 || py < 0)
            {
                return null;
            }
            return (px, py, partial);
        }

        /// <summary>
        /// 估计某世界轴 → 某像素分量的位移率 dPixel/dWorld：
        /// 遍历所有已采集点对，取世界坐标差最大的一对计算（差值越大除法越稳定）。
        /// 无有效点对（仅 1 个采集点 / 该轴世界坐标全部相同）返回 NaN。
        /// </summary>
        private static double EstimateAxisRate(
            List<CalibrationPointModel> points,
            Func<CalibrationPointModel, double> worldValue,
            Func<CalibrationPointModel, double> pixelValue)
        {
            double bestAbsDw = 0;
            double rate = double.NaN;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    double dw = worldValue(points[i]) - worldValue(points[j]);
                    if (Math.Abs(dw) > bestAbsDw && Math.Abs(dw) > 1e-6)
                    {
                        bestAbsDw = Math.Abs(dw);
                        rate = (pixelValue(points[i]) - pixelValue(points[j])) / dw;
                    }
                }
            }
            return rate;
        }

        /// <summary>
        /// 用已采旋转点拟合圆并预测指定角度下 Mark 的期望像素位置（2026-09-03）：
        /// 旋转采样点都在同一圆上（圆心=旋转轴在图像中的投影，半径=偏心距像素），
        /// 已采 ≥3 点后用 Kåsa 圆拟合得圆心 C/半径 R，再把"离目标角度最近的已采点向量"
        /// 绕 C 旋转 (目标角-基准角) 即得预测位置。作为 seed 传入提取 → ROI 局部搜索，
        /// 大幅抑制全图搜索被反光/工件边缘等圆特征干扰的误检与漏检（旋转后段角度
        /// 0/±30 已采、±60 待采的场景收益最大）。
        /// 预测明显出视野 / 圆心不在图内 / 拟合失败 → 返回 null（调用方走全图搜索）。
        /// </summary>
        private (double Px, double Py)? PredictRotationFeaturePixel(double angleDeg)
        {
            var captured = RotationPoints.Where(p => p.IsCaptured).ToList();
            if (captured.Count < 3)
            {
                return null; // 圆拟合至少 3 点
            }
            try
            {
                var fit = Calib2DTool.FitCircleKasa(
                    captured.Select(p => p.PixelX).ToArray(),
                    captured.Select(p => p.PixelY).ToArray());
                if (!fit.Success || double.IsNaN(fit.Data.Radius) || fit.Data.Radius <= 0.1)
                {
                    return null;
                }
                double cx = fit.Data.Cx, cy = fit.Data.Cy, r = fit.Data.Radius;
                // 选离目标角度最近的已采点为相位基准（角度差最小，旋转外推最稳）
                double bestDiff = double.MaxValue;
                RotationPointModel bestRef = null;
                foreach (var p in captured)
                {
                    double d = Math.Abs(NormalizeAngle(p.AngleDeg - angleDeg));
                    if (d < bestDiff)
                    {
                        bestDiff = d;
                        bestRef = p;
                    }
                }
                if (bestRef == null)
                {
                    return null;
                }
                double rotRad = (angleDeg - bestRef.AngleDeg) * Math.PI / 180.0;
                // 基准向量（圆心 → 基准点），旋转 rotRad 后加到圆心
                double vx = bestRef.PixelX - cx;
                double vy = bestRef.PixelY - cy;
                // 图像坐标系：row=Y(向下) col=X(向右)。机械角正方向在图像上的旋向未知，
                // 两种旋向都算，取离圆心距离更接近 R 且仍在图内的那个（实际两点共圆，旋向只差符号）。
                double cosA = Math.Cos(rotRad), sinA = Math.Sin(rotRad);
                var cand = new[]
                {
                    (px: cx + vx * cosA - vy * sinA, py: cy + vx * sinA + vy * cosA), // 顺时针(图像 y 向下时表现为常见方向)
                    (px: cx + vx * cosA + vy * sinA, py: cy - vx * sinA + vy * cosA)  // 逆时针
                };
                // 两者都在合理范围内时，选与"已采相邻点"更接近的（避免选到圆对侧误判）。
                // ⚠ 不做图尺寸裁剪判断（WpfUI 层不可直接持有 halcondotnet 对象取尺寸）；
                //   若预测点出视野，ExtractFeaturePoint 的 ROI 生成已做边界 clamp，失败会自动降级全图重试。
                var nearest = captured.OrderBy(p => Math.Abs(NormalizeAngle(p.AngleDeg - angleDeg))).FirstOrDefault();
                double bestScore = double.MaxValue;
                (double Px, double Py)? best = null;
                foreach (var c in cand)
                {
                    if (c.px < 0 || c.py < 0)
                    {
                        continue;
                    }
                    double score = 0;
                    if (nearest != null)
                    {
                        score = (c.px - nearest.PixelX) * (c.px - nearest.PixelX)
                              + (c.py - nearest.PixelY) * (c.py - nearest.PixelY);
                    }
                    else
                    {
                        double rr = Math.Sqrt((c.px - cx) * (c.px - cx) + (c.py - cy) * (c.py - cy));
                        score = Math.Abs(rr - r);
                    }
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = (c.px, c.py);
                    }
                }
                return best;
            }
            catch
            {
                return null; // 预测失败不阻断：调用方回落全图搜索
            }
        }

        /// <summary>角度差归一化到 [-180, 180]</summary>
        private static double NormalizeAngle(double angle)
        {
            while (angle > 180) angle -= 360;
            while (angle < -180) angle += 360;
            return angle;
        }

        /// <summary>
        /// 提取旋转采样标记点像素坐标（真实提取；2026-09-03 起支持 seed 预测引导）
        /// </summary>
        private (double PixelX, double PixelY)? EstimateRotationFeaturePoint(double angleDeg)
        {
            // 用 _latestFrameContext（最新帧），理由同 ExtractFeatureAndAnnotate
            var imageObj = _latestFrameContext?.Image;
            if (imageObj == null)
            {
                AppendLog($"[错误] 旋转 {angleDeg:F1}° 采样失败：当前图像上下文为空。");
                return null;
            }

            // seed 预测：已采 ≥3 点时用圆拟合外推本角度 Mark 位置 → ROI 局部搜索，
            // 减少全图搜索被反光/同类圆特征干扰导致"在视野内却识别失败"的问题
            double seedPx = -1, seedPy = -1;
            var predicted = PredictRotationFeaturePixel(angleDeg);
            if (predicted.HasValue)
            {
                seedPx = predicted.Value.Px;
                seedPy = predicted.Value.Py;
                AppendLog($"[旋转 {angleDeg:F1}°] 圆拟合外推 seed=({seedPx:F0},{seedPy:F0})，ROI 局部搜索。");
            }

            // 真实提取，不硬编码估算（按第二步配置的特征类型路由：圆 Mark / 十字 Mark）
            var extractRes = CalibService.ExtractRotationFeaturePoint(imageObj, angleDeg, TargetProfile.FeatureType, seedPx, seedPy);
            if (!extractRes.Success && seedPx > 0 && seedPy > 0)
            {
                // ROI 引导失败 → 自动降级全图搜索重试（seed 仍保留做多候选取近）
                AppendLog($"[旋转 {angleDeg:F1}°] seed ROI 未命中，自动降级全图搜索重试...");
                extractRes = CalibService.ExtractFeaturePointByType(imageObj, TargetProfile.FeatureType, 0, seedPx, seedPy, forceFullImage: true);
            }
            if (extractRes.Success)
            {
                return (extractRes.Data.PixelX, extractRes.Data.PixelY);
            }

            AppendLog($"[警告] 旋转 {angleDeg:F1}° 特征提取失败: {extractRes.Message}");
            return null;
        }

        #endregion

        /// <summary>
        /// 相机帧接收事件回调：
        /// 收到相机原始帧，创建渲染上下文，交给WPF图像控件显示；
        /// 设置AutoResetEvent信号，通知采集函数：图像已经到达
        /// </summary>
        private void OnCameraFrameReceived(object sender, FrameEventArgs e)
        {
            var context = _renderService.CreateRenderContextFromFrame(e, GetDeviceDisplayName(SelectedCameraDevice), "CalibrationFrame_" + Interlocked.Increment(ref _frameSequence));
            if (context == null)
            {
                return;
            }

            // 关键：必须在相机回调线程（非 UI 线程）直接登记最新帧并唤醒等待采集的线程。
            // 若把 Set() 放进 Dispatcher.BeginInvoke，而采集线程正在 UI 线程上同步
            // WaitOne(1200) 阻塞，Dispatcher 队列永远不会被消费，Set() 永远不执行，
            // 导致每次采集都等满 1200ms 超时（captured 只能靠旧图兜底）。
            var oldContext = Interlocked.Exchange(ref _latestFrameContext, context);

            // 同步更新 ActiveImageContext（在回调线程直接赋值，而非 BeginInvoke）：
            // 1) setter 仅触发 PropertyChanged / OnRequestRender / 日志，均线程安全
            //    （WPF 绑定与 HalconImageDisplayHost.Display 内部都会 marshal 回 UI 线程）；
            // 2) 这样"图像渲染任务"（DispObj 底图）立即入队 Dispatcher，
            //    特征叠加绘制（DrawOnWindow 的 BeginInvoke）必然排在其后执行，
            //    不会被渲染任务覆盖 —— 否则表现为"叠加画了但看不到"。
            // ★ 2026-09-06 try/finally：任何订阅方在回调线程抛异常（历史上宿主把
            //   SelectedImageInfo 镜像进 DP 的首帧跨线程异常曾在此中断执行）都不得
            //   阻止 _frameArrivedEvent.Set()——采集线程同步 WaitOne 只依赖这一次唤醒，
            //   错过即整点超时等满 1200ms（靠旧图兜底），表现为每会话首采明显偏慢。
            try
            {
                CalibrationImageDisplay.ActiveImageContext = context;
            }
            finally
            {
                _frameArrivedEvent.Set();
            }

            // 旧帧内存释放放回 UI 线程，避免在相机回调线程释放 Halcon 对象
            if (oldContext != null && !ReferenceEquals(oldContext, context))
            {
                var toDispose = oldContext;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => toDispose.Dispose()));
            }
        }

        /// <summary>
        /// 标定算法计算完成回调入口
        /// res：标定算法返回结果对象，包含单应矩阵文件路径、RMS重投影误差
        /// RMS重投影误差含义：把标定点像素代入矩阵反算世界坐标，对比真实机械坐标的平均误差；单位mm，越小精度越高
        /// </summary>
        public void ProcessCalibrationResult(Result<CalibrationResult> res)
        {
            if (res == null)
            {
                MessageBox.Show("标定计算未返回结果。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (res.Success)
            {
                _fitComputedOnce = true;
                RaiseNextGate();
                OutputHomMatPath = res.Data != null ? res.Data.SavedFilePath : null;
                CalculatedRms = res.Data != null ? res.Data.RmsError : 0;
                // 回填标定方案属性
                TargetProfile.IsCalibrated = true;
                TargetProfile.RmsError = CalculatedRms;
                TargetProfile.HomMatFilePath = OutputHomMatPath;
                TargetProfile.UpdatedAt = DateTime.Now;
                // 记录矩阵健康检查报告（BuildHomMatHealthReport 多行文本），供发布前体检第④项判定
                _lastHealthReport = res.Data != null ? res.Data.HealthReport : null;
                // 带旋转类型：拟合旋转中心并映射世界坐标。
                // ⚠ 吸放式(PickPlace)不在此拟合——其偏心含"工件参考点级吸持偏差"，须由
                //   UpdatePickPlaceRotationResults 在矩阵生成后单独调用（策略 ExecuteCalibration 负责），
                //   避免普通版拟合覆盖吸放式结果/摘要（2026-09-04 修正 ToolEccWx/Wy 恒 0 缺陷）。
                if (!IsPickPlaceProfile)
                {
                    UpdateRotationCenterResults();
                }

                AppendLog($"[计算成功] RMS 拟合误差: {CalculatedRms:F5} mm");
                // 换算语义元信息（2026-09-06）：EyeMode/基准/步长/点数 一并落日志——分析重标数据时
                // 必须知道这套 H 是“机械手带相机走网格拍固定工件”还是“相机固定、工件走网格”，
                // 二者 H(u) 的消费公式完全不同（前者输出=机械手拍照位，后者输出=工件真位）。
                string eyeTag = IsPickPlaceProfile ? "PickPlace吸放" : (TargetProfile.EyeMode == EyeMode.EyeInHand ? "EyeInHand(相机随动)" : "EyeToHand(相机固定)");
                AppendLog($"[H拟合] 眼型={eyeTag} Base=({TargetProfile.BasePosX:F3},{TargetProfile.BasePosY:F3}) 步长=({GridStepX:F1},{GridStepY:F1}) U0={TargetProfile.CalibU0:F2} 拟合点数={CalibrationPoints.Count(p => p.IsCaptured)} 路径={OutputHomMatPath}");
                // 矩阵健康检查报告（两轴当量/正交性/行列式/网格重建）——不止 RMS，多维度度量标定质量
                if (res.Data != null && !string.IsNullOrWhiteSpace(res.Data.HealthReport))
                {
                    AppendLog(res.Data.HealthReport);
                }
                if (CalculatedRms > 1.0)
                {
                    // RMS 超过 1mm 几乎必然有数据质量问题——提示用户查日志定位坏点
                    AppendLog($"⚠ RMS={CalculatedRms:F2}mm 过大！常见原因："
                        + "\n  1) 某些点 Mark 识别命中了错误位置（中心亮边缘暗→阈值分割到噪声而非 Mark）"
                        + "\n  2) 程序未重新生成（旧代码有坐标偏移 bug / 未做灰度转换 / 第三步写死圆算法）"
                        + "\n  3) 步长 100mm 超出相机视野——Mark 走出半视野时圆度骤降被筛掉，兜底动态阈值可能抓到噪声"
                        + "\n  → 请对照上方逐点数据，检查哪个点的 Pixel 坐标明显偏离网格规律");
                    MessageBox.Show(
                        $"标定计算完成，但 RMS={CalculatedRms:F2}mm 过大（正常应 <0.1mm）！\n\n"
                        + "常见原因：\n"
                        + "1. 程序未重新生成——旧代码有坐标偏移 bug、未做灰度转换、第三步写死圆算法\n"
                        + "2. 某些点 Mark 识别命中了噪声而非真正的 Mark（中心亮边缘暗→全局阈值失效）\n"
                        + "3. 步长 100mm 可能超出相机视野——Mark 走出半视野时识别不准\n\n"
                        + "请查看向导日志中「九点标定拟合数据」的逐点 Pixel 坐标，"
                        + "检查哪个点的像素坐标明显偏离 3×3 网格规律。",
                        "RMS 过大警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show($"标定计算成功！\nRMS 重投影误差: {CalculatedRms:F5} mm", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                // 拟合完成后立即刷新发布前体检（进入步骤4/保存前都会再次刷新，此处保证即时可见）
                RunOnUi(RefreshPublishChecks);
            }
            else
            {
                AppendLog($"[计算失败] {res.Message}");
                MessageBox.Show("计算失败：" + res.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>保存标定方案到仓储（数据库/配置文件）</summary>
        public bool SaveProfile()
        {
            try
            {
                // ── P3 采样点快照落盘（2026-09-05）：校验台"残差反投影/现场复验"的数据源。
                //    把本次会话已采集点（世界命令位 + 像素识别位 + 可靠度）包成采样批次写进
                //    TargetProfile.Samples，随 po.Model 一并持久化；旧档案无 Samples → 校验台提示重标。
                var captured = CalibrationPoints.Where(p => p.IsCaptured).ToList();
                if (captured.Count > 0)
                {
                    var sample = new CalibrationSampleModel
                    {
                        Points = new System.Collections.ObjectModel.ObservableCollection<CalibrationPointModel>(captured)
                    };
                    TargetProfile.Samples = new System.Collections.Generic.List<CalibrationSampleModel> { sample };
                }
                else if (TargetProfile.Samples == null)
                {
                    TargetProfile.Samples = new System.Collections.Generic.List<CalibrationSampleModel>();
                }

                TargetProfile.UpdatedAt = DateTime.Now;
                var po = new CalibrationProfilePo
                {
                    Id = string.IsNullOrWhiteSpace(TargetProfile.Id) ? Guid.NewGuid().ToString("N") : TargetProfile.Id,
                    ProfileName = TargetProfile.Name,
                    CalibrationType = TargetProfile.Type,
                    BoundStationCode = TargetProfile.BoundStationCode,
                    BoundDeviceId = TargetProfile.BoundDeviceId,
                    IsCalibrated = TargetProfile.IsCalibrated,
                    Model = TargetProfile
                };
                // 存在则更新，不存在插入
                var existing = _calibrationProfileRepository.GetById(po.Id) ?? _calibrationProfileRepository.GetByName(TargetProfile.Name);
                bool saved = existing == null ? _calibrationProfileRepository.Insert(po) : _calibrationProfileRepository.Update(po);
                if (!saved)
                {
                    AppendLog("[警告] 标定方案保存返回失败。");
                }
                return saved;
            }
            catch (Exception ex)
            {
                AppendLog("[错误] 标定方案保存异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把标定矩阵从生成时的 %TEMP% 暂存路径，幂等落盘到"设备级"持久目录
        /// Recipes\Devices\{BoundDeviceId|Default}\Calib\{方案名}_HandEye.tup（P1-3，2026-09-04）。
        /// 收敛在向导内部"✔ 完成并保存"链路：保存前先落盘并回填路径，
        /// 杜绝"profile 显示已标定、矩阵文件却在 %TEMP% 重启即丢"的空窗。
        /// 管理页 OpenWizard 回调里的同类落盘保留为幂等兜底（源==目标时直接返回）。
        /// </summary>
        private void EnsureMatrixPersisted()
        {
            _matrixPersistFailed = false;
            if (string.IsNullOrWhiteSpace(OutputHomMatPath) || !File.Exists(OutputHomMatPath))
            {
                return;
            }
            string device = string.IsNullOrWhiteSpace(TargetProfile.BoundDeviceId) ? "Default" : TargetProfile.BoundDeviceId;
            string targetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes", "Devices", device, "Calib");
            string targetFile = Path.Combine(targetDir, $"{SanitizeFileName(TargetProfile.Name)}_HandEye.tup");
            try
            {
                Directory.CreateDirectory(targetDir);
                if (string.Equals(Path.GetFullPath(OutputHomMatPath), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                {
                    return; // 已在目标位
                }
                var saveRes = CalibService.SaveHomMatFile(OutputHomMatPath, targetFile);
                if (!saveRes.Success)
                {
                    // 目标文件可能被在线校验/流程节点短暂读取占用——等待后重试一次
                    Thread.Sleep(300);
                    saveRes = CalibService.SaveHomMatFile(OutputHomMatPath, targetFile);
                }
                if (!saveRes.Success)
                {
                    _matrixPersistFailed = true;
                    AppendLog($"[落盘] 矩阵自动落盘设备目录失败：{saveRes.Message}；暂保留 %TEMP% 副本（保存后请用管理页『保存并应用』发布）。");
                    return;
                }
                OutputHomMatPath = targetFile;
                TargetProfile.HomMatFilePath = targetFile;
                AppendLog($"[落盘] 标定矩阵已落盘设备目录：{targetFile}");
            }
            catch (Exception ex)
            {
                _matrixPersistFailed = true;
                AppendLog($"[落盘] 矩阵自动落盘异常：{ex.Message}");
            }
        }

        /// <summary>文件名清洗：替换 Windows 非法文件名字符（防方案名含冒号/斜杠等导致落盘失败）</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Calibration";
            }
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        /// <summary>
        /// 保存标定结果并关闭向导。
        /// 门控（P1-1 2026-09-04）：发布前体检 红项=阻断保存，黄项=弹窗确认后才允许；
        /// 保存前先 EnsureMatrixPersisted 落盘（P1-3），落盘后重刷体检使"矩阵已落盘"项变绿。
        /// </summary>
        // ==================== t（EyeInHand 间接对针）执行：示教 R_n → 抬Z抓拍 → 点选 → 结算 TCO（2026-09-08） ====================
        // 语义：工具头尖压住工件特征(回转中心=R_n，相机拍不到工具尖没关系) → 抬 Z 回标定高度 XY 不动 →
        //       相机拍到同一特征得像素 u_feature(=p_tip) → TCO = H(u_feature) − R_n。
        // 结算时把 三元组（NozzleAlignX/Y=R_n + ToolAlignPixelX/Y=p_tip + ToolOffsetWx/Wy=TCO·EyeInHandIndirect）
        // 一并写入档案 —— 校验台消费端 ToolAlignReady/NozzleAlignReady 直接置真，日常只点 u_click 即可走位。

        /// <summary>EIH 间接对针 · 示教基准位：读当前机械 XYZ 写入 NozzleAlignX/Y/Z（R_n 三元含压住高度）</summary>
        private void RecordToolOffsetOrigin()
        {
            if (!IsEihToolOffsetSession || TargetProfile == null) return;
            var pose = ReadCurrentPose(out string failMsg);
            if (!pose.HasValue)
            {
                _lastOriginFail = true;
                _lastOriginFailMsg = string.IsNullOrWhiteSpace(failMsg) ? "读不到当前位置" : failMsg;
                AppendLog("记基准位 R_n 失败：" + _lastOriginFailMsg);
                OnPropertyChanged(nameof(ToolOffsetOriginStatusText));
                OnPropertyChanged(nameof(ToolOffsetOriginStatusBrush));
                return;
            }
            TargetProfile.NozzleAlignX = pose.Value.X; // setter 自动置 IsNozzleAlignSet
            TargetProfile.NozzleAlignY = pose.Value.Y;
            TargetProfile.NozzleAlignZ = pose.Value.Z; // 压住高度：校验台到位目视下压目标
            _lastOriginFail = false;
            string uWarn = string.Empty;
            if (TargetProfile.CalibU0.HasValue
                && Math.Abs(pose.Value.U - TargetProfile.CalibU0.Value) > 2.0)
            {
                uWarn = $" ⚠ 当前回转角 U={pose.Value.U:F1}°≠U0={TargetProfile.CalibU0.Value:F1}°——请把 U 转回 U0 后重记（TCO 以 U0 为参考系）";
            }
            AppendLog($"[记基准位 R_n] R_n=({TargetProfile.NozzleAlignX:F3},{TargetProfile.NozzleAlignY:F3})mm Z={TargetProfile.NozzleAlignZ:F1}mm（工具头尖轻压工件特征时回转中心 XYZ；Z=压住高度）—— 下一步进『间接对针』步抓拍点选。{uWarn}");
            OnPropertyChanged(nameof(ToolOffsetOriginStatusText));
            OnPropertyChanged(nameof(ToolOffsetOriginStatusBrush));
            OnPropertyChanged(nameof(IsToolOffsetOriginSet));
            RaiseNextGate(); // DefineOrigin 门禁=IsNozzleAlignSet，记完须重算 Next
        }

        /// <summary>
        /// EIH 间接对针 · 抓拍（XY 保持压住位；2026-09-08 泛化：按档案相机安装特性 CameraMovesWithZ 分支）。
        /// 相机随 Z（FollowsZ/未声明保守）→ 像素当量是 Z 高度的函数：抬 Z 回标定高度 CalibZ 抓拍
        /// （当量纪律 + 同时抬离被压特征）；相机固定（不随 Z）→ 成像与 Z 无关，抬 Z 只为露特征：
        /// 仍压住（距 R_nZ≤3mm）→ 抬到最高 0；已离开 → 不动直接抓拍。
        /// </summary>
        private void CaptureAlignEihFrame()
        {
            if (!IsEihToolOffsetSession || TargetProfile == null) return;
            if (!TargetProfile.IsNozzleAlignSet)
            {
                AppendLog("抓拍被跳过：尚未记录基准位 R_n —— 请先在『示教基准位』步 JOG 工具头尖压住特征并点【📍 记基准位 R_n】。");
                return;
            }
            var pose = ReadCurrentPose(out _);
            bool camFollowsZ = ResolveCameraMount(emitLog: true);
            if (camFollowsZ)
            {
                // 随 Z 机型：拍照高度必须与九点标定高度一致（HomMat 只在该高度的放大率下成立）。
                // ⚠ 2026-09-09：这里不代劳搬轴（操作员可能正停在某处查看），只把"这张图是在哪个高度拍的"
                //   记下来，结算时按【成像高度】做门禁 —— 旧实现按"结算时刻的当前 Z"判，被"点完再抬 Z"绕过。
                if (pose.HasValue && TargetProfile.CalibZ.HasValue
                    && Math.Abs(pose.Value.Z - TargetProfile.CalibZ.Value) > 1.0)
                {
                    AppendLog($"[间接对针] ⚠ 相机随 Z 升降：抓拍高度 Z={pose.Value.Z:F1}mm ≠ 标定高度 {TargetProfile.CalibZ.Value:F1}mm"
                              + " —— 本张图的放大率与九点标定不一致，画面上点的像素【不能】用于结算。"
                              + $"请把 Z 移到 {TargetProfile.CalibZ.Value:F1}mm 后重新抓拍并重新点选。");
                }
            }
            else
            {
                // 固定相机机型：成像与 Z 无关；抬 Z 仅为让尖离开被压特征露出成像
                bool stillPressed = false;
                if (pose.HasValue)
                {
                    double anchorZ = TargetProfile.NozzleAlignZ ?? double.NaN;
                    stillPressed = !double.IsNaN(anchorZ)
                        ? pose.Value.Z <= anchorZ + 3.0            // 距压住位 ≤3mm 视为尖仍压/贴工件
                        : pose.Value.Z < (TargetProfile.CalibZ ?? 0.0) - 3.0; // 旧档案无锚 Z：Z 低于标定高度视为贴工件
                }
                if (stillPressed)
                {
                    AppendLog($"[间接对针] 工具头尖仍压住特征（当前 Z={pose.Value.Z:F1}mm≈压住位）—— 抬 Z 至 0（最高，尖完全离件、特征露出）后抓拍，XY 保持不动。相机固定、成像与 Z 无关。");
                    if (!MoveZTo(0.0)) return;
                }
                else
                {
                    AppendLog("[间接对针] Z 已离开工件（相机固定、成像与 Z 无关）—— 不动 Z，直接抓拍。");
                }
            }
            if (CaptureAndDisplayFrame("间接对针·抓拍", true))
            {
                // 记录【这张图】对应的机位；新图作废旧点选，防止跨图混点
                var pose2 = ReadCurrentPose(out _);
                _alignGrabPose = pose2;
                _alignPickSet = false;
                _alignPickU = double.NaN;
                string zTag = (pose2.HasValue && !double.IsNaN(pose2.Value.Z)) ? $"（成像高度 Z={pose2.Value.Z:F1}mm）" : "";
                AppendLog($"[间接对针] 抓拍完成{zTag}。请在左侧画面单击【工具头尖刚才压住的那个特征】（黄十字标记确认），再点【🔍 结算 TCO】。");
                if (camFollowsZ && pose2.HasValue && TargetProfile.CalibZ.HasValue
                    && Math.Abs(pose2.Value.Z - TargetProfile.CalibZ.Value) > 1.0)
                {
                    AppendLog($"[间接对针] ⚠ 本张图成像高度 Z={pose2.Value.Z:F1}mm ≠ 标定高度 {TargetProfile.CalibZ.Value:F1}mm —— 这张图上点选的像素结算时会被拒绝。");
                }
            }
        }

        /// <summary>AlignTool 步图像点选入口：code-behind 把 HalconDisplayAlign 鼠标事件换算成图像坐标后调用</summary>
        public void SetAlignFeaturePixel(double row, double col)
        {
            if (!IsEihToolOffsetSession) return;
            _alignPickCol = col;
            _alignPickRow = row;
            _alignPickSet = true;
            // 顺手记下点选时刻的 U：求 e 时按 R(U0−U) 归一要用它（读不到保持 NaN，结算时退化用当前 U）
            var pu = ReadCurrentPose(out _);
            _alignPickU = (pu.HasValue && !double.IsNaN(pu.Value.U)) ? pu.Value.U : double.NaN;
            AppendLog($"[间接对针·点选] 特征像素 (col={col:F1}, row={row:F1}) —— 该像素即对准像素 p_tip，可点【🔍 结算 TCO】。"
                      + (double.IsNaN(_alignPickU) ? "" : $"（记录点选时 U={_alignPickU:F1}°，用于 e 的角度归一）"));
            OnPropertyChanged(nameof(ToolOffsetAlignPickText));
        }

        /// <summary>
        /// 同工位档案里找旋转中心 O（标定体系按"量"分档：H / e / t 可能各自建档，
        /// 对针 t 档案上不一定有 O）。优先取 HasRotationCenter 且最近更新的那一份。
        /// </summary>
        private (bool Found, double X, double Y, string Source) TryResolveRotationCenterFromSiblings()
        {
            try
            {
                if (TargetProfile == null || string.IsNullOrWhiteSpace(TargetProfile.BoundStationCode))
                {
                    return (false, 0, 0, null);
                }
                string station = TargetProfile.BoundStationCode;
                string myId = TargetProfile.Id;
                var candidates = new System.Collections.Generic.List<(DateTime Updated, double X, double Y, string Name)>();
                foreach (var po in _calibrationProfileRepository.GetAll())
                {
                    if (po?.Model == null) continue;
                    if (!string.Equals(po.BoundStationCode, station, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(myId) && string.Equals(po.Model.Id, myId, StringComparison.Ordinal)) continue;
                    if (!po.Model.HasRotationCenter) continue;
                    candidates.Add((po.Model.UpdatedAt, po.Model.ToolCenterWx, po.Model.ToolCenterWy, po.ProfileName));
                }
                if (candidates.Count == 0) return (false, 0, 0, null);
                var best = candidates.OrderByDescending(c => c.Updated).First();
                return (true, best.X, best.Y, best.Name);
            }
            catch (Exception ex)
            {
                AppendLog("[间接对针·结算] 同工位档案查找旋转中心失败：" + ex.Message);
                return (false, 0, 0, null);
            }
        }

        /// <summary>
        /// 【九点同工位共享】2026-09-09。
        /// e / t 会话本身不产出 HomMat；若本档案没有矩阵文件，到同工位其它档案里找一份已发布的
        /// 九点结果直接复用（顺带继承 CalibZ / BasePos / U0 / 相机安装特性），避免同一工位反复重采九点。
        /// 返回值 true=已复用外部九点（调用方不要再要求采样九点）。
        /// </summary>
        private bool TryReuseSharedNinePoint()
        {
            if (TargetProfile == null) return false;
            // H 会话（九点标定本身）必须自己采样
            if (IsToolOffsetSession == false && IsRotationSession == false) return false;
            if (!string.IsNullOrWhiteSpace(OutputHomMatPath) && File.Exists(OutputHomMatPath)) return true;
            if (!string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath) && File.Exists(TargetProfile.HomMatFilePath)) return true;
            if (string.IsNullOrWhiteSpace(TargetProfile.BoundStationCode)) return false;

            try
            {
                var donors = _calibrationProfileRepository.GetAll()
                    .Where(po => po?.Model != null
                                 && string.Equals(po.BoundStationCode, TargetProfile.BoundStationCode, StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrEmpty(po.Model.Id)
                                 && !string.Equals(po.Model.Id, TargetProfile.Id, StringComparison.Ordinal)
                                 && !string.IsNullOrWhiteSpace(po.Model.HomMatFilePath)
                                 && File.Exists(po.Model.HomMatFilePath))
                    .OrderByDescending(po => po.Model.UpdatedAt)
                    .ToList();
                if (donors.Count == 0)
                {
                    AppendLog("[九点共享] 本工位还没有已发布的九点矩阵 —— 本任务仍需完成一次九点标定（之后同工位其它任务可直接复用）。");
                    return false;
                }

            var donor = donors[0].Model;
            string donorName = donors[0].ProfileName;
            OutputHomMatPath = donor.HomMatFilePath;
            TargetProfile.HomMatFilePath = donor.HomMatFilePath;
            if (TargetProfile.CalibZ == null && donor.CalibZ != null) TargetProfile.CalibZ = donor.CalibZ;
            if (!TargetProfile.IsBasePosSet && donor.IsBasePosSet)
            {
                TargetProfile.BasePosX = donor.BasePosX;
                TargetProfile.BasePosY = donor.BasePosY;
            }
            if (TargetProfile.CalibU0 == null && donor.CalibU0 != null) TargetProfile.CalibU0 = donor.CalibU0;
            TargetProfile.CameraMovesWithZ = donor.CameraMovesWithZ;

                AppendLog($"[九点共享] ♻ 已复用同工位《{donorName}》的九点结果，本任务无需重采九点：{donor.HomMatFilePath}");
                if (donors.Count > 1)
                {
                    AppendLog($"[九点共享] 提示：本工位共有 {donors.Count} 份九点矩阵，取最近更新的一份；"
                              + "如需指定某一套，请先在标定管理里把它设为最新。");
                }
                return true;
            }
            catch (Exception ex)
            {
                AppendLog("[九点共享] 查找同工位九点结果失败：" + ex.Message);
                return false;
            }
        }

        /// <summary>EIH 间接对针 · 结算：连 p_tip 一并落库；并结算真吸嘴偏心 e = O − H(p_tip)</summary>
        private void SettleToolOffsetEih()
        {
            if (!IsEihToolOffsetSession || TargetProfile == null) return;
            if (!TargetProfile.IsNozzleAlignSet)
            {
                AppendLog("[间接对针·结算] 未记录基准位 R_n：请回『示教基准位』步记 R_n。");
                return;
            }
            if (!_alignPickSet)
            {
                AppendLog("[间接对针·结算] 未点选特征像素：请先在画面单击特征（抬Z后相机拍到的那一个）再结算。");
                return;
            }
            // ★ Z 门禁（2026-09-09 重写）：相机安装特性不再是"本档案一个字段"——
            //   ① 本会话显式选择 → ② 本档案声明 → ③ 继承同工位其它档案 → ④ 未声明保守=true(随 Z)。
            //   门禁必须比较【点选像素所属那张图】的高度；用"结算时的当前 Z"会被"点完再抬 Z"绕过
            //   （9-9 实机：Z=-10 拍照点选 → 抬到 -130 再结算，门禁放行 → 放大率不一致、e 直接报废）。
            var mount = ResolveCameraMount();
            if (mount && TargetProfile.CalibZ.HasValue && _alignGrabPose.HasValue)
            {
                double zGrab = _alignGrabPose.Value.Z;
                if (Math.Abs(zGrab - TargetProfile.CalibZ.Value) > 1.0)
                {
                    AppendLog($"[间接对针·结算] ✖ 相机随 Z 升降：抓拍时高度 Z={zGrab:F1}mm ≠ 标定高度 {TargetProfile.CalibZ.Value:F1}mm。\n"
                              + $"  请把 Z 移到 {TargetProfile.CalibZ.Value:F1}mm 后【重新抓拍 + 重新点选】再结算；\n"
                              + "  （若本机相机其实固定在小臂基座不随 Z 升降，请勾选『🚫 相机不随 Z』后再结算。）");
                    _alignPickSet = false;   // 作废这次点选（必须重拍重选，不能拿错高度的像素去结算）
                    return;
                }
            }
            // 像素→机械映射源：优先本会话 H 拟合输出，否则【同工位九点共享】，最后才用本档案已发布矩阵
            TryReuseSharedNinePoint();
            if (string.IsNullOrWhiteSpace(OutputHomMatPath))
            {
                if (!string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                    && File.Exists(TargetProfile.HomMatFilePath))
                {
                    OutputHomMatPath = TargetProfile.HomMatFilePath;
                    AppendLog($"[间接对针] 复用档案 H 矩阵做像素→机械映射：{TargetProfile.HomMatFilePath}");
                }
                else
                {
                    AppendLog("[间接对针·结算] 无可用 H 矩阵（档案 HomMatFilePath 缺失/文件不在）——请先完成并保存 H 段会话（矩阵已发布）后再对针。");
                    return;
                }
            }
            var map = CalibService.MapPixelToWorld(OutputHomMatPath, _alignPickCol, _alignPickRow);
            if (!map.Success)
            {
                AppendLog("[间接对针·结算] 像素→机械映射异常：" + map.Message);
                return;
            }
            double wX = map.Data.WorldX, wY = map.Data.WorldY;
            double tcoX = wX - TargetProfile.NozzleAlignX;
            double tcoY = wY - TargetProfile.NozzleAlignY;

            // ★ 2026-09-08 定案：真吸嘴偏心 e = O − H(p_tip)（U0 参考）。
            //   O=ToolCenterW 由旋转中心标定（三点定圆只取圆心，与延伸杆长度/偏心无关）提供；
            //   p_tip=本次对针点选像素。F0（九点特征世界位）在该式中自动消掉，无需显式求。
            //   ⚠ 旧量 TCO=H(p_tip)−R_n 含绝对坐标 R_n，已证伪（仿真 451mm 误差），仅保留兼容与对照。
            double u0 = TargetProfile.CalibU0 ?? 0.0;
            // 归一角优先取【点选时刻】的 U（_alignPickU），其次才是结算时的 U，都没有才按 U0
            double uAlign = u0;
            string uSrc = "U0(默认)";
            if (!double.IsNaN(_alignPickU))
            {
                uAlign = _alignPickU;
                uSrc = "点选时刻";
            }
            else
            {
                var poseNow = ReadCurrentPose(out _);
                if (poseNow.HasValue && !double.IsNaN(poseNow.Value.U))
                {
                    uAlign = poseNow.Value.U;
                    uSrc = "结算时刻(未记点选U)";
                }
            }

            // ★ 旋转中心 O 可能与对针 t 不在同一档案（标定体系按"量"分档：H / e / t）。
            //   本档案没有 O 时，到同工位的其它档案里找（优先 HasRotationCenter 且最近更新），
            //   找到就复制进本档案 → 让 t 档案自包含，校验台/发布链无需再跨档案查找。
            if (!TargetProfile.HasRotationCenter)
            {
                var inherited = TryResolveRotationCenterFromSiblings();
                if (inherited.Found)
                {
                    TargetProfile.ToolCenterWx = inherited.X;
                    TargetProfile.ToolCenterWy = inherited.Y;
                    TargetProfile.HasRotationCenter = true;
                    AppendLog($"[间接对针·结算] 本档案无旋转中心 O，已从同工位档案「{inherited.Source}」继承 O=({inherited.X:F3},{inherited.Y:F3}) 并写入本档案。");
                }
            }
            if (TargetProfile.HasRotationCenter)
            {
                var ecc = CalibrationGeometry.SolveNozzleEcc(
                    TargetProfile.ToolCenterWx, TargetProfile.ToolCenterWy, wX, wY, uAlign, u0);
                TargetProfile.ToolOffsetPureWx = ecc.X;
                TargetProfile.ToolOffsetPureWy = ecc.Y;
                TargetProfile.IsNozzleEccCalibrated = true;
                AppendLog($"[间接对针·结算] ★ 真吸嘴偏心 e = O − H(p_tip) = ({TargetProfile.ToolCenterWx:F3},{TargetProfile.ToolCenterWy:F3}) − ({wX:F3},{wY:F3})"
                          + $" = ({ecc.X:F3},{ecc.Y:F3})mm（U0={u0:F1}° 参考，对针角 U={uAlign:F1}°[{uSrc}]）—— 已落库，供消费式 P_go = X_obj − R(U−U0)·e 使用。");
                if (Math.Abs(uAlign - u0) > 2.0)
                {
                    AppendLog($"[间接对针·结算] ⚠ 对针角 U={uAlign:F1}°≠U0={u0:F1}°：e 已按 R(U0−U) 归一到 U0 参考，但建议今后在 U0 对针以减小换算误差。");
                }
            }
            else
            {
                TargetProfile.IsNozzleEccCalibrated = false;
                AppendLog("[间接对针·结算] ⚠ 档案缺少旋转中心 O（未完成旋转标定）：无法求真吸嘴偏心 e。\n"
                          + "  · U 恒=U0 作业时仍可用差分式 P_go = P_photo + H(p_tip) − H(u)（不需要 e）；\n"
                          + "  · 一旦 U 要旋转，必须先补做旋转中心标定（三点定圆，延伸杆长度不影响）。");
            }
            // ★ 三元组一并落库：p_tip(=u_feature) + TCO（R_n 已在示教步写入 NozzleAlignX/Y）
            TargetProfile.ToolAlignPixelX = _alignPickCol;
            TargetProfile.ToolAlignPixelY = _alignPickRow;
            TargetProfile.ApplyToolOffset(tcoX, tcoY, ToolOffsetMethod.EyeInHandIndirect);
            _alignPickSet = false; // 结算后清除点选态，防重复结算；状态以落库结果为准
            _alignPickU = double.NaN;
            AppendLog($"[间接对针·结算] TCO=H(u_feature)−R_n=({wX:F3},{wY:F3})−({TargetProfile.NozzleAlignX:F3},{TargetProfile.NozzleAlignY:F3})=({tcoX:F3},{tcoY:F3})mm"
                      + $"；p_tip=({TargetProfile.ToolAlignPixelX:F1},{TargetProfile.ToolAlignPixelY:F1}) 已随 TCO 落库（方法=EyeInHandIndirect）。"
                      + "校验台打开即 Ready：日常只需 放工件→抓拍→点选 u_click→到位→目视判定。");
            OnPropertyChanged(nameof(ToolOffsetAlignPickText));
            OnPropertyChanged(nameof(ToolOffsetSettledStatusText));
            RaiseNextGate();      // AlignTool 门禁=IsToolOffsetCalibrated，结算完须重算 Next
            NotifyStepPanelUi();
        }

        private void SaveResult()
        {
            // ★ P2 单量段（e/t）保存分叉：本会话不产出 HomMat——跳过矩阵体检/落盘链（其判据按 H 口径，
            //   会把 e 会话误阻断成"矩阵未生成"红项）。旋转/偏距结果已在拟合时写入 TargetProfile，直接落库。
            if (IsRotationSession || IsToolOffsetSession)
            {
                bool computed = IsRotationSession ? _fitComputedOnce : TargetProfile.IsToolOffsetCalibrated;
                if (!computed)
                {
                    string msg = IsToolOffsetSession
                        ? "尚未完成 TCP 对针。请先在『间接对针』步完成：示教基准位 R_n(压点含 Z) → 抬Z露出特征抓拍 → 点选特征 → 结算 TCO。"
                        : "尚未完成本段拟合计算。请先在『计算』步骤执行拟合（旋转采样 ≥3 点且角度覆盖 ≥90°）。";
                    MessageBox.Show(msg, IsToolOffsetSession ? "未完成对针" : "未完成拟合",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveProfile();
                // P4 口径：e/t 段会话保存后【不】自动打发布标记——其发布依赖跨档案 H 状态
                // （任务卡 DepOk 门禁），由标定中心任务卡动作在依赖 H 已发布后执行（PublishCard）。
                // 若此处自动发布，H 未发时会出现"卡 Published 但依赖 ✗"的矛盾编排。
                AppendLog(IsRotationSession
                    ? "[保存] e 段会话完成：旋转中心/偏心结果已写入方案（矩阵仍属 H 段，未被改动）。发布请在任务卡上执行。"
                    : "[保存] t 段会话完成：对针偏距 ToolOffset 已写入方案。发布请在任务卡上执行。");
                if (_owner != null)
                {
                    IsSessionCompleted = true; // 2026-09-06 非模态：DialogResult 仅对 ShowDialog 窗口可用
                    _owner.Close();
                }
                return;
            }

            // 像素当量标定无 HomMat 矩阵文件，直接保存
            if (TargetProfile.Type == CalibrationType.PixelScale)
            {
                // P4 自动发布：v2 s 会话完成即打发布标记（像素当量无矩阵体检域；卡上无需再手动发布）
                if (IsSessionV2 && _sessionSpec != null)
                {
                    CalibrationCardDeriver.AppendPublishMarker(TargetProfile, CalibrationQuantity.PixelScale, false,
                        "向导末步完成（像素当量 s）自动发布");
                    AppendLog("[发布] s 会话完成自动发布留痕（q=s）。");
                }
                SaveProfile();
                if (_owner != null)
                {
                    IsSessionCompleted = true; // 2026-09-06 非模态
                    _owner.Close();
                }
                return;
            }

            // ① 第一遍体检：以当前状态判定红/黄（矩阵未生成 / RMS>1 / 旋转缺失等都在这时暴露）
            RefreshPublishChecks();
            var reds = PublishChecks.Where(c => c.Level == 2).ToList();
            if (reds.Count > 0)
            {
                string list = string.Join(Environment.NewLine + "· ", reds.Select(r => r.Text));
                AppendLog("[保存阻断] 发布前体检存在红项：\n· " + list);
                MessageBox.Show(
                    $"发布前体检有 {reds.Count} 项未通过（红色），已阻止保存：\n\n· {list}\n\n"
                    + "请先回到对应步骤修正（红项原因都写在上方，常见：未执行拟合计算 / RMS 严重超差 / 带旋转类型没做旋转采样或角度覆盖不足 / 矩阵镜像)。",
                    "保存被阻断", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // ② 吸放式姿态同步：固定拍照位(X/Y/Z/U)每次回位一致=等效固定相机，直接作为本次标定姿态落档
            //    （九点/九点+旋转已在采样时实测写入 CalibZ/CalibU0）
            if (IsPickPlaceProfile && TargetProfile.IsPhotoPoseSet)
            {
                if (!TargetProfile.CalibZ.HasValue)
                {
                    TargetProfile.CalibZ = TargetProfile.PhotoPoseZ;
                }
                if (!TargetProfile.CalibU0.HasValue)
                {
                    TargetProfile.CalibU0 = TargetProfile.PhotoPoseU;
                }
            }

            // ③ 落盘 %TEMP% → 设备目录（P1-3），失败会在下一遍体检变黄项提示
            EnsureMatrixPersisted();
            RefreshPublishChecks();

            // ④ 黄项：弹窗确认后才放行（默认 No，防止误回车把不理想的数据入库）
            var yellows = PublishChecks.Where(c => c.Level == 1).ToList();
            if (yellows.Count > 0)
            {
                string list = string.Join(Environment.NewLine + "· ", yellows.Select(r => r.Text));
                var confirm = MessageBox.Show(
                    $"发布前体检有 {yellows.Count} 项黄色提醒：\n\n· {list}\n\n"
                    + "仍要保存吗？（建议先处理黄项再保存；选“否”返回修正）",
                    "确认保存", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (confirm != MessageBoxResult.Yes)
                {
                    AppendLog("[保存] 操作员因黄色提醒取消保存。");
                    return;
                }
            }

            // P4 自动发布：v2 H 会话末步体检（红阻断→已 return / 黄确认→放行）通过 → 打发布标记随库落盘，
            //   任务卡直接 Published，无需回标定中心再点一次"发布"。e/t 段会话不打自动标记
            //   （其发布依赖跨档案 H 状态（DepOk），由任务卡动作在依赖满足后发布——见 SaveResult e/t 出口注释）。
            if (IsSessionV2 && _sessionSpec != null && _sessionSpec.Quantity == CalibrationQuantity.HandEye)
            {
                CalibrationCardDeriver.AppendPublishMarker(TargetProfile, CalibrationQuantity.HandEye, false,
                    "向导末步发布前体检通过，自动发布");
                AppendLog("[发布] H 会话自动发布留痕（q=H）。");
            }
            SaveProfile();
            AppendLog("[保存] 标定方案已保存并落盘。");
            if (_owner != null)
            {
                IsSessionCompleted = true; // 2026-09-06 非模态
                _owner.Close();
            }
        }

        /// <summary>向导上一步</summary>
        private void PrevStep()
        {
            if (CurrentStep > 0)
            {
                CurrentStep--;
            }
        }

        /// <summary>
        /// 跳到拟合步（AutoRunAll 采集完成后由策略调用，替代硬编码 CurrentStep=3）：
        /// v2 会话定位到首个 ComputeFit 步骤（若模板无拟合角色则末步）；旧兼容四步 = 第 4 步，行为不变。
        /// </summary>
        public void GoToComputeStep()
        {
            int target = _stepItems.Count - 1;
            for (int i = 0; i < _stepItems.Count; i++)
            {
                if (_stepItems[i].Role == WizardStepRole.ComputeFit)
                {
                    target = i;
                    break;
                }
            }
            if (target >= 0)
            {
                CurrentStep = target;
            }
        }

        /// <summary>向导下一步（v2 会话含步骤门禁：当前步未就绪不得前进）</summary>
        private void NextStep()
        {
            if (CanAdvanceFrom(CurrentStep))
            {
                CurrentStep++;
            }
            else if (_stepItems.Count > 0 && CurrentStep < _stepItems.Count)
            {
                string reason = DescribeBlockReason(_stepItems[CurrentStep].Role);
                if (!string.IsNullOrEmpty(reason))
                {
                    AppendLog("[步骤门禁] 未满足当前步完成条件：" + reason);
                }
            }
            NotifySamplingUi();
            RefreshGeometryChecks();
        }

        /// <summary>写入日志，自动带上时间，通知UI更新LogText；并镜像到 LogBus（VS 输出窗口/日志文件可见，调试取证用）</summary>
        public void AppendLog(string message)
        {
            // 镜像到 LogBus：与 [Info] [CalibrationService] 同通道，VS 输出窗口可直接看到（2026-09-06）
            try
            {
                Grayson.Vision.Contracts.Infrastructure.Logging.LogBus.Info("CalibrationWizard", message);
            }
            catch { /* 日志镜像失败不影响主流程 */ }

            // 线程安全：采样流程在后台线程跑，日志 UI 更新统一回 UI 线程
            RunOnUi(() =>
            {
                _log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                OnPropertyChanged(nameof(LogText));
            });
        }

        #region 可视化辅助（采样覆盖格/进度/残差/几何自检/复制排障，2026-09-03）

        /// <summary>九点覆盖格（绿=已采 红=缺失/跳过 灰=未采）——Step 采样页顶部 3×3 显示</summary>
        public ObservableCollection<CalibCoverageCellModel> NineCells { get; } = new ObservableCollection<CalibCoverageCellModel>();

        private string _samplingProgressText = "平移 0/9 · 旋转 0/5";
        /// <summary>采样进度文字（胶囊显示）</summary>
        public string SamplingProgressText
        {
            get => _samplingProgressText;
            private set => Set(ref _samplingProgressText, value);
        }

        private string _rotationFitSummaryText = "";
        /// <summary>旋转/偏心拟合摘要（圆心/半径/偏心，拟合后更新）</summary>
        public string RotationFitSummaryText
        {
            get => _rotationFitSummaryText;
            private set => Set(ref _rotationFitSummaryText, value);
        }

        private string _residualSummaryText = "";
        /// <summary>逐点残差总览（拟合后更新，超阈值标红点名列）</summary>
        public string ResidualSummaryText
        {
            get => _residualSummaryText;
            private set => Set(ref _residualSummaryText, value);
        }

        /// <summary>逐点残差行（结果页展示：点号/像素/偏差mm/是否告警）</summary>
        public ObservableCollection<CalibResidualRowModel> ResidualRows { get; } = new ObservableCollection<CalibResidualRowModel>();

        /// <summary>几何自检项（吸放式/通用）：坐标已设/行程/Z 序/真空通道</summary>
        public ObservableCollection<CalibCheckItemModel> GeometryChecks { get; } = new ObservableCollection<CalibCheckItemModel>();

        /// <summary>俯视示意小地图点（吸/拍/网格/中心；Canvas 坐标已换算）</summary>
        public ObservableCollection<CalibDotModel> GeometryMapDots { get; } = new ObservableCollection<CalibDotModel>();

        /// <summary>旋转/偏心拟合散点（采样点/圆心/偏心端；Canvas 坐标已换算）</summary>
        public ObservableCollection<CalibDotModel> RotationPlotDots { get; } = new ObservableCollection<CalibDotModel>();

        /// <summary>俯视示意图例文字</summary>
        public string GeometryMapLegend => "蓝=吸取位 · 青=拍照位 · 橙=网格中心 · 灰=网格点";

        /// <summary>旋转图例文字</summary>
        public string RotationPlotLegend => "蓝=角度采样点 · 红=圆心 · 橙=偏心端(θ=0)";

        // ================= e 会话旋转残差明细（P3：逐角度偏差表 + 圆拟合 RMS） =================

        /// <summary>旋转残差逐点行（e 会话计算页；角度/半径/径向偏差，IsWarn=&gt;0.5px 标红）</summary>
        public class RotationResidualDisplayRow
        {
            public int Index { get; set; }
            public string AngleText { get; set; }
            public string RadiusText { get; set; }
            public string ResidualText { get; set; }
            public bool IsWarn { get; set; }
        }

        /// <summary>旋转残差行集合</summary>
        public ObservableCollection<RotationResidualDisplayRow> RotationResidualRows { get; } =
            new ObservableCollection<RotationResidualDisplayRow>();

        public bool HasRotationResidual => RotationResidualRows.Count > 0;

        private string _rotationResidualSummaryText = "";
        /// <summary>旋转残差总览（拟合后更新；e 会话发布/体检数据源）</summary>
        public string RotationResidualSummaryText
        {
            get => _rotationResidualSummaryText;
            private set => Set(ref _rotationResidualSummaryText, value);
        }

        /// <summary>
        /// 用当前 RotationPoints（像素域）重算旋转圆拟合残差（复用 Contracts RotationResidualCalculator）。
        /// 圆拟合坐标域 = 采样像素域；行 IsWarn 阈值 0.5px（相对平均半径）。
        /// </summary>
        private void RefreshRotationResiduals()
        {
            RotationResidualRows.Clear();
            var pts = new List<RotationResidualPoint>();
            foreach (var r in RotationPoints)
            {
                if (r.IsCaptured)
                {
                    pts.Add(new RotationResidualPoint { AngleDeg = r.AngleDeg, X = r.PixelX, Y = r.PixelY });
                }
            }
            var rep = RotationResidualCalculator.Compute(pts);
            if (rep.PointCount < 3)
            {
                RotationResidualSummaryText = string.Empty;
                OnPropertyChanged(nameof(HasRotationResidual));
                return;
            }
            RotationResidualSummaryText =
                $"拟合圆心 ≈ ({rep.CenterX:F2}, {rep.CenterY:F2}) px · 平均半径 {rep.MeanRadius:F2} px · " +
                $"残差 RMS = {rep.RmsResidual:F3} px · MAX = {rep.MaxResidual:F3} px @ {rep.MaxResidualAngleDeg:F0}°";
            foreach (var row in rep.Rows)
            {
                RotationResidualRows.Add(new RotationResidualDisplayRow
                {
                    Index = row.Index,
                    AngleText = $"{row.AngleDeg:F0}°",
                    RadiusText = $"r={row.Radius:F2}",
                    ResidualText = $"{row.Residual:F3}",
                    IsWarn = row.Residual > 0.5
                });
            }
            OnPropertyChanged(nameof(HasRotationResidual));
            OnPropertyChanged(nameof(RotationResidualSummaryText));
        }

        // ================= Step3 模板类型感知文案 =================
        // HandEyeWithRotationStepViews（DataTemplate）被「九点+旋转(HandEyeWithRotation)」与
        // 「吸放式(PickPlace)」两类共用：PickPlace 的工件是"被吸放移动"的，标题/图例/按钮/
        // 提示若仍显示"平移 9 点 / R 轴 / 装延伸杆"会严重误导操作员（延伸杆是固定件场景才需要）。
        // 以下文案按 TargetProfile.Type 自适应（2026-09-04）。

        /// <summary>
        /// 当前会话是否为吸放式（PickPlace 专属 UI 分支开关）。
        /// ★ 2026-09-06 彻底会话化：v2 单量会话由 spec 采集路径唯一裁决（H 吸放=PickPlaceReturn、
        /// e 吸放旋转=RotatePickPlace）；旧兼容壳（无 spec）才回退按档案 Type 判定——
        /// 不再让"已建档混合档案 Type=PickPlaceHandEye"污染其它路径会话的 UI/行为。
        /// </summary>
        public bool IsPickPlaceProfile =>
            IsSessionV2
                ? (_sessionSpec != null && IsPickPlacePath(_sessionSpec.PrimaryPath))
                : TargetProfile != null && TargetProfile.Type == CalibrationType.PickPlaceHandEye;

        /// <summary>
        /// 本次会话是否含旋转/偏心采样段（决定旋转轴选择行、旋转进度、旋转图卡、Compute 旋转残差区等 UI 是否出现）。
        /// ★ 2026-09-06 彻底会话化：v2 单量会话只有 e(旋转) 会话含旋转段；H/s/t 会话即便档案是旧
        /// 混合型（九点+旋转/吸放）也不含——旋转已拆 e 段独立会话，向导内绝不要求 H 会话采旋转。
        /// 旧兼容壳（无 spec）保持按档案混合 Type 判定。
        /// </summary>
        public bool HasRotationStage =>
            IsSessionV2
                ? IsRotationSession
                : TargetProfile != null
                  && (TargetProfile.Type == CalibrationType.HandEyeWithRotation
                      || TargetProfile.Type == CalibrationType.PickPlaceHandEye);

        /// <summary>是否为纯九点（无旋转阶段）：旋转相关 UI 一律隐藏，避免"旋转 0/0、空画布"误导</summary>
        public bool IsNinePointOnly =>
            TargetProfile != null && TargetProfile.Type == CalibrationType.NinePointHandEye;

        /// <summary>EyeMode 是否可编辑：吸放式放料点=基准+网格偏移，与相机装在手上/外无关（放料点公式不含镜像），
        /// 故吸放式下该选项灰显，避免现场误改导致困惑。</summary>
        public bool IsEyeModeEditable => !IsPickPlaceProfile;

        /// <summary>是否存在可绑定的前置畸变标定方案（相机内参类型当前未实现→恒空，UI 空态提示用）</summary>
        public bool HasDistortionProfiles => AvailableDistortionProfiles.Count > 0;

        /// <summary>畸变绑定行的空态提示（P1-5：下拉恒空时的原因说明，避免误以为漏选）</summary>
        public string DistortionHintText => HasDistortionProfiles
            ? "选择后，运行时像素坐标先做镜头去畸变再乘矩阵（仅对带畸变标定的相机生效）"
            : "（当前没有可用方案：『相机内参/标定板』标定尚未实现，暂为开发中占位。拿到标定板并完成相机内参标定后，此处即可绑定并自动对像素去畸变。）";

        /// <summary>
        /// 步骤3采集页顶部黄条（可绕过，不阻断）：尚未在步骤2成功识别过特征时提醒——
        /// 直接全自动采集且特征从未验证过，走位后大概率大量漏点（行业调试常识：先验证特征再跑网格）。
        /// </summary>
        public bool ShowPreSamplingVerifyWarning =>
            LegacyPhase == 3
            && !CalibrationPoints.Any(p => p.IsCaptured)
            && !RotationPoints.Any(p => p.IsCaptured)
            && MatchScoreText == "尚未提取";

        /// <summary>发布前体检项（步骤4卡列表：矩阵/RMS/旋转覆盖/健康报告，红=阻断 黄=确认 绿=通过）</summary>
        public ObservableCollection<CalibCheckItemModel> PublishChecks { get; } = new ObservableCollection<CalibCheckItemModel>();

        private string _publishSummaryText = "尚未执行拟合计算——体检在拟合后生效。";
        /// <summary>发布前体检汇总文字（红黄数量与结论）</summary>
        public string PublishSummaryText
        {
            get => _publishSummaryText;
            private set => Set(ref _publishSummaryText, value);
        }

        /// <summary>Step3 面板主标题（图像区角标）——按会话段组合（v2 单量会话只描述本段；旧壳描述档案全貌）</summary>
        public string Step3PanelTitle
        {
            get
            {
                if (IsRotationSession)
                {
                    // e 会话：只有旋转段，不出现"九点吸放"字样
                    return IsPickPlaceProfile
                        ? "🎥 吸放式旋转偏心标定（U 轴角度采样）"
                        : "🎥 旋转轴偏心拟合（R/U 轴角度采样）";
                }
                if (IsSessionV2)
                {
                    // H/s 等非旋转 v2 会话：只有平移网格，不出现旋转字样
                    return IsPickPlaceProfile
                        ? "🎥 吸放式九点标定（网格吸放采样）"
                        : "🎥 平移九点手眼标定";
                }
                return IsPickPlaceProfile
                    ? "🎥 吸放式 Pick&Place 标定（九点吸放 + U 轴偏心拟合）"
                    : "🎥 多点手眼标定 (平移 9 点 + R 轴偏心拟合)";
            }
        }

        /// <summary>Step3 面板图例副标题——同主标题会话段组合</summary>
        public string Step3PanelLegend
        {
            get
            {
                if (IsRotationSession)
                {
                    return IsPickPlaceProfile
                        ? "红色：U 轴旋转偏心采样点"
                        : "红色：R/U 轴旋转拟合圆";
                }
                if (IsSessionV2)
                {
                    return IsPickPlaceProfile
                        ? "绿色：吸放网格 9 点"
                        : "绿色：九点平移网格";
                }
                return IsPickPlaceProfile
                    ? "绿色：放料网格 9 点  |  红色：U 轴旋转偏心采样"
                    : "绿色：九点平移网格  |  红色：R 轴旋转拟合圆";
            }
        }

        /// <summary>Step3 Tab1 名称</summary>
        public string Step3TabTranslateHeader => IsPickPlaceProfile
            ? "1. 九点吸放矩阵"
            : "1. 平移矩阵 (9点)";

        /// <summary>Step3 Tab2 名称</summary>
        public string Step3TabRotationHeader => IsPickPlaceProfile
            ? "2. U轴旋转偏心（吸放）"
            : "2. R轴旋转拟合 (3~5点)";

        /// <summary>Step3 Tab1 主采集按钮文案（单步/下一点）</summary>
        public string BtnCollectTranslateText => IsPickPlaceProfile
            ? "🔄 单步吸放采样（吸→放网格点→回拍照位→识别）"
            : "⚡ 自动采集平移 9 点";

        /// <summary>Step3 Tab1 全自动按钮文案（仅 PickPlace 显示：可全程自动吸放，无需中途换件）——按会话段表述，H 段不含"旋转"字样</summary>
        public string BtnAutoRunAllText
        {
            get
            {
                if (IsRotationSession)
                {
                    return "🤖 一键全自动旋转采样";
                }
                if (IsSessionV2)
                {
                    return "🤖 一键全自动吸放（九点）";
                }
                return "🤖 一键全自动吸放（九点+旋转）";
            }
        }

        /// <summary>Step3 Tab2 旋转采样按钮文案</summary>
        public string BtnCollectRotationText => IsPickPlaceProfile
            ? "🔄 步进旋转采样（吸→转U→放回中心→回拍照位）"
            : "🔄 步进旋转 R 轴并采样";

        /// <summary>Step3 Tab2 旋转提示（PickPlace 与普通场景物理前提不同，必须分开描述）</summary>
        public string RotationSampleHintText => IsPickPlaceProfile
            ? "吸放式旋转标定：工件被吸嘴转 U 角后放回网格中心，各角度像素绕 U 轴投影画弧（默认 -45°/0°/+45° 3 点、跨度 90°，可在表内改）。拟合圆心=旋转轴投影；偏心含吸持偏差（工件参考点级），用于运行时 U 角补偿。偏心大/模板范围有限时请勿盲目扩角——特征须全程在视野且模板角度范围够，跨度/分布越大圆心越稳。"
            : "旋转标定要求被观测 Mark 随 R/U 轴转动——若麻将/工件固定不动，需先在 U 轴末端装延伸杆并在杆端贴 Mark（移入相机视野），再旋转 3 个角度（默认 -45°/0°/+45°、跨度 90°）。系统拟合圆心=旋转轴投影。角度可在表格内直接修改（偏心大时特征出视野/失配即收窄，清晰且模板范围够才扩角，跨度/分布越大圆心越稳）。";

        /// <summary>Step3 类型感知文案批量通知（构造/预设/采样刷新时调用，属性为只读 getter）</summary>
        private void NotifyStep3UiText()
        {
            OnPropertyChanged(nameof(IsPickPlaceProfile));
            OnPropertyChanged(nameof(Step3PanelTitle));
            OnPropertyChanged(nameof(Step3PanelLegend));
            OnPropertyChanged(nameof(Step3TabTranslateHeader));
            OnPropertyChanged(nameof(Step3TabRotationHeader));
            OnPropertyChanged(nameof(BtnCollectTranslateText));
            OnPropertyChanged(nameof(BtnAutoRunAllText));
            OnPropertyChanged(nameof(BtnCollectRotationText));
            OnPropertyChanged(nameof(RotationSampleHintText));
        }

        private void NotifySamplingUi()
        {
            void Update()
            {
                int n = CalibrationPoints.Count(p => p.IsCaptured);
                if (HasRotationStage)
                {
                    int r = RotationPoints.Count(p => p.IsCaptured);
                    SamplingProgressText = $"平移 {n}/9 · 旋转 {r}/{RotationPoints.Count}";
                }
                else
                {
                    // 纯九点无旋转阶段：进度只显示平移；旋转摘要清空，避免"旋转 0/0 / 空摘要"误导（2026-09-04）
                    SamplingProgressText = $"平移 {n}/9";
                    RotationFitSummaryText = "";
                }
                OnPropertyChanged(nameof(ShowPreSamplingVerifyWarning));

                NineCells.Clear();
                for (int i = 1; i <= 9; i++)
                {
                    var p = CalibrationPoints.FirstOrDefault(x => x.Index == i);
                    string brush;
                    if (p == null || !p.IsCaptured)
                    {
                        brush = _skippedPointIndices.Contains(i) ? "#F09595" : "#D3D1C7"; // 红=缺失/跳过 灰=未采
                    }
                    else
                    {
                        brush = p.IsReliable ? "#97C459" : "#EF9F27"; // 绿=可靠已采 橙=不可靠已采
                    }
                    NineCells.Add(new CalibCoverageCellModel { Index = i, StatusBrush = brush });
                }
                // 采样点/覆盖状态变化后刷新步骤门禁（Next 按钮态 + 阻断文案）
                RaiseNextGate();
                OnPropertyChanged(nameof(ViewChipText));
            }
            if (Application.Current?.Dispatcher.CheckAccess() == true)
            {
                Update();
            }
            else
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(Update));
            }
        }

        /// <summary>档案中参与「配置自检 + Origin 门禁」的几何字段（含手动 TextBox 编辑路径）</summary>
        private static readonly HashSet<string> GeometrySensitiveProfileProps = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(CalibrationProfile.BasePosX), nameof(CalibrationProfile.BasePosY),
            nameof(CalibrationProfile.PickBaseX), nameof(CalibrationProfile.PickBaseY), nameof(CalibrationProfile.PickBaseU),
            nameof(CalibrationProfile.PhotoPoseX), nameof(CalibrationProfile.PhotoPoseY),
            nameof(CalibrationProfile.PhotoPoseZ), nameof(CalibrationProfile.PhotoPoseU),
            nameof(CalibrationProfile.SafeZ), nameof(CalibrationProfile.PickZ), nameof(CalibrationProfile.PlaceZ),
            nameof(CalibrationProfile.PickVacuumIoIndex),
            // X/Y 方向反转：勾选即时重算配置自检与俯视小地图（走位网格取反预览，2026-09-06）
            nameof(CalibrationProfile.InvertXAxis), nameof(CalibrationProfile.InvertYAxis),
        };

        /// <summary>
        /// 几何字段写回联动：自检红绿清单（GeometryChecks 快照）与 Next 门禁（RelayCommand 惰性求值）
        /// 均不会因字段变更自刷新 —— 按钮示教或手动输入任一途径写回后若不显式刷新，
        /// 会表现为"基准已写入但自检仍 0,0 红、Next 仍禁用"（2026-09-06 现场修复）。
        /// </summary>
        private void OnTargetProfileGeometryChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || !GeometrySensitiveProfileProps.Contains(e.PropertyName)) return;
            RunOnUi(() =>
            {
                RefreshGeometryChecks();
                RaiseNextGate();
            });
        }

        /// <summary>几何自检：把"需要用户判断"的事变成代码可查的红绿清单</summary>
        public void RefreshGeometryChecks()
        {
            GeometryChecks.Clear();
            bool pp = IsPickPlaceProfile; // 会话级（v2 按 spec 路径；旧壳按档案 Type）

            void Add(bool ok, string text)
            {
                GeometryChecks.Add(new CalibCheckItemModel { Level = ok ? 0 : 2, Text = text });
            }

            if (pp)
            {
                Add(TargetProfile.IsPickBaseSet, $"吸取位已设置 ({TargetProfile.PickBaseX:F1}, {TargetProfile.PickBaseY:F1}, U{TargetProfile.PickBaseU:F1})");
                Add(TargetProfile.IsPhotoPoseSet, $"固定拍照位已设置 ({TargetProfile.PhotoPoseX:F1}, {TargetProfile.PhotoPoseY:F1}, Z{TargetProfile.PhotoPoseZ:F1})");
            }
            Add(TargetProfile.IsBasePosSet, $"网格中心基准已设置 ({TargetProfile.BasePosX:F1}, {TargetProfile.BasePosY:F1})");
            Add(GridStepX > 0 && GridStepY > 0, $"网格步长有效 (X {GridStepX:F1} / Y {GridStepY:F1} mm)");
            if (pp)
            {
                // Z 序语义（2026-09-04 现场确认）：Epson SCARA 轴负向均为向下 → 数值上
                // Safe(安全高位) 最大 > Pick(吸取位) ≤ Place(放置位/最低)。若将来平台 Z 正向
                // 向下（如 ZMC 轴 0 向下为正），此比较须整体反转，勿仅改提示文案。
                bool zOk = TargetProfile.SafeZ > TargetProfile.PickZ && TargetProfile.PickZ <= TargetProfile.PlaceZ;
                Add(zOk, $"Z 序检查：Safe {TargetProfile.SafeZ:F1} > Pick {TargetProfile.PickZ:F1} ≤ Place {TargetProfile.PlaceZ:F1}" + (zOk ? "" : "（Z 轴向下为负：Safe 应最大/最高）"));
                Add(TargetProfile.PickVacuumIoIndex >= 0, $"真空通道有效（通道 {TargetProfile.PickVacuumIoIndex}，吸嘴{(TargetProfile.NozzleKey ?? "1")}）");
            }
            OnPropertyChanged(nameof(GeometryChecks));
            UpdateGeometryMap();
        }

        private const double DotCanvasW = 230;
        private const double DotCanvasH = 150;
        private const double DotPad = 14;

        /// <summary>把平面坐标点集缩放进 230×150 画布（纯展示，不参与标定计算）</summary>
        private static List<CalibDotModel> FitDots(List<(double X, double Y, string Label, string Brush, double Size)> pts, double pad)
        {
            var res = new List<CalibDotModel>();
            if (pts.Count == 0) return res;
            double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            double minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            if (maxX - minX < 1e-6) { minX -= 1; maxX += 1; }
            if (maxY - minY < 1e-6) { minY -= 1; maxY += 1; }
            double sx = (DotCanvasW - 2 * pad) / (maxX - minX);
            double sy = (DotCanvasH - 2 * pad) / (maxY - minY);
            double sc = Math.Min(sx, sy);
            // 居中：统一用中心缩放避免畸变
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
            double spanX = (DotCanvasW - 2 * pad) / sc / 2;
            double spanY = (DotCanvasH - 2 * pad) / sc / 2;
            foreach (var p in pts)
            {
                double x = DotCanvasW / 2 + (p.X - cx) * sc;
                double y = DotCanvasH / 2 + (p.Y - cy) * sc;
                res.Add(new CalibDotModel { Label = p.Label, X = Math.Max(2, Math.Min(DotCanvasW - 2, x)), Y = Math.Max(2, Math.Min(DotCanvasH - 2, y)), Brush = p.Brush, Size = p.Size });
            }
            return res;
        }

        /// <summary>俯视示意小地图：吸取位/拍照位/网格中心/九网格点 同屏（吸放式为主，九点也显示基准网格）</summary>
        public void UpdateGeometryMap()
        {
            var pts = new List<(double, double, string, string, double)>();
            bool pp = IsPickPlaceProfile; // 会话级（v2 按 spec 路径；旧壳按档案 Type）
            if (pp && TargetProfile.IsPickBaseSet)
            {
                pts.Add((TargetProfile.PickBaseX, TargetProfile.PickBaseY, "吸取位", "#185FA5", 10));
            }
            if (pp && TargetProfile.IsPhotoPoseSet)
            {
                pts.Add((TargetProfile.PhotoPoseX, TargetProfile.PhotoPoseY, "拍照位", "#0F6E56", 10));
            }
            if (TargetProfile.IsBasePosSet)
            {
                pts.Add((TargetProfile.BasePosX, TargetProfile.BasePosY, "网格中心(基准)", "#854F0B", 9));
                bool eih = TargetProfile.EyeMode == EyeMode.EyeInHand;
                for (int i = 1; i <= 9; i++)
                {
                    int row = (i - 1) / 3, col = (i - 1) % 3;
                    double ox = (col - 1) * GridStepX;
                    double oy = (row - 1) * GridStepY;
                    if (TargetProfile.InvertXAxis) ox = -ox;
                    if (TargetProfile.InvertYAxis) oy = -oy;
                    // 吸放式放料点 = Base + offset（与相机装法无关，2026-09-04 与采样公式一致）；
                    // 普通式：EIH 相机反向走位 → Base − offset
                    double gx = (pp || !eih) ? (TargetProfile.BasePosX + ox) : (TargetProfile.BasePosX - ox);
                    double gy = (pp || !eih) ? (TargetProfile.BasePosY + oy) : (TargetProfile.BasePosY - oy);
                    pts.Add((gx, gy, $"网格点{i}", "#B4B2A9", 6));
                }
            }
            GeometryMapDots.Clear();
            foreach (var d in FitDots(pts, DotPad))
            {
                GeometryMapDots.Add(d);
            }
            OnPropertyChanged(nameof(GeometryMapDots));
        }

        /// <summary>旋转/偏心散点：角度采样点 + 拟合圆心 + 偏心端(θ=0)</summary>
        public void UpdateRotationPlot()
        {
            RotationPlotDots.Clear();
            var sampled = RotationPoints.Where(p => p.IsCaptured && p.PixelX > 0).ToList();
            if (sampled.Count == 0)
            {
                OnPropertyChanged(nameof(RotationPlotDots));
                return;
            }
            var pts = new List<(double, double, string, string, double)>();
            foreach (var p in sampled)
            {
                pts.Add((p.PixelX, p.PixelY, $"{p.AngleDeg:F0}°", "#185FA5", 7));
            }
            if (TargetProfile.ToolCenterPx > 0 || TargetProfile.ToolCenterPy > 0)
            {
                pts.Add((TargetProfile.ToolCenterPx, TargetProfile.ToolCenterPy, "旋转中心", "#E24B4A", 12));
            }
            if ((TargetProfile.ToolEccPx != 0 || TargetProfile.ToolEccPy != 0) && TargetProfile.ToolCenterPx > 0)
            {
                pts.Add((TargetProfile.ToolCenterPx + TargetProfile.ToolEccPx, TargetProfile.ToolCenterPy + TargetProfile.ToolEccPy, "偏心端", "#D85A30", 8));
            }
            foreach (var d in FitDots(pts, DotPad))
            {
                RotationPlotDots.Add(d);
            }
            OnPropertyChanged(nameof(RotationPlotDots));
        }

        /// <summary>拟合后逐点残差（世界域：实际放置坐标 vs 由像素反算的坐标）</summary>
        public void ComputeResiduals()
        {
            ResidualRows.Clear();
            ResidualSummaryText = "";
            var captured = CalibrationPoints.Where(p => p.IsCaptured).ToList();
            if (captured.Count == 0 || string.IsNullOrWhiteSpace(OutputHomMatPath))
            {
                return;
            }
            double maxDev = 0;
            int worst = -1;
            var rows = new List<CalibResidualRowModel>();
            foreach (var p in captured)
            {
                var map = CalibService.MapPixelToWorld(OutputHomMatPath, p.PixelX, p.PixelY);
                if (!map.Success)
                {
                    continue;
                }
                double dev = Math.Sqrt(DistanceSq(map.Data.WorldX - p.WorldX, map.Data.WorldY - p.WorldY));
                bool warn = dev > 0.5;
                if (dev > maxDev)
                {
                    maxDev = dev;
                    worst = p.Index;
                }
                rows.Add(new CalibResidualRowModel
                {
                    Index = p.Index,
                    Pixel = $"({p.PixelX:F1}, {p.PixelY:F1})",
                    ResidualText = dev.ToString("F4"),
                    IsWarn = warn
                });
            }
            foreach (var row in rows.OrderBy(r => r.Index))
            {
                ResidualRows.Add(row);
            }
            ResidualSummaryText = $"逐点残差校验：共 {rows.Count} 点，最大偏差 {maxDev:F3} mm（点 #{worst}）；>0.5mm 已标红——红色点常为取错特征，绿色点可放行";
            OnPropertyChanged(nameof(ResidualRows));
            OnPropertyChanged(nameof(ResidualSummaryText));
        }

        /// <summary>
        /// 发布前体检（P1-1，2026-09-04）：把"能不能保存"变成代码可查的三态清单。
        /// 四项：① 矩阵已生成 ② RMS 合格 ③ 旋转覆盖（带旋转类型才评估）④ 矩阵健康检查。
        /// Level：0=绿(通过) 1=黄(提醒，确认后可保存) 2=红(阻断保存)。
        /// 保存入口 SaveResult 依据本清单做红阻/黄确认门控。
        /// </summary>
        /// <summary>e 单量会话的发布体检（旋转口径：采样覆盖 + 拟合完成 + 依赖 H 在位）</summary>
        private void RefreshRotationSessionChecks()
        {
            PublishChecks.Clear();
            void Add(int lv, string text) => PublishChecks.Add(new CalibCheckItemModel { Level = lv, Text = text });

            var sampled = RotationPoints.Where(p => p.IsCaptured).ToList();
            int got = sampled.Count;
            if (got == 0)
            {
                Add(2, "旋转/偏心尚未采样——请先完成旋转角度采样（默认 -45°/0°/+45°、跨度 90°；跨度过大特征出视野/失配时收窄）");
            }
            else if (got < 3)
            {
                Add(2, $"旋转仅采 {got} 点（<3 不足以拟合圆），请补采分散角度");
            }
            else
            {
                double cov = RotationArcCoverageDeg(sampled);
                string tip = $"旋转采样 {got} 点 · 角度覆盖 {cov:F0}°";
                if (_fitComputedOnce)
                {
                    tip += $" · 圆心 Px={TargetProfile.ToolCenterPx:F2}, Py={TargetProfile.ToolCenterPy:F2}";
                    // 2026-09-06 观测式/吸放式均导出 ToolEcc——有偏心值即展示（不再按吸放限定）
                    if (TargetProfile.ToolEccPx != 0 || TargetProfile.ToolEccPy != 0)
                    {
                        tip += $" · 偏心 Px={TargetProfile.ToolEccPx:F2}, Py={TargetProfile.ToolEccPy:F2}（{TargetProfile.ToolEccAngleDeg:F0}°）";
                    }
                }
                if (cov < 90.0)
                {
                    Add(2, tip + "——角度覆盖 <90°（阻断）：请补采更分散角至 ≥90°（默认 ±45° 即达标）；受视野/模板限制扩不动时需更换观测特征后重标");
                }
                else if (cov < 180.0)
                {
                    Add(0, tip + "——覆盖 ≥90° 达标（缺弧方向误差较 ≥180° 略大；现场若可扩角建议拉分散，以残差 RMS/MAX 为准）");
                }
                else
                {
                    Add(0, tip + "——覆盖充足，偏心补偿可信");
                }
            }
            if (!_fitComputedOnce)
            {
                Add(2, "旋转拟合尚未执行——请先在『计算』步骤执行拟合");
            }
            else
            {
                Add(0, "旋转拟合已完成：圆心/偏心已结算并写入方案（矩阵仍属 H 段）");
            }

            bool hasH = TargetProfile != null
                        && !string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                        && File.Exists(TargetProfile.HomMatFilePath);
            if (hasH)
            {
                Add(0, "依赖 H 矩阵在位——像素→机械映射可用（机械域旋转中心/偏心已随拟合写回）");
            }
            else
            {
                Add(1, "档案暂无 H 矩阵文件——本次结果仅落像素域（ToolCenterPx/Py）。先完成 H 段会话并发布后重跑本段可得机械域结果");
            }

            FinishPublishChecksSummary();
        }

        /// <summary>t 单量会话的发布体检（对针口径）</summary>
        private void RefreshToolOffsetSessionChecks()
        {
            PublishChecks.Clear();
            void Add(int lv, string text) => PublishChecks.Add(new CalibCheckItemModel { Level = lv, Text = text });

            if (TargetProfile.IsToolOffsetCalibrated)
            {
                Add(0, $"对针已完成：ToolOffset=({TargetProfile.ToolOffsetWx:F3}, {TargetProfile.ToolOffsetWy:F3})mm · {TargetProfile.ToolOffsetCalibTime:HH:mm:ss}");
            }
            else
            {
                Add(2, "对针尚未完成——请在『对针』步骤步进移动吸嘴并对准特征后锁定结算");
            }
            bool hasH = TargetProfile != null
                        && !string.IsNullOrWhiteSpace(TargetProfile.HomMatFilePath)
                        && File.Exists(TargetProfile.HomMatFilePath);
            Add(hasH ? 0 : 1, hasH
                ? "依赖 H 矩阵在位（对针基准相机坐标系可用）"
                : "档案暂无 H 矩阵文件——对针基准坐标系缺失，请先完成 H 段会话");
            FinishPublishChecksSummary();
        }

        private void FinishPublishChecksSummary()
        {
            int red = PublishChecks.Count(c => c.Level == 2);
            int yellow = PublishChecks.Count(c => c.Level == 1);
            PublishSummaryText = red == 0 && yellow == 0
                ? "✔ 发布前体检全部通过——可以保存"
                : red > 0
                    ? $"✘ 存在 {red} 项红色阻断（禁止保存）" + (yellow > 0 ? $"；另有 {yellow} 项黄色提醒" : "——请先修正红项再保存")
                    : $"⚠ 存在 {yellow} 项黄色提醒——可确认后保存，但请先阅读提醒内容";
            OnPropertyChanged(nameof(PublishChecks));
            OnPropertyChanged(nameof(PublishSummaryText));
            OnPropertyChanged(nameof(ViewChipText));
        }

        public void RefreshPublishChecks()
        {
            if (IsRotationSession)
            {
                RefreshRotationSessionChecks();
                return;
            }
            if (IsToolOffsetSession)
            {
                RefreshToolOffsetSessionChecks();
                return;
            }
            PublishChecks.Clear();
            void Add(int lv, string text) => PublishChecks.Add(new CalibCheckItemModel { Level = lv, Text = text });

            // ── ① 标定矩阵是否已生成（并落盘）──
            bool matrixExists = !string.IsNullOrWhiteSpace(OutputHomMatPath) && File.Exists(OutputHomMatPath);
            if (!matrixExists)
            {
                Add(2, "标定矩阵尚未生成——请先回到上方点【🧮 立即执行标定拟合计算】并确认其成功");
            }
            else if (_matrixPersistFailed && OutputHomMatPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                Add(1, "矩阵已生成，但自动落盘设备目录失败——现仅存系统临时目录，重启后可能丢失（可保存，稍后请用管理页『保存并应用』发布）");
            }
            else if (OutputHomMatPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                Add(0, "矩阵已生成（暂存系统临时目录）——点下方【✔ 完成并保存】时会自动落盘到 设备\\Calib 目录");
            }
            else
            {
                Add(0, "矩阵已生成并落盘到设备目录");
            }

            // ── ② RMS 重投影误差 ──
            if (!matrixExists)
            {
                Add(2, "RMS 未计算（需先拟合）");
            }
            else if (CalculatedRms <= 0.3)
            {
                Add(0, $"RMS = {CalculatedRms:F4} mm（≤0.3mm，良好）");
            }
            else if (CalculatedRms <= 1.0)
            {
                Add(1, $"RMS = {CalculatedRms:F4} mm（0.3~1.0mm，偏大——常见于个别点取错特征或吸放一致性差，可对照逐点残差定位坏点后确认）");
            }
            else
            {
                Add(2, $"RMS = {CalculatedRms:F4} mm（>1.0mm 严重超差——必为数据问题，禁止保存，请按日志定位坏点重新采样）");
            }

            // ── ③ 旋转/偏心覆盖（仅带旋转阶段的类型评估；纯九点不适用）──
            if (HasRotationStage)
            {
                var sampled = RotationPoints.Where(p => p.IsCaptured).ToList();
                int got = sampled.Count;
                if (got == 0)
                {
                    Add(2, "旋转/偏心尚未采样——本方案含旋转偏心补偿，需先完成旋转采样（吸放式默认 -45°/0°/+45° 3 点）");
                }
                else if (got < 3)
                {
                    Add(2, $"旋转仅采 {got} 点（<3 不足以拟合圆），请补采分散角度");
                }
                else
                {
                    bool fitDone = !RotationCenterPixelResult.StartsWith("Px: --");
                    double cov = RotationArcCoverageDeg(sampled);
                    string baseTip = $"旋转 {got} 角采样 · 角度覆盖 {cov:F0}° · 圆心 Cx={TargetProfile.ToolCenterPx:F2}, Cy={TargetProfile.ToolCenterPy:F2}";
                    if (IsPickPlaceProfile)
                    {
                        baseTip += $" · 偏心 Px={TargetProfile.ToolEccPx:F2}, Py={TargetProfile.ToolEccPy:F2}（方向 {TargetProfile.ToolEccAngleDeg:F0}°）";
                    }
                    if (!fitDone)
                    {
                        Add(1, $"{baseTip}——已采样但尚未拟合旋转中心，请执行拟合计算");
                    }
                    else if (cov < 90.0)
                    {
                        Add(2, $"{baseTip}——角度覆盖 <90°（阻断）：请补采更分散角至 ≥90°（默认 ±45° 即达标）；受视野/模板限制扩不动时需更换观测特征后重标");
                    }
                    else if (cov < 180.0)
                    {
                        Add(0, $"{baseTip}——覆盖 ≥90° 达标（缺弧方向误差较 ≥180° 略大；现场若可扩角建议拉分散，以残差 RMS/MAX 为准）");
                    }
                    else
                    {
                        Add(0, $"{baseTip}——覆盖充足，偏心补偿可信");
                    }
                }
            }
            else
            {
                // v2 H/s 会话（即便档案为旧混合型）：旋转/偏心已拆 e 段独立会话，本会话不评估
                Add(0, IsSessionV2
                    ? "本会话为平移段标定：旋转/偏心已拆分至 e 段会话执行（此处不适用）"
                    : "纯九点方案：无旋转/偏心阶段（不适用）");
            }

            // ── ④ 矩阵健康检查报告（BuildHomMatHealthReport：两轴当量/正交剪切/镜像行列式/网格重建）──
            //    2026-09-06 修正判据：det<0 仅提示"镜像"，是否错误取决于眼型——
            //    EyeInHand(相机随动)下机械 X+ 使静止特征相对视场左移 → H 首列系数反号 → det 恒负，
            //    属固有物理（界面镜像反转无效正验证此点），不再判红；质量由 RMS/网格重建 CV/夹角兜底。
            //    EyeToHand(固定相机)的镜像才保留红色阻断。
            string h = _lastHealthReport;
            if (string.IsNullOrWhiteSpace(h))
            {
                Add(2, "矩阵健康检查尚未执行（拟合成功后生成，详见日志）");
            }
            else if (h.Contains("⛔"))
            {
                // 2026-09-09：形状非法必须红阻。此前当量差 30% 只落进 contains("⚠") 的黄警分支
                // （"可保存"），坏矩阵被一路存下来 —— 直到校验台实拍才发现偏 20mm。
                // 落点误差 = 失真 × 差分距离，九点 RMS 小根本发现不了。
                Add(2, "矩阵健康检查：⛔ 形状非法（各向异性 σ1/σ2 偏离 1）→ 九点数据被污染。"
                    + "此矩阵用于引导会按距离线性放大误差（失真 22.7% 时跨 80mm 就偏约 19mm），禁止保存发布。"
                    + "请重做九点：全程同一 Z、每点确认真的走到位、模板别误匹配、相机别动。"
                    + "若反复重做仍非法，做 XY 位移实测定性：沿 +X 走 15mm 与沿 +Y 走 15mm 各记下特征像素位移——"
                    + "两数相等=仍是数据问题（继续查九点）；两数不等=相机物理斜视/镜头各向异性（调相机安装，别再重标）。");
            }
            else if (h.Contains("镜像"))
            {
                bool eihCamera = TargetProfile.EyeMode == EyeMode.EyeInHand;
                if (eihCamera)
                {
                    Add(1, "矩阵健康检查：负行列式=眼在手上（相机随动）固有镜像（机械 X+ 时特征图像左移，H 首列反号）——物理正常，不阻断保存；请以 RMS/网格规整为准（其他告警详见日志）");
                }
                else
                {
                    Add(2, "矩阵健康检查：检测到负行列式（镜像变换）——固定相机(眼在外)配置下轴方向或相机成像方向疑似配置错误，禁止保存（详见日志）");
                }
            }
            else if (h.Contains("⚠"))
            {
                Add(1, "矩阵健康检查存在告警项（剪切偏大 / 边长离散等，详见日志）——可保存，但建议现场移位验证确认方向与比例");
            }
            else
            {
                Add(0, "矩阵健康检查通过（两轴当量一致 / 近正交 / 无镜像 / 网格规整）");
            }

            // ── ⑤ 标定姿态记录与一致性（HomMat 对拍照 Z 敏感；吸放式取固定拍照位静态值，其余取采样实测值）──
            double? zNow = IsPickPlaceProfile
                ? (TargetProfile.IsPhotoPoseSet ? TargetProfile.PhotoPoseZ : (double?)null)
                : TargetProfile.CalibZ;
            double? uNow = IsPickPlaceProfile
                ? (TargetProfile.IsPhotoPoseSet ? TargetProfile.PhotoPoseU : (double?)null)
                : TargetProfile.CalibU0;
            if (!zNow.HasValue && !uNow.HasValue)
            {
                Add(1, "本次标定未记录拍照姿态（需网格中心 #5 成功采集；纯平移矩阵仍可保存，但换高度后将无法比对提醒）");
            }
            else
            {
                bool reopened = _openedCalibZ.HasValue || _openedCalibU0.HasValue;
                bool zDiff = reopened && _openedCalibZ.HasValue && zNow.HasValue && Math.Abs(zNow.Value - _openedCalibZ.Value) > 2.0;
                bool uDiff = reopened && _openedCalibU0.HasValue && uNow.HasValue && Math.Abs(uNow.Value - _openedCalibU0.Value) > 5.0;
                if (zDiff || uDiff)
                {
                    string zTxt = _openedCalibZ.HasValue ? $"{_openedCalibZ.Value:F1} → {zNow:F1}" : $"-- → {zNow:F1}";
                    string uTxt = uNow.HasValue
                        ? (_openedCalibU0.HasValue ? $"{_openedCalibU0.Value:F1} → {uNow:F1}" : $"-- → {uNow:F1}")
                        : "--";
                    Add(1, $"本次标定姿态与上次不同（Z {zTxt}mm；U 基准 {uTxt}°）——矩阵对拍照 Z 敏感，若是有意重标（换高度/吸嘴/治具）可忽略，否则请确认是否误用旧基准");
                }
                else
                {
                    Add(0, $"本次标定姿态：Z={zNow:F1}mm，U 基准={uNow:F1}°（矩阵仅在相同拍照高度下精确，请保持业务拍照位与之一致）");
                }
            }

            // ── 汇总 ──
            FinishPublishChecksSummary();
        }

        /// <summary>策略/外部触发：刷新覆盖格与进度（线程安全）</summary>
        public void RefreshCoverageUi() => NotifySamplingUi();

        /// <summary>复制排障块：预设快照 + 最近 60 行日志（发给维护者即可定位）</summary>
        public void CopyTroubleshoot()
        {
            var lines = _log.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var tail = lines.Length > 60 ? lines.Skip(lines.Length - 60) : lines;
            string text = "===== 标定向导排障块 =====" + Environment.NewLine
                + "· 类型: " + TargetProfile.Type + "  特征: " + TargetProfile.FeatureType + Environment.NewLine
                + "· 设备相机/运动: " + (SelectedCameraDevice?.DeviceName ?? "未选") + " / " + (SelectedMotionDevice?.DeviceName ?? "未选") + Environment.NewLine
                + "· 最近日志（60 行）:" + Environment.NewLine
                + string.Join(Environment.NewLine, tail);
            try
            {
                System.Windows.Clipboard.SetText(text);
                AppendLog("[排障] 已复制排障块到剪贴板（含配置快照+最近60行日志），可直接粘贴发给维护者。");
            }
            catch (Exception ex)
            {
                AppendLog("[排障] 复制剪贴板失败: " + ex.Message);
            }
        }

        #endregion

        #region 标定显示上下文（句柄模式）

        private ICalibrationDisplayContext _calibrationDisplayContext;

        /// <summary>
        /// 标定显示上下文（Halcon 视图窗口句柄）。
        /// 由 View 在 HalconImageDisplayHost 控件加载完成后注入（该控件实现 ICalibrationDisplayContext）。
        /// 注入后同步给 CalibrationService，使其提取特征时可直接用 Halcon 原生算子
        /// 把 ROI / 候选区域 / 拟合圆 / 十字 / 文字标注精细绘制到标定图像窗口。
        /// </summary>
        public ICalibrationDisplayContext CalibrationDisplayContext
        {
            get => _calibrationDisplayContext;
            set
            {
                if (Set(ref _calibrationDisplayContext, value) && CalibService != null)
                {
                    CalibService.DisplayContext = value;
                    AppendLog(value != null ? "标定显示上下文已注入：特征识别过程将实时叠加到视图窗口。" : "标定显示上下文已解除。");
                }
            }
        }

        #endregion
    }

    /// <summary>采样覆盖格（3×3）：Index 1-9；StatusBrush=绿/橙/红/灰</summary>
    public class CalibCoverageCellModel
    {
        public int Index { get; set; }
        public string StatusBrush { get; set; }
    }

    /// <summary>逐点残差行（结果页）：偏差>阈值时 IsWarn 标红</summary>
    public class CalibResidualRowModel
    {
        public int Index { get; set; }
        public string Pixel { get; set; }
        public string ResidualText { get; set; }
        public bool IsWarn { get; set; }
    }

    /// <summary>几何自检项（红绿清单）</summary>
    public class CalibCheckItemModel
    {
        /// <summary>0=绿(通过) 1=黄(提醒，可确认后保存) 2=红(阻断保存)。GeometryChecks 旧用法只产生 0/2。</summary>
        public int Level { get; set; }

        /// <summary>是否通过（Level==0）。兼容旧 GeometryChecks 的 Ok 触发器（XAML 绑 {Binding Ok}）。</summary>
        public bool Ok => Level == 0;

        public string Text { get; set; }
    }

    /// <summary>小地图/散点图上的点（Canvas 像素坐标已换算好，纯展示）</summary>
    public class CalibDotModel
    {
        public string Label { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public string Brush { get; set; }
        public double Size { get; set; }
    }
}