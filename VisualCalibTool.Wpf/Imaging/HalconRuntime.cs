using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HalconDotNet;
using Microsoft.Win32;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★ HALCON 原生库的运行时引导 + 可用性探测。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么必须有这一层（一次真实崩溃换来的）
    /// ══════════════════════════════════════════════════════════════════
    /// 托管的 <c>halcondotnet.dll</c> 是项目直接引用的，所以它<b>一定</b>在输出目录里。
    /// 但它 P/Invoke 的原生库 <c>halcon.dll</c>（85 MB，外加 halconcpp / halcondl / Qt 一串）
    /// 既不会被 MSBuild 拷过来，也不在进程 PATH 里（HALCON 安装程序默认只给 HDevelop 配环境）。
    ///
    /// 于是会出现一个特别坑的失败形态：
    ///   · 编译 100% 过，ACL、静态检查、甚至 XAML 加载也全绿；
    ///   · 直到第一次真的建 <c>HImage</c>，才从 <c>Loaded</c> 事件里抛出
    ///     「DllNotFoundException: 无法加载 DLL“halcon”」——整个程序当场没了，
    ///     而现场的人看到的只是一个英文堆栈，看不出是环境问题还是代码问题。
    ///
    /// 对策是两件事，缺一不可：
    ///   ① <b>引导</b>：进程最早时刻把 HALCON 的 bin 目录挂进 DLL 搜索路径
    ///      （<see cref="EnsureNativeLibrary"/>）。这样从 VS 启动、双击 exe、被宿主嵌入都能跑。
    ///   ② <b>探测</b>：给调用方一个「现在到底能不能用 HALCON」的布尔量
    ///      （<see cref="IsAvailable"/>），让图像链路在缺库时<b>降级并说人话</b>，
    ///      而不是把异常抛到 WPF 的 Loaded 里。
    ///
    /// ★ 放在 <c>Imaging/</c> 是刻意的：防腐层里只有 Imaging/、Controls/、Simulation/
    ///   被允许 using HalconDotNet，而「HALCON 能不能用」本来就是图像适配层该回答的问题。
    /// </summary>
    public static class HalconRuntime
    {
        private const string NativeDll = "halcon.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private static readonly object Sync = new object();

        private static bool _nativeMounted;
        private static bool _probed;
        private static bool _available;
        private static string _binDir;
        private static string _failure;
        private static Exception _lastError;
        private static List<string> _scanned = new List<string>();

        /// <summary>
        /// 候选中「有 <c>halcon.dll</c> 但缺伴生模块」的目录（半残拷贝）。
        ///
        /// ★ 必须单独记下来：这种目录比"压根没装 HALCON"难查得多 ——
        ///   基础算子（CountSeconds / HImage）全都正常，看起来一切健康，
        ///   只有窗口子系统坏掉，而它报的还是一句风马牛不相及的
        ///   「Wrong number of values of control parameter 1」。
        /// </summary>
        private static List<string> _partialBins = new List<string>();

        /// <summary>HALCON 现在能不能用（首次访问时才真正探测，之后缓存）。</summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureProbed();
                return _available;
            }
        }

        /// <summary>找到并挂载成功的原生库目录；没找到则是 null。</summary>
        public static string BinDirectory
        {
            get
            {
                EnsureNativeLibrary();
                return _binDir;
            }
        }

        /// <summary>不可用时的人话原因（含扫过哪些目录、该怎么配）。可用时是 null。</summary>
        public static string FailureReason
        {
            get
            {
                EnsureProbed();
                return _failure;
            }
        }

        /// <summary>探测时抛出的原始异常（排错用）。</summary>
        public static Exception LastError
        {
            get
            {
                EnsureProbed();
                return _lastError;
            }
        }

        /// <summary>一行状态，直接给状态栏用。</summary>
        public static string StatusText
        {
            get
            {
                EnsureProbed();
                if (_available)
                {
                    string ok = "HALCON 就绪：" + (_binDir == null ? "原生库已由宿主加载" : _binDir);
                    if (_partialBins != null && _partialBins.Count > 0)
                    {
                        ok += "（★ 已跳过 " + _partialBins.Count
                            + " 个只拷了 halcon.dll 的目录；建议删掉那些拷贝 —— 半残的 HALCON "
                            + "基础算子正常但窗口画不出来，换个环境就会翻车）";
                    }

                    return ok;
                }

                return _failure;
            }
        }

        /// <summary>
        /// 尽力把 HALCON 原生库目录挂进本进程的 DLL 搜索路径。
        /// 幂等；返回找到的 bin 目录，找不到返回 null。
        ///
        /// ★ 必须在<b>任何</b> halcondotnet 类型被触碰之前调用才有效 ——
        ///   原生库一旦加载失败，CLR 不会给你第二次机会（异常会被静态构造缓存下来）。
        /// </summary>
        public static string EnsureNativeLibrary()
        {
            if (_nativeMounted)
            {
                return _binDir;
            }

            lock (Sync)
            {
                if (_nativeMounted)
                {
                    return _binDir;
                }

                _nativeMounted = true;
                _binDir = MountNative();
                return _binDir;
            }
        }

        // ────────────────────────────────────────────────────────── 挂载

        private static string MountNative()
        {
            // ① 宿主（例如主程序）已经把原生库加载进进程了 → 什么都不用做
            if (GetModuleHandle(NativeDll) != IntPtr.Zero)
            {
                return null;
            }

            string arch = IntPtr.Size == 8 ? "x64-win64" : "x86-win32";
            var candidates = new List<string>();

            // ② HALCONROOT（+ 可选 HALCONARCH）—— 明确指定时最可信
            string root = Environment.GetEnvironmentVariable("HALCONROOT");
            if (!string.IsNullOrEmpty(root))
            {
                string a = Environment.GetEnvironmentVariable("HALCONARCH");
                candidates.Add(Path.Combine(root, "bin", string.IsNullOrEmpty(a) ? arch : a));
                candidates.Add(Path.Combine(root, "bin", arch));
            }

            // ③ 应用目录：如果按"把原生库拷进 bin"的部署方式，这里就能直接命中
            candidates.Add(AppDomain.CurrentDomain.BaseDirectory);

            // ④ PATH 里已经有了
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string p in path.Split(Path.PathSeparator))
            {
                candidates.Add(p);
            }

            // ⑤ 注册表（键名随版本变，所以把 MVTec\HALCON 下的值扫一遍）
            foreach (string r in ProbeRegistry(arch))
            {
                candidates.Add(r);
            }

            // ⑥ 常见安装目录（新版优先）
            foreach (string r in ProbeProgramFiles(arch))
            {
                candidates.Add(r);
            }

            var scanned = new List<string>();
            var partial = new List<string>();
            foreach (string dir in candidates)
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                scanned.Add(dir);
                if (!File.Exists(Path.Combine(dir, NativeDll)))
                {
                    continue;
                }

                // ★★ 只拷了 halcon.dll 的目录**不算命中**（2026-09-14 真踩，见 LooksComplete 注释）。
                //   必须继续往下找一个"整套"的安装目录。
                if (!LooksComplete(dir))
                {
                    if (partial.IndexOf(dir) < 0)
                    {
                        partial.Add(dir);
                    }

                    continue;
                }

                ApplySearchPath(dir, arch);
                _scanned = scanned;
                _partialBins = partial;
                return dir;
            }

            _scanned = scanned;
            _partialBins = partial;
            return null;
        }

        /// <summary>
        /// 这个目录是不是「整套」HALCON 运行时，而不是只拷了几个 DLL 的半残拷贝。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 为什么必须有这道判断（2026-09-14 现场，代价很大）
        /// ══════════════════════════════════════════════════════════════════
        /// 有人为了"省事/防缺库"把 <c>halcon.dll</c> + <c>halconcpp.dll</c> 拷进了应用目录。
        /// 应用目录在候选顺序里靠前，于是引导层**命中了它**，一切看起来都对：
        ///   · <c>CountSeconds</c> 成功 → <see cref="IsAvailable"/> = true；
        ///   · <c>new HImage(...)</c> 成功 → 图像链路正常；
        ///   · <c>--diag</c> 也会报「HALCON 就绪」。
        ///
        /// 但 HALCON 的**窗口子系统**（<c>hcanvas</c> 那一套）不在那个目录里，
        /// <c>HSmartWindowControlWPF</c> 于是建不出窗口，第一次 Arrange 就抛：
        /// <code>
        /// HalconDotNet.HOperatorException: HALCON error #1401:
        ///   Wrong number of values of control parameter 1 in operator dump_window_image
        ///    在 HalconDotNet.HSmartWindowControlWPF.OnRender(DrawingContext)
        /// </code>
        /// 现场现象与"缺库"长得**完全不一样**，几乎不可能往"少拷了两个 DLL"上想。
        ///
        /// 判据用 <c>hcanvas.dll</c> / <c>halcondl.dll</c> 作为"整套"的标志（二选一即可）：
        /// 它们分别对应窗口绘制与 DL 子系统，任何**完整**安装都带，
        /// 而"顺手拷两个"的目录一定不带。
        /// </summary>
        private static bool LooksComplete(string dir)
        {
            return File.Exists(Path.Combine(dir, "hcanvas.dll"))
                || File.Exists(Path.Combine(dir, "halcondl.dll"));
        }

        /// <summary>
        /// 把 <paramref name="dir"/> 挂进本进程的 DLL 搜索路径，并按需补设 HALCON 自己的环境变量。
        ///
        /// ★★ <c>HALCONROOT</c> / <c>HALCONARCH</c> 绝不能"拿 dir 往上两级"糊弄：
        ///   从<b>应用目录</b>（例如 <c>...\App\bin\Debug</c>）加载时，往上两级是 <c>...\App\bin</c>，
        ///   目录名 <c>Debug</c> 也不是架构名 —— 于是 HALCON 会照着
        ///   <c>%HALCONROOT%\bin\%HALCONARCH%</c> 去找 <c>calib/</c> 标定板描述文件与 license，
        ///   结果全部落空。症状是"库加载成功了，但标定板读不出来"，比直接崩还难排查。
        ///   所以：只有目录名真的是 <c>x64-win64</c> / <c>x86-win32</c> 才按安装布局反推；
        ///   否则另外去找真正的安装根。
        /// </summary>
        private static void ApplySearchPath(string dir, string arch)
        {
            // SetDllDirectory 把该目录插到"应用目录之后、系统目录之前"，
            // 不会把应用目录顶掉，所以本地自带的同名库仍然优先。
            SetDllDirectory(dir);

            string p = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (p.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + p);
            }

            string archName = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            bool standard = archName == "x64-win64" || archName == "x86-win32";

            // 只有标准安装布局（...\HALCON-24.11\bin\x64-win64）才能反推安装根；
            // 否则（例如库就放在应用目录里）得另外去找，不能拿"往上两级"糊弄。
            string root = standard ? ParentOfParent(dir) : FindInstallRoot(arch);

            if (root != null && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HALCONROOT")))
            {
                Environment.SetEnvironmentVariable("HALCONROOT", root);
            }

            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HALCONARCH")))
            {
                Environment.SetEnvironmentVariable("HALCONARCH", standard ? archName : arch);
            }
        }

        private static string ParentOfParent(string dir)
        {
            try
            {
                DirectoryInfo bin = Directory.GetParent(dir);
                if (bin != null && bin.Parent != null)
                {
                    return bin.Parent.FullName;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        /// <summary>
        /// 从注册表 / Program Files 找真正的 HALCON 安装根。
        /// 只在"原生库是从非标准目录加载"时才需要 —— 那时加载目录推不出安装根。
        /// </summary>
        private static string FindInstallRoot(string arch)
        {
            string other = arch == "x64-win64" ? "x86-win32" : "x64-win64";
            var candidates = new List<string>();
            foreach (string d in ProbeRegistry(arch)) { candidates.Add(d); }
            foreach (string d in ProbeRegistry(other)) { candidates.Add(d); }
            foreach (string d in ProbeProgramFiles(arch)) { candidates.Add(d); }
            foreach (string d in ProbeProgramFiles(other)) { candidates.Add(d); }

            foreach (string d in candidates)
            {
                if (File.Exists(Path.Combine(d, NativeDll)))
                {
                    return ParentOfParent(d);
                }
            }

            return null;
        }

        private static IEnumerable<string> ProbeRegistry(string arch)
        {
            var res = new List<string>();
            var bases = new string[]
            {
                @"SOFTWARE\MVTec\HALCON",
                @"SOFTWARE\WOW6432Node\MVTec\HALCON"
            };

            foreach (string b in bases)
            {
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(b))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        var found = new List<string>();
                        CollectInstallDirs(key, found, 0);
                        foreach (string d in found)
                        {
                            res.Add(Path.Combine(d, "bin", arch));
                        }
                    }
                }
                catch (Exception)
                {
                    // 注册表不可读/不存在：只是少一条候选，不影响别的路径
                }
            }

            return res;
        }

        private static void CollectInstallDirs(RegistryKey key, List<string> res, int depth)
        {
            foreach (string name in key.GetValueNames())
            {
                var s = key.GetValue(name) as string;
                if (!string.IsNullOrEmpty(s)
                    && s.IndexOf("MVTec", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (res.IndexOf(s) < 0)
                    {
                        res.Add(s);
                    }
                }
            }

            if (depth >= 2)
            {
                return;
            }

            foreach (string sub in key.GetSubKeyNames())
            {
                try
                {
                    using (RegistryKey k = key.OpenSubKey(sub))
                    {
                        if (k != null)
                        {
                            CollectInstallDirs(k, res, depth + 1);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static IEnumerable<string> ProbeProgramFiles(string arch)
        {
            var res = new List<string>();
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrEmpty(pf))
            {
                pf = @"C:\Program Files";
            }

            string mvtec = Path.Combine(pf, "MVTec");
            if (!Directory.Exists(mvtec))
            {
                return res;
            }

            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(mvtec, "HALCON-*");
            }
            catch (Exception)
            {
                return res;
            }

            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            Array.Reverse(dirs);   // 版本号倒序：新装的优先

            foreach (string d in dirs)
            {
                res.Add(Path.Combine(d, "bin", arch));
            }

            return res;
        }

        // ────────────────────────────────────────────────────────── 探测

        private static void EnsureProbed()
        {
            if (_probed)
            {
                return;
            }

            lock (Sync)
            {
                if (_probed)
                {
                    return;
                }

                _probed = true;
                Probe();
            }
        }

        private static void Probe()
        {
            EnsureNativeLibrary();

            try
            {
                // 最轻的一个算子：不做图像、不开窗口，纯粹验证原生库能进能出
                HTuple seconds;
                HOperatorSet.CountSeconds(out seconds);
                _available = true;
                return;
            }
            catch (Exception ex)
            {
                _lastError = ex;

                // 静态构造失败时真正的原因在 InnerException 里（TypeInitializationException）
                Exception root = ex;
                var tie = ex as TypeInitializationException;
                if (tie != null && tie.InnerException != null)
                {
                    root = tie.InnerException;
                }

                var sb = new StringBuilder();
                sb.Append("HALCON 原生库不可用：").Append(root.Message).Append("。");
                sb.Append("已按顺序扫描 ").Append(_scanned.Count).Append(" 个目录都没找到 ")
                  .Append(NativeDll).Append("：");
                int n = 0;
                foreach (string s in _scanned)
                {
                    if (n >= 4)
                    {
                        sb.Append(" …");
                        break;
                    }

                    sb.Append(n == 0 ? string.Empty : "；").Append(s);
                    n++;
                }

                sb.Append("。请装 HALCON 运行时，或把环境变量 HALCONROOT 指到安装目录")
                  .Append("（例如 C:\\Program Files\\MVTec\\HALCON-24.11-Progress-Steady）。");

                // ★ 半残拷贝要单独点出来：它会让"缺件"以一种完全不像缺件的方式表现
                if (_partialBins != null && _partialBins.Count > 0)
                {
                    sb.Append(" ★ 另外发现 ").Append(_partialBins.Count)
                      .Append(" 个目录里有 halcon.dll 但没有伴生模块（hcanvas.dll / halcondl.dll 都没有），已跳过：");
                    int m = 0;
                    foreach (string s in _partialBins)
                    {
                        if (m >= 2)
                        {
                            sb.Append(" …");
                            break;
                        }

                        sb.Append(m == 0 ? string.Empty : "；").Append(s);
                        m++;
                    }

                    sb.Append("。这种半残拷贝最坑：基础算子正常、--diag 也说就绪，"
                            + "但窗口建不出来（表现为 dump_window_image #1401）。把它删掉。");
                }

                // ★ 位数不匹配要单独点出来：它长得和"没装"一模一样（都是找不到 halcon.dll），
                //   但处置完全不同 —— 典型场景是 32 位宿主/设计器 + 只装了 x64 的 HALCON。
                string mismatch = ArchMismatchHint();
                if (mismatch != null)
                {
                    sb.Append(" ").Append(mismatch);
                }

                _failure = sb.ToString();
            }
        }

        /// <summary>
        /// 位数不匹配的专项提示。
        /// ★ 为什么必须单独说：它和"压根没装 HALCON"的表现<b>一模一样</b>（都是找不到 halcon.dll），
        ///   但处置完全不同 —— 32 位宿主 / XAML 设计器 + 只装了 x64 的 HALCON 是典型场景，
        ///   靠"重装一遍"是修不好的，只能改位数或补装 32 位运行时。
        /// </summary>
        private static string ArchMismatchHint()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            if (string.IsNullOrEmpty(pf))
            {
                pf = @"C:\Program Files";
            }

            string mvtec = Path.Combine(pf, "MVTec");
            if (!Directory.Exists(mvtec))
            {
                return null;
            }

            bool have64 = false;
            bool have32 = false;
            try
            {
                foreach (string d in Directory.GetDirectories(mvtec, "HALCON-*"))
                {
                    if (File.Exists(Path.Combine(d, "bin", "x64-win64", NativeDll)))
                    {
                        have64 = true;
                    }

                    if (File.Exists(Path.Combine(d, "bin", "x86-win32", NativeDll)))
                    {
                        have32 = true;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            if (IntPtr.Size == 8)
            {
                // 64 位进程：装了 x64 就不该走到这里；只有"只装了 32 位"才值得点破
                return !have64 && have32
                    ? "注意：机器上装的是【32 位】HALCON，而本进程是 64 位 —— 需要装 x64 版本。"
                    : null;
            }

            return have64 && !have32
                ? "注意：本进程是【32 位】，但机器上只装了 64 位 HALCON —— 32 位进程加载不了它。"
                  + "把工程改成 x64（宿主 / 设计器也要是 64 位），或补装 32 位 HALCON。"
                : null;
        }
    }
}
