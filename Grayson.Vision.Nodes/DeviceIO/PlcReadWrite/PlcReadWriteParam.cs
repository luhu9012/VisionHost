using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.DeviceIO.PlcReadWrite
{
    public enum PlcOpType { Read, Write }

    public class PlcReadWriteParam
    {
        public string PlcAlias { get; set; } = "PLC_Main";
        public PlcOpType Operation { get; set; } = PlcOpType.Read;
        public string DbAddress { get; set; } = "DB100.DBD0";
        public string DataType { get; set; } = "Float"; // Int, Float, Bool, String
        public string WriteValue { get; set; } = "0";
        public int TimeoutMs { get; set; } = 2000;
    }
}