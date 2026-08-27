//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 特征提取算子参数选项。标定向导"特征配置"步骤通过滑块实时调节，
//        替代原先硬编码在 CalibrationService 内的阈值/圆度/面积/搜索半径等常量，
//        使操作员能按现场光源、工件材质、Mark 大小即时调参并实时预览效果。
//        实现 INotifyPropertyChanged（继承 ViewModelBase）以便滑块值实时回显。
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;

namespace Grayson.Vision.HalconWrapper.Calibration
{
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

        /// <summary>值域夹取</summary>
        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
