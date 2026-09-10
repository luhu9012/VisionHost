using Grayson.Vision.Contracts.Calibration.Models;
using System.Linq;
using System.Windows;

namespace Grayson.Vision.WpfUI.ViewModel.Steps
{
    /// <summary>
    /// 吸放式标定策略（CalibrationType.PickPlaceHandEye，行业标准 Pick&Place 标定）：
    /// · 九点：机械臂用吸嘴吸住工件 → 平移到规划网格点放料 → 回【固定拍照位】拍照提取特征
    ///   （工件放哪、机械坐标就精确已知；相机每次同姿态成像=等效固定相机，精度/光照稳定）；
    /// · 旋转：吸住工件 → U 转到采样角 → 放回网格中心 → 回拍照位拍照，圆拟合求旋转中心 + 工具偏心矢量。
    /// 与"工件固定、相机走位"旧范式的差异：吸放式全程不要求工件背面 Mark 始终朝上（特征随工件
    /// 被吸放），只要放置后特征可见（圆/十字/模板匹配均可）；几何前提：吸放不翻面。
    /// </summary>
    public class PickPlaceCalibrationStrategy : ICalibrationStepStrategy
    {
        public string GetStepGuideTip(int step)
        {
            switch (step)
            {
                case 1:
                    return "吸放式标定（Pick&Place）：先选择相机/运动卡；在「吸放几何」区填入 初始吸取位 X/Y（吸嘴吸住工件处）与 固定拍照位 X/Y/Z/U（每次拍照回位姿态），Z 高度与真空 IO 按现场设置，网格步长/基准与九点相同。";
                case 2:
                    return "回拍照位抓图确认特征：放一个工件在网格中心附近，用 十字/圆/模板匹配 任一种确认特征稳定可识别（模板匹配需先选模板，无模板先在模板管理创建）。旋转段默认采样角 -45°/0°/+45°（跨度 90°，可在表格改）。";
                case 3:
                    return "点【单步吸放采样】= 吸住工件→放下一个网格点→回拍照位→自动提取；点【步进旋转采样】= 吸住→转 U→放中心→回拍（默认 -45°/0°/+45° 3 点）。全自动会连续完成 9 点+3 角（失败自动跳过并第二轮补采，已采≥6+≥3 可拟合）。";
                case 4:
                    return "拟合：九点解 HomMat（像素↔世界），旋转点圆拟合旋转中心并自动导出 工具偏心 ToolEcc（写回 Profile，供业务旋转补偿）。";
                default:
                    return "请按照提示操作。";
            }
        }

        public void InitializePoints(CalibrationWizardViewModel vm)
        {
            // ★ 2026-09-06 彻底会话化：v2 单量会话只种本会话所需点表——
            //   e(旋转)会话只种旋转采样 3 角（无吸放九点表）；H 段会话只种吸放九点（无旋转表）。
            //   旧壳（无 spec，吸放混合档案整体执行）才种 9 点 + 3 角全表。
            bool rotationOnly = vm.IsSessionV2 && vm.SessionSpec != null
                                 && vm.SessionSpec.Quantity == CalibrationQuantity.ToolRotation;
            bool hOnly = vm.IsSessionV2 && vm.SessionSpec != null
                         && vm.SessionSpec.Quantity == CalibrationQuantity.HandEye;

            if (!rotationOnly)
            {
                vm.CalibrationPoints.Clear();
                vm.CalibService.ResetMarkReference();
                for (int i = 1; i <= 9; i++)
                {
                    vm.CalibrationPoints.Add(new CalibrationPointModel { Index = i, PixelX = 0, PixelY = 0, WorldX = 0, WorldY = 0 });
                }
            }

            vm.RotationPoints.Clear();
            if (hOnly)
            {
                // H 段会话：旋转归 e 段，不种旋转表（体检/UI/进度全部随 HasRotationStage=false 收敛）
                return;
            }
            // ★ 旋转采样角度默认（2026-09-06 现场修正）：原 0/45/90/180/270 五点覆盖 270° 对圆心拟合
            //   最稳，但偏心半径大（工件特征离旋转轴远）时 U 转 90°+ 后特征轨迹会甩出相机视野、
            //   超出模板角度范围匹配分骤降 → 采不到远角点反而全段失败。默认收窄 -45°/0°/+45° 3 点
            //   （跨度 90°，过覆盖门控；对称角拟合偏心仍成立）。现场特征在更大跨度仍清晰时，
            //   可在表格内自行扩角/加分散点（跨度/分布越大圆心越稳；<90° 被拟合门控拦截）。
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = -45, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 0, PixelX = 0, PixelY = 0 });
            vm.RotationPoints.Add(new RotationPointModel { AngleDeg = 45, PixelX = 0, PixelY = 0 });
        }

        public void TriggerSample(CalibrationWizardViewModel context)
        {
            if (context.LegacyPhase <= 2)
            {
                context.CaptureFeatureFrame("吸放式特征预览");
                return;
            }

            context.RunSamplingOnBackground(() =>
            {
                if (context.CalibrationPoints.Any(p => !p.IsCaptured))
                {
                    context.CaptureNextPickPlacePoint();
                    return;
                }
                // ★ P2 单量会话守卫：v2 H 段会话九点已满 → 旋转归 e 段，不在此顺带执行
                if (context.IsSessionV2 && context.SessionSpec != null
                    && context.SessionSpec.Quantity == Grayson.Vision.Contracts.Calibration.Models.CalibrationQuantity.HandEye)
                {
                    context.AppendLog("[H 段会话] 平移九点已采完；旋转偏心请另开 e 段会话（档案入口选 e 段）执行。");
                    return;
                }
                context.CaptureNextPickPlaceRotationPoint();
            });
        }

        public void AutoRunAll(CalibrationWizardViewModel context)
        {
            // ★ P2 单量会话守卫：v2 H 段会话（PickPlaceReturn）只采平移九点——
            //   旋转偏心已拆到 e 段独立会话，不再由 H 会话顺带执行
            if (context.IsSessionV2 && context.SessionSpec != null
                && context.SessionSpec.Quantity == Grayson.Vision.Contracts.Calibration.Models.CalibrationQuantity.HandEye)
            {
                context.AppendLog("[H 段会话] 吸放式自动采样：仅平移九点（旋转偏心归 e 段会话）...");
                bool hasAnyCap = context.CalibrationPoints.Any(p => p.IsCaptured);
                if (!hasAnyCap)
                {
                    InitializePoints(context);
                }
                context.RunSamplingOnBackground(() =>
                {
                    context.AutoCollectPickPlaceNinePointSamples();
                    context.RunOnUi(() =>
                    {
                        context.AppendLog("[H 段会话] 吸放九点采样结束，进入拟合计算...");
                        context.GoToComputeStep();
                        ExecuteCalibration(context);
                    });
                });
                return;
            }

            context.AppendLog("开始吸放式（Pick&Place）全自动采样：九点吸放 + 旋转偏心采样...");
            bool hasAny = context.CalibrationPoints.Any(p => p.IsCaptured) || context.RotationPoints.Any(p => p.IsCaptured);
            if (!hasAny)
            {
                InitializePoints(context);
            }
            else
            {
                context.AppendLog("检测到已有已采点：保留进度，仅补采缺失点。");
            }
            context.RunSamplingOnBackground(() =>
            {
                context.AutoCollectPickPlaceNinePointSamples();
                context.AutoCollectPickPlaceRotationSamples();
                context.AppendLog("吸放式九点 + 旋转采样结束，进入拟合计算...");
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

            if (nPts < 6)
            {
                MessageBox.Show($"九点已采 {nPts}/9，至少需 6 点才能可靠拟合。请先补采缺失点。",
                    "数据不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var missingPts = context.CalibrationPoints.Where(p => !p.IsCaptured).Select(p => p.Index).ToList();
            if (missingPts.Count > 0)
            {
                string msg = $"当前已采：平移 {nPts}/9 点。缺失: #{string.Join(", #", missingPts)}\n";
                if (nRot >= 3)
                {
                    msg += $"旋转已采 {nRot} 点，可求旋转中心与偏心。\n";
                }
                else if (nRot > 0)
                {
                    msg += $"旋转仅 {nRot} 点（<3 无法求偏心，将跳过旋转结果，只保存平移矩阵）。\n";
                }
                else
                {
                    msg += "未做旋转采样：本次仅输出平移矩阵（如需偏心请补旋转采样）。\n";
                }
                msg += "\n仍可继续（缺失平移点会降低精度，以 RMS 判断是否可接受）？";
                if (MessageBox.Show(msg, "存在缺失点，确认继续？", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            context.AppendLog("═══ 吸放式九点标定拟合数据 ═══");
            foreach (var p in context.CalibrationPoints)
            {
                context.AppendLog(p.IsCaptured
                    ? $"  点{p.Index}: Pixel({p.PixelX:F1}, {p.PixelY:F1})  World({p.WorldX:F3}, {p.WorldY:F3})  分:{p.MatchScore:F0}"
                    : $"  点{p.Index}: (未采集，跳过)");
            }

            double[] px = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.PixelX).ToArray();
            double[] py = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.PixelY).ToArray();
            double[] wx = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.WorldX).ToArray();
            double[] wy = context.CalibrationPoints.Where(p => p.IsCaptured).Select(p => p.WorldY).ToArray();
            var res = context.CalibService.CalcNinePointHomMat(px, py, wx, wy);

            // ⚠ 顺序关键（2026-09-04）：必须先 ProcessCalibrationResult（成功→回填 OutputHomMatPath），
            //   再 UpdatePickPlaceRotationResults——其内部要把"像素偏心端=圆心+ToolEcc(px,py)"经矩阵
            //   映射成世界偏心 ToolEccWx/Wy。此前顺序颠倒：拟合时 OutputHomMatPath 尚空，
            //   ToolEccWx/Wy 恒为 0，业务端 U 角补偿取到的是未补偿的空偏心。
            //   ProcessCalibrationResult 内已按类型排除 PickPlace 的普通旋转拟合，不会再覆盖本结果。
            context.ProcessCalibrationResult(res);
            if (nRot >= 3)
            {
                context.UpdatePickPlaceRotationResults();
            }
            context.ComputeResiduals();
            context.RefreshCoverageUi();
        }
    }
}
