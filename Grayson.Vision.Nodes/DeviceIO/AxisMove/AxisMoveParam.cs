using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.AxisMove
{
    public class AxisMoveParam
    {
        public string AxisName { get; set; } = "Axis_X";
        public bool IsRelative { get; set; } = false; // false=绝对运动, true=相对运动
        public double TargetPosition { get; set; } = 100.0;
        public double Speed { get; set; } = 50.0;
        public bool WaitUntilDone { get; set; } = true;
        public int TimeoutMs { get; set; } = 10000;
    }
}
