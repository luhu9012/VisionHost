//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: LoginViewModel.cs
// 创 建: 2026-07-18
// 修 改: 2026-07-28
// 说 明: 登录界面的 ViewModel
//===================================================================================

using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 登录界面 ViewModel
    /// </summary>
    public class LoginViewModel : ViewModelBase
    {
        private readonly IAuthenticationService _authService;
        private readonly Action<bool> _onLoginCompleted;

        public LoginViewModel(IAuthenticationService authService, Action<bool> onLoginCompleted)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _onLoginCompleted = onLoginCompleted;

            LoginCommand = new RelayCommand(async _ => await ExecuteLogin(), _ => CanLogin());
            ExitCommand = new RelayCommand(_ => Environment.Exit(0));
        }

        #region 双向绑定属性区域

        private string _userName = "admin";
        public string UserName
        {
            get => _userName;
            set
            {
                if (Set(ref _userName, value))
                {
                    ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private string _password = "admin123";
        public string Password
        {
            get => _password;
            set
            {
                if (Set(ref _password, value))
                {
                    ((RelayCommand)LoginCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isLoggingIn;
        public bool IsLoggingIn
        {
            get => _isLoggingIn;
            set => Set(ref _isLoggingIn, value);
        }

        private string _errorMessage;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => Set(ref _errorMessage, value);
        }

        private bool _rememberPassword;
        public bool RememberPassword
        {
            get => _rememberPassword;
            set => Set(ref _rememberPassword, value);
        }

        #endregion

        #region UI命令绑定区域

        public ICommand LoginCommand { get; }
        public ICommand ExitCommand { get; }

        #endregion

        #region 内部业务逻辑方法区域

        private bool CanLogin()
        {
            return !string.IsNullOrWhiteSpace(UserName) &&
                   !string.IsNullOrWhiteSpace(Password) &&
                   !IsLoggingIn;
        }

        private async Task ExecuteLogin()
        {
            ErrorMessage = string.Empty;
            IsLoggingIn = true;

            try
            {
                var result = await _authService.LoginAsync(UserName, Password);

                if (result.IsSuccess)
                {
                    GlobalData.Instance.CurrentUserName = result.UserName;
                    GlobalData.Instance.CurrentUserRole = result.Role;
                    GlobalData.Instance.RaiseUserLoggedIn(result.UserName);

                    if (RememberPassword)
                    {
                        // SavePassword(UserName, Password);
                    }
                    _onLoginCompleted?.Invoke(true);
                }
                else
                {
                    ErrorMessage = result.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"登录异常: {ex.Message}";
            }
            finally
            {
                IsLoggingIn = false;
            }
        }

        #endregion
    }
}