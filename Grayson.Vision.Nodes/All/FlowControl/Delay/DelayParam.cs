using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.Delay
{
    public class DelayParam : ParamBase
    {
        private int _delayMs = 100;
        /// <summary>
        /// 延时时长 (毫秒)
        /// </summary>
        public int DelayMs
        {
            get => _delayMs;
            set => Set(ref _delayMs, value);
        }

        #region 参数校验
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(DelayMs) && DelayMs < 0)
                    return "延时时间不能为负数";
                return null;
            }
        }
        #endregion
    }
}