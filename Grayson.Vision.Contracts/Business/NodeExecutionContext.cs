//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Text;
//using System.Threading.Tasks;

//namespace Grayson.Vision.Contracts.Business
//{
//    public class NodeExecutionContext
//    {
//        /// <summary>
//        /// 全局/工位数据管线 (你原有的 ExecutionContext)[cite: 7]
//        /// </summary>
//        public ExecutionContext ExecutionContext { get; }

//        /// <summary>
//        /// 本次节拍的元数据 (触发时间、批次号、图像缓存等)
//        /// </summary>
//        public FrameCycleContext CycleContext { get; }

//        /// <summary>
//        /// 硬件提供者 (支持配方逻辑名称转物理设备)
//        /// </summary>

//        /// <summary>
//        /// 便捷日志输出
//        /// </summary>
//        public void Log(string msg) => ExecutionContext.Log(msg);

//        public NodeExecutionContext(ExecutionContext executionContext, FrameCycleContext cycleContext)
//        {
//            ExecutionContext = executionContext;
//            CycleContext = cycleContext;
//        }

//        /// <summary>
//        /// 核心方法：节点通过逻辑名称获取映射后的硬件
//        /// </summary>
//        public TDevice GetHardware<TDevice>(string logicalName) where TDevice : class
//        {
//            // 1. 从工位映射表里获取实际物理设备 ID
//            // 2. 从全局 SystemContext 获取设备句柄
//            // 避免了节点直接写死物理相机 IP 或设备 ID！
//            return default(TDevice);
//        }
//    }
//}
