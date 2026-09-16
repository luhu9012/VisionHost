using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Algorithm
{
    /// <summary>
    /// HALCON ASCII 元组文件（<c>.tup</c>）编解码器。★ 纯文本、零 HALCON 依赖。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 真机产物实测格式（155 字节，逐位复刻，别改）
    /// ══════════════════════════════════════════════════════════════════
    ///   b'06\n2 -5.3606559567018668e-03\n2 2.0331699576237314e-01\n...\n'
    ///
    ///   · 换行符是 <b>LF</b>（\n），<b>不是</b> CRLF；
    ///   · 第 1 行 = 元素个数（十六进制，至少 2 位）；
    ///   · 之后每行 = "&lt;类型码&gt; &lt;值&gt;"，double 的类型码是 <b>2</b>；
    ///   · 值的格式等价于 C 的 <c>%.16e</c>（小数点后 16 位 + 带符号两位指数）。
    ///
    /// 主项目 <c>CalibrationManagerViewModel.ImportMatrixFile()</c> 只把文件<b>路径</b>
    /// 存进 <c>profile.HomMatFilePath</c>，运行时由 <c>ICalibrationService</c> 读。
    /// 所以只要本工具能写出格式严格一致的 .tup，主项目<b>零改动</b>即可消费。
    /// </summary>
    public static class TupTupleIO
    {
        /// <summary>HALCON 元组元素类型码：double。</summary>
        private const int TypeDouble = 2;

        /// <summary>HALCON 元组元素类型码：integer。</summary>
        private const int TypeInteger = 1;

        private static readonly char[] LineSplit = new char[] { '\r', '\n' };

        /// <summary>
        /// 按 HALCON 的 <c>%.16e</c> 口径格式化一个 double。
        ///
        /// ★ 不能用 .NET 的自定义格式串（如 <c>"0.0000000000000000e+00"</c>）：
        ///   .NET Framework 的自定义浮点格式<b>只有 15 位有效数字</b>，第 16/17 位会被补零，
        ///   于是 -5.3606559567018668e-03 会写成 -5.3606559567018700e-03 ——
        ///   与 HALCON 产物<b>不是逐位一致</b>，主项目读回来就是另一个矩阵。
        ///
        /// 做法：先用 "R" 拿到最短可往返表示（17 位有效数字全覆盖），
        /// 再用<b>纯字符串</b>把小数点搬到科学计数法位置 —— 不动数值，因此不丢精度。
        /// </summary>
        public static string FormatValue(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                throw new ArgumentException("HALCON 元组不支持 NaN/Infinity：" + v);
            }

            if (v == 0.0)
            {
                return "0.0000000000000000e+00";
            }

            string r = v.ToString("R", CultureInfo.InvariantCulture);

            // 拆指数
            int ePos = r.IndexOfAny(new char[] { 'e', 'E' });

            bool negative = r.Length > 0 && r[0] == '-';
            string body = negative ? r.Substring(1) : r;
            int extraExp = 0;
            if (ePos >= 0)
            {
                string expPart = r.Substring(ePos + 1);
                extraExp = int.Parse(expPart, CultureInfo.InvariantCulture);
                body = r.Substring(negative ? 1 : 0, ePos - (negative ? 1 : 0));
            }

            // 拆整数位 / 小数位
            int dot = body.IndexOf('.');
            string intPart = dot < 0 ? body : body.Substring(0, dot);
            string fracPart = dot < 0 ? string.Empty : body.Substring(dot + 1);

            string digits = intPart + fracPart;
            int leadingZeros = 0;
            while (leadingZeros < digits.Length && digits[leadingZeros] == '0')
            {
                leadingZeros++;
            }

            if (leadingZeros == digits.Length)
            {
                return "0.0000000000000000e+00";   // 全零（含 -0）
            }

            // 首位数字的十进制阶码
            int exp10 = extraExp + (intPart.Length - 1) - leadingZeros;

            string sig = digits.Substring(leadingZeros);
            string mantissa = sig.Substring(0, 1) + "." + sig.Substring(1).PadRight(16, '0').Substring(0, 16);

            char sign = exp10 < 0 ? '-' : '+';
            int absExp = Math.Abs(exp10);
            string expText = (absExp < 10 ? "0" + absExp : absExp.ToString(CultureInfo.InvariantCulture));

            return (negative ? "-" : string.Empty) + mantissa + "e" + sign + expText;
        }

        /// <summary>把 double 数组编码成 HALCON ASCII 元组文本。</summary>
        public static string Encode(IList<double> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException("values");
            }

            var sb = new StringBuilder();
            sb.Append(values.Count.ToString("x2", CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < values.Count; i++)
            {
                sb.Append(TypeDouble.ToString(CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(FormatValue(values[i]))
                  .Append('\n');
            }

            return sb.ToString();
        }

        /// <summary>
        /// 解码 HALCON ASCII 元组文本。容错处理 CR/LF、空行与额外的空白。
        /// integer 元素（类型码 1）也一并转成 double。
        /// </summary>
        public static double[] Decode(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new double[0];
            }

            string[] lines = text.Split(LineSplit, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return new double[0];
            }

            int declared;
            if (!int.TryParse(lines[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out declared))
            {
                throw new FormatException("无法解析 .tup 首行的元素个数（应为十六进制）：" + lines[0]);
            }

            var result = new List<double>(declared);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                // "2 -5.36e-03" → 类型码 + 值
                int sp = line.IndexOf(' ');
                string numPart = sp >= 0 ? line.Substring(sp + 1).Trim() : line;

                double v;
                if (!double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                {
                    throw new FormatException("无法解析 .tup 的元组元素：" + line);
                }

                result.Add(v);
            }

            return result.ToArray();
        }

        /// <summary>
        /// 写出 .tup。★ 用 UTF-8 <b>无 BOM</b> + LF —— 与 HALCON 产物逐位一致。
        /// </summary>
        public static void Write(string path, IList<double> values)
        {
            string text = Encode(values);
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(path, bytes);
        }

        /// <summary>读出 .tup，返回元素数组。</summary>
        public static double[] Read(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            return Decode(text);
        }
    }

    /// <summary>
    /// HomMat2D ⇄ .tup 的读写门面（6 参数仿射，顺序 [h11 h12 h13 h21 h22 h23]）。
    /// ★ 这是本工具与主项目之间<b>唯一必须逐位兼容</b>的落盘格式。
    /// </summary>
    public static class HomMatIO
    {
        public const int ParameterCount = 6;

        /// <summary>写 .tup（可直接被主项目「导入外部标定矩阵文件」消费）。</summary>
        public static void WriteTup(string path, HomMat2D h)
        {
            if (!h.IsFinite)
            {
                throw new InvalidOperationException("拒绝写出非法矩阵（含 NaN/Inf）：" + h);
            }

            TupTupleIO.Write(path, h.ToArray());
        }

        /// <summary>读 .tup。参数个数不是 6 时抛异常（不做"猜测式"兼容）。</summary>
        public static HomMat2D ReadTup(string path)
        {
            double[] v = TupTupleIO.Read(path);
            if (v.Length != ParameterCount)
            {
                throw new FormatException(string.Format(
                    CultureInfo.InvariantCulture,
                    "HomMat2D 应为 {0} 个参数，实际 {1} 个：{2}",
                    ParameterCount, v.Length, path));
            }

            return HomMat2D.FromArray(v);
        }
    }
}
