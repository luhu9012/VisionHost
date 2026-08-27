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
    /// 标定类型枚举
    /// </summary>
    public enum CalibrationType
    {
        NinePointHandEye,      // 1. 标准九点手眼标定 (仅 X/Y 平移矩阵)
        Checkerboard2D,        // 3. 2D 棋盘格/畸变矫正标定
        HandEyeWithRotation,   // 多点手眼 + 旋转中心拟合
        CameraLensDistortion,  // 畸变/内参标定 (棋盘格/圆点阵列)
        PixelScale             // 像素当量标定
    }
    /// <summary>
    /// 相机安装物理模式
    /// </summary>
    public enum EyeMode
    {
        EyeToHand, // 眼在手外 (相机固定，工件/平台移动)
        EyeInHand  // 眼在手上 (相机随轴/机器人末端移动)
    }
    /// <summary>
    /// 标定特征类型（第二步"特征配置"中由用户选择，决定特征提取算子）
    /// </summary>
    public enum CalibrationFeatureType
    {
        CircleMark, // 圆形 Mark 点 (提取圆心)：阈值分割 + 圆度筛选 + 亚像素圆拟合
        CrossMark   // 十字 Mark 点 (形状匹配)：几何结构法（骨架 + 直线交叉点）
    }
    public class CalibrationProfile : ViewModelBase
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string BoundStationCode { get; set; }
        public string BoundDeviceId { get; set; }
        public string BindingInfo { get; set; }
        public string CameraId { get; set; } = "Cam_01";
        public string AxisId { get; set; } = "Axis_X";
        public DateTime UpdatedAt { get; set; } = DateTime.Now;

        // --- 新增：物理场景参数 ---
        private EyeMode _eyeMode = EyeMode.EyeToHand;
        public EyeMode EyeMode
        {
            get => _eyeMode;
            set => Set(ref _eyeMode, value);
        }

        private bool _hasToolOffset;
        /// <summary>轴末端是否有吸嘴/夹具旋转偏移 (TCP)</summary>
        public bool HasToolOffset
        {
            get => _hasToolOffset;
            set => Set(ref _hasToolOffset, value);
        }

        private string _boundDistortionProfileId;
        /// <summary>关联的前置相机畸变标定方案 ID (若为空则不执行畸变矫正)</summary>
        public string BoundDistortionProfileId
        {
            get => _boundDistortionProfileId;
            set => Set(ref _boundDistortionProfileId, value);
        }

        // --- 新增：拟合计算输出结果 ---
        private double _toolCenterPx;
        public double ToolCenterPx { get => _toolCenterPx; set => Set(ref _toolCenterPx, value); }

        private double _toolCenterPy;
        public double ToolCenterPy { get => _toolCenterPy; set => Set(ref _toolCenterPy, value); }

        private double _toolCenterWx;
        public double ToolCenterWx { get => _toolCenterWx; set => Set(ref _toolCenterWx, value); }

        private double _toolCenterWy;
        public double ToolCenterWy { get => _toolCenterWy; set => Set(ref _toolCenterWy, value); }

        private bool _isCalibrated;
        public bool IsCalibrated { get => _isCalibrated; set => Set(ref _isCalibrated, value); }

        private double _rmsError;
        public double RmsError { get => _rmsError; set => Set(ref _rmsError, value); }

        private string _homMatFilePath;
        public string HomMatFilePath { get => _homMatFilePath; set => Set(ref _homMatFilePath, value); }

        private CalibrationType _type = CalibrationType.NinePointHandEye;
        public CalibrationType Type { get => _type; set => Set(ref _type, value); }

        // --- 新增：标定所绑定的轴索引和基准位置 ---
        private int _bindXAxisIndex = 1;
        /// <summary>标定所绑定的 X 轴索引 (默认1号轴)</summary>
        public int BindXAxisIndex
        {
            get => _bindXAxisIndex;
            set => Set(ref _bindXAxisIndex, value);
        }

        private int _bindYAxisIndex = 3;
        /// <summary>标定所绑定的 Y 轴索引 (左工位设为3，右工位设为2)</summary>
        public int BindYAxisIndex
        {
            get => _bindYAxisIndex;
            set => Set(ref _bindYAxisIndex, value);
        }

        private double _basePosX;
        /// <summary>标定起始基准 X 坐标 (Mark对准相机视野中心时的绝对物理位置)</summary>
        public double BasePosX
        {
            get => _basePosX;
            set
            {
                if (Set(ref _basePosX, value))
                {
                    IsBasePosSet = true; // 手动输入或按钮设置均视为已设置基准
                }
            }
        }

        private double _basePosY;
        /// <summary>标定起始基准 Y 坐标</summary>
        public double BasePosY
        {
            get => _basePosY;
            set
            {
                if (Set(ref _basePosY, value))
                {
                    IsBasePosSet = true;
                }
            }
        }

        private bool _isBasePosSet;
        /// <summary>
        /// 基准位置是否已通过"设当前轴位置为基准"或手动输入设置。
        /// 基准坐标可能恰为 0（机台原点），因此不能用 BasePosX==0 判断是否设置过；
        /// 九点采样前必须校验该标志，避免未设基准就采样导致走位出视野、首点提取失败。
        /// </summary>
        public bool IsBasePosSet
        {
            get => _isBasePosSet;
            set => Set(ref _isBasePosSet, value);
        }

        private CalibrationFeatureType _featureType = CalibrationFeatureType.CircleMark;
        /// <summary>标定特征类型：圆形 Mark (提取圆心) / 十字 Mark (形状匹配)，第二步特征配置中由用户选择</summary>
        public CalibrationFeatureType FeatureType
        {
            get => _featureType;
            set => Set(ref _featureType, value);
        }
    }




    /// <summary>
    /// 标定数据存储模型
    /// </summary>
    public class CalibrationProfileModel
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "默认标定方案";
        public CalibrationType Type { get; set; } = CalibrationType.NinePointHandEye;
        public string BoundDeviceOrStation { get; set; } = "Cam1 / Station1";

        /// <summary>重投影均方根误差 (RMS) mm</summary>
        public double RmsError { get; set; }

        /// <summary>标定矩阵存储路径 (.tup)</summary>
        public string MatrixFilePath { get; set; }

        /// <summary>最后标定时间</summary>
        public DateTime LastCalibratedTime { get; set; }

        /// <summary>是否有效标定</summary>
        public bool IsCalibrated => !string.IsNullOrEmpty(MatrixFilePath) && System.IO.File.Exists(MatrixFilePath);
    }
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

        private bool _isCaptured;
        /// <summary>
        /// 该点是否已成功采集回填。显式标志替代 (0,0) 哨兵判断：
        /// 真实特征点像素坐标可能恰为 0（图像左上角），哨兵会把已采集点误判为未采集，
        /// 导致 FirstOrDefault 永远选中同一个点、坐标反复不回填。
        /// </summary>
        public bool IsCaptured
        {
            get => _isCaptured;
            set => Set(ref _isCaptured, value);
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

        private bool _isCaptured;
        /// <summary>是否已采集回填（同 CalibrationPointModel，替代 0 哨兵判断）</summary>
        public bool IsCaptured
        {
            get => _isCaptured;
            set => Set(ref _isCaptured, value);
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