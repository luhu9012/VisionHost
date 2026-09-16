using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using VisualCalibTool.Domain;

namespace VisualCalibTool.Abstractions
{
    /// <summary>内存 + 事件式日志（宿主可订阅转发到主项目日志总线）。</summary>
    public sealed class SimpleCalibLog : ICalibLog
    {
        private readonly List<string> _lines = new List<string>();
        private readonly object _gate = new object();

        /// <summary>每条日志产生时触发（参数：级别, 文本）。</summary>
        public event Action<string, string> Emitted;

        public IList<string> Lines
        {
            get
            {
                lock (_gate)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Info(string message)
        {
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            Write("WARN", message);
        }

        public void Error(string message, Exception ex = null)
        {
            Write("ERROR", ex == null ? message : message + " | " + ex.Message);
        }

        public void Step(string stepKey, string message)
        {
            Write("STEP:" + stepKey, message);
        }

        private void Write(string level, string message)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "[{0:HH:mm:ss}] {1,-12} {2}",
                DateTime.Now, level, message);

            lock (_gate)
            {
                _lines.Add(line);
                if (_lines.Count > 5000)
                {
                    _lines.RemoveRange(0, 1000);
                }
            }

            var h = Emitted;
            if (h != null)
            {
                h(level, message);
            }
        }
    }

    /// <summary>
    /// 文件系统存储。★ 只写自己的目录（默认 %LOCALAPPDATA%\VisualCalibTool 或工具自定根），
    /// <b>绝不写主项目的库或 Recipes 目录</b> —— 那会引入"两边同时改一个文件"的风险。
    /// </summary>
    public sealed class FileSystemCalibStore : ICalibStore, ICalibStoreExtras
    {
        private readonly string _root;

        public FileSystemCalibStore(string rootDir = null)
        {
            if (string.IsNullOrEmpty(rootDir))
            {
                rootDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VisualCalibTool");
            }

            _root = rootDir;
        }

        public string RootDir
        {
            get { return _root; }
        }

        public string SaveFrame(string sessionId, int sampleIndex, byte[] encodedBytes, string extension)
        {
            if (encodedBytes == null || encodedBytes.Length == 0)
            {
                return null;
            }

            if (string.IsNullOrEmpty(extension))
            {
                extension = ".bin";
            }
            else if (extension[0] != '.')
            {
                extension = "." + extension;
            }

            try
            {
                string dir = Path.Combine(_root, "frames", Sanitize(sessionId));
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, string.Format(CultureInfo.InvariantCulture, "{0:D2}{1}", sampleIndex, extension));
                File.WriteAllBytes(path, encodedBytes);
                return path;
            }
            catch (Exception)
            {
                // 留档失败绝不影响标定主流程
                return null;
            }
        }

        public string SaveSession(string sessionId, string json)
        {
            string dir = Path.Combine(_root, "sessions");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Sanitize(sessionId) + ".json");
            File.WriteAllText(path, json ?? string.Empty, new UTF8Encoding(false));
            return path;
        }

        public string SaveExport(string sessionId, string exportJson, HomMat2D? h)
        {
            string dir = Path.Combine(_root, "exports");
            Directory.CreateDirectory(dir);

            string baseName = Sanitize(sessionId);
            string jsonPath = Path.Combine(dir, baseName + ".calib.json");
            File.WriteAllText(jsonPath, exportJson ?? string.Empty, new UTF8Encoding(false));

            if (h.HasValue)
            {
                // ★ 与主项目 .tup 逐位兼容：直接可被「导入外部标定矩阵文件」消费
                Algorithm.HomMatIO.WriteTup(Path.Combine(dir, baseName + ".tup"), h.Value);
            }

            return dir;
        }

        public IList<string> ListExports()
        {
            string dir = Path.Combine(_root, "exports");
            if (!Directory.Exists(dir))
            {
                return new string[0];
            }

            string[] files = Directory.GetFiles(dir, "*.calib.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }

        /// <summary>
        /// 附加产物（内参链的 <c>intrinsics.json</c>）。
        /// ★ 文件名一律以会话号开头（<c>&lt;session&gt;.intrinsics.json</c>）：
        ///   exports 目录是<b>按历史累积</b>的，同名文件会被上一份悄悄覆盖 ——
        ///   而标定产物最忌讳"这份到底是哪次跑的"说不清。
        /// </summary>
        public string SaveExtraFile(string sessionId, string fileName, string content)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            try
            {
                string dir = Path.Combine(_root, "exports");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, Sanitize(sessionId) + "." + fileName);
                File.WriteAllText(path, content ?? string.Empty, new UTF8Encoding(false));
                return path;
            }
            catch (Exception)
            {
                // 附加产物写不进去不该让标定失败
                return null;
            }
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "session";
            }

            var sb = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool bad = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (invalid[j] == c)
                    {
                        bad = true;
                        break;
                    }
                }

                sb.Append(bad ? '_' : c);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 自动作答的用户交互（仿真 / 无人回归用）。
    /// ★ 让"跑通全流程"可以在没有人的情况下验证 —— 这是 P0 能做到 0 硬件闭环的关键之一。
    /// </summary>
    public sealed class AutoYesUserPrompt : IUserPrompt
    {
        /// <summary>选择类问题的默认答案索引。</summary>
        public int DefaultSelection;

        /// <summary>是否一律确认。</summary>
        public bool AlwaysConfirm = true;

        public readonly List<string> Transcript = new List<string>();

        public bool Confirm(string title, string message)
        {
            Transcript.Add("CONFIRM: " + title + " | " + message);
            return AlwaysConfirm;
        }

        public void Inform(string title, string message)
        {
            Transcript.Add("INFO: " + title + " | " + message);
        }

        public void Warn(string title, string message)
        {
            Transcript.Add("WARN: " + title + " | " + message);
        }

        public void ShowError(string title, string message, CalibError error)
        {
            Transcript.Add("ERROR: " + title + " | " + message + " | " + (error == null ? "-" : error.ToString()));
        }

        public int Select(string title, string message, IList<string> options)
        {
            Transcript.Add("SELECT: " + title + " | " + message);
            if (options == null || options.Count == 0)
            {
                return -1;
            }

            return Math.Max(0, Math.Min(DefaultSelection, options.Count - 1));
        }
    }

    /// <summary>
    /// 矩形软限位守卫（保守外围校验）。
    /// ★ XYLim 是<b>矩形</b>：只防外围，防不了内圈（可达域不是圆也不是圆环）。
    ///   真正可达性一律交给控制器的零运动校核（<c>CHECK</c>）；本守卫只做上位机的粗筛。
    /// </summary>
    public sealed class RectSafetyGuard : ISafetyGuard
    {
        public double XMin = -2000.0;
        public double XMax = 2000.0;
        public double YMin = -2000.0;
        public double YMax = 2000.0;
        public double ZMin = -144.0;
        public double ZMax = 0.0;

        /// <summary>XY 半径上限（|XY| ≤ R），null 表示不检查。</summary>
        public double? MaxRadiusXy = 2000.0;

        public CalibError Validate(MotionPose target)
        {
            if (!target.IsFinite)
            {
                return CalibError.Create(CalibFailureKind.SafetyBlocked, "目标位含 NaN/Inf");
            }

            if (target.Z < ZMin || target.Z > ZMax)
            {
                return CalibError.Create(
                    CalibFailureKind.SafetyBlocked,
                    string.Format(CultureInfo.InvariantCulture, "Z={0:F3} 超出安全范围 [{1:F3}, {2:F3}]", target.Z, ZMin, ZMax));
            }

            if (MaxRadiusXy.HasValue && target.Xy.Length > MaxRadiusXy.Value)
            {
                return CalibError.Create(
                    CalibFailureKind.SafetyBlocked,
                    string.Format(CultureInfo.InvariantCulture, "|XY|={0:F3} 超出半径上限 {1:F3}", target.Xy.Length, MaxRadiusXy.Value));
            }

            if (target.X < XMin || target.X > XMax || target.Y < YMin || target.Y > YMax)
            {
                return CalibError.Create(
                    CalibFailureKind.SafetyBlocked,
                    string.Format(CultureInfo.InvariantCulture, "XY=({0:F3}, {1:F3}) 超出矩形软限位", target.X, target.Y));
            }

            return null;
        }
    }
}
