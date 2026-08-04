using System;
using System.Linq;
using Grayson.Vision.Contracts.Business.Factories;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Business.Engine;
using Grayson.Vision.Contracts.Logging;
using Grayson.Vision.Core;
using Newtonsoft.Json;

namespace Grayson.Vision.WorkerHost
{
    class Program
    {
        private static StationWorker _worker;
        private static NamedPipeIpcServer _ipcServer;

        static void Main(string[] args)
        {
            // 1. 解析命令行启动参数 (如: --stationId=Station_01)
            string stationId = ParseArgument(args, "--stationId") ?? "Station_01";

            Console.Title = $"Grayson Vision Worker Host - [{stationId}]";
            Console.WriteLine("=================================================");
            Console.WriteLine($"  Grayson Vision 独立工位 Worker 运行宿主进程");
            Console.WriteLine($"  当前分配工位 ID: {stationId}");
            Console.WriteLine("=================================================");

            try
            {
                // 2. 初始化算子工厂 (扫描节点程序集，确保可动态执行)
                Console.WriteLine("[System] 正在扫描并注册 Vision 算子节点...");
                NodeFactory.Initialize();

                // 3. 实例化工位核心业务 Worker
                _worker = new StationWorker(stationId);

                // 4. 实例化并启动 IPC 服务端，接收来自 WpfUI 的管控指令
                _ipcServer = new NamedPipeIpcServer(stationId);

                // 注册 IPC 指令监听
                _ipcServer.OnCommandReceived += HandleIpcCommand;

                // 订阅 Worker 业务事件，并将其异步广播给 UI 客户端
                _worker.OnStateChanged += (s, state) =>
                {
                    Console.WriteLine($"[Worker State] 工位状态更新为: {state}");
                    _ipcServer.BroadcastEvent("OnStateChanged", state);
                };

                _worker.OnFrameRendered += (s, frameArgs) =>
                {
                    Console.WriteLine($"[Frame Render] 节点 [{frameArgs.NodeId}] 完成渲染，准备推送 UI...");
                    _ipcServer.BroadcastEvent("OnFrameRendered", frameArgs);
                };

                // 启动管道服务
                _ipcServer.Start();

                Console.WriteLine("\n[Ready] WorkerHost 已完全就绪，等待 UI 连接与指令控制...");
                Console.WriteLine("按 'Q' 键退出该 Worker 进程。\n");

                // 保持控制台主线程运行
                while (true)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Q)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Fatal Error] WorkerHost 发生致命错误: {ex.Message}\n{ex.StackTrace}");
                Console.ResetColor();
                Console.ReadLine();
            }
            finally
            {
                // 释放资源
                _ipcServer?.Dispose();
                _worker?.Dispose();
            }
        }

        /// <summary>
        /// 处理 UI 通过 IPC 管道发来的命令
        /// </summary>
        private static async void HandleIpcCommand(string action, string payloadJson)
        {
            Console.WriteLine($"[IPC Command Received] Action: {action}");

            try
            {
                switch (action)
                {
                    case "LoadRecipe":
                        var recipe = JsonConvert.DeserializeObject<FlowProcessModel>(payloadJson);
                        await _worker.LoadRecipeAsync(recipe);
                        break;

                    case "Start":
                        await _worker.StartAsync();
                        break;

                    case "Stop":
                        await _worker.StopAsync();
                        break;

                    case "TriggerOnce":
                        string batchId = payloadJson;
                        await _worker.TriggerOnceAsync(batchId);
                        break;
                    case "StepNode":
                        var node = JsonConvert.DeserializeObject<FlowNodeBase>(payloadJson);
                        await _worker.StepNodeAsync(node);
                        break;

                    default:
                        Console.WriteLine($"[IPC Command Warning] 无法识别的 Action 命令: {action}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Command Execute Error] 执行指令 [{action}] 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 解析命令行参数帮助函数
        /// </summary>
        private static string ParseArgument(string[] args, string prefix)
        {
            var arg = args.FirstOrDefault(a => a.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase));
            if (arg != null)
            {
                return arg.Substring(prefix.Length + 1).Trim('"');
            }
            return null;
        }
    }
}