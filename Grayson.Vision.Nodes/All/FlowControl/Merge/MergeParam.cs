using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.Merge
{
    public class MergeParam : ParamBase
    {
        private int _inputPortCount = 2;
        /// <summary>
        /// 汇合入口数量（默认支持 2 路，可拓展为多路动态端口）
        /// </summary>
        public int InputPortCount
        {
            get => _inputPortCount;
            set => Set(ref _inputPortCount, value);
        }

        private bool _passFirstNonNull = true;
        /// <summary>
        /// 是否开启首个有效值透传模式（Coalesce）
        /// </summary>
        public bool PassFirstNonNull
        {
            get => _passFirstNonNull;
            set => Set(ref _passFirstNonNull, value);
        }
    }
}