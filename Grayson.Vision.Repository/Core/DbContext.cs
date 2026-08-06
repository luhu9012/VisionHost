using LiteDB;
using System;
using System.IO;

namespace Grayson.Vision.Repository.Core
{
    public static class DbContext
    {
        private static string _dbPath;

        public static void Initialize(string customDbPath = null)
        {
            if (!string.IsNullOrEmpty(customDbPath))
            {
                _dbPath = customDbPath;
            }
            else
            {
                var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
                _dbPath = Path.Combine(dataDir, "GraysonVision.db");
            }

            // 【关键修复】确保数据库文件的父级文件夹一定存在
            var parentDir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }
        }

        public static LiteDatabase GetDatabase()
        {
            if (string.IsNullOrEmpty(_dbPath))
            {
                Initialize();
            }

            var connectionString = new ConnectionString(_dbPath)
            {
                Connection = ConnectionType.Shared
            };

            return new LiteDatabase(connectionString);
        }
    }
}