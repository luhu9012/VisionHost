//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 标定相关业务模型
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.ObjectModel;

namespace Grayson.Vision.Contracts.Calibration.Models
{
    /// <summary>
    /// 标定点数据模型
    /// </summary>
    public class CalibrationPointModel : ViewModelBase
    {
        private int _index;
        public int Index
        {
            get => _index;
            set => Set(ref _index, value);
        }

        private double _pixelX;
        public double PixelX
        {
            get => _pixelX;
            set => Set(ref _pixelX, value);
        }

        private double _pixelY;
        public double PixelY
        {
            get => _pixelY;
            set => Set(ref _pixelY, value);
        }

        private double _worldX;
        public double WorldX
        {
            get => _worldX;
            set => Set(ref _worldX, value);
        }

        private double _worldY;
        public double WorldY
        {
            get => _worldY;
            set => Set(ref _worldY, value);
        }
    }

    /// <summary>
    /// R轴旋转拟合点模型
    /// </summary>
    public class RotationPointModel : ViewModelBase
    {
        private double _angleDeg;
        public double AngleDeg
        {
            get => _angleDeg;
            set => Set(ref _angleDeg, value);
        }

        private double _pixelX;
        public double PixelX
        {
            get => _pixelX;
            set => Set(ref _pixelX, value);
        }

        private double _pixelY;
        public double PixelY
        {
            get => _pixelY;
            set => Set(ref _pixelY, value);
        }
    }

    /// <summary>
    /// 标定样本模型 - 包含一张标定图像及其标定点集合
    /// </summary>
    public class CalibrationSampleModel : ViewModelBase
    {
        private string _imagePath;
        /// <summary>标定图像路径</summary>
        public string ImagePath
        {
            get => _imagePath;
            set => Set(ref _imagePath, value);
        }

        private byte[] _thumbnailData;
        /// <summary>缩略图二进制数据（支持序列化存储）</summary>
        public byte[] ThumbnailData
        {
            get => _thumbnailData;
            set => Set(ref _thumbnailData, value);
        }

        private double[] _px;
        /// <summary>像素 X 坐标数组</summary>
        public double[] Px
        {
            get => _px;
            set => Set(ref _px, value);
        }

        private double[] _py;
        /// <summary>像素 Y 坐标数组</summary>
        public double[] Py
        {
            get => _py;
            set => Set(ref _py, value);
        }

        private bool _accepted;
        /// <summary>是否通过标定验收</summary>
        public bool Accepted
        {
            get => _accepted;
            set => Set(ref _accepted, value);
        }

        /// <summary>标定点详细列表</summary>
        public ObservableCollection<CalibrationPointModel> Points { get; set; } =
            new ObservableCollection<CalibrationPointModel>();
    }
}