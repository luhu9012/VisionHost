//===================================================================================
// 文件名: StationStatisticsService.cs
// 说 明: 工位生产统计持久化服务（JSON 按工位键控 + 水位法防重复计数）
//===================================================================================
using Newtonsoft.Json;
using System;
using System.IO;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>工位生产统计持久化数据（跨会话累加）。</summary>
    public class StationStatsData
    {
        public string StationCode { get; set; }

        /// <summary>累计工单总数（离开界面/重启程序不归零）</summary>
        public long TotalCount { get; set; }

        /// <summary>累计 OK 数</summary>
        public long OkCount { get; set; }

        /// <summary>累计 NG 数</summary>
        public long NgCount { get; set; }

        /// <summary>最近一次周期耗时 ms（-1 = 尚无记录）</summary>
        public long LastCycleTimeMs { get; set; } = -1;

        /// <summary>最近一次结果：0=未知 1=OK -1=NG</summary>
        public int LastWorkOrderResult { get; set; }

        /// <summary>水位线：已计入累计的 Worker Metrics 读数（防重复计数）</summary>
        public long WatermarkTotal { get; set; }

        /// <summary>水位线：已计入累计的 OK 读数</summary>
        public long WatermarkOk { get; set; }

        /// <summary>水位线：已计入累计的 NG 读数</summary>
        public long WatermarkNg { get; set; }

        /// <summary>最近一次落盘时间</summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 工位生产统计持久化服务。
    ///
    /// 存储：{exe目录}\Data\StationStats\{工位编码}.stats.json（与 LiteDB 数据同目录层级）。
    ///
    /// 水位法防重复计数：文件同时保存「累计计数」与「已消费的 Worker Metrics 水位线」，
    /// 每次轮询只把 Worker 当前读数超出水位线的增量累进文件——
    ///   - 程序重启后 Worker 计数从 0 开始（读数 &lt; 水位线），不产生任何累计；
    ///   - 文件被删除即视为「清除统计」，下次轮询以当前 Worker 读数为新基线重建水位线，
    ///     已跑过的周期不会重新计入。
    /// </summary>
    public class StationStatisticsService
    {
        private static readonly object _ioLock = new object();

        private static string StatsDir =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "StationStats");

        private static string PathOf(string stationCode)
        {
            var safe = string.Join("_",
                (stationCode ?? "unknown").Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return Path.Combine(StatsDir, safe + ".stats.json");
        }

        /// <summary>读取工位统计；文件不存在（含被清除）返回 null。</summary>
        public StationStatsData Load(string stationCode)
        {
            if (string.IsNullOrEmpty(stationCode)) return null;
            try
            {
                lock (_ioLock)
                {
                    var file = PathOf(stationCode);
                    if (!File.Exists(file)) return null;
                    return JsonConvert.DeserializeObject<StationStatsData>(File.ReadAllText(file));
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存工位统计（落盘失败静默——不影响生产，下次轮询重试）。</summary>
        public void Save(StationStatsData data)
        {
            if (data == null || string.IsNullOrEmpty(data.StationCode)) return;
            try
            {
                lock (_ioLock)
                {
                    Directory.CreateDirectory(StatsDir);
                    data.UpdatedAt = DateTime.Now;
                    File.WriteAllText(PathOf(data.StationCode),
                        JsonConvert.SerializeObject(data, Formatting.Indented));
                }
            }
            catch
            {
                // 统计落盘失败不影响运行
            }
        }

        /// <summary>
        /// 纯视觉链工位（未挂业务过程）：链完成事件直接累计一次结果。
        /// 业务过程工位不走此方法（由轮询按 Worker Metrics 水位累计）。
        /// </summary>
        public void RecordChainCompleted(string stationCode, bool isOk, long cycleTimeMs)
        {
            lock (_ioLock)
            {
                var data = Load(stationCode) ?? new StationStatsData { StationCode = stationCode };
                data.TotalCount++;
                if (isOk) data.OkCount++; else data.NgCount++;
                data.LastCycleTimeMs = cycleTimeMs;
                data.LastWorkOrderResult = isOk ? 1 : -1;
                Save(data);
            }
        }

        /// <summary>清除全部工位统计数据（删除文件，返回清除的工位数）。运行中的监视页会在下次轮询时以当前计数为新基线。</summary>
        public int ClearAll()
        {
            try
            {
                lock (_ioLock)
                {
                    if (!Directory.Exists(StatsDir)) return 0;
                    var files = Directory.GetFiles(StatsDir, "*.stats.json");
                    foreach (var f in files)
                    {
                        try { File.Delete(f); } catch { }
                    }
                    return files.Length;
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
