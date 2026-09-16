using System;
using System.Globalization;
using VisualCalibTool.Abstractions;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>三条链 runner 的公共收尾（会话建立、剔点、导出、发布）。</summary>
    internal static class ChainRunnerSupport
    {
        public static CalibSession NewSession(IVisualCalibEnvironment env, CalibTopology topo,
            CalibChainKind chain, string sessionId)
        {
            var s = new CalibSession
            {
                SessionId = string.IsNullOrEmpty(sessionId) ? DefaultSessionId(chain) : sessionId,
                Chain = chain,
                State = CalibRunState.Running,
                StartedUtc = DateTime.UtcNow,
                Topology = topo ?? new CalibTopology(),
                EnvironmentKind = env == null ? "Unknown" : env.Kind,
                StationCode = topo == null ? null : topo.StationCode,
                StationName = topo == null ? null : topo.StationName,
                CameraSlotKey = topo == null ? null : topo.CameraSlotKey
            };

            return s;
        }

        private static string DefaultSessionId(CalibChainKind chain)
        {
            return chain.ToString() + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>人工剔点（精确到点，不改变其它点的序号语义）。</summary>
        public static int ApplyExclusions(CalibSampleSet set, int[] indices)
        {
            if (set == null || indices == null || indices.Length == 0)
            {
                return 0;
            }

            int n = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                set.Exclude(indices[i]);
                n++;
            }

            return n;
        }

        public static void Finish(ChainRunContext ctx, bool success, CalibError error = null)
        {
            if (ctx == null || ctx.Session == null)
            {
                return;
            }

            ctx.Session.FinishedUtc = DateTime.UtcNow;
            ctx.Session.State = ctx.Result.Cancelled
                ? CalibRunState.Cancelled
                : (success ? CalibRunState.Succeeded : CalibRunState.Failed);
            ctx.Session.Error = error;
            ctx.Session.Diagnostics = ctx.Result.Diagnostics;
            ctx.Result.Success = success && !ctx.Result.Cancelled;
        }

        /// <summary>
        /// 第 8 步：导出中性产物 + 宿主回调发布。
        /// ★ 两件事的次序是固定的：<b>先落文件再发布</b> ——
        ///   发布失败（宿主异常/库写不进去）时，文件仍然在，现场至少还有可交付的物证。
        /// </summary>
        public static void ExportAndPublish(ChainRunContext ctx, ChainRunOptions options,
            IVisualCalibEnvironment env, CalibExport export)
        {
            if (export == null)
            {
                return;
            }

            ctx.Result.Export = export;

            if (options != null && options.ExportFiles && env != null && env.Store != null)
            {
                try
                {
                    string json = CalibExportJson.Write(export, true);
                    HomMat2D? h = export.HasH ? export.H : (HomMat2D?)null;
                    ctx.Result.ExportDir = env.Store.SaveExport(
                        ctx.Session == null ? export.SourceSessionId : ctx.Session.SessionId, json, h);
                    ctx.Log("产物已导出：" + ctx.Result.ExportDir);
                }
                catch (Exception ex)
                {
                    ctx.Result.Add(ChainIssue.Warn("EXPORT_FAILED",
                        "产物落文件失败：" + ex.Message, ServiceStage.Export));
                    ctx.Error("导出失败", ex);
                }
            }

            bool wantPublish = options == null || options.PublishToHost;
            if (!wantPublish)
            {
                return;
            }

            if (env == null || env.Publisher == null)
            {
                // ★ null 不是错误 —— 独立模式就是没有宿主，只导文件。
                ctx.Result.PublishMessage = "无宿主发布者（独立模式）：产物只落文件，未写入任何外部库。";
                ctx.Log(ctx.Result.PublishMessage);
                return;
            }

            string msg;
            try
            {
                bool ok = env.Publisher.Publish(export, out msg);
                ctx.Result.Published = ok;
                ctx.Result.PublishMessage = ok
                    ? "已由「" + env.Publisher.Name + "」发布"
                    : "发布失败：" + msg;
                if (!ok)
                {
                    ctx.Result.Add(ChainIssue.Warn("PUBLISH_FAILED",
                        "宿主回调发布失败：" + msg, ServiceStage.Export));
                }
            }
            catch (Exception ex)
            {
                ctx.Result.Published = false;
                ctx.Result.PublishMessage = "发布抛出异常：" + ex.Message;
                ctx.Result.Add(ChainIssue.Warn("PUBLISH_FAILED",
                    ctx.Result.PublishMessage, ServiceStage.Export));
            }

            ctx.Log(ctx.Result.PublishMessage);
        }

        public static string HandednessText(Handedness h)
        {
            switch (h)
            {
                case Handedness.Lefty: return "Lefty";
                case Handedness.Righty: return "Righty";
                default: return "Unknown";
            }
        }

        public static string MountText(CameraMountKind m)
        {
            return m.ToString();
        }
    }
}
