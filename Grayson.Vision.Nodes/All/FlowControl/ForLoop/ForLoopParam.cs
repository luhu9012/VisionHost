using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.ForLoop
{
    public class ForLoopParam : ParamBase
    {
        private int _startIndex = 0;
        /// <summary>
        /// 起始索引 (默认 0)
        /// </summary>
        public int StartIndex
        {
            get => _startIndex;
            set => Set(ref _startIndex, value);
        }

        private int _count = 10;
        /// <summary>
        /// 循环次数/迭代总数
        /// </summary>
        public int Count
        {
            get => _count;
            set => Set(ref _count, value);
        }

        private int _step = 1;
        /// <summary>
        /// 步长 (每次递增量，默认 1)
        /// </summary>
        public int Step
        {
            get => _step;
            set => Set(ref _step, value);
        }

        #region 参数校验
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(Count) && Count < 0)
                    return "循环次数不能为负数";
                if (columnName == nameof(Step) && Step <= 0)
                    return "循环步长必须大于 0";
                return null;
            }
        }
        #endregion
    }
}