using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace VisualCalibTool.App
{
    /// <summary>
    /// 独立模式宿主。
    /// ★ 这里刻意只做"起壳"这一件事：设备环境、依赖装配全在类库内部，
    ///   宿主不引 Plugins.*、不引 HalconWrapper —— 保证"独立程序"与"嵌入控件"跑的是同一套逻辑。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// ★ 静态构造：必须在<b>任何</b> halcondotnet 类型被触碰之前把原生库挂上。
        ///
        /// 为什么是静态构造而不是实例构造：WPF 自动生成的 <c>Main</c> 会先建 <see cref="App"/>，
        /// 而 XAML 里的 <c>HSmartWindowControlWPF</c> 一实例化就可能去加载原生库 ——
        /// 等到实例构造里再挂就晚了。静态构造在类型首次被用到时执行，够早。
        ///
        /// 挂不上也不会抛：<see cref="Imaging.HalconRuntime"/> 只是记下原因，
        /// 图像链路会降级提示，不会让程序在 Loaded 里炸掉。
        /// </summary>
        static App()
        {
            Imaging.HalconRuntime.EnsureNativeLibrary();
        }

        public App()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        /// <summary>
        /// 支持 <c>--diag</c>：只做环境自检、不建窗口。
        /// 现场遇到「一打开就崩」「图像出不来」时，命令行跑
        /// <c>VisualCalibTool.App.exe --diag</c> 就能拿到一段能照着做的话，不用去猜。
        /// 加 <c>--quiet</c> 则只落文件、不弹框（自动化用）。
        /// </summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            bool diag = false;
            bool quiet = false;
            if (e.Args != null)
            {
                foreach (string a in e.Args)
                {
                    if (a == "--diag" || a == "-diag")
                    {
                        diag = true;
                    }
                    else if (a == "--quiet" || a == "-quiet")
                    {
                        quiet = true;
                    }
                }
            }

            if (diag)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                RunDiagnostics(quiet);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        /// <summary>把环境状态写成一段人话：能用于判断"缺什么、去哪补"。</summary>
        private static void RunDiagnostics(bool quiet)
        {
            var sb = new StringBuilder();
            sb.AppendLine("VisualCalibTool 环境诊断");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("进程位数：" + (IntPtr.Size == 8 ? "x64（HALCON 只提供 x64 原生库）" : "x86 —— 与 HALCON 不匹配！"));
            sb.AppendLine("应用目录：" + AppDomain.CurrentDomain.BaseDirectory);
            sb.AppendLine();
            sb.AppendLine("HALCON：" + Imaging.HalconRuntime.StatusText);
            sb.AppendLine();

            string root = Environment.GetEnvironmentVariable("HALCONROOT");
            sb.AppendLine("HALCONROOT = " + (string.IsNullOrEmpty(root) ? "（未设置）" : root));
            string arch = Environment.GetEnvironmentVariable("HALCONARCH");
            sb.AppendLine("HALCONARCH = " + (string.IsNullOrEmpty(arch) ? "（未设置）" : arch));

            Exception last = Imaging.HalconRuntime.LastError;
            if (last != null)
            {
                sb.AppendLine();
                sb.AppendLine("原始异常：");
                sb.AppendLine(last.ToString());
            }

            string text = sb.ToString();
            try
            {
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "halcon-diag.txt"),
                    text, Encoding.UTF8);
            }
            catch (Exception)
            {
                // 写不下就算了，弹框/日志还能看到
            }

            if (!quiet)
            {
                MessageBox.Show(text, "VisualCalibTool 环境诊断",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // UI 线程异常：给出可读信息而不是静默崩掉（标定现场最怕"点一下没了"）
            ShowFatal("界面线程异常", e.Exception);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowFatal("后台线程异常", e.ExceptionObject as Exception);
        }

        private static void ShowFatal(string title, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine();
            if (ex != null)
            {
                sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                sb.AppendLine();
                sb.AppendLine(ex.StackTrace);
            }
            else
            {
                sb.AppendLine("（无异常对象）");
            }

            // ★ 先落盘再弹框：弹框是给现场的人看的，crash.log 是给"看不到屏幕的人"看的
            //   （无头冒烟、事后复盘、贴给别人）。这两件事以前只有前者有，于是
            //   smoke_calib_app.py 里那段"进程提前退出就读 crash.log"永远读不到东西 ——
            //   注释里写了、代码里没有，属于哑接缝。
            //   写文件失败绝不能影响弹框（可能没有写权限），所以整个包在 try 里。
            string path = null;
            try
            {
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    + Environment.NewLine + sb.ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                path = null;
            }

            string text = path == null
                ? sb.ToString()
                : sb.ToString() + Environment.NewLine + "（已写入 " + path + "）";

            MessageBox.Show(text, "VisualCalibTool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
