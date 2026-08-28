using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Grayson.Vision.Contracts.Ai
{
    /// <summary>
    /// AI 推理引擎注册表（静态服务定位器，模式对齐 Infrastructure.Logging.LogBus——
    /// 平台各层没有统一 DI 容器，Contracts 用静态总线承载全局单例服务）。
    ///
    /// 【加载机制】首次通过 EnsureDefaultLoaded() 或 Get() 访问时，
    /// 自动扫描程序运行目录下所有 Plugins.Inference.*.dll（命名约定，与硬件插件
    /// "程序集名含 Plugin" 的扫描约定一致），反射实例化其中的 IInferenceProvider 并注册。
    ///
    /// 【为什么不在启动时显式注册】该平台有多个宿主（FlowEdit 嵌入式 / WorkerHost 独立进程），
    /// 静态注册表 + 惰性扫描保证任何宿主、任何线程第一次用 AI 推理时都能自动就绪，
    /// 无需每个宿主手写初始化代码（忘了初始化是最常见的坑）。
    ///
    /// 【线程安全】Registry 内部加锁；Load/Run 的线程安全由 Provider 实现负责。
    /// </summary>
    public static class InferenceProviderRegistry
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, IInferenceProvider> _providers =
            new Dictionary<string, IInferenceProvider>(StringComparer.OrdinalIgnoreCase);

        private static bool _autoScanDone;

        /// <summary>插件程序集命名前缀约定：Plugins.Inference.OnnxRuntime.dll 等</summary>
        private const string PluginAssemblyPrefix = "Plugins.Inference.";

        /// <summary>已注册的全部引擎（快照副本）</summary>
        public static IReadOnlyList<IInferenceProvider> Registered
        {
            get
            {
                lock (_lock) { return _providers.Values.ToList(); }
            }
        }

        /// <summary>
        /// 手动注册一个引擎实例（供单元测试 / 自定义宿主显式注入用；
        /// 正常运行走 EnsureDefaultLoaded 的自动扫描，不需要调用本方法）。
        /// </summary>
        public static void Register(IInferenceProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));

            lock (_lock)
            {
                _providers[provider.ProviderId] = provider;
            }
        }

        /// <summary>
        /// 按引擎标识取实例（如 "OnnxRuntime"）。找不到返回 null，不抛异常。
        /// </summary>
        public static IInferenceProvider Resolve(string providerId)
        {
            if (string.IsNullOrEmpty(providerId)) return null;

            lock (_lock)
            {
                IInferenceProvider p;
                return _providers.TryGetValue(providerId, out p) ? p : null;
            }
        }

        /// <summary>
        /// 取默认引擎：优先已注册的第一个；没有则触发一次自动扫描。
        /// 【约定】DlInference 节点当前使用默认引擎；将来参数面板加"引擎选择"
        /// 下拉框时，改为 Resolve(param.ProviderId) 即可。
        /// </summary>
        public static IInferenceProvider GetDefault()
        {
            lock (_lock)
            {
                if (_providers.Count > 0)
                    return _providers.Values.First();
            }

            EnsureDefaultLoaded();

            lock (_lock)
            {
                return _providers.Count > 0 ? _providers.Values.First() : null;
            }
        }

        /// <summary>
        /// 扫描并加载 Plugins.Inference.*.dll（只执行一次，幂等）。
        /// 扫描失败不会抛异常——AI 是可选能力，不能让平台因缺插件起不来。
        /// </summary>
        public static void EnsureDefaultLoaded()
        {
            lock (_lock)
            {
                if (_autoScanDone) return;
                _autoScanDone = true;
            }

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var files = Directory.Exists(baseDir)
                    ? Directory.GetFiles(baseDir, PluginAssemblyPrefix + "*.dll")
                    : new string[0];

                foreach (var file in files)
                {
                    TryLoadAssembly(file);
                }
            }
            catch
            {
                // 扫描目录失败（权限/路径异常）静默忽略：节点执行时会得到
                // "未找到推理引擎" 的明确报错，比启动崩掉更友好。
            }
        }

        private static void TryLoadAssembly(string file)
        {
            try
            {
                var assembly = Assembly.LoadFrom(file);
                var types = assembly.GetTypes()
                    .Where(t => typeof(IInferenceProvider).IsAssignableFrom(t)
                                && !t.IsInterface && !t.IsAbstract);

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is IInferenceProvider provider)
                        {
                            Register(provider);
                        }
                    }
                    catch
                    {
                        // 单个类型实例化失败不影响其余引擎注册
                    }
                }
            }
            catch
            {
                // 依赖缺失（如未装 onnxruntime 原生 dll）导致整个程序集加载失败：
                // 静默跳过，用户在节点执行时会看到明确提示。
                // 【TODO】此处可接入 LogBus 输出告警日志，帮助现场排查缺哪些依赖
            }
        }
    }
}
