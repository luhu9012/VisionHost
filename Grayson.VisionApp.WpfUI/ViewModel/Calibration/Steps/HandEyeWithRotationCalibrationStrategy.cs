using Grayson.Vision.Contracts.Calibration.Models;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    public class HandEyeWithRotationCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1: return "请选择多点手眼标定所需的相机与运动控制卡。";
                case 2: return "先抓图确认特征：平移阶段用麻将/工件 Mark；旋转阶段若工件不动需另装延伸杆（U 轴末端）贴 Mark，确认其随 U 轴转动且入相机视野。";
                case 3: return "先完成平移 9 点（工件 Mark），再换延伸杆端 Mark 执行旋转角度采样（默认 -45°/0°/+45° 3 点，跨度 90°；偏心大/模板角度范围有限时转大角度 Mark 会出视野或失配，可在表内改分散角扩覆盖）拟合旋转中心（固定件无法标旋转）。★ 表内角度为【相对基准角的增量】：首个采样点读当前 U 锁为基准角 U_ref，实际 U = U_ref + 表内角度，0°=当前姿态（不会拉回绝对 0°）。";
                case 4: return "系统将同时求解平移 HomMat2D 矩阵与旋转中心结果。";
                default: return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            // ★ 2026-09-06 彻底会话化：v2 单量会话只种本会话所需点表——
            //   e(旋转)会话只种旋转采样 3 角（无九点表）；H 段会话只种平移九点（无旋转表）。
            //   旧壳（无 spec，混合档案整体执行）才种 9 点 + 3 角全表。
            bool rotationOnly = vm.IsSessionV2 && vm.SessionSpec != null
                                 && vm.SessionSpec.Quantity == CalibrationQuantity.ToolRotation;
            bool hOnly = vm.IsSessionV2 && vm.SessionSpec != null
                         && vm.SessionSpec.Quantity == CalibrationQuantity.HandEye;

            if (!rotationOnly)
            {
                vm.CalibrationPoints.Clear();
                // 重置参考 Mark 半径：重新采集/更换 Mark 板后旧参考失效（预览或首点成功会自动重新记录）
                vm.CalibService.ResetMarkReference();
                for (int i = 1; i <= 9; i++)
                {
                    vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
                }
            }

            vm.RotationPoints.Clear();
            // ★ 2026-09-10 相对角：点表重建 = 新一轮旋转采样，清掉旧基准角缓存（下次采样重读当前 U）
            vm.ResetRotationBaseU();
            if (hOnly)
            {
                // H 段会话：旋转归 e 段，不种旋转表（体检/UI/进度全部随 HasRotationStage=false 收敛）
                return;
            }
            // ★ 旋转采样角度默认（2026-09-06 现场修正）：原 0/45/90/180/270 五点覆盖 270° 对圆心拟合
            //   最稳，但偏心半径大（Mark 离旋转轴远）时 U 转 90°+ 后 Mark 轨迹会甩出相机视野，
            //   且超出模板形状模型角度范围后匹配分骤降（实测 180°/270° 0 命中）→ 采不到远角点反而
            //   全段失败。默认收窄为 -45°/0°/+45° 3 点（跨度 90°，过覆盖门控）：Mark 全程留在
            //   视野内、模板角度范围足够（对称角拟合偏心仍成立）。现场若特征在更大跨度仍清晰，
            //   可在表格内自行扩角/加分散点（跨度/分布越大圆心越稳；<90° 会被拟合门控拦截）。
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = -45, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 0, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 45, PixelX = 0, PixelY = 0 });
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.LegacyPhase <= 2)
            {
                context.CaptureFeatureFrame("旋转手眼特征预览");
                return;
            }

            // 后台线程执行采样：UI 线程保持泵消息，算子每步绘制即时上屏；内部有防重入保护
            context.RunSamplingOnBackground(() =>
            {
                if (context.CalibrationPoints.Any(p => !p.IsCaptured))
                {
                    context.CaptureNextCalibrationPoint();
                    return;
                }
                // ★ P2 单量会话守卫：v2 H 段会话九点已满 → 旋转归 e 段，不在此顺带执行
                if (context.IsSessionV2 && context.SessionSpec != null
                    && context.SessionSpec.Quantity == Grayson.Vision.Contracts.Calibration.Models.CalibrationQuantity.HandEye)
                {
                    context.AppendLog("[H 段会话] 平移九点已采完；旋转偏心请另开 e 段会话（档案入口选 e 段）执行。");
                    return;
                }
                context.CaptureNextRotationPoint();
            });
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            // ★ P2 单量会话守卫：v2 H 段会话（走位九点）只采平移——旋转段已拆 e 会话
            if (context.IsSessionV2 && context.SessionSpec != null
                && context.SessionSpec.Quantity == Grayson.Vision.Contracts.Calibration.Models.CalibrationQuantity.HandEye)
            {
                context.AppendLog("[H 段会话] 自动采样：仅平移九点（旋转偏心归 e 段会话）...");
                bool hasAny = context.CalibrationPoints.Any(p => p.IsCaptured);
                if (!hasAny)
                {
                    InitializePoints(context);
                }
                context.RunSamplingOnBackground(() =>
                {
                    context.AutoCollectNinePointSamples();
                    context.RunOnUi(() =>
                    {
                        context.AppendLog("[H 段会话] 平移九点采样结束，进入拟合计算...");
                        context.GoToComputeStep();
                        ExecuteCalibration(context);
                    });
                });
                return;
            }

            context.AppendLog("开始多点手眼 + 旋转中心自动采样流程...");
            // ★ 2026-09-03 失败宽容化：已有已采点时【不】清空重来，仅自动补采缺失点
            //   （此前 AutoRunAll 一律 InitializePoints 清空——单点失败后重按"全自动"就把
            //   已采的好点全抹掉，是"一点失败整局重来"的元凶）。
            bool hasAnyCaptured = context.CalibrationPoints.Any(p => p.IsCaptured)
                                || context.RotationPoints.Any(p => p.IsCaptured);
            if (!hasAnyCaptured)
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
                context.AutoCollectRotationSamples();
                context.AppendLog("平移九点 + 旋转采样流程结束，进入拟合计算...");
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
            int nRot = context.RotationPoints.Count(p => p.IsCaptured);

            // ★ 2026-09-06 彻底会话化：v2 H 段会话只输出平移矩阵，旋转硬门槛必须让路——
            //   正常情况下 v2 H 段已由 SelectStrategy 挂纯九点策略不会进到这里；此守卫拦截
            //   任何遗漏路径（如旧混合档案直开等），保证 H 段永不因"旋转未采"被拒绝。
            bool hOnly = context.IsSessionV2 && context.SessionSpec != null
                         && context.SessionSpec.Quantity == CalibrationQuantity.HandEye;

            // ★ 2026-09-03 失败宽容化：不再要求 9/9 全采 + 旋转全采——
            //   九点 ≥6（网格覆盖足够）且旋转 ≥3（圆拟合最低点数）即可计算，
            //   缺失点越多 RMS/圆心误差越大，由健康检查报告与 RMS 兜底提示。
            if (nPts < 6)
            {
                MessageBox.Show($"平移九点已采 {nPts}/9 点，至少需 6 点才能可靠拟合。请先补采缺失点（点击【轴步进并采单个点】）或调整参数。",
                    "数据不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!hOnly && nRot < 3)
            {
                MessageBox.Show($"旋转采样已采 {nRot} 点，至少需 3 点才能拟合旋转中心圆。请补采或调整光源/曝光。",
                    "数据不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var missingPts = context.CalibrationPoints.Where(p => !p.IsCaptured).Select(p => p.Index).ToList();
            var missingRot = hOnly
                ? new System.Collections.Generic.List<double>()
                : context.RotationPoints.Where(p => !p.IsCaptured).Select(p => p.AngleDeg).ToList();
            if (missingPts.Count > 0 || missingRot.Count > 0)
            {
                string msg = hOnly
                    ? $"当前已采：平移 {nPts}/9 点（H 段拟合最低要求：平移≥6）。\n"
                    : $"当前已采：平移 {nPts}/9 点，旋转 {nRot} 点（拟合最低要求：平移≥6、旋转≥3）。\n";
                if (missingPts.Count > 0) msg += $"缺失平移点: #{string.Join(", #", missingPts)}\n";
                if (missingRot.Count > 0) msg += $"缺失旋转角: {string.Join("°, ", missingRot)}°\n";
                msg += "\n仍可继续计算（缺失点会降低精度，请以 RMS/残差与健康检查判断是否可接受）；\n也可返回补采后重算。是否继续？";
                var confirm = MessageBox.Show(msg, "存在缺失点，确认继续？",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            // 诊断：拟合前逐点打印
            context.AppendLog("═══ 九点标定拟合数据（含旋转标定）═══");
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
            context.UpdateRotationCenterResults();
            context.ProcessCalibrationResult(res);
            context.ComputeResiduals();
            context.RefreshCoverageUi();
        }
    }
}
