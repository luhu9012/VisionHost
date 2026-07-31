using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Business.Models
{
    public class CompositeFlowNode : FlowNodeBase
    {
        // 关联的 JSON 模板文件相对路径 (例如: "Recipes/卡尺检测模板.json")
        private string _recipeFilePath;
        public string RecipeFilePath
        {
            get => _recipeFilePath;
            set => Set(ref _recipeFilePath, value);
        }

        // 内存中的内部子流程（数据上下文）
        private FlowProcessModel _subProcess = new FlowProcessModel();
        public FlowProcessModel SubProcess
        {
            get => _subProcess;
            set => Set(ref _subProcess, value);
        }

        public CompositeFlowNode()
        {
            Category = NodeCategory.CompositeEx;
            Type = NodeType.CompositeFlow;
            DisplayName = "📦 复合配方节点";
            Description = "双击可进入内部子流程展开编排。";

            // 默认复合节点入端口与出端口
            InputPorts.Add(new NodePort { PortName = "In", PortType = PortType.In, RelativeY = 35 });
            OutputPorts.Add(new NodePort { PortName = "Out", PortType = PortType.Out, RelativeY = 35, ColorHex = "#9B59B6" });
        }
    }
}
