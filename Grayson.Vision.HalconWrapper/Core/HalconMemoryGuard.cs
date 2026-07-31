using System;
using System.Collections.Generic;
using Grayson.Vision.Common.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// Halcon资源托管守卫
    /// 统一管理临时HObject生命周期，批量释放，避免零散忘记Dispose造成内存持续上涨
    /// 业务单元执行完毕统一调用Clean，所有临时图像全部回收
    /// </summary>
    public class HalconMemoryGuard : IDisposable
    {
        /// <summary>托管的所有临时图像容器</summary>
        private readonly List<HObject> _managedImages = new List<HObject>();
        private bool _disposed = false;

        /// <summary>将需要管控的HObject加入托管列表</summary>
        public void Register(HObject hoObj)
        {
            if (hoObj == null || hoObj.IsInitialized() == false)
                return;
            _managedImages.Add(hoObj);
        }

        /// <summary>批量释放所有托管图像，清空列表</summary>
        public void CleanAll()
        {
            foreach (var img in _managedImages)
            {
                try
                {
                    if (img.IsInitialized())
                        img.Dispose();
                }
                catch (Exception ex)
                {
                    GlobalLogger.Warn($"Halcon图像释放异常:{ex.Message}", nameof(HalconMemoryGuard));
                }
            }
            _managedImages.Clear();
        }

        #region 标准IDisposable实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                CleanAll();
            }
            _disposed = true;
        }

        ~HalconMemoryGuard()
        {
            Dispose(false);
        }
        #endregion
    }
}