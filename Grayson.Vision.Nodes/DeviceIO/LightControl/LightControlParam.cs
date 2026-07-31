using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.LightControl
{
    public class LightControlParam
    {
        public string ControllerAlias { get; set; } = "Light_Controller_1";
        public int Channel { get; set; } = 1;
        public int Brightness { get; set; } = 120; // 0-255
        public bool TurnOn { get; set; } = true;
    }
}
