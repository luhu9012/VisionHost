using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.FlowControl.TerminateFlow
{
    public class TerminateFlowParam : ParamBase
    {
        private string _reason = "触发紧急中断条件，流程强行终止";
        /// <summary>
        /// 终止原因 / 异常日志信息
        /// </summary>
        public string Reason
        {
            get => _reason;
            set => Set(ref _reason, value);
        }

        private bool _isError = true;
        /// <summary>
        /// 是否标记为异常退出 (True: 抛出错误 / False: 正常提前结束)
        /// </summary>
        public bool IsError
        {
            get => _isError;
            set => Set(ref _isError, value);
        }

        private int _exitCode = -1;
        /// <summary>
        /// 退出代码 (Exit Code)
        /// </summary>
        public int ExitCode
        {
            get => _exitCode;
            set => Set(ref _exitCode, value);
        }

        #region 参数校验
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(Reason) && string.IsNullOrWhiteSpace(Reason))
                    return "终止原因不能为空";
                return null;
            }
        }
        #endregion
    }
}