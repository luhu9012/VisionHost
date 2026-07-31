using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace Grayson.Vision.Nodes.Logic.SwitchCase
{
    public class SwitchCaseParam
    {
        public string SelectorKey { get; set; } = "Type_A";
        // 配置的分支条件集合
        public List<string> Cases { get; set; } = new List<string> { "Type_A", "Type_B", "Type_C" };
    }
}
