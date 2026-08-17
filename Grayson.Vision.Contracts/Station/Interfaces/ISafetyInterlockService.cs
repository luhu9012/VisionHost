using System;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// 安全联锁服务契约：
    /// - 输出：工位就绪、工位故障、工位复位中等 IO 信号给 PLC
    /// - 输入：监听 PLC 急停信号，触发后立刻终止所有工单并进入 ErrorLocked
    /// </summary>
    public interface ISafetyInterlockService
    {
        /// <summary>工位就绪输出信号</summary>
        bool StationReadyOutput { get; set; }

        /// <summary>工位故障输出信号</summary>
        bool StationFaultOutput { get; set; }

        /// <summary>工位复位中输出信号</summary>
        bool StationResettingOutput { get; set; }

        /// <summary>PLC 急停输入信号</summary>
        bool EmergencyStopInput { get; }

        /// <summary>急停触发事件</summary>
        event EventHandler<string> OnEmergencyStopTriggered;

        /// <summary>刷新输出 IO 状态</summary>
        Task UpdateOutputsAsync();

        /// <summary>监听输入 IO（由外部循环调用）</summary>
        Task PollInputsAsync();
    }
}
