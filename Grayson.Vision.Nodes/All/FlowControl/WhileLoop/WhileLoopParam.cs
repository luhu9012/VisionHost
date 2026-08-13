using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.WhileLoop
{
    public class WhileLoopParam : ParamBase
    {
        private int _maxIterations = 1000;
        /// <summary>
        /// 最大允许循环迭代次数（防死循环机制）
        /// </summary>
        public int MaxIterations
        {
            get => _maxIterations;
            set => Set(ref _maxIterations, value);
        }

        private int _intervalMs = 0;
        /// <summary>
        /// 每次迭代间隔时间 (ms)
        /// </summary>
        public int IntervalMs
        {
            get => _intervalMs;
            set => Set(ref _intervalMs, value);
        }

        private bool _checkConditionAtStart = true;
        /// <summary>
        /// 是否在每次迭代开始前检查条件 (True=While, False=DoWhile)
        /// </summary>
        public bool CheckConditionAtStart
        {
            get => _checkConditionAtStart;
            set => Set(ref _checkConditionAtStart, value);
        }

        #region 参数校验
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(MaxIterations) && MaxIterations <= 0)
                    return "最大循环次数必须大于 0";
                if (columnName == nameof(IntervalMs) && IntervalMs < 0)
                    return "间隔时间不能为负数";
                return null;
            }
        }
        #endregion
    }
}