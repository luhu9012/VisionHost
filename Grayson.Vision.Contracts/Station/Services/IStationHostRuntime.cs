using Grayson.Vision.Contracts.Devices.Services;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Enums;
using Grayson.Vision.Contracts.Station.Interfaces;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Triggers;
using Grayson.Vision.Contracts.Station.WorkOrderTracking;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Services
{
    /// <summary>
    /// StationHost 全局运行时契约。
    /// 统一持有设备池与所有工位 Worker 实例，是同进程内多线程运行的核心入口。
    /// </summary>
    public interface IStationHostRuntime : IDisposable
    {
        /// <summary>
        /// 安全联锁服务（允许为空，未注入时不启用联锁）。
        /// </summary>
        ISafetyInterlockService SafetyInterlock { get; }

        /// <summary>
        /// 注册安全联锁服务。
        /// </summary>
        void RegisterSafetyInterlock(ISafetyInterlockService safetyInterlockService);

        /// <summary>
        /// 全局设备池（插件 + 已注册设备实例）。
        /// </summary>
        IDevicePool DevicePool { get; }

        /// <summary>
        /// 初始化：加载插件、恢复设备池、初始化内部运行时。
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 创建或获取嵌入式工位 Worker 客户端。
        /// </summary>
        Task<IWorkerClient> CreateEmbeddedStationAsync(string stationId);

        /// <summary>
        /// 创建工位并加载配方、绑定设备映射、装配触发源（UI 配置后的推荐入口）。
        /// 
        /// 使用统一的 RecipeDeviceMappingModel 模型。
        /// </summary>
        Task<IWorkerClient> CreateStationWithRecipeAsync(
            string stationId,
            RecipeModel recipe,
            IEnumerable<Recipe.Models.RecipeDeviceMappingModel> deviceMappings = null,
            WorkMode mode = WorkMode.Production,
            TriggerSourceConfig triggerSourceConfig = null);

        /// <summary>
        /// 获取已存在的工位客户端。
        /// </summary>
        IWorkerClient GetStationClient(string stationId);

        /// <summary>
        /// 外部触发指定工位执行一次（单帧/单物料）。
        /// 生产模式下由 PLC/IO 输入、MES 或 UI 手动触发信号的统一入口。
        /// </summary>
        Task<bool> TriggerStationAsync(string stationId, string batchId = null);

        /// <summary>
        /// 关闭并释放指定工位。
        /// </summary>
        bool RemoveStation(string stationId);

        /// <summary>
        /// 已注册的所有工位 StationId。
        /// </summary>
        IEnumerable<string> GetStationIds();

        /// <summary>
        /// 获取指定工位的工单追踪器；若工位不存在则返回 null。
        /// </summary>
        IWorkOrderTracker GetWorkOrderTracker(string stationId);

        // ===== 触发源管理 =====

        /// <summary>
        /// 获取指定工位的触发源实例（供 UI 读取节拍统计 / 手动触发测试）。无则返回 null。
        /// </summary>
        ITriggerSource GetTriggerSource(string stationId);

        /// <summary>
        /// 为工位配置并装配触发源（创建后、启动前调用）。配置为 null 时默认 Manual。
        /// </summary>
        void SetupTriggerSource(string stationId, TriggerSourceConfig config);

        /// <summary>
        /// 启动指定工位的触发源（工位 StartAsync 后调用）。
        /// </summary>
        void StartTriggerSource(string stationId);

        /// <summary>
        /// 停止指定工位的触发源（工位 StopAsync 后调用）。
        /// </summary>
        void StopTriggerSource(string stationId);

        /// <summary>
        /// 手动触发指定工位——Manual 源的正常路径 / 其他源的"测试触发"。
        /// </summary>
        void ManualTrigger(string stationId);
    }
}
