using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vison.FlowEdit.Services
{
    public class LayoutService : IFlowLayoutService
    {
        /// <summary>
        /// 智能 S 型 (蛇形) 折叠拓扑排布算法：避免横向拉得太长
        /// </summary>
        public void AutoLayout(FlowProcessModel currentProcess, Action<string> logAction)
        {
            if (currentProcess == null || currentProcess.Nodes.Count == 0) return;

            var nodes = currentProcess.Nodes;
            var connections = currentProcess.Connections;

            // 1. 计算节点入度与拓扑层级
            var inDegree = nodes.ToDictionary(n => n, n => 0);
            var layers = nodes.ToDictionary(n => n, n => 0);

            foreach (var conn in connections)
            {
                if (conn.TargetNode != null && inDegree.ContainsKey(conn.TargetNode))
                    inDegree[conn.TargetNode]++;
            }

            var queue = new Queue<FlowNodeBase>(nodes.Where(n => inDegree[n] == 0));

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int currentLayer = layers[current];

                foreach (var conn in connections.Where(c => c.SourceNode == current && c.TargetNode != null))
                {
                    var target = conn.TargetNode;
                    layers[target] = Math.Max(layers[target], currentLayer + 1);
                    inDegree[target]--;
                    if (inDegree[target] == 0) queue.Enqueue(target);
                }
            }

            // 2. 按拓扑层级分组
            var layerGroups = nodes.GroupBy(n => layers[n]).OrderBy(g => g.Key).ToList();

            // 3. 参数设置
            int maxNodesPerRow = 4;        // 每一行最多展示的节点/层级数 (超过自动折行)
            double stepX = 220;            // 节点横向间距
            double stepY = 160;            // 节点纵向间距
            double rowOffsetStepY = 220;   // 折行后的行间距
            double startX = 60;
            double startY = 60;

            // 4. S型 (蛇形) 坐标计算
            for (int i = 0; i < layerGroups.Count; i++)
            {
                int rowIndex = i / maxNodesPerRow;       // 第几行
                int colIndex = i % maxNodesPerRow;       // 第几列
                bool isReverse = (rowIndex % 2 != 0);    // 奇数行反向排列 (蛇形蛇走)

                // 蛇形走位计算 X
                int actualCol = isReverse ? (maxNodesPerRow - 1 - colIndex) : colIndex;
                double currentX = startX + actualCol * stepX;

                var groupNodes = layerGroups[i].ToList();
                double groupHeight = (groupNodes.Count - 1) * stepY;
                double currentRowStartY = startY + (rowIndex * rowOffsetStepY);

                for (int j = 0; j < groupNodes.Count; j++)
                {
                    var node = groupNodes[j];
                    node.PosX = currentX;
                    node.PosY = currentRowStartY + (j * stepY);
                }
            }

            // 5. 刷新连线端点位置
            foreach (var conn in connections) conn.UpdatePoints();
            logAction?.Invoke("✨ 已完成 S 型自适应折叠排布。");
        }
    }
}