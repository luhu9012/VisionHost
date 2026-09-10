//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: FitCircleParam.cs
// 说 明: FitCircle 节点参数（P2 §5.2 测量节点族）：环形/弧形卡尺在种子圆心周围取样，
//        亚像素拟合圆心与半径。种子几何=参数设定值，可被上游数据端口（ShapeMatch 等）覆盖，
//        组成"粗定位 → 精测"链；测量参数语义与模板资产 TemplateCaliper(Circle) 完全一致。
//===================================================================================
using System;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Measurement2D.FitCircle
{
    public class FitCircleParam : ParamBase
    {
        /// <summary>调参时预览实时刷新测量带与拟合结果。</summary>
        public override bool SupportsPreview => true;

        // ── 种子几何（可被上游 SeedRow/SeedCol/SeedRadius 数据端口覆盖）──
        private double _seedRow;
        /// <summary>粗圆心行（图像 Y；环形带取样中心，拟合圆通常收敛到真圆心的 ±10px 内即可）</summary>
        public double SeedRow { get => _seedRow; set => Set(ref _seedRow, value); }

        private double _seedCol;
        /// <summary>粗圆心列（图像 X）</summary>
        public double SeedCol { get => _seedCol; set => Set(ref _seedCol, value); }

        private double _radius = 50.0;
        /// <summary>期望半径（px，环形测量带的中线半径；拟合输出为亚像素修正后的半径）</summary>
        public double Radius { get => _radius; set => Set(ref _radius, value); }

        private double _annulusHalf = 8.0;
        /// <summary>环半宽（px）：取样带覆盖半径 ±AnnulusHalf 的环形区域；太小漏边、太大混入相邻边缘</summary>
        public double AnnulusHalf { get => _annulusHalf; set => Set(ref _annulusHalf, value); }

        private double _arcStartDeg = 0.0;
        /// <summary>弧段起始角（°；0=右侧，逆时针为正；整圆=0）</summary>
        public double ArcStartDeg { get => _arcStartDeg; set => Set(ref _arcStartDeg, value); }

        private double _arcExtentDeg = 360.0;
        /// <summary>弧段跨度（°；整圆=360；半圆工件可设 180 减少干扰边）</summary>
        public double ArcExtentDeg { get => _arcExtentDeg; set => Set(ref _arcExtentDeg, value); }

        // ── 测量参数（HALCON MeasurePos）──
        private double _sigma = 1.0;
        /// <summary>高斯平滑 Sigma（越大边缘越钝越稳）</summary>
        public double Sigma { get => _sigma; set => Set(ref _sigma, value); }

        private double _threshold = 30.0;
        /// <summary>边缘对比度阈值（灰度差；太小抓噪声、太大漏边）</summary>
        public double Threshold { get => _threshold; set => Set(ref _threshold, value); }

        private CaliperTransition _transition = CaliperTransition.All;
        /// <summary>边缘极性：All=任意明暗过渡 / Positive=暗→亮 / Negative=亮→暗（受光照影响，先用 All）</summary>
        public CaliperTransition Transition
        {
            get => _transition;
            set => Set(ref _transition, value);
        }

        private CaliperEdgeSelect _select = CaliperEdgeSelect.All;
        /// <summary>环带内多条边缘时取哪条链：All=幅度更大的一条链 / First=距种子半径近 / Last=远</summary>
        public CaliperEdgeSelect Select
        {
            get => _select;
            set => Set(ref _select, value);
        }

        private int _minEdgePoints = 8;
        /// <summary>有效边缘点下限（不足则本次拟合失败并提示）</summary>
        public int MinEdgePoints { get => _minEdgePoints; set => Set(ref _minEdgePoints, value); }

        /// <summary>极性下拉数据源</summary>
        public CaliperTransition[] TransitionItems { get; } =
            (CaliperTransition[])Enum.GetValues(typeof(CaliperTransition));

        /// <summary>取边下拉数据源</summary>
        public CaliperEdgeSelect[] SelectItems { get; } =
            (CaliperEdgeSelect[])Enum.GetValues(typeof(CaliperEdgeSelect));

        /// <summary>种子是否有效（圆心在图内 + 半径>0 + 环半宽>0）</summary>
        public bool HasValidSeed(int imgW, int imgH) =>
            _radius > 0.5 && _annulusHalf > 0.5 &&
            _seedRow > 0 && _seedRow < imgH &&
            _seedCol > 0 && _seedCol < imgW;
    }
}
