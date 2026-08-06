using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Communication
{
    // 1. 通信日志数据模型
    public class CommunicationMessage
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string DeviceKey { get; set; }
        public string Direction { get; set; } // "TX" (发送) 或 "RX" (接收)
        public string Content { get; set; }   // 报文十六进制 HEX 或 ASCII 字符串
        public bool IsSuccess { get; set; }
        public string Remark { get; set; }
    }

    // 2. 具备通信报文暴露能力的设备/策略接口
    public interface ICommunicationObservable
    {
        event EventHandler<CommunicationMessage> MessageTransmitted;
    }
}
