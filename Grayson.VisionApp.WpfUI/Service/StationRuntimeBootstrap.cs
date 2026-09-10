//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationRuntimeBootstrap.cs
// 说 明: 程序启动全量同步服务。
//        · 背景：程序每次启动后 StationHostRuntime 内 _workers/_clients/_triggerSources
//          为空（Core 不做 DB 自动还原），工位监视页连接只会建「裸工位」（CreateEmbeddedStationAsync
//          ——不装配方/设备/业务过程/触发源）。此前必须到「工位工程工作台」点一次
//          「💾 保存并同步 Runtime」触发 CreateStationWithRecipeAsync 做完整装配，监视页才能启动。
//        · 本服务在登录进入主界面后异步执行：遍历数据库全部已启用工位，把每个工位按配置
//          完整装配到运行时（等价于逐个「保存并同步」的装配部分，但只读 DB、不落库），
//          使监视页/总览页直接可启动，无需再去工作台手动同步。
//        · 幂等：运行时已存在该工位 client（同会话已装配）则跳过；逐工位 try/catch 隔离，
//          单个工位装配失败不影响其余。
//===================================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Client;
using Grayson.Vision.Repository.Services;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 启动自动全量同步：把数据库已启用工位完整装配进 StationHostRuntime。
    /// </summary>
    public class StationRuntimeBootstrap
    {
        private readonly StationConfigService _configService;
        private readonly IRecipeStorageService _recipeStorage;
        private readonly StationRuntimeManager _runtimeManager;

        public StationRuntimeBootstrap(
            StationConfigService configService = null,
            IRecipeStorageService recipeStorage = null,
            StationRuntimeManager runtimeManager = null)
        {
            _configService = configService ?? new StationConfigService();
            _recipeStorage = recipeStorage ?? RecipeStorageFactory.CreateRecipeStorageService();
            _runtimeManager = runtimeManager ?? new StationRuntimeManager();
        }

        /// <summary>
        /// 同步期间标记（避免并发重复触发两次全量同步：登录回调 + 其他入口可能同时调用）。
        /// </summary>
        private static readonly object _syncLock = new object();
        private static bool _syncing;

        /// <summary>
        /// 已启用工位是否需要同步（runtime 中尚无该 client 即需要）。
        /// </summary>
        private bool NeedsSync(string stationCode)
            => _runtimeManager.GetClient(stationCode) == null;

        /// <summary>
        /// 后台启动一次全量同步。可重复调用，幂等（内部判定哪些工位尚未装配）。
        /// 不阻塞调用线程；全程异常兜底只记日志，绝不向 UI 抛。
        /// </summary>
        public void SyncAllEnabledStationsInBackground()
        {
            bool shouldRun;
            lock (_syncLock)
            {
                if (_syncing) return; // 已有一次在跑，跳过
                _syncing = true;
                shouldRun = true;
            }
            if (!shouldRun) return;

            // fire-and-forget，逐工位容错；结束后释放锁
            _ = Task.Run(async () =>
            {
                try
                {
                    await SyncAllEnabledStationsCoreAsync();
                }
                catch (Exception ex)
                {
                    LogBus.Error(nameof(StationRuntimeBootstrap), "启动全量同步发生未预期异常", ex);
                }
                finally
                {
                    lock (_syncLock) { _syncing = false; }
                }
            });
        }

        /// <summary>读取数据库全部已启用工位并逐个完整装配到运行时。</summary>
        public async Task SyncAllEnabledStationsCoreAsync()
        {
            List<LineConfigModel> lines;
            try
            {
                lines = _configService.LoadAllLines();
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(StationRuntimeBootstrap), "启动同步：读取产线工位配置失败（跳过启动同步）", ex);
                return;
            }

            var stations = (lines ?? new List<LineConfigModel>())
                .SelectMany(l => l.Stations ?? new List<StationConfigModel>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.StationCode))
                .Where(s => s.IsEnabled)
                .ToList();

            if (stations.Count == 0)
            {
                LogBus.Info(nameof(StationRuntimeBootstrap), "启动同步：数据库无已启用工位，无需装配。");
                return;
            }

            int ok = 0, skipped = 0, failed = 0;
            foreach (var cfg in stations)
            {
                var code = cfg.StationCode.Trim();
                try
                {
                    if (!NeedsSync(code))
                    {
                        skipped++;
                        continue;
                    }
                    if (await SyncOneStationAsync(cfg, code)) ok++;
                    else failed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    LogBus.Error(nameof(StationRuntimeBootstrap),
                        $"启动同步：工位 [{code}] 装配失败 - {ex.Message}", ex);
                }
            }

            LogBus.Info(nameof(StationRuntimeBootstrap),
                $"启动同步完成：装配成功 {ok} 个，跳过(已就绪) {skipped} 个，失败 {failed} 个。");
        }

        /// <summary>
        /// 按工位数据库配置完整装配单个工位到运行时（等价于工作台「保存并同步」的装配部分）。
        /// </summary>
        private async Task<bool> SyncOneStationAsync(StationConfigModel cfg, string code)
        {
            var hostRuntime = _runtimeManager.HostRuntime;
            if (hostRuntime == null) return false;

            // 加载绑定配方（装配逻辑与工作台一致：MainProcess 进 Worker；DeviceMappings 领用设备）
            RecipeModel boundRecipe = null;
            if (!string.IsNullOrWhiteSpace(cfg.BoundRecipeId))
            {
                try { boundRecipe = _recipeStorage.LoadRecipe(cfg.BoundRecipeId); }
                catch (Exception ex)
                {
                    LogBus.Warn(nameof(StationRuntimeBootstrap),
                        $"启动同步：工位 [{code}] 加载配方 [{cfg.BoundRecipeId}] 失败，将仅装配配方外配置 - {ex.Message}");
                }
            }

            var client = await hostRuntime.CreateStationWithRecipeAsync(
                code,
                boundRecipe,
                cfg.DeviceMappings,
                WorkMode.Production,
                cfg.TriggerSource,
                cfg.ProcessKey,
                cfg.ProcessConfigJson,
                cfg.TaskTemplateCode);

            if (client == null)
            {
                LogBus.Warn(nameof(StationRuntimeBootstrap),
                    $"启动同步：工位 [{code}] CreateStationWithRecipeAsync 返回空（装配未完成）。");
                return false;
            }

            LogBus.Info(nameof(StationRuntimeBootstrap),
                $"启动同步：工位 [{code}] ({cfg.StationName}) 已完整装配就绪" +
                (string.IsNullOrWhiteSpace(cfg.ProcessKey) ? "" : $"，业务过程 [{cfg.ProcessKey}]") +
                (string.IsNullOrWhiteSpace(cfg.TaskTemplateCode) ? "" : $"，任务模板 [{cfg.TaskTemplateCode}]"));
            return true;
        }
    }
}
