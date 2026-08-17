using Grayson.Vision.Contracts.Station.Services;
using Grayson.Vision.Core.Station;
using Grayson.Vision.Repository;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Grayson.Vison.FlowEdit
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 全局 StationHost 运行时（Core 唯一入口）。
        /// </summary>
        /// <summary>
        /// 全局 StationHost 运行时（Core 唯一入口）。
        /// 当 FlowEdit 独立启动时由 OnStartup 初始化；
        /// 当作为 UserControl 嵌入 WpfUI 时，由宿主 App 注入，避免重复创建设备池。
        /// </summary>
        public static IStationHostRuntime StationHostRuntime { get; set; }

        protected override async void OnStartup(StartupEventArgs e)
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "GraysonVision.db");
            StorageFactory.Initialize(dbPath);

            StationHostRuntime = new StationHostRuntime();
            await StationHostRuntime.InitializeAsync();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StationHostRuntime?.Dispose();
            base.OnExit(e);
        }
    }
}
