//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: IAuthenticationService.cs
// 创 建: 2026-07-18
// 说 明: 认证服务接口 (Mock 实现,后续对接实际的用户管理系统)
//===================================================================================

using System.Threading.Tasks;
using Grayson.Vision.Contracts.Infrastructure.Permission;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 用户认证结果
    /// </summary>
    public class AuthenticationResult
    {
        public bool IsSuccess { get; set; }
        public string UserName { get; set; }
        public UserRole Role { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 认证服务接口
    /// </summary>
    public interface IAuthenticationService
    {
        /// <summary>
        /// 用户登录
        /// </summary>
        Task<AuthenticationResult> LoginAsync(string userName, string password);

        /// <summary>
        /// 用户登出
        /// </summary>
        void Logout();

        /// <summary>
        /// 检查是否已登录
        /// </summary>
        bool IsAuthenticated { get; }

        /// <summary>
        /// 获取当前用户名
        /// </summary>
        string CurrentUserName { get; }

        /// <summary>
        /// 获取当前用户权限
        /// </summary>
        UserRole CurrentUserRole { get; }
    }

    /// <summary>
    /// Mock 认证服务实现 (TODO: 对接实际的用户数据库或配置文件)
    /// </summary>
    public class MockAuthenticationService : IAuthenticationService
    {
        private bool _isAuthenticated;
        private string _currentUserName;
        private UserRole _currentUserRole;

        public bool IsAuthenticated => _isAuthenticated;
        public string CurrentUserName => _currentUserName;
        public UserRole CurrentUserRole => _currentUserRole;

        /// <summary>
        /// Mock 登录实现 - 简单的用户名密码验证
        /// </summary>
        public async Task<AuthenticationResult> LoginAsync(string userName, string password)
        {
            // 模拟网络延迟
            await Task.Delay(500);

            // TODO: 对接实际的用户管理系统,验证用户名密码
            // 这里使用简单的 Mock 数据
            var result = new AuthenticationResult();

            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            {
                result.ErrorMessage = "用户名或密码不能为空";
                return result;
            }

            // Mock 用户数据
            // 实际项目应该从数据库或配置文件读取
            if (userName == "admin" && password == "admin123")
            {
                result.IsSuccess = true;
                result.UserName = userName;
                result.Role = UserRole.Administrator;

                _isAuthenticated = true;
                _currentUserName = userName;
                _currentUserRole = UserRole.Administrator;
            }
            else if (userName == "engineer" && password == "eng123")
            {
                result.IsSuccess = true;
                result.UserName = userName;
                result.Role = UserRole.Engineer;

                _isAuthenticated = true;
                _currentUserName = userName;
                _currentUserRole = UserRole.Engineer;
            }
            else if (userName == "operator" && password == "op123")
            {
                result.IsSuccess = true;
                result.UserName = userName;
                result.Role = UserRole.Operator;

                _isAuthenticated = true;
                _currentUserName = userName;
                _currentUserRole = UserRole.Operator;
            }
            else
            {
                result.ErrorMessage = "用户名或密码错误";
            }

            return result;
        }

        /// <summary>
        /// 用户登出
        /// </summary>
        public void Logout()
        {
            _isAuthenticated = false;
            _currentUserName = null;
            _currentUserRole = UserRole.Operator;
        }
    }
}
