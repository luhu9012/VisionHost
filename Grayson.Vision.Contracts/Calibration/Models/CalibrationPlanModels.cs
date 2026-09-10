//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationPlanModels.cs
// 说 明: 标定计划领域模型（2026-09-05 P3 骨架期"H/e 任务拆分"决策落点）。
//        目标：从工位档案(StationProfile)推导"该工位要做哪些标定、各几条、什么类型"，
//        产出有序任务集 StationCalibrationPlan，由标定中心任务卡流引导执行。
//
//        ⚠ 粒度决策（2026-09-05 用户拍板·破坏式）：
//          · 相机级任务（H，像素↔机械平面）按"槽/相机"归属，整机共享 1 条；
//          · 工具级任务（旋转中心+偏心 e / P4 对针 ToolOffset）按"吸嘴"归属，
//            双吸嘴=各吸嘴独立一条——不再让双吸嘴把同一套九点跑两遍。
//        ⚠ 兼容边界（骨架期）：
//          · 本模型只做"派生计划/执行引导"，不替代 CalibrationType（旧数据/配方引用保留）；
//          · CalibrationTask.SuggestType 映射到现有 CalibrationProfile.Type 落库，
//            工具旋转任务当前仍走 HandEyeWithRotation 向导（九点段"跳过复用"留 P3 执行期）；
//          · 任务状态不入库，由标定中心按该任务对应 profile 的存在/已标定实时反推。
//===================================================================================
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// 标定任务语义分类（驱动计划推导与任务卡 UI；与 CalibrationType 并存不互斥）。
    /// 族划分：
    ///   相机级（槽/相机归属，像素↔机械平面 H）   —— CameraHandEyeWalk / CameraHandEyePickPlace
    ///   工具级（吸嘴/工具归属，偏心 e / 对针）    —— ToolRotationCalib / ToolOffsetCalib
    ///   辅助                                     —— FlyCorrection / LensDistortion(预留)
    /// </summary>
    public enum CalibrationTaskKind
    {
        /// <summary>相机级 · 走位式手眼 H（相机随执行机构 EyeInHand，特征不丢，走 3×3 网格）</summary>
        CameraHandEyeWalk = 0,

        /// <summary>相机级 · 吸放式九点 H（相机固定/送拍，机构吸工件放网格命令位后回拍）</summary>
        CameraHandEyePickPlace = 1,

        /// <summary>工具级 · 旋转中心 + 偏心 e（吸嘴级；带角度对位/偏心/多吸嘴时需要；依赖相机 H 提供坐标系）</summary>
        ToolRotationCalib = 2,

        /// <summary>工具级 · 对针补偿 ToolOffset 自学习（P4 里程碑；换吸嘴/撞机后重新对针）</summary>
        ToolOffsetCalib = 3,

        /// <summary>辅助 · 飞拍纠偏（固定相机看运动工件，出相对偏差；像素当量+触发/相位补偿）</summary>
        FlyCorrection = 4,

        /// <summary>辅助 · 镜头畸变前置（斜拍/大视场高精度；当前无真实标定板角点检测，不可执行——预留）</summary>
        LensDistortion = 5
    }

    /// <summary>
    /// 计划中的单个标定任务（档案需求 → 一次可执行标定的最小单元）。
    /// 相机级任务以 SlotKey 归属；工具级任务以 NozzleKey 归属（可带其视觉槽 SlotKey 作上下文）。
    /// </summary>
    public class CalibrationTask
    {
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>任务族（相机级/工具级/辅助）</summary>
        public CalibrationTaskKind Kind { get; set; }

        /// <summary>相机槽键（Cam_A/Cam_B…；工具/辅助任务可空或仅作上下文）</summary>
        public string SlotKey { get; set; }

        /// <summary>吸嘴/工具通道键（"1"/"2"…）；相机级任务恒 "1"</summary>
        public string NozzleKey { get; set; } = "1";

        /// <summary>任务显示名（任务卡主标题，如"相机手眼标定（Cam_A）"）</summary>
        public string DisplayName { get; set; }

        /// <summary>建议执行方式人读文案（任务卡副文案）</summary>
        public string Suggestion { get; set; }

        /// <summary>新建该任务 profile 时建议的 CalibrationType（落库映射，兼容旧类型）</summary>
        public CalibrationType SuggestType { get; set; } = CalibrationType.NinePointHandEye;

        /// <summary>建议 EyeMode（决定走位镜像语义：随动=EyeInHand / 固定=EyeToHand）</summary>
        public EyeMode SuggestEyeMode { get; set; } = EyeMode.EyeInHand;

        /// <summary>档案归属工位</summary>
        public string BoundStationCode { get; set; }

        /// <summary>工具级任务？true=按吸嘴独立（旋转e/对针），false=相机级/辅助</summary>
        public bool IsToolLevel => Kind == CalibrationTaskKind.ToolRotationCalib
                                   || Kind == CalibrationTaskKind.ToolOffsetCalib;

        /// <summary>可选任务（可跳过；P4 对针等）。false=主线必做项</summary>
        public bool IsOptional { get; set; }

        /// <summary>是否可直接执行（LensDistortion 等预留项 false，UI 灰显/跳过）</summary>
        public bool IsExecutable => Kind != CalibrationTaskKind.LensDistortion;

        /// <summary>推导备注（如"相机固定→送拍吸放式"的现场确认点）</summary>
        public string Note { get; set; }
    }

    /// <summary>
    /// 工位标定计划 = 有序任务集（档案派生，可重算）。
    /// 排序约定：相机级在前（H 是工具级 e 的前置依赖），工具级随后，辅助末位。
    /// 本类为纯派生视图，不落库；标定中心定位工位时按档案实时 Build。
    /// </summary>
    public class StationCalibrationPlan
    {
        public string StationCode { get; set; }
        public string StationName { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;
        public List<CalibrationTask> Tasks { get; set; } = new List<CalibrationTask>();

        /// <summary>主线必做任务数（可选任务不计）</summary>
        public int RequiredCount
        {
            get
            {
                int n = 0;
                foreach (var t in Tasks)
                {
                    if (!t.IsOptional) n++;
                }
                return n;
            }
        }
    }
}
