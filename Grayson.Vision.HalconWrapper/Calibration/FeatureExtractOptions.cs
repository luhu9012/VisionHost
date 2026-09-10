//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 特征提取算子参数选项。标定向导"特征配置"步骤通过滑块实时调节，
//        替代原先硬编码在 CalibrationService 内的阈值/圆度/面积/搜索半径等常量，
//        使操作员能按现场光源、工件材质、Mark 大小即时调参并实时预览效果。
//        实现 INotifyPropertyChanged（继承 ViewModelBase）以便滑块值实时回显。
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 单个候选特征（用于特征匹配步骤向用户展示"找到了哪几个候选、各自质量如何"）。
    /// 多候选时用户可直观看出是否存在干扰候选（反光点/螺丝/字符等圆特征），
    /// 辅助调整阈值/面积/圆度参数把干扰滤掉，而不是盲调。
    /// </summary>
    public class MatchCandidateInfo
    {
        /// <summary>候选序号（从 1 开始，UI 展示用）</summary>
        public int Index { get; set; }
        /// <summary>是否最终被选中（识别结果）</summary>
        public bool IsSelected { get; set; }
        /// <summary>中心像素 X（列）</summary>
        public double PixelX { get; set; }
        /// <summary>中心像素 Y（行）</summary>
        public double PixelY { get; set; }
        /// <summary>等效半径（px，区域面积换算或 XLD 拟合半径）</summary>
        public double Radius { get; set; }
        /// <summary>圆度（0~1，越接近 1 越圆；区域级特征）</summary>
        public double Circularity { get; set; }
        /// <summary>区域面积（px²）</summary>
        public double Area { get; set; }
        /// <summary>该候选单项质量分（0~100，仅作对比展示）</summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// 特征匹配质量报告：每次提取（预览/调参重试/采样）后由 CalibrationService 产出，
    /// 供标定向导第二步"特征配置"界面实时展示直观匹配分（0~100）。
    /// 评分维度（圆 Mark）：
    ///   ① 圆度（选中候选实际圆度，越高越好，权重 0.35）
    ///   ② 半径一致性（与参考 Mark 半径偏差 ≤40% 内线性，权重 0.25）
    ///   ③ 候选唯一性（是否存在近距离干扰候选，权重 0.2）
    ///   ④ 位置可预测性（与期望位置偏差 ≤3×搜索半径内线性，权重 0.2）
    /// 十字 Mark：按是否找到垂直直线对 / 夹角误差给分。
    /// 语义：≥85 极佳、70~84 良好、55~69 可用（有歧义）、&lt;55 建议调参，失败=0。
    /// </summary>
    public class FeatureMatchReport
    {
        /// <summary>本次提取是否成功（false 时 Score=0，Detail 为失败诊断）</summary>
        public bool Success { get; set; }
        /// <summary>匹配分 0~100</summary>
        public double Score { get; set; }
        /// <summary>分数等级文字（极佳/良好/可用/偏弱/失败）</summary>
        public string Verdict { get; set; }
        /// <summary>多行成分明细（每行一个评分维度+数值），UI 直接展示</summary>
        public string Detail { get; set; }
        /// <summary>候选总数（含被排除干扰前通过几何筛选的数量）</summary>
        public int CandidateCount { get; set; }
        /// <summary>候选明细列表（供"候选列表"展示，最多保留若干条）</summary>
        public List<MatchCandidateInfo> Candidates { get; set; } = new List<MatchCandidateInfo>();
        /// <summary>识别中心像素 X（成功时有效）</summary>
        public double PixelX { get; set; }
        /// <summary>识别中心像素 Y（成功时有效）</summary>
        public double PixelY { get; set; }
        /// <summary>是否走了降级路径（动态阈值兜底/区域中心兜底）——降级命中分数打折并提示</summary>
        public bool UsedFallback { get; set; }
    }

    /// <summary>
    /// 特征提取算子参数（圆形 Mark / 十字 Mark 共用）。
    /// 所有属性带默认值，默认值与改造前硬编码参数完全一致，行为不变。
    /// </summary>
    public class FeatureExtractOptions : ViewModelBase
    {
        #region 圆形 Mark 参数（ExtractFeaturePoint 阈值分割 + 圆度筛选 + 亚像素圆拟合）

        private double _thresholdMin = 100;
        /// <summary>圆形 Mark 阈值分割下限（原硬编码 Threshold(100,255) 的下限）</summary>
        public double ThresholdMin
        {
            get => _thresholdMin;
            set => Set(ref _thresholdMin, Clamp(value, 0, 254));
        }

        private double _thresholdMax = 255;
        /// <summary>圆形 Mark 阈值分割上限</summary>
        public double ThresholdMax
        {
            get => _thresholdMax;
            set => Set(ref _thresholdMax, Clamp(value, 1, 255));
        }

        private double _minCircularity = 0.7;
        /// <summary>候选区域最小圆度（0~1，越接近 1 越圆）</summary>
        public double MinCircularity
        {
            get => _minCircularity;
            set => Set(ref _minCircularity, Clamp(value, 0.1, 1.0));
        }

        private double _minArea = 80;
        /// <summary>候选区域最小面积（像素²）</summary>
        public double MinArea
        {
            get => _minArea;
            set => Set(ref _minArea, Math.Max(1, value));
        }

        private double _maxArea = 999999;
        /// <summary>候选区域最大面积（像素²）</summary>
        public double MaxArea
        {
            get => _maxArea;
            set => Set(ref _maxArea, Math.Max(1, value));
        }

        private double _searchRadius = 150;
        /// <summary>局部 ROI 搜索半径（像素）：有参考位置时在期望点周围开搜索框；无参考时全图搜索</summary>
        public double SearchRadius
        {
            get => _searchRadius;
            set => Set(ref _searchRadius, Math.Max(10, value));
        }

        private double _subPixThreshold = 128;
        /// <summary>亚像素轮廓提取阈值 ThresholdSubPix</summary>
        public double SubPixThreshold
        {
            get => _subPixThreshold;
            set => Set(ref _subPixThreshold, Clamp(value, 1, 255));
        }

        #endregion

        #region 十字 Mark 参数（ExtractCrossMarkPoint 几何结构法）

        private double _crossDarkThresholdMax = 90;
        /// <summary>十字 Mark 暗色分割上限：优先 Threshold(0, 上限) 找暗十字</summary>
        public double CrossDarkThresholdMax
        {
            get => _crossDarkThresholdMax;
            set => Set(ref _crossDarkThresholdMax, Clamp(value, 1, 254));
        }

        private double _crossLightThresholdMin = 160;
        /// <summary>十字 Mark 亮色分割下限：暗色无结果时 Threshold(下限, 255) 找亮十字</summary>
        public double CrossLightThresholdMin
        {
            get => _crossLightThresholdMin;
            set => Set(ref _crossLightThresholdMin, Clamp(value, 1, 254));
        }

        private double _crossMinArea = 60;
        /// <summary>十字候选区域最小面积（像素²）</summary>
        public double CrossMinArea
        {
            get => _crossMinArea;
            set => Set(ref _crossMinArea, Math.Max(1, value));
        }

        private double _crossMaxArea = 99999999;
        /// <summary>十字候选区域最大面积（像素²）</summary>
        public double CrossMaxArea
        {
            get => _crossMaxArea;
            set => Set(ref _crossMaxArea, Math.Max(1, value));
        }

        private double _crossMinSize = 4;
        /// <summary>十字候选区域最小宽/高（像素）</summary>
        public double CrossMinSize
        {
            get => _crossMinSize;
            set => Set(ref _crossMinSize, Math.Max(1, value));
        }

        private double _crossMaxSize = 3000;
        /// <summary>十字候选区域最大宽/高（像素）</summary>
        public double CrossMaxSize
        {
            get => _crossMaxSize;
            set => Set(ref _crossMaxSize, Math.Max(1, value));
        }

        #endregion

        #region 模板匹配特征参数（FeatureType=TemplateMatch，复用全局模板库 Shape/NCC）
        // 与生产 ShapeMatch 节点同源（TemplateManager.MatchByName），对光照/反光最稳。
        // 吸放式标定中若工件被吸持后背面 Mark 不可见/无反光图案，改用工件正面整体模板。

        private string _templateName;
        /// <summary>模板名称（全局唯一；向导 Step2 下拉选择）</summary>
        public string TemplateName
        {
            get => _templateName;
            set => Set(ref _templateName, value);
        }

        private double _templateMinScore = 0.6;
        /// <summary>模板匹配最低分数（0~1）</summary>
        public double TemplateMinScore
        {
            get => _templateMinScore;
            set => Set(ref _templateMinScore, Clamp(value, 0.05, 1.0));
        }

        private double _templateAngleStart = -180;
        /// <summary>模板角度搜索下限（°）</summary>
        public double TemplateAngleStart
        {
            get => _templateAngleStart;
            set => Set(ref _templateAngleStart, value);
        }

        private double _templateAngleEnd = 180;
        /// <summary>模板角度搜索上限（°）</summary>
        public double TemplateAngleEnd
        {
            get => _templateAngleEnd;
            set => Set(ref _templateAngleEnd, value);
        }

        #endregion

        /// <summary>值域夹取</summary>
        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
