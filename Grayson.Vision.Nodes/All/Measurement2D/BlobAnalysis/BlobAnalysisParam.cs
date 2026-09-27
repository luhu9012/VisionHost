using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.Measurement2D.BlobAnalysis
{
    /// <summary>
    /// Blob 分析参数（2026-09-26，梯队 B：计数 / 有无 / 异物 / 划痕判定）
    /// AreaMin/AreaMax 参与分析（连通域筛选）；CountMin/CountMax 供引擎判据消费
    /// （引擎反射读取，见 StandaloneVisionProcess 的 Blob 计数分支）。
    /// </summary>
    public class BlobAnalysisParam : ParamBase
    {
        /// <summary>
        /// 预览样板节点：属性面板改动 → 右侧预览窗口实时刷新连通域叠加与计数标注。
        /// </summary>
        public override bool SupportsPreview => true;

        private double _areaMin = 50;
        /// <summary>连通域面积下限（像素²）：小于该面积的斑块按噪声丢弃</summary>
        public double AreaMin
        {
            get => _areaMin;
            set => Set(ref _areaMin, value);
        }

        private double _areaMax = 1000000;
        /// <summary>连通域面积上限（像素²）</summary>
        public double AreaMax
        {
            get => _areaMax;
            set => Set(ref _areaMax, value);
        }

        private int _countMin = 1;
        /// <summary>判据下限：检出个数 &lt; CountMin ⇒ NG（有无检测配 1 即"必须有"）</summary>
        public int CountMin
        {
            get => _countMin;
            set => Set(ref _countMin, value);
        }

        private int _countMax = 999999;
        /// <summary>判据上限：检出个数 &gt; CountMax ⇒ NG（异物检测配 0 即"一个都不能有"）</summary>
        public int CountMax
        {
            get => _countMax;
            set => Set(ref _countMax, value);
        }
    }
}
