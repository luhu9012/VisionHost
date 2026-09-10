//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: FitLineParam.cs
// 说 明: FitLine 节点参数（P2 §5.2 测量节点族）：沿目标边缘散布多探针的直线卡尺，
//        取样后 FitLineContourXld("tukey") 亚像素拟合直线（输出端点+拟合残差）。
//        几何种子=参数值，可被上游数据端口覆盖，组成"粗定位 → 精测"链；
//        测量参数语义与模板资产 TemplateCaliper(Line) 完全一致。
//===================================================================================
using System;
using Grayson.Vision.Contracts.Templates.Models;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Measurement2D.FitLine
{
    public class FitLineParam : ParamBase
    {
        /// <summary>调参时预览实时刷新探针与拟合直线。</summary>
        public override bool SupportsPreview => true;

        // ── 种子几何（可被上游 SeedMidRow/SeedMidCol/SeedPhi 端口覆盖）──
        private double _seedMidRow;
        /// <summary>边缘段中点行（图像 Y；探针带中心，粗定位落在边缘±ScanHalf 内即可）</summary>
        public double SeedMidRow { get => _seedMidRow; set => Set(ref _seedMidRow, value); }

        private double _seedMidCol;
        /// <summary>边缘段中点列（图像 X）</summary>
        public double SeedMidCol { get => _seedMidCol; set => Set(ref _seedMidCol, value); }

        private double _edgePhiDeg = 0.0;
        /// <summary>边缘走向（°）：0=水平边缘（探针沿水平散布、竖直扫描）；90=竖直边缘；斜边填实际角度</summary>
        public double EdgePhiDeg { get => _edgePhiDeg; set => Set(ref _edgePhiDeg, value); }

        // ── 卡尺带尺寸 ──
        private double _halfSpanAlongEdge = 40.0;
        /// <summary>探针沿边散布半跨距（px；边缘段长度一半，覆盖约 NumPoints 个探针）</summary>
        public double HalfSpanAlongEdge { get => _halfSpanAlongEdge; set => Set(ref _halfSpanAlongEdge, value); }

        private double _scanHalf = 10.0;
        /// <summary>横跨边缘探测半长（px；单探针沿法向扫 ±ScanHalf，须盖住边缘模糊带）</summary>
        public double ScanHalf { get => _scanHalf; set => Set(ref _scanHalf, value); }

        private double _probeAvgHalf = 2.0;
        /// <summary>单探针沿边平均半宽（px；默认 2，过大边缘点沿边模糊）</summary>
        public double ProbeAvgHalf { get => _probeAvgHalf; set => Set(ref _probeAvgHalf, value); }

        private int _numPoints = 7;
        /// <summary>沿边探针数（3~15；越多拟合越稳，边缘短则减少）</summary>
        public int NumPoints { get => _numPoints; set => Set(ref _numPoints, value); }

        // ── 测量参数（HALCON MeasurePos）──
        private double _sigma = 1.0;
        /// <summary>高斯平滑 Sigma（越大边缘越钝越稳）</summary>
        public double Sigma { get => _sigma; set => Set(ref _sigma, value); }

        private double _threshold = 30.0;
        /// <summary>边缘对比度阈值（灰度差；太小抓噪声、太大漏边）</summary>
        public double Threshold { get => _threshold; set => Set(ref _threshold, value); }

        private CaliperTransition _transition = CaliperTransition.All;
        /// <summary>边缘极性：All=任意明暗过渡 / Positive=暗→亮 / Negative=亮→暗</summary>
        public CaliperTransition Transition
        {
            get => _transition;
            set => Set(ref _transition, value);
        }

        private CaliperEdgeSelect _select = CaliperEdgeSelect.All;
        /// <summary>单探针带内多条边缘时取哪条：All=幅度最大 / First=距带中心近 / Last=远</summary>
        public CaliperEdgeSelect Select
        {
            get => _select;
            set => Set(ref _select, value);
        }

        private int _minEdgePoints = 3;
        /// <summary>有效边缘点下限（不足则本次拟合失败并提示）</summary>
        public int MinEdgePoints { get => _minEdgePoints; set => Set(ref _minEdgePoints, value); }

        /// <summary>极性下拉数据源</summary>
        public CaliperTransition[] TransitionItems { get; } =
            (CaliperTransition[])Enum.GetValues(typeof(CaliperTransition));

        /// <summary>取边下拉数据源</summary>
        public CaliperEdgeSelect[] SelectItems { get; } =
            (CaliperEdgeSelect[])Enum.GetValues(typeof(CaliperEdgeSelect));

        /// <summary>种子是否有效（中点在图内 + 带尺寸合法）</summary>
        public bool HasValidSeed(int imgW, int imgH) =>
            _scanHalf > 0.5 && _halfSpanAlongEdge > 0.5 &&
            _seedMidRow > 0 && _seedMidRow < imgH &&
            _seedMidCol > 0 && _seedMidCol < imgW;
    }
}
