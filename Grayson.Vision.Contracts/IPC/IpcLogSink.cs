using Grayson.Vision.Contracts.Logging;
using System;

namespace Grayson.Vision.Contracts.IPC
{
    public class IpcLogSink
    {
        private readonly Action<string> _sendIpcAction;

        public IpcLogSink(Action<string> sendIpcAction)
        {
            _sendIpcAction = sendIpcAction;
        }

        public void Enable()
        {
            LogBus.OnLogProduced += ForwardLogToIpc;
        }

        public void Disable()
        {
            LogBus.OnLogProduced -= ForwardLogToIpc;
        }

        private void ForwardLogToIpc(LogEntry entry)
        {
            try
            {
                var msg = new IpcMessage
                {
                    MessageType = IpcMessageType.EventBroadcast,
                    Action = "OnLog",
                    //PayloadJson = JsonConvert.SerializeObject(entry)
                };
                //_sendIpcAction?.Invoke(JsonConvert.SerializeObject(msg));
            }
            catch { /* 防挂掉 */ }
        }
    }
}