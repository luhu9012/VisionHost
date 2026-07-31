using System;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Common.Logging;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 多线程、异步任务通用工具
    /// 适配多工位并行执行、硬件异步读写、UI调度安全
    /// 提供锁封装、安全异步执行、超时控制
    /// </summary>
    public static class ThreadSafeHelper
    {
        /// <summary>
        /// 带超时的线程锁，防止死锁永久等待
        /// </summary>
        /// <param name="lockObj">锁对象</param>
        /// <param name="timeoutMs">等待超时毫秒</param>
        /// <param name="action">拿到锁后执行逻辑</param>
        /// <returns>true拿到锁执行；false超时未获取锁</returns>
        public static bool LockWithTimeout(object lockObj, int timeoutMs, Action action)
        {
            bool acquire = Monitor.TryEnter(lockObj, timeoutMs);
            if (!acquire)
            {
                GlobalLogger.Warn($"线程获取锁超时{timeoutMs}ms", nameof(ThreadSafeHelper));
                return false;
            }
            try
            {
                action.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("锁内业务执行异常", ex, nameof(ThreadSafeHelper));
                return false;
            }
            finally
            {
                Monitor.Exit(lockObj);
            }
        }

        /// <summary>
        /// 安全启动后台任务，统一异常捕获，防止工位线程崩溃无日志
        /// </summary>
        public static void RunSafeTask(Action taskAction, string taskName = "BackgroundTask")
        {
            Task.Run(() =>
            {
                try
                {
                    taskAction.Invoke();
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error($"后台任务[{taskName}]异常终止", ex, nameof(ThreadSafeHelper));
                }
            });
        }
    }
}