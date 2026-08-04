// Grayson.Vision.Contracts/Business/Engine/Execution/ExecutionChain.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Flow.Nodes;
using Grayson.Vision.Contracts.Infrastructure.Logging;

namespace Grayson.Vision.Contracts.Flow.Executants
{
    /// <summary>
    /// 构建执行链后的返回结果实体
    /// 封装构建状态、错误信息、异常节点、最终拓扑链，方便上层编辑器/引擎做校验提示
    /// </summary>
    public class BuildChainResult
    {
        /// <summary>
        /// 构建&校验是否全部通过 true=无环无异常 false=存在环形/流程为空等问题
        /// </summary>
        public bool IsSuccess { get; set; } = true;

        /// <summary>
        /// 构建失败时的错误描述文本，用于UI弹窗/日志提示用户
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// 校验失败时，无法参与拓扑排序的异常节点集合（环形依赖内的节点）
        /// </summary>
        public List<FlowNodeBase> InvalidNodes { get; set; } = new List<FlowNodeBase>();

        /// <summary>
        /// 校验通过后生成的有序拓扑执行链；校验失败时该字段为空
        /// </summary>
        public ExecutionChain Chain { get; set; }
    }

    /// <summary>
    /// 拓扑执行链实体
    /// 原理：基于Kahn拓扑排序算法，对画布有向流程图生成合法执行顺序
    /// 规则：有依赖关系时，上游输出节点一定排在下游输入节点前面，保证运行时数据就绪
    /// 用途：流程运行前预编译执行顺序，同时提前检测环形死锁（A依赖B、B又依赖A这类死循环）
    /// </summary>
    public class ExecutionChain
    {
        /// <summary>
        /// Kahn算法排序完成后的有序节点列表，引擎严格按该顺序逐个执行节点
        /// 有前置依赖的节点，一定会在它所有上游节点执行完毕后才出现
        /// </summary>
        public List<FlowNodeBase> Nodes { get; private set; } = new List<FlowNodeBase>();

        /// <summary>
        /// 流程内全部有效连线缓存
        /// 过滤掉源/目标为空的脏连线，执行时依靠该集合传递节点端口数据
        /// </summary>
        public List<ConnectionModel> Connections { get; private set; } = new List<ConnectionModel>();

        /// <summary>
        /// 拓扑链内总节点数量
        /// </summary>
        public int Count => Nodes.Count;

        /// <summary>
        /// 在编辑器阶段构建拓扑执行链，并完整校验流程合法性（空流程、环形死锁）
        /// 核心算法：Kahn拓扑排序（入度法）
        /// 返回封装好的结果对象，包含成功标记、错误、异常节点、有序执行链
        /// </summary>
        /// <param name="process">画布完整流程模型（包含全部节点、连线）</param>
        /// <returns>构建校验结果</returns>
        public static BuildChainResult BuildAndValidate(FlowProcessModel process)
        {
            // 初始化返回结果容器
            var result = new BuildChainResult();
            // 初始化空拓扑执行链
            var chain = new ExecutionChain();

            // 校验1：流程对象为空 或 流程内不存在任何节点，直接判定失败
            if (process == null || process.Nodes == null || !process.Nodes.Any())
            {
                result.IsSuccess = false;
                result.ErrorMessage = "流程为空，无需生成执行链。";
                return result;
            }

            // 取出全部节点转为本地列表，方便后续遍历计算
            var nodes = process.Nodes.ToList();
            // 过滤有效连线：剔除源节点、目标节点为空的无效连线，避免计算异常
            var connections = process.Connections?
                .Where(c => c.SourceNode != null && c.TargetNode != null).ToList() ?? new List<ConnectionModel>();
            // 将有效连线存入执行链，供引擎运行时读取数据流关系
            chain.Connections = connections;

            #region Kahn拓扑排序核心步骤1：计算所有节点的「入度 In-Degree」
            /*
             * 入度概念：一个节点有多少条输入连线 = 有多少个上游节点需要先执行完成
             * 举例：A→C、B→C，C的入度=2，必须A、B全部执行完，C才能运行
             * 入度=0：代表该节点无任何前置依赖，可以最先启动执行
             */
            // 构建字典：key=节点，value=该节点当前入度初始值0
            var inDegree = nodes.ToDictionary(n => n, n => 0);
            // 遍历全部连线，累加每个目标节点的入度计数
            foreach (var conn in connections)
            {
                // 仅统计当前流程内存在的节点，防止脏数据报错
                if (inDegree.ContainsKey(conn.TargetNode))
                {
                    inDegree[conn.TargetNode]++;
                }
            }
            #endregion

            #region Kahn拓扑排序核心步骤2：初始化队列，存入所有无依赖（入度=0）的起始节点
            var queue = new Queue<FlowNodeBase>(nodes.Where(n => inDegree[n] == 0));
            #endregion

            #region Kahn拓扑排序核心步骤3：循环出队，生成有序执行序列
            // 循环处理队列中可执行的节点
            while (queue.Count > 0)
            {
                // 取出队首无依赖节点
                var curr = queue.Dequeue();
                // 加入最终有序执行链
                chain.Nodes.Add(curr);

                // 找到当前节点所有下游输出连线（所有以curr作为上游的节点）
                var outgoingConnections = connections.Where(c => c.SourceNode == curr);
                foreach (var conn in outgoingConnections)
                {
                    var target = conn.TargetNode;
                    if (inDegree.ContainsKey(target))
                    {
                        // 当前上游节点执行完毕，下游节点的依赖计数-1
                        inDegree[target]--;
                        // 下游节点所有上游全部执行完，入度变为0，加入队列等待执行
                        if (inDegree[target] == 0)
                        {
                            queue.Enqueue(target);
                        }
                    }
                }
            }
            #endregion

            #region 强校验：判断是否存在环形依赖（死锁流程）
            /*
             * Kahn算法特性：
             * 正常无环流程：所有节点都会被取出加入chain.Nodes，chain.Nodes.Count = 总节点数
             * 存在环形依赖（A→B，B→A）：环内所有节点入度永远无法减到0，永远不会进入队列，不会被加入有序列表
             * 数量不一致=存在环，直接返回失败，收集异常节点给编辑器高亮提示
             */
            if (chain.Nodes.Count < nodes.Count)
            {
                // 收集所有未进入有序列表的环内节点
                var unvisitedNodes = nodes.Where(n => !chain.Nodes.Contains(n)).ToList();
                result.IsSuccess = false;
                result.ErrorMessage = $"检测到流程中存在环形逻辑/死锁，涉及 {unvisitedNodes.Count} 个节点无法排序！";
                result.InvalidNodes = unvisitedNodes;
                return result;
            }
            #endregion

            #region 可选扩展校验：遍历有序节点，执行自定义节点合法性校验
            // 此处预留扩展点：可循环每个节点调用自身校验方法
            // 例如：检查参数是否为空、端口配置非法、节点配置缺失等业务校验
            foreach (var node in chain.Nodes)
            {
                // node.Validate();
            }
            #endregion

            // 全部校验通过，把生成好的拓扑链赋值给返回对象
            result.Chain = chain;
            // 输出日志告知构建完成
            LogBus.Info("Engine", $"[ExecutionChain] 编辑器成功构建并校验拓扑执行链，节点总数: {chain.Nodes.Count}");
            return result;
        }
    }
}