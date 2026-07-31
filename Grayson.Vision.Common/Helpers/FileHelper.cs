using Grayson.Vision.Common.Logging;
using System;
using System.IO;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 文件、文件夹路径通用工具
    /// 封装目录创建、文件删除、过期文件清理、路径拼接，全项目统一调用
    /// 适配日志、NG图片、配方文件、模板文件管理
    /// </summary>
    public static class FileHelper
    {
        /// <summary>
        /// 路径不存在则创建目录，存在无操作
        /// </summary>
        public static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// 删除指定文件，文件不存在不抛异常
        /// </summary>
        public static void SafeDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.Warn($"文件删除失败：{filePath}，{ex.Message}", nameof(FileHelper));
            }
        }

        /// <summary>
        /// 清理文件夹内N天前的过期文件，用于自动清理老旧NG图片、日志
        /// </summary>
        /// <param name="folderPath">目标文件夹</param>
        /// <param name="keepDay">保留天数，超出天数删除</param>
        public static void CleanExpiredFiles(string folderPath, int keepDay)
        {
            if (!Directory.Exists(folderPath))
                return;

            DateTime expireTime = DateTime.Now.AddDays(-keepDay);
            string[] allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

            foreach (string file in allFiles)
            {
                try
                {
                    FileInfo fi = new FileInfo(file);
                    // 按文件最后修改时间判断过期
                    if (fi.LastWriteTime < expireTime)
                    {
                        fi.Delete();
                    }
                }
                catch
                {
                    // 单个文件删除失败不阻断整体清理
                }
            }
        }

        /// <summary>
        /// 获取程序运行根目录（exe所在目录）
        /// </summary>
        public static string GetAppBasePath()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 拼接完整路径，自动兼容正反斜杠
        /// </summary>
        public static string CombinePath(params string[] paths)
        {
            return Path.Combine(paths);
        }
    }
}