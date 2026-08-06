using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Client
{
    /// <summary>
    /// WorkerHost 独立进程生命周期管理器
    /// 负责启动、拉起、监控、杀死外部 WorkerHost.exe 进程
    /// </summary>
    public class StationProcessManager : IDisposable
    {
        // 记录当前绑定的工位 ID 与对应的 System.Diagnostics.Process 实例
        private readonly ConcurrentDictionary<string, Process> _runningProcesses
            = new ConcurrentDictionary<string, Process>();

        private readonly string _workerHostExePath;

        public StationProcessManager()
        {
            // WorkerHost.exe 的相对路径 (发布输出目录)
            _workerHostExePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkerHost", "Grayson.Vision.WorkerHost.exe");
        }

        /// <summary>
        /// 启动指定工位的外部进程
        /// </summary>
        public bool StartWorkerProcess(string stationId)
        {
            if (_runningProcesses.TryGetValue(stationId, out var existingProc) && !existingProc.HasExited)
            {
                return true; // 已经运行中
            }

            if (!File.Exists(_workerHostExePath))
            {
                throw new FileNotFoundException($"未找到 WorkerHost 宿主可执行程序: {_workerHostExePath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _workerHostExePath,
                Arguments = $"--stationId={stationId}", // 传入工位 ID 参数
                UseShellExecute = false,
                CreateNoWindow = false, // 调试阶段可设为 false 显示控制台黑框，生产态设为 true 后台运行
            };

            var process = new Process { StartInfo = startInfo };
            if (process.Start())
            {
                _runningProcesses[stationId] = process;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 停止/杀死指定工位进程
        /// </summary>
        public void StopWorkerProcess(string stationId)
        {
            if (_runningProcesses.TryRemove(stationId, out var process))
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.Dispose();
                }
            }
        }

        public void Dispose()
        {
            foreach (var process in _runningProcesses.Values)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.Dispose();
                }
            }
            _runningProcesses.Clear();
        }
    }
}