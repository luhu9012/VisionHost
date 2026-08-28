using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Station.Processes;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>
    /// 业务过程工厂（静态注册表模式，仿 InferenceProviderRegistry）。
    ///
    /// 键（ProcessKey）→ 构造工厂。StationHostRuntime 创建工位时读取
    /// StationConfigModel.ProcessKey + ProcessConfigJson，经此工厂实例化
    /// 业务过程并 AttachProcess 到 StationWorker。
    ///
    /// 新增业务过程三步：
    ///   1. Core/Processes 下新建 Process 类（继承 StationProcessBase）+ Config 类；
    ///   2. 在构造函数中 Register("键", (json, worker) => new XxxProcess(worker, 反序列化(json)));
    ///   3. WpfUI 工位管理"业务过程"下拉即自动出现（数据源 = GetSupportedKeys()）。
    /// </summary>
    public static class StationProcessFactory
    {
        private static readonly Dictionary<string, Func<string, StationWorker, IStationProcess>> _registry =
            new Dictionary<string, Func<string, StationWorker, IStationProcess>>(StringComparer.OrdinalIgnoreCase);

        static StationProcessFactory()
        {
            Register("MahjongPick", (json, worker) =>
                new MahjongPickProcess(worker, Deserialize(json, new MahjongPickConfig())));

            Register("MahjongDualNozzle", (json, worker) =>
                new MahjongDualNozzleProcess(worker, Deserialize(json, new MahjongDualNozzleConfig())));
        }

        private static void Register(string key, Func<string, StationWorker, IStationProcess> factory)
        {
            if (!_registry.ContainsKey(key))
            {
                _registry[key] = factory;
            }
        }

        /// <summary>
        /// 反序列化过程参数 JSON；空/非法时回退默认参数（保证工位能启动并给出可诊断日志）。
        /// </summary>
        private static T Deserialize<T>(string json, T fallback) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                return JsonConvert.DeserializeObject<T>(json) ?? fallback;
            }
            catch (Exception ex)
            {
                LogBus.Warn("StationProcessFactory", $"过程配置 JSON 反序列化失败，使用默认参数: {ex.Message}");
                return fallback;
            }
        }

        /// <summary>
        /// 按键创建业务过程实例。未知键/构造异常返回 null（上层仅告警不中断工位创建）。
        /// </summary>
        public static IStationProcess Create(string processKey, string configJson, StationWorker worker)
        {
            if (string.IsNullOrWhiteSpace(processKey) || worker == null) return null;

            if (!_registry.TryGetValue(processKey.Trim(), out var factory))
            {
                LogBus.Error("StationProcessFactory",
                    $"工位 [{worker.StationId}] 请求的业务过程 [{processKey}] 未注册，请检查拼写或到 Core.Processes.StationProcessFactory 注册。");
                return null;
            }

            try
            {
                return factory(configJson, worker);
            }
            catch (Exception ex)
            {
                LogBus.Error("StationProcessFactory",
                    $"工位 [{worker.StationId}] 实例化业务过程 [{processKey}] 失败: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>是否支持指定过程键（忽略大小写）。</summary>
        public static bool IsSupported(string processKey)
        {
            return !string.IsNullOrWhiteSpace(processKey) && _registry.ContainsKey(processKey.Trim());
        }

        /// <summary>已注册的全部过程键（UI 下拉数据源）。</summary>
        public static IEnumerable<string> GetSupportedKeys()
        {
            return _registry.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
