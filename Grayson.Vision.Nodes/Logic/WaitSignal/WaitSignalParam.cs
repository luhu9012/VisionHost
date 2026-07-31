using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.WaitSignal
{
    public class WaitSignalParam
    {
        public string SignalName { get; set; } = "PLC_Ready_Signal";
        public bool TargetState { get; set; } = true;
        public int TimeoutMs { get; set; } = 5000;
        public int CheckIntervalMs { get; set; } = 50;
    }
}
