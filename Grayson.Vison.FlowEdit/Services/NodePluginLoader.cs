// 业务基础、特性、数据模型、节点工厂命名空间引用
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Attributes;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Business.Factories;
// IO、反射、资源读取、WPF相关依赖
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Windows;

namespace Grayson.Vison.FlowEdit.Services
{
    /// <summary>
    /// 插件加载服务静态类
    /// 整体作用：加载外部算子插件DLL，完成两件核心工作
    /// 1. 读取DLL内所有带[Node]特性的执行器，注册到全局NodeFactory工厂，供给工具箱/流程引擎使用
    /// 2. 解析DLL内嵌的XAML模板资源，提取DataTemplate合并到应用全局资源，画布自动加载节点UI样式
    /// </summary>
    public static class NodePluginLoader
    {
        /// <summary>
        /// 入口方法：批量扫描插件目录，加载所有节点插件DLL
        /// </summary>
        /// <param name="pluginFolder">插件存放文件夹路径</param>
        /// <param name="logAction">日志输出回调，用于打印加载成功/失败信息</param>
        public static void LoadPlugins(string pluginFolder, Action<string> logAction = null)
        {
            // 文件夹不存在直接终止加载
            if (!Directory.Exists(pluginFolder))
                return;

            // 匹配命名规则：Grayson.Vision.Nodes开头的dll算子插件
            string[] dllFiles = Directory.GetFiles(pluginFolder, "Grayson.Vision.Nodes*.dll");
            // 遍历每一个插件文件
            foreach (string file in dllFiles)
            {
                try
                {
                    // 加载dll到当前程序域
                    Assembly assembly = Assembly.LoadFrom(file);
                    // 第一步：注册dll内所有节点执行器到全局工厂
                    RegisterNodeTypes(assembly, logAction);
                    // 第二步：解析dll内内嵌XAML模板，注入全局资源
                    RegisterXamlTemplates(assembly, logAction);
                }
                catch (Exception ex)
                {
                    // 单个dll加载异常不中断整体流程，打印错误日志
                    logAction?.Invoke($"❌ 加载插件 DLL 失败 [{file}]: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 解析程序集，提取所有合法节点执行器，注册到全局NodeFactory
        /// </summary>
        /// <param name="assembly">插件dll程序集对象</param>
        /// <param name="logAction">日志回调</param>
        private static void RegisterNodeTypes(Assembly assembly, Action<string> logAction)
        {
            // 筛选条件：实现INodeExecutor接口 + 标记了NodeAttribute节点特性
            List<Type> types = assembly.GetTypes()
                .Where(t => typeof(INodeExecutor).IsAssignableFrom(t) && t.GetCustomAttribute<NodeAttribute>() != null)
                .ToList();

            // 遍历每一个节点执行器类型
            foreach (Type type in types)
            {
                // 调用公共工厂方法，将节点元数据存入全局缓存，全项目共享
                NodeFactory.RegisterExecutorType(type);
                // 获取节点展示名称，输出注册成功日志
                NodeAttribute nodeAttr = type.GetCustomAttribute<NodeAttribute>();
                logAction?.Invoke($"🧩 成功注册节点算子: [{nodeAttr.DisplayName}] ({type.Name})");
            }
        }

        /// <summary>
        /// 解析插件dll内嵌的BAML编译资源，自动加载*Template.xaml / *TemplatePage.xaml模板
        /// 提取模板内DataTemplate全局注册，画布无需手动引入资源即可渲染节点UI
        /// C#7.3兼容语法，使用标准using代码块释放流
        /// </summary>
        /// <param name="assembly">插件程序集</param>
        /// <param name="logAction">日志回调</param>
        private static void RegisterXamlTemplates(Assembly assembly, Action<string> logAction)
        {
            try
            {
                // 程序集内嵌资源清单固定命名：程序集名.g.resources
                string manifestName = assembly.GetName().Name + ".g.resources";

                // 读取资源流，using自动释放流资源
                using (Stream stream = assembly.GetManifestResourceStream(manifestName))
                {
                    // 无内嵌资源直接退出
                    if (stream == null)
                        return;

                    // 资源读取器，遍历dll内所有内嵌资源
                    using (ResourceReader reader = new ResourceReader(stream))
                    {
                        // 遍历每一条内嵌资源记录
                        foreach (DictionaryEntry entry in reader)
                        {
                            // 资源内部路径（编译后后缀为baml）
                            string resourcePath = entry.Key.ToString();

                            // 只匹配节点模板文件
                            if (resourcePath.EndsWith("templatepage.baml") || resourcePath.EndsWith("template.baml"))
                            {
                                // 将编译后的baml后缀还原为原始xaml路径，用于构造WPF资源Uri
                                string xamlPath = resourcePath.Replace(".baml", ".xaml");
                                Uri uri = new Uri($"/{assembly.GetName().Name};component/{xamlPath}", UriKind.Relative);

                                try
                                {
                                    // 根据Uri加载XAML资源对象
                                    object loadedObj = Application.LoadComponent(uri);

                                    // 分支1：文件本身就是ResourceDictionary资源字典
                                    if (loadedObj is ResourceDictionary dict)
                                    {
                                        // 提取内部DataTemplate注册到全局应用资源
                                        RegisterResourceDictionary(dict);
                                        logAction?.Invoke($"🎨 自动注入资源字典: {xamlPath}");
                                    }
                                    // 分支2：文件是Page/UserControl页面，页面Resources内存放节点模板
                                    else if (loadedObj is FrameworkElement element && element.Resources != null)
                                    {
                                        // 提取页面内置资源字典注册
                                        RegisterResourceDictionary(element.Resources);
                                        logAction?.Invoke($"🧩 从 [{element.GetType().Name}] 成功注册 DataTemplate: {xamlPath}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // 单个模板加载失败仅打印日志，不中断其余模板扫描
                                    logAction?.Invoke($"⚠️ 注入 XAML 模板失败 [{xamlPath}]: {ex.Message}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 读取整个资源清单发生致命异常时统一捕获
                logAction?.Invoke($"⚠️ 扫描 BAML 资源异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 递归解析资源字典，提取所有DataTemplate，添加到应用全局资源
        /// 去重判断：已存在的模板不重复添加，避免资源冲突
        /// 递归处理字典内嵌套的MergedDictionaries合并资源
        /// </summary>
        /// <param name="dict">待解析的资源字典</param>
        private static void RegisterResourceDictionary(ResourceDictionary dict)
        {
            if (dict == null)
                return;

            // 遍历字典内所有资源项
            foreach (object key in dict.Keys)
            {
                // 只提取DataTemplate节点模板样式
                if (dict[key] is DataTemplate template)
                {
                    // 全局资源不存在才添加，防止重复注册覆盖
                    if (!Application.Current.Resources.Contains(key))
                    {
                        Application.Current.Resources.Add(key, template);
                    }
                }
            }

            // 递归处理当前字典内部嵌套的合并资源字典
            foreach (ResourceDictionary merged in dict.MergedDictionaries)
            {
                RegisterResourceDictionary(merged);
            }
        }
    }
}