// Grayson.Vision.WpfUI/Service/StationMonitorExtensionRegistry.cs
// 引擎「参数/示教面板」公共机制（注册表命名沿用 Monitor，宿主已不限于监视页）。
// 背景：业务引擎（VisionPickPlace / MahjongPick…）的参数表单是自包含业务控件。
// 2026-09-06 职责收敛后：宿主 = 「工位工程工作台 → ④ 执行方案」Tab（StationManageViewModel 选中工位
// 时按 ProcessKey 创建面板并 Bind）。注册表保持通用契约，新业务只需：
//   1) 实现 IStationMonitorExtension（独立 UserControl + 独立 VM）；
//   2) 在 App 启动时 StationMonitorExtensionRegistry.Register("ProcessKey", () => new XxxPanel());
using System;
using System.Collections.Generic;
using System.Windows;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 引擎参数/示教面板契约（宿主 = StationManageViewModel「④ 执行方案」Tab）。
    /// 宿主按工位 ProcessKey 通过注册表创建面板 → 订阅 Log → Bind(stationCode) 挂载。
    /// 切换工位/卸载时宿主调用 Dispose（实现方负责退订 worker 事件等资源）。
    /// </summary>
    public interface IStationMonitorExtension : IDisposable
    {
        /// <summary>面板日志输出（宿主转发到工位工作台 ④Tab 就地文本）；level ∈ INFO/WARN/ERROR。</summary>
        event Action<string, string> Log;

        /// <summary>绑定到指定工位（切换工位时宿主调用；实现方可在此订阅 worker 节点事件、加载配置）。</summary>
        void Bind(string stationCode);
    }

    /// <summary>
    /// 业务扩展面板注册表：ProcessKey → 面板工厂。
    /// 工作台 ④ 执行方案 Tab 零业务耦合：只按工位 ProcessKey 查注册表创建面板，
    /// 查不到（该引擎无参数面板）则不显示，不影响公共功能。
    /// </summary>
    public static class StationMonitorExtensionRegistry
    {
        private static readonly Dictionary<string, Func<FrameworkElement>> Factories
            = new Dictionary<string, Func<FrameworkElement>>(StringComparer.OrdinalIgnoreCase);

        public static void Register(string processKey, Func<FrameworkElement> factory)
        {
            if (string.IsNullOrWhiteSpace(processKey)) throw new ArgumentNullException(nameof(processKey));
            Factories[processKey.Trim()] = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        /// <summary>按过程键创建面板实例；无注册返回 null。</summary>
        public static FrameworkElement Create(string processKey)
        {
            if (string.IsNullOrWhiteSpace(processKey)) return null;
            return Factories.TryGetValue(processKey.Trim(), out var factory) ? factory() : null;
        }

        /// <summary>是否已为该过程键注册扩展面板（无副作用探测，不构造实例）。</summary>
        public static bool IsRegistered(string processKey)
        {
            if (string.IsNullOrWhiteSpace(processKey)) return false;
            return Factories.ContainsKey(processKey.Trim());
        }
    }
}
