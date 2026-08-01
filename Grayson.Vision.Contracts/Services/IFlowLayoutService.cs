//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 流程图自动布局服务契约，算法本身与 UI 无关。
//===================================================================================

using System;
using Grayson.Vision.Contracts.Business.Models;

namespace Grayson.Vision.Contracts.Services
{
    /// <summary>
    /// 流程自动布局服务契约
    /// </summary>
    public interface IFlowLayoutService
    {
        /// <summary>
        /// 对当前流程模型执行自动拓扑布局。
        /// </summary>
        /// <param name="currentProcess">当前流程模型</param>
        /// <param name="logAction">布局日志回调</param>
        void AutoLayout(FlowProcessModel currentProcess, Action<string> logAction = null);
    }
}
