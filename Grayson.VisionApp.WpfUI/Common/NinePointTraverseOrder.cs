using System;

namespace Grayson.Vision.WpfUI.Common
{
    /// <summary>
    /// 九点走位方式（原属旧标定向导 Steps 族；R4/R5 退役后由机械手调试台可达图继续使用）。
    /// 仅改变"采集先后顺序"，不改变点编号与网格位置的映射（Index 1~9 固定对应 3x3 网格，
    /// 拟合计算、数据表展示、历史数据兼容均不受影响）。
    /// </summary>
    public enum NinePointTraverseMode
    {
        /// <summary>中心优先螺旋走位（推荐）：先采中心点，再按螺旋向外扩展。
        /// ① 中心点成像质量最好、全图搜索最易命中，首点成功率最高；
        /// ② 相邻两点恒为 1 个网格步长，行程最短且速度均匀，对运动系统最友好；
        /// ③ 从中心向外逐层扩展，Mark 不易走出视野。</summary>
        SpiralCenterFirst = 0,

        /// <summary>传统逐行扫描走位：从左上角起，逐行从左到右采集；与旧版本行为一致。</summary>
        RowScan = 1,
    }

    /// <summary>
    /// 九点走位顺序定义。数组元素为点 Index 编号，
    /// Index → 网格位置映射固定为：1=(0,0)左上, 2=(0,1), 3=(0,2)右上, 4=(1,0), 5=(1,1)中心,
    /// 6=(1,2), 7=(2,0)左下, 8=(2,1), 9=(2,2)右下（row/col 从 0 起）。
    /// </summary>
    public static class NinePointTraverseOrder
    {
        /// <summary>中心优先螺旋：5→6→3→2→1→4→7→8→9（相邻点恒为 1 步长）</summary>
        public static readonly int[] SpiralCenterFirst = { 5, 6, 3, 2, 1, 4, 7, 8, 9 };

        /// <summary>传统逐行扫描：1→2→3→…→9（左上角起行优先）</summary>
        public static readonly int[] RowScan = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

        /// <summary>按走位方式返回对应的采集顺序数组</summary>
        public static int[] GetOrder(NinePointTraverseMode mode)
        {
            return mode == NinePointTraverseMode.RowScan ? RowScan : SpiralCenterFirst;
        }
    }
}
