using System;
using System.Globalization;
using System.Text;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Services
{
    /// <summary>
    /// 中性导出契约的 JSON 序列化（工具唯一对外持久化格式）。
    ///
    /// ★ 手写而不是引第三方库，有两个实在的理由：
    ///   ① 依赖铁律 —— 这个工具只允许依赖 halcondotnet + Contracts，引 Json.NET 等于给
    ///      "嵌入主项目"埋一个版本冲突源；
    ///   ② 数值口径必须自己说了算 —— 一律 <c>"R"</c>（往返格式）。历史上 .tup 就是因为
    ///      自定义浮点格式只有 15 位有效数字而写歪过，这里不再重犯。
    ///
    /// ★ 字段只增不删：将来无论选"仅诊断 / H 改口径 / 生产端补偿"哪条路，
    ///   都只改消费端读哪些字段，不改格式。
    /// </summary>
    public static class CalibExportJson
    {
        public static string Write(CalibExport e, bool indented = true)
        {
            if (e == null)
            {
                return "{}";
            }

            var sb = new StringBuilder(2048);
            sb.Append('{');
            string nl = indented ? "\n" : string.Empty;
            string pad = indented ? "  " : string.Empty;

            sb.Append(nl).Append(pad).Append("\"formatVersion\": ").Append(Str(e.FormatVersion));
            sb.Append(',').Append(nl).Append(pad).Append("\"sourceSessionId\": ").Append(Str(e.SourceSessionId));
            sb.Append(',').Append(nl).Append(pad).Append("\"chain\": ").Append(Str(e.Chain.ToString()));
            sb.Append(',').Append(nl).Append(pad).Append("\"stationCode\": ").Append(Str(e.StationCode));
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraSlotKey\": ").Append(Str(e.CameraSlotKey));

            // ── 几何量 ──
            sb.Append(',').Append(nl).Append(pad).Append("\"hasH\": ").Append(Bool(e.HasH));
            sb.Append(',').Append(nl).Append(pad).Append("\"H\": ");
            if (e.HasH)
            {
                sb.Append(Arr(e.H.ToArray()));
            }
            else
            {
                sb.Append("null");
            }

            sb.Append(',').Append(nl).Append(pad).Append("\"hasRotCenter\": ").Append(Bool(e.HasRotCenter));
            sb.Append(',').Append(nl).Append(pad).Append("\"rotCenter\": ").Append(Vec(e.RotCenterWorld, e.HasRotCenter));

            sb.Append(',').Append(nl).Append(pad).Append("\"hasEcc\": ").Append(Bool(e.HasEcc));
            sb.Append(',').Append(nl).Append(pad).Append("\"ecc\": ").Append(Vec(e.Ecc, e.HasEcc));

            sb.Append(',').Append(nl).Append(pad).Append("\"hasTipWorld\": ").Append(Bool(e.HasTipWorld));
            sb.Append(',').Append(nl).Append(pad).Append("\"tipWorld\": ").Append(Vec(e.TipWorld, e.HasTipWorld));

            sb.Append(',').Append(nl).Append(pad).Append("\"refU0\": ").Append(Num(e.RefU0));
            sb.Append(',').Append(nl).Append(pad).Append("\"handedness\": ").Append(Str(e.Handedness));
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraMount\": ").Append(Str(e.CameraMount));

            // ── 内参 / 畸变 ──
            sb.Append(',').Append(nl).Append(pad).Append("\"focalLengthPx\": ").Append(ArrOrNull(e.FocalLengthPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"principalPointPx\": ").Append(ArrOrNull(e.PrincipalPointPx));

            // ★ 米制口径的焦距与像元：消费端拼 HALCON campar 用的就是这两个，
            //   光有像素焦距是喂不进 area_scan_division 的。
            sb.Append(',').Append(nl).Append(pad).Append("\"focalLengthM\": ").Append(Num(e.FocalLengthM));
            sb.Append(',').Append(nl).Append(pad).Append("\"pixelPitchM\": ").Append(Num(e.PixelPitchM));

            // ★ distortion 是 HALCON 除法模型口径（1/m²），distortionNormalizedK1 是同一份畸变的
            //   归一化口径 —— 两个字段都写出来，免得下游按错口径用（差一个 f²，不会报错，只会算歪）。
            sb.Append(',').Append(nl).Append(pad).Append("\"distortion\": ").Append(ArrOrNull(e.Distortion));
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionConvention\": ")
                .Append(Str("halcon_area_scan_division_kappa_1_per_m2"));
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionNormalizedK1\": ")
                .Append(Num(e.DistortionNormalizedK1));

            sb.Append(',').Append(nl).Append(pad).Append("\"imageSize\": ").Append(IntArrOrNull(e.ImageSize));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionErrorPx\": ").Append(Num(e.ReprojectionErrorPx));

            sb.Append(',').Append(nl).Append(pad).Append("\"board\": ")
                .Append(e.Board.HasValue ? Str(e.Board.Value.ToString()) : "null");
            sb.Append(',').Append(nl).Append(pad).Append("\"boardDescription\": ").Append(Str(e.BoardDescription));

            // ── 可复现信息 ──
            sb.Append(',').Append(nl).Append(pad).Append("\"producedBy\": ").Append(Str(e.ProducedBy));
            sb.Append(',').Append(nl).Append(pad).Append("\"producedUtc\": ")
                .Append(Str(e.ProducedUtc.ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(',').Append(nl).Append(pad).Append("\"framesDir\": ").Append(Str(e.FramesDir));

            sb.Append(',').Append(nl).Append(pad).Append("\"diagnostics\": ")
                .Append(WriteDiagnostics(e.Diagnostics, indented));

            sb.Append(nl).Append('}');
            return sb.ToString();
        }

        private static string WriteDiagnostics(CalibDiagnostics d, bool indented)
        {
            if (d == null)
            {
                return "null";
            }

            var sb = new StringBuilder(1024);
            string nl = indented ? "\n" : string.Empty;
            string pad = indented ? "    " : string.Empty;

            sb.Append('{');
            sb.Append(nl).Append(pad).Append("\"columnNormFirst\": ").Append(Num(d.ColumnNormFirst));
            sb.Append(',').Append(nl).Append(pad).Append("\"columnNormSecond\": ").Append(Num(d.ColumnNormSecond));
            sb.Append(',').Append(nl).Append(pad).Append("\"axisScaleDeviationPct\": ").Append(Num(d.AxisScaleDeviationPct));
            sb.Append(',').Append(nl).Append(pad).Append("\"sigma1\": ").Append(Num(d.Sigma1));
            sb.Append(',').Append(nl).Append(pad).Append("\"sigma2\": ").Append(Num(d.Sigma2));
            sb.Append(',').Append(nl).Append(pad).Append("\"sigmaRatio\": ").Append(Num(d.SigmaRatio));
            sb.Append(',').Append(nl).Append(pad).Append("\"anisotropyPct\": ").Append(Num(d.AnisotropyPct));
            sb.Append(',').Append(nl).Append(pad).Append("\"badShape\": ").Append(Bool(d.BadShape));
            sb.Append(',').Append(nl).Append(pad).Append("\"columnAngleDeg\": ").Append(Num(d.ColumnAngleDeg));
            sb.Append(',').Append(nl).Append(pad).Append("\"shearRatio\": ").Append(Num(d.ShearRatio));
            sb.Append(',').Append(nl).Append(pad).Append("\"shearSuspicious\": ").Append(Bool(d.ShearSuspicious));
            sb.Append(',').Append(nl).Append(pad).Append("\"detA\": ").Append(Num(d.DetA));
            sb.Append(',').Append(nl).Append(pad).Append("\"mirrorDetected\": ").Append(Bool(d.MirrorDetected));
            sb.Append(',').Append(nl).Append(pad).Append("\"mirrorIsBlocker\": ").Append(Bool(d.MirrorIsBlocker));
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraMount\": ").Append(Str(d.CameraMount.ToString()));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridRowEdgeMeanMm\": ").Append(Num(d.GridRowEdgeMeanMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridRowEdgeCvPct\": ").Append(Num(d.GridRowEdgeCvPct));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridColEdgeMeanMm\": ").Append(Num(d.GridColEdgeMeanMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridColEdgeCvPct\": ").Append(Num(d.GridColEdgeCvPct));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridIrregular\": ").Append(Bool(d.GridIrregular));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridAngleDeg\": ").Append(Num(d.GridAngleDeg));
            sb.Append(',').Append(nl).Append(pad).Append("\"gridAngleSuspicious\": ").Append(Bool(d.GridAngleSuspicious));
            sb.Append(',').Append(nl).Append(pad).Append("\"rmsMm\": ").Append(Num(d.RmsMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"looRmsMm\": ").Append(Num(d.LooRmsMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"perPointResidualMm\": ").Append(ArrOrNull(d.PerPointResidualMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"perPointRadiusMm\": ").Append(ArrOrNull(d.PerPointRadiusMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"excludedCount\": ").Append(d.ExcludedCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(',').Append(nl).Append(pad).Append("\"nearestCornerRadiusMm\": ").Append(Num(d.NearestCornerRadiusMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionRmsPx\": ").Append(Num(d.ReprojectionRmsPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionMaxPx\": ").Append(Num(d.ReprojectionMaxPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionRmsMm\": ").Append(Num(d.ReprojectionRmsMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionWithinTolerance\": ").Append(Bool(d.ReprojectionWithinTolerance));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionBiasPx\": ").Append(Num(d.ReprojectionBiasPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionRadialTrendMmPerMm\": ").Append(Num(d.ReprojectionRadialTrendMmPerMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"innerMarginMm\": ").Append(Num(d.InnerMarginMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"outerMarginMm\": ").Append(Num(d.OuterMarginMm));

            // ★★ 决策 3 的核心证据：畸变在工作视野内到底造成多少毫米偏差。
            //   没测出来也要写（measured=false + reason），否则下游会以为是"没有影响"。
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionImpact\": ")
                .Append(WriteDistortionImpact(d.DistortionImpact, indented));

            sb.Append(',').Append(nl).Append(pad).Append("\"notes\": ").Append(StrArr(d.Notes));
            sb.Append(nl).Append(indented ? "  " : string.Empty).Append('}');
            return sb.ToString();
        }

        private static string WriteDistortionImpact(DistortionImpactAssessment a, bool indented)
        {
            if (a == null)
            {
                return "null";
            }

            var sb = new StringBuilder(1024);
            string nl = indented ? "\n" : string.Empty;
            string pad = indented ? "      " : string.Empty;

            sb.Append('{');
            sb.Append(nl).Append(pad).Append("\"measured\": ").Append(Bool(a.Measured));
            sb.Append(',').Append(nl).Append(pad).Append("\"source\": ").Append(Str(a.Source));
            if (!string.IsNullOrEmpty(a.Reason))
            {
                sb.Append(',').Append(nl).Append(pad).Append("\"reason\": ").Append(Str(a.Reason));
            }

            sb.Append(',').Append(nl).Append(pad).Append("\"scaleMmPerPx\": ").Append(Num(a.ScaleMmPerPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"workRadiusMm\": ").Append(Num(a.WorkRadiusMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"worstRadiusPx\": ").Append(Num(a.WorstRadiusPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"maxShiftPx\": ").Append(Num(a.MaxShiftPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"maxShiftMm\": ").Append(Num(a.MaxShiftMm));

            sb.Append(',').Append(nl).Append(pad).Append("\"curveRadiusPx\": ").Append(ArrOrNull(a.CurveRadiusPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"curveShiftPx\": ").Append(ArrOrNull(a.CurveShiftPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"curveShiftMm\": ").Append(ArrOrNull(a.CurveShiftMm));

            sb.Append(',').Append(nl).Append(pad).Append("\"sigmaRatioOnRaw\": ").Append(Num(a.SigmaRatioOnRaw));
            sb.Append(',').Append(nl).Append(pad).Append("\"sigmaRatioOnUndistorted\": ").Append(Num(a.SigmaRatioOnUndistorted));
            sb.Append(',').Append(nl).Append(pad).Append("\"looRmsOnRawMm\": ").Append(Num(a.LooRmsOnRawMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"looRmsOnUndistortedMm\": ").Append(Num(a.LooRmsOnUndistortedMm));
            sb.Append(',').Append(nl).Append(pad).Append("\"looImprovementPct\": ").Append(Num(a.LooImprovementPct));

            sb.Append(',').Append(nl).Append(pad).Append("\"pointShiftPx\": ").Append(ArrOrNull(a.PointShiftPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"pointShiftMm\": ").Append(ArrOrNull(a.PointShiftMm));

            sb.Append(',').Append(nl).Append(pad).Append("\"verdict\": ").Append(Str(a.Verdict));
            sb.Append(nl).Append(indented ? "    " : string.Empty).Append('}');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ intrinsics.json

        /// <summary>
        /// ★ 内参链的独立产物 <c>intrinsics.json</c>：把相机参数写成<b>可直接消费</b>的形式。
        ///
        /// 为什么单开一份而不是只塞进 <c>.calib.json</c>：这两份产物的用途不同 ——
        ///   · <c>.calib.json</c> 是<b>标定产物</b>（H/O/e 怎么消费、发布链怎么走）；
        ///   · <c>intrinsics.json</c> 是<b>相机模型</b>（喂给 HALCON 的 campar、去畸变、
        ///     以及将来"把 H 标到矫正图上"时用的那套参数）。
        /// 宿主拿到这一份就能拼出 campar，不必再从像素焦距反推像元尺寸。
        /// </summary>
        public static string WriteIntrinsics(CalibExport e, bool indented = true)
        {
            if (e == null)
            {
                return "{}";
            }

            var sb = new StringBuilder(1024);
            string nl = indented ? "\n" : string.Empty;
            string pad = indented ? "  " : string.Empty;

            double fx = First(e.FocalLengthPx);
            double fy = e.FocalLengthPx != null && e.FocalLengthPx.Length > 1 ? e.FocalLengthPx[1] : fx;
            double cx = First(e.PrincipalPointPx);
            double cy = e.PrincipalPointPx != null && e.PrincipalPointPx.Length > 1 ? e.PrincipalPointPx[1] : cx;
            double kappa = First(e.Distortion);
            int w = e.ImageSize != null && e.ImageSize.Length > 0 ? e.ImageSize[0] : 0;
            int h = e.ImageSize != null && e.ImageSize.Length > 1 ? e.ImageSize[1] : 0;

            sb.Append('{');
            sb.Append(nl).Append(pad).Append("\"formatVersion\": ").Append(Str(e.FormatVersion));
            sb.Append(',').Append(nl).Append(pad).Append("\"kind\": ").Append(Str("camera_intrinsics"));
            sb.Append(',').Append(nl).Append(pad).Append("\"sourceSessionId\": ").Append(Str(e.SourceSessionId));
            sb.Append(',').Append(nl).Append(pad).Append("\"stationCode\": ").Append(Str(e.StationCode));
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraSlotKey\": ").Append(Str(e.CameraSlotKey));
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraMount\": ").Append(Str(e.CameraMount));
            sb.Append(',').Append(nl).Append(pad).Append("\"handedness\": ").Append(Str(e.Handedness));

            // ★ campar 直接按 HALCON 的参数顺序排好，消费端可以原样喂给
            //   change_radial_distortion_cam_offline / gen_radial_distortion_map 等算子。
            sb.Append(',').Append(nl).Append(pad).Append("\"cameraType\": ").Append(Str("area_scan_division"));
            sb.Append(',').Append(nl).Append(pad).Append("\"campar\": ").Append(string.Format(
                CultureInfo.InvariantCulture,
                "[\"area_scan_division\", {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}]",
                Num(e.FocalLengthM), Num(kappa), Num(e.PixelPitchM), Num(e.PixelPitchM),
                Num(cx), Num(cy), w.ToString(CultureInfo.InvariantCulture), h.ToString(CultureInfo.InvariantCulture)));
            sb.Append(',').Append(nl).Append(pad).Append("\"camparOrder\": ")
                .Append(Str("type, focus_m, kappa, sx_m, sy_m, cx_px, cy_px, width, height"));

            sb.Append(',').Append(nl).Append(pad).Append("\"focalLengthPx\": ").Append(ArrOrNull(e.FocalLengthPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"focalLengthM\": ").Append(Num(e.FocalLengthM));
            sb.Append(',').Append(nl).Append(pad).Append("\"principalPointPx\": ").Append(ArrOrNull(e.PrincipalPointPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"pixelPitchM\": ").Append(Num(e.PixelPitchM));
            sb.Append(',').Append(nl).Append(pad).Append("\"imageSize\": ").Append(IntArrOrNull(e.ImageSize));

            sb.Append(',').Append(nl).Append(pad).Append("\"distortion\": ").Append(ArrOrNull(e.Distortion));
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionConvention\": ")
                .Append(Str("halcon_area_scan_division_kappa_1_per_m2"));
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionNormalizedK1\": ")
                .Append(Num(e.DistortionNormalizedK1));
            sb.Append(',').Append(nl).Append(pad).Append("\"distortionUnitNote\": ").Append(Str(
                "kappa 乘的是像面公制半径（米），量纲 1/m²。"
                + "归一化口径 k1 = kappa·f²（f 单位米）；两者混用不会报错，只会算歪。"));

            sb.Append(',').Append(nl).Append(pad).Append("\"reprojectionErrorPx\": ").Append(Num(e.ReprojectionErrorPx));
            sb.Append(',').Append(nl).Append(pad).Append("\"board\": ")
                .Append(e.Board.HasValue ? Str(e.Board.Value.ToString()) : "null");
            sb.Append(',').Append(nl).Append(pad).Append("\"boardDescription\": ").Append(Str(e.BoardDescription));

            sb.Append(',').Append(nl).Append(pad).Append("\"producedBy\": ").Append(Str(e.ProducedBy));
            sb.Append(',').Append(nl).Append(pad).Append("\"producedUtc\": ")
                .Append(Str(e.ProducedUtc.ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(',').Append(nl).Append(pad).Append("\"framesDir\": ").Append(Str(e.FramesDir));

            sb.Append(',').Append(nl).Append(pad).Append("\"distortionImpact\": ")
                .Append(WriteDistortionImpact(e.Diagnostics == null ? null : e.Diagnostics.DistortionImpact, indented));

            sb.Append(nl).Append('}');
            return sb.ToString();
        }

        private static double First(double[] a)
        {
            return a != null && a.Length > 0 ? a[0] : double.NaN;
        }

        private static string Str(string s)
        {
            if (s == null)
            {
                return "null";
            }

            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>★ 一律 "R"（往返）：保证 JSON 里读回来的 double 与写出去的前一位不差。</summary>
        private static string Num(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return "null";
            }

            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Bool(bool v)
        {
            return v ? "true" : "false";
        }

        private static string Vec(Vec2 v, bool valid)
        {
            if (!valid || !v.IsFinite)
            {
                return "null";
            }

            return "[" + Num(v.X) + ", " + Num(v.Y) + "]";
        }

        private static string Arr(double[] a)
        {
            if (a == null)
            {
                return "null";
            }

            var sb = new StringBuilder(64);
            sb.Append('[');
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Num(a[i]));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string ArrOrNull(double[] a)
        {
            return a == null ? "null" : Arr(a);
        }

        private static string IntArrOrNull(int[] a)
        {
            if (a == null)
            {
                return "null";
            }

            var sb = new StringBuilder(32);
            sb.Append('[');
            for (int i = 0; i < a.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(a[i].ToString(CultureInfo.InvariantCulture));
            }

            sb.Append(']');
            return sb.ToString();
        }

        private static string StrArr(System.Collections.Generic.IList<string> a)
        {
            if (a == null || a.Count == 0)
            {
                return "[]";
            }

            var sb = new StringBuilder(256);
            sb.Append('[');
            for (int i = 0; i < a.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(Str(a[i]));
            }

            sb.Append(']');
            return sb.ToString();
        }
    }
}
