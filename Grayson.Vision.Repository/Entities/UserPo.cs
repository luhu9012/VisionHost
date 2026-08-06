// Entities/UserPo.cs
using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Repository.Core;

namespace Grayson.Vision.Repository.Entities
{
    /// <summary>
    /// 用户账号持久化表
    /// </summary>
    public class UserPo : BaseEntity
    {
        public string Username { get; set; }
        public string PasswordHash { get; set; }       // 加密后的密码
        public string RealName { get; set; }
        public UserRole Role { get; set; }             // 直接引用 Contracts 的 UserRole 枚举
        public bool IsLocked { get; set; }
    }
}