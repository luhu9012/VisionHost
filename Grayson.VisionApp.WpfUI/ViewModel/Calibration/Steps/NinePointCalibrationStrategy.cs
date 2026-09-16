using Grayson.Vision.Contracts.Calibration.Models;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    /// <summary>
    /// 九点标定走位方式。
    /// 仅改变"采集先后顺序"，不改变点编号与网格位置的映射（Index 1~9 固定对应 3x3 网格，
    /// 拟合计算、数据表展示、历史数据兼容均不受影响）。
    /// </summary>
    public enum NinePointTraverseMode
    {
        /// <summary>中心优先螺旋走位（推荐）：先采中心点，再按螺旋向外扩展。
        /// ① 中心点成像质量最好、全图搜索最易命中，首点成功率最高；
        /// ② 相邻两点恒为 1 个网格步长，行程最短且速度均匀，对运动系统最友好；
        /// ③ 从中心向外逐层扩展，Mark 不易走出视野。</summary>
        SpiralCenterFirst = 0,

        /// <summary>传统逐行扫描走位：从左上角起，逐行从左到右采集；与旧版本行为一致。</summary>
        RowScan = 1,
    }

    /// <summary>
    /// 九点走位顺序定义。数组元素为 CalibrationPointModel.Index 编号，
    /// Index → 网格位置映射固定为：1=(0,0)左上, 2=(0,1), 3=(0,2)右上, 4=(1,0), 5=(1,1)中心,
    /// 6=(1,2), 7=(2,0)左下, 8=(2,1), 9=(2,2)右下（row/col 从 0 起）。
    /// </summary>
    public static class NinePointTraverseOrder
    {
        /// <summary>中心优先螺旋：5→6→3→2→1→4→7→8→9（相邻点恒为 1 步长）</summary>
        public static readonly int[] SpiralCenterFirst = { 5, 6, 3, 2, 1, 4, 7, 8, 9 };

        /// <summary>传统逐行扫描：1→2→3→…→9（左上角起行优先）</summary>
        public static readonly int[] RowScan = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>按走位方式返回对应的采集顺序数组</summary>
        public static int[] GetOrder(NinePointTraverseMode mode)
        {
            return mode == NinePointTraverseMode.RowScan ? RowScan : SpiralCenterFirst;
        }
    }

    /// <summary>九点走位方式下拉选项（UI 绑定）</summary>
    public class TraverseModeOption
    {
        public NinePointTraverseMode Mode { get; set; }
        public string DisplayName { get; set; }
    }

    public class NinePointCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "请选择标定用到的相机与运动控制卡，并设置平移走位步长。";
                case 2: return "点击抓图按钮，确认 Halcon 视图中显示当前相机实时画面与标定特征。";
                case 3: return "执行九点采样后，向导会驱动 X/Y 走位并记录像素与物理坐标。";
                case 4: return "点击拟合计算生成 2D 仿射变换矩阵 HomMat2D。";
                default: return "请按照提示完成当前步骤设置。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            vm.CalibrationPoints.Clear();
            // 重置参考 Mark 半径：重新采集/更换 Mark 板后旧参考失效（预览或首点成功会自动重新记录）
            vm.CalibService.ResetMarkReference();
            for (int i = 1; i <= 9; i++)
            {
                vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
            }
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.LegacyPhase <= 2)
            {
                context.CaptureFeatureFrame("九点标定特征预览");
                return;
            }

            // 后台线程执行采样：UI 线程保持泵消息，Halcon 算子每步绘制即时上屏（可对照画面调试），
            // 走位等待/采图等待期间界面不卡。内部有防重入保护，连点安全。
            context.RunSamplingOnBackground(() => context.CaptureNextCalibrationPoint());
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            context.AppendLog($"开始九点全自动走位标定流程（走位方式: {(context.TraverseMode == NinePointTraverseMode.RowScan ? "传统逐行扫描" : "中心优先螺旋")}）...");
            // ★ 2026-09-03 失败宽容化：已有已采点时【不】清空重来，仅自动补采缺失点
            // ★ 2026-09-10 需求：9 点已全采满时再点本按钮 = 明确要求「重走位重采」——
            //   此时若仍走"仅补采缺失点"分支会因无缺点而空转（点了没反应，操作员以为按钮失效）。
            //   故全采满 → 先确认再清空重来；有缺点 → 保持原有补采语义（不打扰）。
            bool hasAnyCaptured = context.CalibrationPoints.Any(p => p.IsCaptured);
            bool allCaptured = context.CalibrationPoints.Count > 0
                               && context.CalibrationPoints.All(p => p.IsCaptured);

            if (allCaptured)
            {
                // 全采满：确认为"重走位重采"（清空现有数据），避免误点丢失已采点
                bool confirmed = true;
                context.RunOnUi(() =>
                {
                    var r = MessageBox.Show(
                        "九点已全部采集完成。\n\n" +
                        "继续将【清空现有 9 点数据并重新走位采集一轮】（用于更换/移动标定件后重标）。\n" +
                        "如需保留现有数据，请选『否』。\n\n" +
                        "是否重新采集？",
                        "重新走位采集确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    confirmed = r == MessageBoxResult.Yes;
                });

                if (!confirmed)
                {
                    context.AppendLog("[九点采集] 操作员取消重采，保留现有 9 点数据。");
                    return;
                }

                context.AppendLog("[九点采集] 全采满 → 清空现有数据，重新走位采集一轮。");
                InitializePoints(context);
            }
            else if (!hasAnyCaptured)
            {
                InitializePoints(context);
            }
            else
            {
                context.AppendLog("检测到已有已采点：保留现有进度，仅自动补采缺失点（含失败点第二轮补采）。");
            }
            context.RunSamplingOnBackground(() =>
            {
                context.AutoCollectNinePointSamples();
                context.AppendLog("九点数据采集流程结束，进入拟合计算...");
                // 集合/界面状态变更与拟合弹窗回到 UI 线程执行
                context.RunOnUi(() =>
                {
                    context.GoToComputeStep();
                    ExecuteCalibration(context);
                });
            });
        }

        public void ExecuteCalibration(CalibrationWizardViewModel context)
        {
            int nPts = context.CalibrationPoints.Count(p => p.IsCaptured);
            // ★ 2026-09-03 失败宽容化：不再要求 9/9 全采，已采 ≥6 即可计算（缺失点 RMS 会体现）
            if (nPts < 6)
            {
                MessageBox.Show($"九点标定已采 {nPts}/9 点，至少需 6 点才能可靠拟合。请先补采缺失点（点击【轴步进并采单个点】）或调整参数。",
                    "数据不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var missingPts = context.CalibrationPoints.Where(p => !p.IsCaptured).Select(p => p.Index).ToList();
            if (missingPts.Count > 0)
            {
                string msg = $"当前已采 {nPts}/9 点（拟合最低要求 6 点）。\n缺失点: #{string.Join(", #", missingPts)}\n\n" +
                             "仍可继续计算（缺失点会降低拟合精度，请以 RMS 与健康检查判断是否可接受）；\n也可返回补采后重算。是否继续？";
                var confirm = MessageBox.Show(msg, "存在缺失点，确认继续？",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // 诊断：拟合前逐点打印，便于在向导日志中排查哪个点像素坐标不可靠
            context.AppendLog("═══ 九点标定拟合数据 ═══");
            foreach (var p in context.CalibrationPoints)
            {
                if (p.IsCaptured)
                {
                    context.AppendLog($"  点{p.Index}: Pixel({p.PixelX:F1}, {p.PixelY:F1})  World({p.WorldX:F3}, {p.WorldY:F3})  分:{p.MatchScore:F0}");
                }
                else
                {
                    context.AppendLog($"  点{p.Index}: (未采集，跳过)");
                }
            }

            double[] px = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.PixelX).ToArray();
            double[] py = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.PixelY).ToArray();
            double[] wx = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.WorldX).ToArray();
            double[] wy = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.WorldY).ToArray();
            var res = context.CalibService.CalcNinePointHomMat(px, py, wx, wy);
            context.ProcessCalibrationResult(res);
            context.ComputeResiduals();
            context.RefreshCoverageUi();
        }
    }
}
