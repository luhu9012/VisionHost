using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.DigitalOutput
{
    public class DigitalOutputParam
    {
        public string IoCardAlias { get; set; } = "IOCard_0";
        public int Channel { get; set; } = 1;
        public bool OutputValue { get; set; } = true;
        public bool IsPulse { get; set; } = false; // 是否为脉冲信号 (例如吹气/气缸吸附)
        public int PulseDurationMs { get; set; } = 200;
    }
}
