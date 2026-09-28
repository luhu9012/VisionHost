using System;
using System.Collections.Generic;
using System.IO;
using Grayson.Vision.Contracts.Calibration.Services;

namespace Grayson.Vision.Contracts.Calibration.Chain
{
    /// <summary>
    /// ★★范式2 链图【唯一装载入口】——生产端（VisionPickPlaceProcess）与 UI 侧（示教面板/校验台）
    /// 都从这里拿链图，杜绝「两处各自 TryLoad+Validate⇒两把尺」：
    ///
    ///   · 路径真源唯一：Recipes\Workstations\{工位码}\Calib\Chain.json（GetStationCalibDir）
    ///   · TryLoadValidated = TryLoad + ChainEngine.Validate(G0~G4)，任一失败 ⇒ ok=false＋人类可读原因
    ///   · LoadValidated   = 失败直接抛 ChainResolveException（生产端 fail-closed 语义）
    ///
    /// 消费入口解析（按 Edges.Usage 找吸点引导相机/下相机）也收口在这里——
    /// 链的形状=消费入口的宣告，读形状的代码不该散在多处。
    /// </summary>
    public static class ChainRuntime
    {
        /// <summary>链边 Usage：上相机引导吸点（向导产出，禁止手填）</summary>
        public const string PickAnchorUsage = "PickAnchor";
        /// <summary>链边 Usage：下相机相对纠偏</summary>
        public const string DownCorrectUsage = "DownCameraCorrect";

        /// <summary>链图落盘唯一路径：Recipes\Workstations\{工位码}\Calib\Chain.json</summary>
        public static string ChainPathFor(string stationCode)
        {
            return Path.Combine(CalibrationMatrixStore.GetStationCalibDir(stationCode), "Chain.json");
        }

        /// <summary>装载＋全量门禁。返回 false 时 error=「缺失原因 / 门禁失败清单」人类可读。</summary>
        public static bool TryLoadValidated(string stationCode, out StationCalibGraph graph, out string error)
        {
            graph = null;
            string path = ChainPathFor(stationCode);
            if (!StationCalibGraph.TryLoad(path, out graph, out string lerr))
            {
                error = "链图不可得：" + lerr
                      + "。处方：运行【标定中心 → 链向导】为工位 " + (stationCode ?? "(null)") + " 产出 Chain.json（" + path + "）。";
                return false;
            }
            List<string> errs = ChainEngine.Validate(graph, stationCode);
            if (errs.Count > 0)
            {
                error = "链图未过门禁：" + string.Join("；", errs) + "。处方：链校验台看逐项，缺什么回向导补采。";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>装载＋全量门禁，失败抛 ChainResolveException（生产端 fail-closed 唯一语义）。</summary>
        public static StationCalibGraph LoadValidated(string stationCode)
        {
            if (!TryLoadValidated(stationCode, out StationCalibGraph g, out string err))
                throw new ChainResolveException("范式2 拒绝继续（fail-closed）：" + err);
            return g;
        }

        /// <summary>按 Usage 找吸点引导相机（PickAnchor 边）。缺 ⇒ null（由调用方决定拦法）。</summary>
        public static ChainCameraNode FindPickCamera(StationCalibGraph graph)
        {
            return FindByUsage(graph, PickAnchorUsage);
        }

        /// <summary>按 Usage 找下相机（DownCameraCorrect 边）。无纠偏 ⇒ null（合法形态）。</summary>
        public static ChainCameraNode FindDownCamera(StationCalibGraph graph)
        {
            return FindByUsage(graph, DownCorrectUsage);
        }

        private static ChainCameraNode FindByUsage(StationCalibGraph graph, string usage)
        {
            if (graph == null) return null;
            foreach (var e in graph.Edges)
            {
                if (string.Equals(e.Usage, usage, StringComparison.OrdinalIgnoreCase))
                    return graph.FindCamera(e.FromCameraId);
            }
            return null;
        }
    }
}
