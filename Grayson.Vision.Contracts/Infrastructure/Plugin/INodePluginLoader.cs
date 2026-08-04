//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 节点插件加载器契约，供应用层实现以扫描并注册外部节点 DLL。
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Infrastructure.Plugin
{
    /// <summary>
    /// 节点插件加载器契约
    /// </summary>
    public interface INodePluginLoader
    {
        /// <summary>
        /// 扫描指定目录下的节点插件 DLL，将节点执行器与模板资源注册到当前应用。
        /// </summary>
        /// <param name="pluginFolder">插件存放文件夹路径</param>
        /// <param name="logAction">加载日志回调</param>
        void LoadPlugins(string pluginFolder, Action<string> logAction = null);
    }
}
