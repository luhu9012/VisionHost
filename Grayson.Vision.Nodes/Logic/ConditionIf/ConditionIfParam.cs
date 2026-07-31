using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Nodes.Logic.ConditionIf
{
    public enum CompareOp { Greater, GreaterEqual, Equal, NotEqual, Less, LessEqual }

    public class ConditionIfParam
    {
        public bool UseInputPort { get; set; } = true; // 是否直接使用布尔端口输入
        public CompareOp Operator { get; set; } = CompareOp.Equal;
        public double TargetValue { get; set; } = 0.0;
    }
}
