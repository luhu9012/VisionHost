// StorageFactory.cs
using Grayson.Vision.Repository.Core;
using Grayson.Vision.Repository.Implementations;
using Grayson.Vision.Repository.Interfaces;

namespace Grayson.Vision.Repository
{
    public static class StorageFactory
    {
        // StorageFactory.cs 中的扩展
        public static void Initialize(string dbPath = null)
        {
            // 1. 初始化数据库上下文
            Core.DbContext.Initialize(dbPath);

            // 2. 检查并种子化默认内置用户
            InitDefaultUsers();
        }

        private static void InitDefaultUsers()
        {
            var userRepo = CreateUserRepository();

            // 初始化 admin 账号
            if (userRepo.GetByUsername("admin") == null)
            {
                userRepo.Insert(new Entities.UserPo
                {
                    Username = "admin",
                    PasswordHash = "admin123", // 对应密码明文或密文
                    RealName = "系统管理员",
                    Role = Grayson.Vision.Contracts.Infrastructure.Permission.UserRole.Administrator,
                    IsLocked = false
                });
            }

            // 初始化 engineer 账号
            if (userRepo.GetByUsername("engineer") == null)
            {
                userRepo.Insert(new Entities.UserPo
                {
                    Username = "engineer",
                    PasswordHash = "eng123",
                    RealName = "视觉工程师",
                    Role = Grayson.Vision.Contracts.Infrastructure.Permission.UserRole.Engineer,
                    IsLocked = false
                });
            }

            // 初始化 operator 账号
            if (userRepo.GetByUsername("operator") == null)
            {
                userRepo.Insert(new Entities.UserPo
                {
                    Username = "operator",
                    PasswordHash = "op123",
                    RealName = "产线操作员",
                    Role = Grayson.Vision.Contracts.Infrastructure.Permission.UserRole.Operator,
                    IsLocked = false
                });
            }
        }

        /// <summary>
        /// ★2026-09-15 存储统一：标定档案改走 JSON（&lt;AppBase&gt;\Config\Calibrations\*.json）。
        /// 原因：该目录早已是【校验台产物聚合】与【节点矩阵候选】的唯一读取源，而写入方却走 LiteDB；
        /// 两套存储导致"库里明明有「吸嘴1_旋转中心 e」，校验台却报 e/O 未标"（空目录→聚合退化为单档案）。
        /// 统一后写读同源。LiteDbCalibrationProfileRepository 类保留以备回退，但不再由工厂产出。
        /// </summary>
        public static ICalibrationProfileRepository CreateCalibrationProfileRepository() => new JsonCalibrationProfileRepository();
        public static IRecipeRepository CreateRecipeRepository() => new LiteDbRecipeRepository();
        public static IDeviceConfigRepository CreateDeviceConfigRepository() => new LiteDbDeviceConfigRepository();
        public static IUserRepository CreateUserRepository() => new LiteDbUserRepository();
        public static IInspectionLogRepository CreateInspectionLogRepository() => new LiteDbInspectionLogRepository();
        public static IStationRepository CreateStationRepository() => new LiteDbStationRepository();
    }
}
