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

        public static ICalibrationProfileRepository CreateCalibrationProfileRepository() => new LiteDbCalibrationProfileRepository();
        public static IRecipeRepository CreateRecipeRepository() => new LiteDbRecipeRepository();
        public static IDeviceConfigRepository CreateDeviceConfigRepository() => new LiteDbDeviceConfigRepository();
        public static IUserRepository CreateUserRepository() => new LiteDbUserRepository();
        public static IInspectionLogRepository CreateInspectionLogRepository() => new LiteDbInspectionLogRepository();
        public static IStationRepository CreateStationRepository() => new LiteDbStationRepository();
    }
}
