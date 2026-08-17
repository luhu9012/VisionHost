//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: LiteDbAuthenticationService.cs
// 说 明: 基于 LiteDB 仓储的用户认证服务实现
//===================================================================================

using System;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Infrastructure.Permission;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;

namespace Grayson.Vision.WpfUI.Service
{
    public class LiteDbAuthenticationService : IAuthenticationService
    {
        private readonly IUserRepository _userRepo;

        private bool _isAuthenticated;
        private string _currentUserName;
        private UserRole _currentUserRole;

        public bool IsAuthenticated => _isAuthenticated;
        public string CurrentUserName => _currentUserName;
        public UserRole CurrentUserRole => _currentUserRole;

        public LiteDbAuthenticationService()
        {
            // 通过仓储工厂创建 IUserRepository 实例
            _userRepo = StorageFactory.CreateUserRepository();
        }

        public async Task<AuthenticationResult> LoginAsync(string userName, string password)
        {
            var result = new AuthenticationResult();

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                result.ErrorMessage = "用户名或密码不能为空";
                return result;
            }

            // 放到 Task 中异步执行 LiteDB 读取，避免卡顿 UI
            return await Task.Run(() =>
            {
                try
                {
                    // 1. 根据用户名从 LiteDB 查询用户实体 (UserPo)
                    var userPo = _userRepo.GetByUsername(userName);
                    if (userPo == null)
                    {
                        result.ErrorMessage = "用户不存在";
                        return result;
                    }

                    // 2. 检查账户锁定状态
                    if (userPo.IsLocked)
                    {
                        result.ErrorMessage = "该账号已被锁定/禁用，请联系管理员";
                        return result;
                    }

                    // 3. 密码哈希校验 (根据项目情况处理，如使用 ComputeHash，明文仅作过渡)
                    string inputHash = ComputeHash(password);

                    // 兼容模式：若数据库存储的是未加密明文或匹配哈希
                    bool isPasswordValid = (userPo.PasswordHash == inputHash) || (userPo.PasswordHash == password);

                    if (!isPasswordValid)
                    {
                        result.ErrorMessage = "用户名或密码错误";
                        return result;
                    }

                    // 4. 直接使用 Contracts 层统一的 UserRole（与仓库层同义）
                    UserRole appRole = userPo.Role;

                    // 5. 更新认证状态
                    _isAuthenticated = true;
                    _currentUserName = userPo.Username;
                    _currentUserRole = appRole;

                    result.IsSuccess = true;
                    result.UserName = userPo.Username;
                    result.Role = appRole;

                    return result;
                }
                catch (Exception ex)
                {
                    result.ErrorMessage = $"数据库校验失败: {ex.Message}";
                    return result;
                }
            });
        }

        public void Logout()
        {
            _isAuthenticated = false;
            _currentUserName = null;
            _currentUserRole = UserRole.Operator;
        }

        #region 私有辅助方法

        /// <summary>
        /// 密码 SHA256/MD5 哈希算法实现 (可根据项目安全性需求调整)
        /// </summary>
        private string ComputeHash(string rawData)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));
                var builder = new System.Text.StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        #endregion
    }
}