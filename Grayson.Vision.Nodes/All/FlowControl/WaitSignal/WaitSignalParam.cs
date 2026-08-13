using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.WaitSignal
{
    public class WaitSignalParam : ParamBase
    {
        private string _signalName = "DI_0_Trigger";
        /// <summary>
        /// 等待的目标信号/变量名称或标识
        /// </summary>
        public string SignalName
        {
            get => _signalName;
            set => Set(ref _signalName, value);
        }

        private bool _expectedState = true;
        /// <summary>
        /// 期待触发的目标状态值 (True / False)
        /// </summary>
        public bool ExpectedState
        {
            get => _expectedState;
            set => Set(ref _expectedState, value);
        }

        private int _timeoutMs = 5000;
        /// <summary>
        /// 等待超时限制 (ms)，设为 <= 0 表示无限等待
        /// </summary>
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        private int _pollIntervalMs = 20;
        /// <summary>
        /// 信号轮询检查间隔时间 (ms)
        /// </summary>
        public int PollIntervalMs
        {
            get => _pollIntervalMs;
            set => Set(ref _pollIntervalMs, value);
        }

        #region 参数校验
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(SignalName) && string.IsNullOrWhiteSpace(SignalName))
                    return "信号标识名称不能为空";
                if (columnName == nameof(PollIntervalMs) && PollIntervalMs <= 0)
                    return "轮询间隔必须大于 0";
                return null;
            }
        }
        #endregion
    }
}