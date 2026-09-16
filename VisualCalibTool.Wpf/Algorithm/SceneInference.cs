using System;
using System.Collections.Generic;
using System.Globalization;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>场景推导的输入（把"当前知道的一切"喂进来）。</summary>
    public sealed class SceneInferRequest
    {
        public CalibTopology Topology = new CalibTopology();

        /// <summary>用户在第 1 步选的目标（决定要不要附加该链特有的前提检查）。</summary>
        public WizardGoal Goal = WizardGoal.CameraAccuracy;

        public FeatureKind FeatureKind = FeatureKind.CircleMark;
        public bool HasTemplate;

        public double MmPerPixel;
        public int ImageWidthPx;
        public int ImageHeightPx;

        /// <summary>控制器是否支持零运动校核（CHECK）。</summary>
        public bool ReachCheckSupported = true;

        /// <summary>该工位是否已经标过旋转中心（偏心链的前置）。</summary>
        public bool RotationCenterKnown;

        /// <summary>
        /// 调用方是否具备"自动补齐前置链"的能力（<c>WizardCoordinator</c> 有，手工操作没有）。
        ///
        /// ★ 这个标志存在的理由：同一个事实（"还没有旋转中心"）在两种调用方式下的结论<b>不同</b> ——
        ///   · 能自动补齐：不该拦，只该说明"开跑时会先自动补做「吸嘴转到哪」"；
        ///   · 不能自动补齐：必须拦，否则用户点下去只会得到一句"算不出来"。
        ///   把这层差别交给推导器判断，而不是让界面自己写一套"能不能跑"的规则 ——
        ///   两套规则最后一定会不一致（表现为"界面说能跑，一点就报错"）。
        /// </summary>
        public bool CanAutoFillPrerequisites;

        /// <summary>旋转轴的物理行程（度）。0 = 未知。用来判断"转得够不够一圈"。</summary>
        public double RotateAxisTravelDeg;
    }

    /// <summary>
    /// ★ 场景推导 = 向导第 2 步的内容。
    ///
    /// 设计文档的手段 #2 是"<b>回显代替提问</b>"：能从拓扑/配置推出来的一律推出来给用户确认，
    /// 不问参数。这个类就是那条原则的实现 —— 而且它把"推论是从哪来的"也一并回显
    /// （<see cref="WizardSceneLine.Source"/>），因为用户没法确认一个"不知道根据什么得出来的结论"。
    ///
    /// ★ 未知项一律<b>明确标未知</b>并说明保守处理方式，绝不悄悄取一个默认值然后装作已知。
    ///   这是本项目最容易出事的地方：一个静默的默认值，会让后面所有数字看起来都对。
    /// </summary>
    public static class SceneInference
    {
        public static WizardScene Infer(SceneInferRequest req)
        {
            if (req == null)
            {
                throw new ArgumentNullException("req");
            }

            CalibTopology topo = req.Topology ?? new CalibTopology();
            var scene = new WizardScene { Topology = topo };

            // ── ① 相机安装方式 ──
            switch (topo.CameraMount)
            {
                case CameraMountKind.EyeInHand:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.CameraMount,
                        Value = "是",
                        Source = "工位拓扑",
                        Known = true,
                        Caution = "消费时需要叠加旋转中心补偿 O",
                        Options = CameraMountOptions(topo.CameraMount)
                    });
                    break;
                case CameraMountKind.EyeToHand:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.CameraMount,
                        Value = "否（相机固定在外部）",
                        Source = "工位拓扑",
                        Known = true,
                        Caution = "若是「用延伸杆辅助标定」，仍需叠加 O 补偿",
                        Options = CameraMountOptions(topo.CameraMount)
                    });
                    break;
                default:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.CameraMount,
                        Value = "未知",
                        Source = "工位拓扑未给出",
                        Known = false,
                        Caution = "按最保守方式处理：当作会跟着动（需要 O 补偿）",
                        Options = CameraMountOptions(topo.CameraMount)
                    });
                    break;
            }

            // ── ② 手系 ──
            switch (topo.Hand)
            {
                case Handedness.Lefty:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.Hand,
                        Value = "左手",
                        Source = "控制器反馈",
                        Known = true,
                        Caution = "全程不得中途变化，变化必须重新标定",
                        Options = HandOptions(topo.Hand)
                    });
                    break;
                case Handedness.Righty:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.Hand,
                        Value = "右手",
                        Source = "控制器反馈",
                        Known = true,
                        Caution = "全程不得中途变化，变化必须重新标定",
                        Options = HandOptions(topo.Hand)
                    });
                    break;
                default:
                    scene.Add(new WizardSceneLine
                    {
                        Label = WizardSceneLabels.Hand,
                        Value = "未知",
                        Source = "控制器未答复",
                        Known = false,
                        Caution = "必须显式下发：左手工位被强制右手解，会报 4007 / 崩任务 / 断 TCP",
                        Options = HandOptions(topo.Hand)
                    });
                    break;
            }

            // ── ③ 工具头 ──
            scene.Add(new WizardSceneLine
            {
                Label = WizardSceneLabels.ToolHeadCount,
                Value = topo.ToolHeadCount == 2 ? "双头" : "单头",
                Source = "工位拓扑",
                Known = true,
                Options = new List<WizardChoiceOption>
                {
                    new WizardChoiceOption
                    {
                        Key = SceneAnswers.ToolHeadSingleKey,
                        Label = "单头",
                        Note = "一个吸嘴，标一次",
                        Recommended = topo.ToolHeadCount != 2
                    },
                    new WizardChoiceOption
                    {
                        Key = SceneAnswers.ToolHeadDualKey,
                        Label = "双头",
                        Note = "两个头各标一轮 —— 选它这次会多跑一遍",
                        Recommended = topo.ToolHeadCount == 2
                    }
                }
            });

            // ── ④ 工作 Z（含三级回退来源）──
            string zSource;
            double z = ResolveWorkZ(topo, out zSource);
            bool zKnown = !double.IsNaN(z);
            scene.Add(new WizardSceneLine
            {
                Label = "工作 Z",
                Value = zKnown ? z.ToString("F2", CultureInfo.InvariantCulture) + " mm" : "未解析",
                Source = zSource,
                Known = zKnown,
                Caution = zKnown
                    ? "标定期间锁 Z：九点走位只做 XY，不动 Z"
                    : "H 是 2D 单应，但像素尺度取决于拍摄高度 —— Z 错了整张 H 都错"
            });

            // ── ⑤ 相机是否随 Z 移动 ──
            if (topo.CameraMovesWithZ.HasValue)
            {
                scene.Add(new WizardSceneLine
                {
                    Label = "相机随 Z 一起升降",
                    Value = topo.CameraMovesWithZ.Value ? "是" : "否（刚性固定）",
                    Source = "工位拓扑",
                    Known = true,
                    Caution = topo.CameraMovesWithZ.Value
                        ? "换 Z 就必须重新采点（相机位姿变了）"
                        : null
                });
            }
            else
            {
                scene.Add(new WizardSceneLine
                {
                    Label = "相机随 Z 一起升降",
                    Value = "未知",
                    Source = "工位拓扑未给出",
                    Known = false,
                    Caution = "按最保守 true 处理（当作会随 Z 动 → 换 Z 要重采）"
                });
            }

            // ── ⑥ 标定基准位 ──
            scene.Add(new WizardSceneLine
            {
                Label = WizardSceneLabels.BasePos,
                Value = topo.BasePosKnown
                    ? string.Format(CultureInfo.InvariantCulture, "({0:F2}, {1:F2})，半径 {2:F2} mm",
                        topo.BasePosXY.X, topo.BasePosXY.Y, topo.BasePosXY.Length)
                    : "尚未确定",
                Source = topo.BasePosKnown ? "用户设定" : "未设定",
                Known = topo.BasePosKnown,
                Caution = topo.BasePosKnown ? null : "没有基准位就没有网格 —— 必须先去目标位置点「设为基准位」",
                Options = BasePosOptions(topo)
            });

            // ── ⑦ 步长（含 FOV 反推的推荐区间）──
            double stepDiag = Math.Sqrt(topo.StepX * topo.StepX + topo.StepY * topo.StepY);
            string stepValue = string.Format(CultureInfo.InvariantCulture,
                "{0:F2} × {1:F2} mm（对角线 {2:F2} mm）", topo.StepX, topo.StepY, stepDiag);
            string stepSource = "工位拓扑";
            string stepCaution = null;

            if (req.MmPerPixel > 0.0 && req.ImageWidthPx > 0 && req.ImageHeightPx > 0)
            {
                int shortSide = Math.Min(req.ImageWidthPx, req.ImageHeightPx);
                double fovShortMm = shortSide * req.MmPerPixel;
                double recMin = fovShortMm / 3.0;
                double recMax = fovShortMm / 2.0;
                stepValue += string.Format(CultureInfo.InvariantCulture,
                    "；视野反推建议 {0:F2} ~ {1:F2} mm（视野短边 {2:F2} mm）", recMin, recMax, fovShortMm);

                if (stepDiag < 0.3 * recMin)
                {
                    stepCaution = "明显偏小：点太密会让矩阵形状退化（σ1/σ2 变差）";
                }
                else if (stepDiag > 2.0 * recMax)
                {
                    stepCaution = "明显偏大：最外圈的点容易把 Mark 推出视野";
                }
            }
            else
            {
                stepCaution = "图像尺寸或像素尺度未知 → 无法反推推荐步长";
            }

            scene.Add(new WizardSceneLine
            {
                Label = WizardSceneLabels.GridStep,
                Value = stepValue,
                Source = stepSource,
                Known = true,
                Caution = stepCaution,
                Options = StepOptions(topo.StepX)
            });

            // ── ⑧ 基准角 ──
            scene.Add(new WizardSceneLine
            {
                Label = "标定基准角",
                Value = topo.CalibU0.ToString("F2", CultureInfo.InvariantCulture) + "°",
                Source = "工位拓扑",
                Known = true,
                Caution = "旋转采样的角度都是相对它的增量；它只在旋转阶段首次采样时锁定一次"
            });

            // ── ⑨ 可达性预检能力 ──
            scene.Add(new WizardSceneLine
            {
                Label = "走位前能不能「先问一句」（零运动校核）",
                Value = req.ReachCheckSupported ? "支持" : "不支持 / 未知",
                Source = req.ReachCheckSupported ? "控制器能力" : "未知",
                Known = req.ReachCheckSupported,
                Caution = req.ReachCheckSupported
                    ? "只保证终点合法，不考虑轨迹 —— 轨迹安全靠控制器侧严格限位模式"
                    : "没有它就只能在发车后才发现点不可达；★ 上位机没有臂长与关节限位，绝不自己写逆解",

                // ★ 这是**设备能力**，不是用户能拍板的事 —— 按 Editable 的定义标 false，
                //   界面上就不会摆一个"本该你定"的假暗示（见 WizardSceneLine.Editable 的注释）。
                Editable = false
            });

            // ── ⑩ 特征 ──
            string featureValue;
            switch (req.FeatureKind)
            {
                case FeatureKind.CircleMark:
                    featureValue = "圆点（阈值分割 + 圆度筛选 + 亚像素圆拟合）";
                    break;
                case FeatureKind.CrossMark:
                    featureValue = "十字（骨架 + 直线对求交）";
                    break;
                case FeatureKind.TemplateMatch:
                    featureValue = req.HasTemplate ? "模板匹配（已示教）" : "模板匹配（尚未示教）";
                    break;
                default:
                    featureValue = req.FeatureKind.ToString();
                    break;
            }

            scene.Add(new WizardSceneLine
            {
                Label = WizardSceneLabels.Feature,
                Value = featureValue,
                Source = "用户选择",
                Known = req.FeatureKind != FeatureKind.TemplateMatch || req.HasTemplate,
                Caution = req.FeatureKind == FeatureKind.TemplateMatch && !req.HasTemplate
                    ? "模板必须先框选 + 训练才能用"
                    : null
            });

            AppendGoalSpecificChecks(scene, req, topo);

            return scene;
        }

        /// <summary>三级回退解析工作 Z（唯一真源口径：NozzleAlignZ → BasePosZ → CalibZ）。</summary>
        public static double ResolveWorkZ(CalibTopology topo, out string source)
        {
            if (topo == null)
            {
                source = "拓扑为空";
                return double.NaN;
            }

            if (!double.IsNaN(topo.WorkZ) && !double.IsInfinity(topo.WorkZ) && topo.WorkZ != 0.0)
            {
                source = "拓扑工作 Z";
                return topo.WorkZ;
            }

            if (topo.BasePosZ.HasValue)
            {
                source = "标定基准位锁定的 Z";
                return topo.BasePosZ.Value;
            }

            if (!double.IsNaN(topo.WorkZ) && !double.IsInfinity(topo.WorkZ))
            {
                source = "拓扑工作 Z（值 0，已回退采用）";
                return topo.WorkZ;
            }

            source = "三级回退均无值（NozzleAlignZ → BasePosZ → CalibZ）";
            return double.NaN;
        }

        private static void AppendGoalSpecificChecks(WizardScene scene, SceneInferRequest req, CalibTopology topo)
        {
            switch (req.Goal)
            {
                case WizardGoal.NozzleOffset:
                    if (!req.RotationCenterKnown)
                    {
                        if (req.CanAutoFillPrerequisites)
                        {
                            // 能自动补齐 ⇒ 不是障碍，只是"这次会多做一步"。
                            scene.Add(new WizardSceneLine
                            {
                                Label = "前置条件：旋转中心",
                                Value = "本会话还没有（开跑时会先自动补做）",
                                Source = "会话状态",
                                Known = false,
                                Caution = "偏心量 e = O − 工具尖位置，没有 O 就算不出来。"
                                             + "所以这次会先自动跑一遍「吸嘴转到哪」，再跑「吸嘴偏了多少」。",
                                Editable = false
                            });
                        }
                        else
                        {
                            scene.HasBlocker = true;
                            scene.Add(new WizardSceneLine
                            {
                                Label = "前置条件：旋转中心",
                                Value = "尚未标定",
                                Source = "会话状态",
                                Known = false,
                                Caution = "偏心量 e = O − 工具尖位置，没有 O 就算不出来。请先做「吸嘴转到哪」。",
                                Editable = false
                            });
                        }
                    }
                    else
                    {
                        scene.Add(new WizardSceneLine
                        {
                            Label = "前置条件：旋转中心",
                            Value = "已标定",
                            Source = "会话状态",
                            Known = true
                        });
                    }

                    break;

                case WizardGoal.LensDistortion:
                    scene.Add(new WizardSceneLine
                    {
                        Label = "标定板的姿态差从哪来",
                        Value = "靠人工按提示摆板",
                        Source = "本工位约束",
                        Known = true,
                        Caution = "本工位是 X/Y/Z/U 四轴，相机不能倾斜 → 姿态差只能由标定板提供"
                                     + "（建议 ≥ 10 个姿态，含 ≥ 3 个倾斜）。算法链不会假设相机可倾斜。",
                        Editable = false
                    });
                    break;

                case WizardGoal.NozzleRotationCenter:
                    if (req.RotateAxisTravelDeg > 0.0 && req.RotateAxisTravelDeg < 180.0)
                    {
                        scene.Add(new WizardSceneLine
                        {
                            Label = "旋转轴行程是否够画圆",
                            Value = string.Format(CultureInfo.InvariantCulture, "仅 {0:F1}°", req.RotateAxisTravelDeg),
                            Source = "控制器参数",
                            Known = true,
                            Caution = "行程不足 180° 仍可定圆，但圆心的标准差会明显变大 —— 有条件就转满一圈"
                        });
                    }

                    break;
            }
        }

        /// <summary>
        /// 「相机装在哪」的候选。
        ///
        /// ★ 为什么这一项必须让用户拍板（而不是只回显）：它决定的是<b>整条链的形状</b> ——
        ///   眼在手里偏心链要按角度归一，眼在外不用；再叠加消费时的 O 补偿差异。
        ///   而工位拓扑里的这个字段是最容易配错、也最容易被复制粘贴带偏的一项。
        /// ★ 未知时默认给眼在手（与 <c>SceneInference</c> 的"按最保守处理"口径一致）——
        ///   不能让"未知"落成"随便选一个"。
        /// </summary>
        private static List<WizardChoiceOption> CameraMountOptions(CameraMountKind current)
        {
            bool eih = current != CameraMountKind.EyeToHand;

            return new List<WizardChoiceOption>
            {
                new WizardChoiceOption
                {
                    Key = SceneAnswers.CameraMountEyeInHandKey,
                    Label = "装在机械手上（跟着一起动）",
                    Note = "转 U 时画面跟着相机滚；消费时要叠加旋转中心补偿",
                    Recommended = eih
                },
                new WizardChoiceOption
                {
                    Key = SceneAnswers.CameraMountEyeToHandKey,
                    Label = "固定在旁边（不动）",
                    Note = "相机固定；若用延伸杆辅助标定，仍需叠加补偿",
                    Recommended = !eih
                }
            };
        }

        /// <summary>
        /// 「手系」的候选。
        /// ★ 这一项本来由控制器反馈，之所以还要让用户过一眼：左手工位被强制按右手下发，
        ///   会直接报 4007 / 崩任务 / 断 TCP —— 代价太高，值得花一秒钟复核。
        /// </summary>
        private static List<WizardChoiceOption> HandOptions(Handedness current)
        {
            bool lefty = current != Handedness.Righty;

            return new List<WizardChoiceOption>
            {
                new WizardChoiceOption
                {
                    Key = SceneAnswers.HandLeftyKey,
                    Label = "左手",
                    Note = "指令按左手系下发",
                    Recommended = lefty
                },
                new WizardChoiceOption
                {
                    Key = SceneAnswers.HandRightyKey,
                    Label = "右手",
                    Note = "发错手系会报 4007 / 崩任务",
                    Recommended = !lefty
                }
            };
        }

        /// <summary>
        /// 「标定基准位」的候选。
        ///
        /// ★ 这一项修前是最该问却自己走了的：<see cref="CalibTopology.BasePosKnown"/> 为 false 时
        ///   推导器只写了一句"必须先去目标位置点设为基准位"，**然后就继续往下走了** ——
        ///   用户按两次「下一步」就开跑，跑的是没有基准位的规划。
        /// ★ 所以这里给的是两条<b>都能立刻定值</b>的路：用现在的位置（现场做法），
        ///   或沿用已设定的值。不给"请填两个数"。
        /// </summary>
        private static List<WizardChoiceOption> BasePosOptions(CalibTopology topo)
        {
            var list = new List<WizardChoiceOption>
            {
                new WizardChoiceOption
                {
                    Key = SceneAnswers.BasePosCurrentKey,
                    Label = "用机械手现在的位置",
                    Note = "先把机械手移动到九点中心，再选这一项",
                    Recommended = !topo.BasePosKnown
                }
            };

            if (topo.BasePosKnown)
            {
                list.Add(new WizardChoiceOption
                {
                    Key = SceneAnswers.BasePosKeepKey,
                    Label = string.Format(CultureInfo.InvariantCulture,
                        "保持 ({0:F2}, {1:F2})", topo.BasePosXY.X, topo.BasePosXY.Y),
                    Note = "沿用拓扑里已设定的基准位",
                    Recommended = true
                });
            }

            return list;
        }

        /// <summary>
        /// 「九点步长」的候选。
        /// ★ 默认选中<b>最接近当前拓扑值</b>的那一档（让用户"确认"，而不是替他挑最优）——
        ///   步长是精度与耗时的权衡，选多大取决于这一站能等多久，那不是推导器该拍的板。
        /// </summary>
        private static List<WizardChoiceOption> StepOptions(double stepX)
        {
            double[] candidate = new double[] { 2.0, 5.0, 10.0 };
            string[] label = new string[] { "2 mm", "5 mm", "10 mm" };
            string[] note = new string[]
            {
                "点更密、拟合更稳；代价是走位更久",
                "常用档，一般视野内都能覆盖",
                "走位快；点太稀会让矩阵形状变差（σ1/σ2）"
            };

            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < candidate.Length; i++)
            {
                double diff = Math.Abs(candidate[i] - stepX);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = i;
                }
            }

            var list = new List<WizardChoiceOption>(candidate.Length);
            for (int i = 0; i < candidate.Length; i++)
            {
                list.Add(new WizardChoiceOption
                {
                    Key = candidate[i].ToString("F1", CultureInfo.InvariantCulture),
                    Label = label[i],
                    Note = note[i],
                    Recommended = i == best
                });
            }

            return list;
        }
    }
}
