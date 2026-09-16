using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HalconDotNet;

namespace VisualCalibTool.Imaging
{
    /// <summary>
    /// ★ HALCON 句柄生命周期管理。
    ///
    /// HALCON 的 <see cref="HObject"/> 持有<b>原生内存</b>，不是 GC 能替你收拾的托管对象 ——
    /// 漏一个句柄就漏一块原生内存，跑几百次采样就会明显。本类提供统一的批量释放，
    /// 让调用点只关心"画什么"，不关心"谁 Dispose"。
    ///
    /// 用法：<c>using (var bag = new HalconHandleBag()) { HObject r = bag.Add(region); ... }</c>
    /// </summary>
    public sealed class HalconHandleBag : IDisposable
    {
        private readonly List<HObject> _handles = new List<HObject>();
        private bool _disposed;

        /// <summary>登记一个句柄，返回原对象（便于链式书写）。</summary>
        public HObject Add(HObject obj)
        {
            if (obj == null)
            {
                return null;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException("HalconHandleBag");
            }

            _handles.Add(obj);
            return obj;
        }

        public int Count
        {
            get { return _handles.Count; }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // 逆序释放：后建的对象通常引用先建的，先放引用者更安全
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                try
                {
                    var h = _handles[i];
                    if (h != null && h.IsInitialized())
                    {
                        h.Dispose();
                    }
                }
                catch (Exception)
                {
                    // 释放失败不抛：已经走到这里，抛出去只会掩盖真正的业务异常
                }
            }

            _handles.Clear();
        }
    }

    /// <summary>
    /// 帧数据 → HALCON 图像。★ 只在这一层出现 <c>HalconDotNet</c> 类型，
    /// 上层（算法 / 视图模型）永远拿到的是 <c>byte[]</c> 与像素坐标。
    /// </summary>
    public static class HalconFrameSource
    {
        /// <summary>
        /// 单通道 8 位灰度裸帧 → HImage。
        /// ★ 铁律：HALCON 原生内存可能在其 GC 期间被移动，不能跨调用持有裸指针；
        ///   <see cref="HImage"/> 的这个构造会<b>立即拷贝</b>像素数据，pin 只需包住构造调用本身。
        /// </summary>
        public static HImage FromRawGray(byte[] raw, int width, int height)
        {
            if (raw == null)
            {
                throw new ArgumentNullException("raw");
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("图像尺寸非法");
            }

            if (raw.Length < width * height)
            {
                throw new ArgumentException(string.Format(
                    CultureInfo.InvariantCulture,
                    "帧数据长度 {0} 小于 {1}x{2}={3}",
                    raw.Length, width, height, width * height));
            }

            GCHandle handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                return new HImage("byte", width, height, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>三通道交错 BGR 裸帧 → HImage（真机相机常用格式）。</summary>
        public static HImage FromRawBgr(byte[] raw, int width, int height)
        {
            if (raw == null)
            {
                throw new ArgumentNullException("raw");
            }

            if (raw.Length < width * height * 3)
            {
                throw new ArgumentException("BGR 帧数据长度不足");
            }

            GCHandle handle = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                // ★ 用 HImage 的实例算子形式（仓库已验证）：先建空图再填充，
                //   避免 HOperatorSet 静态版的 `out HObject` 与 HImage 类型不兼容。
                var rgb = new HImage();

                // GenImageInterleaved 12 参：pixelPointer, colorFormat, origW, origH,
                // alignment, type, imgW, imgH, startRow, startColumn, bitsPerChannel, bitShift
                // （startRow/startColumn 必须 ≥ 0；bitsPerChannel = -1 表示全部位使用）
                rgb.GenImageInterleaved(
                    handle.AddrOfPinnedObject(),
                    "bgr",
                    width, height,
                    -1, "byte",
                    width, height,
                    0, 0,
                    -1, 0);

                return rgb;
            }
            finally
            {
                handle.Free();
            }
        }

        /// <summary>把图像落盘（留帧复盘的最后一环）。失败返回 false，不抛。</summary>
        public static bool TrySaveImage(HObject image, string path, string format = "png")
        {
            if (image == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                HOperatorSet.WriteImage(image, format, 0, path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>编码成 PNG 字节（用于"把留帧交给仓储去落盘"这类分层需求）。</summary>
        public static byte[] TryEncodePng(HObject image)
        {
            if (image == null)
            {
                return null;
            }

            string temp = Path.Combine(Path.GetTempPath(), "vct_" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                HOperatorSet.WriteImage(image, "png", 0, temp);
                if (File.Exists(temp))
                {
                    return File.ReadAllBytes(temp);
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 读一张图（离线排查 / 会话回放用）。
        /// ★ 用 <c>new HImage(path)</c> 而不是 <c>HOperatorSet.ReadImage(out HObject)</c>：
        ///   后者返回的是基类引用，会让下游丢掉 <c>HImage</c> 的具体类型。
        /// </summary>
        public static HImage ReadImage(string path)
        {
            return new HImage(path);
        }

        /// <summary>取图像尺寸 [width, height]。</summary>
        public static bool TryGetSize(HObject image, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (image == null)
            {
                return false;
            }

            try
            {
                HTuple w, h;
                HOperatorSet.GetImageSize(image, out w, out h);
                width = w.I;
                height = h.I;
                return width > 0 && height > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
