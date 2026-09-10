using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using System;
using System.Threading;

namespace Grayson.Vision.Core.Station
{
    /// <summary>
    /// 工位运行记录器：把每次最终判定(OK/NG)的业务周期落一条持久化检测记录，
    /// 供【本地追溯与防错】页面实时/历史查询——当前全仓没有向 inspection_logs 写入的
    /// 生产者，导致追溯页永远查不到运行数据(2026-09-09 打通 #4)。
    ///
    /// 安全设计：写入为 fire-and-forget 且整体 try/catch，任何持久化失败绝不影响业务执行链；
    /// 所有跨工位写库用静态锁串行化(LiteDB Shared 连接 + 本锁，杜绝并发写库竞态)。
    /// 错误码语义：NG 且带错误 → 视为「放错/异常放料」类别(视觉NG/未吸住/错放)，
    /// 追溯页据此分类展示。正常 NG(业务判 NG 但无设备/放错含义)保留 NG 但不标放错。
    /// </summary>
    public static class StationRunRecorder
    {
        private static readonly object _sync = new object();
        private static IInspectionLogRepository _repo;

        private static IInspectionLogRepository Repo
            => _repo ?? (_repo = StorageFactory.CreateInspectionLogRepository());

        /// <summary>
        /// 记录一次已结束的业务/视觉周期。全参可选；失败静默，不抛出。
        /// </summary>
        public static void Record(
            string stationId,
            bool isOk,
            double cycleTimeMs,
            string recipeName = null,
            string batchId = null,
            string errorCode = null,
            string errorMessage = null,
            string imagePath = null,
            DateTime? inspectTime = null)
        {
            if (string.IsNullOrWhiteSpace(stationId)) return;
            try
            {
                var po = new InspectionLogPo
                {
                    StationId = stationId,
                    IsOk = isOk,
                    CycleTimeMs = Math.Max(0, cycleTimeMs),
                    RecipeName = string.IsNullOrWhiteSpace(recipeName) ? "—" : recipeName,
                    BatchId = string.IsNullOrWhiteSpace(batchId) ? Guid.NewGuid().ToString("N").Substring(0, 8) : batchId,
                    ErrorCode = string.IsNullOrWhiteSpace(errorCode)
                        ? (isOk ? null : "NG")
                        : errorCode,
                    ErrorMessage = errorMessage,
                    ImagePath = imagePath,
                    InspectTime = inspectTime ?? DateTime.Now
                };

                lock (_sync)
                {
                    Repo.Insert(po);
                }
            }
            catch
            {
                // 追溯落库失败不影响生产运行(不抛出、不打搅)
            }
        }
    }
}
