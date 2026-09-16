using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// ★ 标定板模型（可插拔）。内参链的"尺子"就是它 —— 板模型错了，标出来的焦距/畸变
    /// 会得到一个<b>自洽但错</b>的解：残差依然很小，主点依然在中心，只有拿真尺子去量才露馅。
    ///
    /// 两条路（<see cref="BoardKind"/>）：
    ///   · <b>HalconCalplate</b>：MVTec 官方标定板（六角点阵，<c>.cpd</c>）或老式矩形阵列
    ///     （<c>.descr</c>）。把<b>文件名</b>交给 HALCON，由它自己去 <c>$HALCONROOT/calib</c> 找，
    ///     我们只额外解析一遍做"文件到底在不在"的前置校验 —— 让操作员在摆板之前就知道
    ///     板文件缺了，而不是摆完十张图才报错。
    ///   · <b>Chessboard</b>：工厂最常见的棋盘格。HALCON 没有"棋盘格描述文件"这种东西，
    ///     必须自己按内角点几何生成 <b>3D 点元组</b> 交给 <c>set_calib_data_calib_object</c>。
    ///     这里生成的就是那份点元组（单位：<b>米</b>，HALCON 一律用米）。
    ///
    /// ★ 关于"mark 数量"：<c>.cpd</c> 是 <c>r 27</c>/<c>c 31</c> 这样的行列数，
    ///   实际 mark 数 = r × c（40 mm 板 = 837 个）；一张图能检出 810~837 个都属正常
    ///   （边缘 mark 会被切掉、被反光吃掉）。所以数量只能用来做"看着离谱就报警"的粗判，
    ///   不能当硬门槛 —— 拿它卡失败率会把正常标定拦成失败。
    /// </summary>
    public sealed class BoardModel
    {
        /// <summary>板类型。</summary>
        public BoardKind Kind = BoardKind.HalconCalplate;

        /// <summary>
        /// 交给 HALCON 的板描述：<c>.cpd</c>/<c>.descr</c> 用<b>文件名</b>（HALCON 自己去
        /// <c>$HALCONROOT/calib</c> 找）；棋盘格为空（改用 <see cref="Point3d"/>）。
        /// </summary>
        public string DescriptionFile;

        /// <summary>棋盘格的内角点 3D 点元组 <c>[X1..Xn, Y1..Yn, Z1..Zn]</c>，单位米。</summary>
        public double[] Point3d;

        /// <summary>解析到的板文件绝对路径；找不到为 null（此时 <see cref="SearchHint"/> 给出找过哪里）。</summary>
        public string ResolvedPath;

        /// <summary>找过哪些地方（找不到时的人话原因）。</summary>
        public string SearchHint;

        /// <summary>点阵行数 / 列数（棋盘格则为内角点行 / 列数）。</summary>
        public int MarkRows;
        public int MarkCols;

        /// <summary>mark 间距（毫米）。</summary>
        public double SpacingMm;

        /// <summary>板外形尺寸（毫米，含边缘留白；仅用于界面提示）。</summary>
        public double PlateSizeMm;

        /// <summary>界面上给操作员看的名字（"40 mm 官方标定板"）。</summary>
        public string DisplayName = "标定板";

        /// <summary>理论 mark 总数（0 = 未知）。</summary>
        public int ExpectedMarks
        {
            get { return MarkRows > 0 && MarkCols > 0 ? MarkRows * MarkCols : 0; }
        }

        /// <summary>是否已解析到桌面上的板文件（HalconCalplate 才要求）。</summary>
        public bool IsAvailable
        {
            get
            {
                if (Kind == BoardKind.Chessboard)
                {
                    return Point3d != null && Point3d.Length >= 9;
                }

                return !string.IsNullOrEmpty(ResolvedPath);
            }
        }

        /// <summary>
        /// 交给 <c>set_calib_data_calib_object</c> 的第三参：官方板传文件名，
        /// 棋盘格传 3D 点元组。★ 两种情况走的是 HALCON 同一个参数的不同重载，
        /// 传错了会得到 #1403 而不是"文件找不到" —— 这个错误信息很不直观，所以在这里收口。
        /// </summary>
        public object CalibObjectDescriptor()
        {
            if (Kind == BoardKind.Chessboard)
            {
                object[] pts = new object[Point3d.Length];
                for (int i = 0; i < Point3d.Length; i++)
                {
                    pts[i] = Point3d[i];
                }

                return pts;
            }

            return string.IsNullOrEmpty(DescriptionFile) ? "calplate_40mm.cpd" : DescriptionFile;
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(DisplayName);
            if (Kind == BoardKind.Chessboard)
            {
                sb.Append("（棋盘格 ").Append(MarkCols).Append("×").Append(MarkRows)
                  .Append(" 内角点，间距 ").Append(Fmt(SpacingMm)).Append(" mm）");
            }
            else
            {
                sb.Append("（").Append(DescriptionFile).Append('，');
                if (ExpectedMarks > 0)
                {
                    sb.Append("点阵 ").Append(MarkCols).Append("×").Append(MarkRows)
                      .Append(" = ").Append(ExpectedMarks).Append(" 个 mark，");
                }

                sb.Append("间距 ").Append(Fmt(SpacingMm)).Append(" mm）");
            }

            if (!IsAvailable)
            {
                sb.Append("  ⚠ 板模型不可用：").Append(SearchHint);
            }

            return sb.ToString();
        }

        public override string ToString()
        {
            return Describe();
        }

        private static string Fmt(double v)
        {
            return v.ToString(v >= 100.0 ? "F0" : "F2", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ 构造

        /// <summary>
        /// 构造 HALCON 官方标定板模型。传 <b>文件名</b>（如 <c>calplate_40mm.cpd</c>），
        /// 也可以传绝对路径（此时不再去 HALCONROOT 找）。
        /// </summary>
        public static BoardModel Calplate(string fileNameOrPath)
        {
            var m = new BoardModel { Kind = BoardKind.HalconCalplate };
            string name = string.IsNullOrEmpty(fileNameOrPath) ? "calplate_40mm.cpd" : fileNameOrPath.Trim();
            m.DescriptionFile = Path.GetFileName(name);

            if (Directory.Exists(Path.GetDirectoryName(name ?? string.Empty))
                && File.Exists(name))
            {
                m.ResolvedPath = name;
            }
            else
            {
                m.ResolvedPath = ResolveCalibFile(m.DescriptionFile, out m.SearchHint);
            }

            if (!string.IsNullOrEmpty(m.ResolvedPath))
            {
                TryParseDescriptor(m.ResolvedPath, m);
            }

            m.DisplayName = m.PlateSizeMm > 0
                ? Fmt(m.PlateSizeMm) + " mm 官方标定板"
                : Path.GetFileNameWithoutExtension(m.DescriptionFile);
            return m;
        }

        /// <summary>
        /// 构造棋盘格模型。<paramref name="innerCols"/> × <paramref name="innerRows"/> 是
        /// <b>内角点</b>个数（不是方格数；一个 9×6 方格棋盘的内角点是 8×5）。
        /// </summary>
        public static BoardModel Chessboard(int innerCols, int innerRows, double spacingMm)
        {
            var m = new BoardModel
            {
                Kind = BoardKind.Chessboard,
                MarkCols = Math.Max(2, innerCols),
                MarkRows = Math.Max(2, innerRows),
                SpacingMm = spacingMm > 0 ? spacingMm : 5.0,
                DisplayName = "棋盘格"
            };

            double s = m.SpacingMm / 1000.0;      // ★ HALCON 一律用米
            int n = m.MarkRows * m.MarkCols;
            var pts = new double[3 * n];

            // HALCON 的点元组是"先所有 X、再所有 Y、再所有 Z"（列优先分块），不是 xyz 交错。
            // 板在自己的坐标系里躺平（Z = 0），原点取一角。
            int k = 0;
            for (int r = 0; r < m.MarkRows; r++)
            {
                for (int c = 0; c < m.MarkCols; c++)
                {
                    pts[k] = c * s;                    // X
                    pts[n + k] = r * s;                // Y
                    pts[2 * n + k] = 0.0;              // Z
                    k++;
                }
            }

            m.Point3d = pts;
            m.PlateSizeMm = Math.Max((m.MarkCols - 1) * m.SpacingMm, (m.MarkRows - 1) * m.SpacingMm);
            return m;
        }

        // ------------------------------------------------------------------ 板文件解析

        /// <summary>
        /// 解析 HALCON 板描述文件，取出行列数与间距。
        /// ★ 优先读注释里那几个<b>说得明明白白</b>的键行（都被 <c>#</c> 注释掉了，但值是权威的）：
        ///     <c># 27 rows x 31 columns</c>
        ///     <c># Distance between mark centers [meter]: 0.00129032</c>
        ///     <c># Width, height of calibration plate [meter]: 0.0425806, 0.0322796</c>
        ///   其次是键行 <c>r 27</c> / <c>c 31</c>。
        /// ★ 千万不要拿"板宽 ÷ (列数−1)"当间距：板四周有留白，这样算出来偏大
        ///   （40 mm 板会被算成 1.419 mm，真值 1.290 mm）—— 数字看着也像那么回事，最容易被信。
        /// 解析失败不算错误（只影响界面提示），绝不因此拦住标定。
        /// </summary>
        private static void TryParseDescriptor(string path, BoardModel m)
        {
            try
            {
                string[] lines = File.ReadAllLines(path);
                int rows = 0;
                int cols = 0;
                double spacingMm = 0.0;
                double widthMm = 0.0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string t = (lines[i] ?? string.Empty).Trim();
                    if (t.Length == 0)
                    {
                        continue;
                    }

                    string body = t.StartsWith("#", StringComparison.Ordinal)
                        ? t.TrimStart('#').Trim()
                        : t;

                    // 「27 rows x 31 columns」
                    // ★ 不要用 Substring(ri+5, ci-ri-5) 去截中间那段：两词之间还有 " x "，
                    //   截出来是 " x 3" —— 解析失败但**不报错**，只是 cols 静默变成 0，
                    //   然后 ExpectedMarks 也是 0，"板有几个 mark"这个判据就悄悄失效了。
                    //   正确做法是从两词之间取出最后一个数。
                    int ri = body.IndexOf(" rows", StringComparison.OrdinalIgnoreCase);
                    int ci = body.IndexOf(" columns", StringComparison.OrdinalIgnoreCase);
                    if (ri > 0 && ci > ri && cols == 0)
                    {
                        int a, b;
                        string rowsText = body.Substring(0, ri).Trim();
                        string colsText = body.Substring(ri + 5, ci - ri - 5).Trim();

                        // 去掉连接词（x / × / by / ,）
                        int cut = colsText.LastIndexOfAny(new char[] { ' ', 'x', 'X', '×', ',', 'b', 'y' });
                        if (cut >= 0)
                        {
                            colsText = colsText.Substring(cut + 1).Trim();
                        }

                        if (int.TryParse(rowsText, out a) && int.TryParse(colsText, out b))
                        {
                            rows = a;
                            cols = b;
                        }

                        continue;
                    }

                    // 「Distance between mark centers [meter]: 0.00129032」
                    int di = body.IndexOf("Distance between mark centers", StringComparison.OrdinalIgnoreCase);
                    if (di >= 0)
                    {
                        double v;
                        if (TryParseLastNumber(body, out v) && v > 0.0)
                        {
                            spacingMm = v * 1000.0;
                        }

                        continue;
                    }

                    // 「Width, height of calibration plate [meter]: 0.0425806, 0.0322796」
                    int wi = body.IndexOf("Width, height of calibration plate", StringComparison.OrdinalIgnoreCase);
                    if (wi >= 0)
                    {
                        int colon = body.IndexOf(':');
                        if (colon > 0)
                        {
                            string[] nums = body.Substring(colon + 1)
                                .Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            double w;
                            if (nums.Length > 0
                                && double.TryParse(nums[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w))
                            {
                                widthMm = w * 1000.0;
                            }
                        }

                        continue;
                    }

                    // 键行 r / c
                    string[] tok = body.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length == 2 && rows == 0 && cols == 0)
                    {
                        double v;
                        if (tok[0] == "r" && double.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        {
                            rows = (int)Math.Round(v);
                        }
                        else if (tok[0] == "c" && double.TryParse(tok[1], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                        {
                            cols = (int)Math.Round(v);
                        }
                    }
                }

                m.MarkRows = rows;
                m.MarkCols = cols;
                if (spacingMm > 0.0)
                {
                    m.SpacingMm = spacingMm;
                }

                if (widthMm > 0.0)
                {
                    m.PlateSizeMm = widthMm;
                }
            }
            catch (Exception)
            {
                // 解析失败只影响提示文案，不影响标定 —— 绝不能因此拦住流程
            }
        }

        /// <summary>取一行里冒号之后的最后一个数（<c>... [meter]: 0.00129032</c> → 0.00129032）。</summary>
        private static bool TryParseLastNumber(string line, out double value)
        {
            value = 0.0;
            int colon = line.IndexOf(':');
            string tail = colon >= 0 ? line.Substring(colon + 1) : line;
            string[] tok = tail.Split(new char[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = tok.Length - 1; i >= 0; i--)
            {
                double v;
                if (double.TryParse(tok[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    value = v;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 找 HALCON 的 <c>calib</c> 目录。顺序：显式路径 → <c>HALCONROOT</c> 环境变量 →
        /// <c>HALCONROOT*</c> 其它家族环境变量 → 常见安装根下的 <c>HALCON-*</c>。
        /// ★ 应用自己也要能做这件事：样本运行器会给进程设 <c>HALCONROOT</c>，
        ///   但独立打包成 exe 之后没有它，必须自己找得到，否则报"板文件缺失"而其实装机就有。
        /// </summary>
        public static string ResolveCalibFile(string fileName, out string hint)
        {
            var tried = new List<string>();
            if (string.IsNullOrEmpty(fileName))
            {
                hint = "板文件名为空。";
                return null;
            }

            string env = Environment.GetEnvironmentVariable("HALCONROOT");
            if (!string.IsNullOrEmpty(env))
            {
                string p = Path.Combine(env, "calib", fileName);
                tried.Add(p);
                if (File.Exists(p))
                {
                    hint = null;
                    return p;
                }
            }

            // HALCON 允许同一台机器装多代（HALCONROOT 只指向"当前"那代）
            string[] roots =
            {
                @"C:\Program Files\MVTec",
                @"C:\Program Files (x86)\MVTec",
                @"D:\Program Files\MVTec"
            };

            for (int i = 0; i < roots.Length; i++)
            {
                try
                {
                    if (!Directory.Exists(roots[i]))
                    {
                        continue;
                    }

                    string[] dirs = Directory.GetDirectories(roots[i], "HALCON-*");
                    Array.Sort(dirs);
                    for (int k = dirs.Length - 1; k >= 0; k--)     // 版本号大的优先
                    {
                        string p = Path.Combine(dirs[k], "calib", fileName);
                        tried.Add(p);
                        if (File.Exists(p))
                        {
                            hint = null;
                            return p;
                        }
                    }
                }
                catch (Exception)
                {
                }
            }

            hint = "找不到板文件「" + fileName + "」，找过："
                 + (tried.Count == 0 ? "（没有可用的 HALCON 安装目录）" : string.Join("；", tried.ToArray()))
                 + "。官方板文件随 HALCON 安装包提供（在 <HALCONROOT>\\calib），"
                 + "也可以在向导里改用「棋盘格」并填内角点数与间距。";
            return null;
        }
    }
}
