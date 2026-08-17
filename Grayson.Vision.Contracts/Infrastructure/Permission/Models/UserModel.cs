//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 用户管理业务模型
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Infrastructure.Permission;
using System;

namespace Grayson.Vision.Contracts.Infrastructure.Permission.Models
{
    /// <summary>
    /// 用户账户业务模型
    /// </summary>
    public class UserModel : ViewModelBase
    {
        private string _userName;
        /// <summary>登录用户名</summary>
        public string UserName
        {
            get => _userName;
            set => Set(ref _userName, value);
        }

        private string _displayName;
        /// <summary>显示名称</summary>
        public string DisplayName
        {
            get => _displayName;
            set => Set(ref _displayName, value);
        }

        private UserRole _role;
        /// <summary>用户角色</summary>
        public UserRole Role
        {
            get => _role;
            set => Set(ref _role, value);
        }

        private string _roleDisplayName;
        /// <summary>角色显示名称</summary>
        public string RoleDisplayName
        {
            get => _roleDisplayName;
            set => Set(ref _roleDisplayName, value);
        }

        private bool _isEnabled;
        /// <summary>是否启用账户</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        private DateTime _createTime;
        /// <summary>创建时间</summary>
        public DateTime CreateTime
        {
            get => _createTime;
            set => Set(ref _createTime, value);
        }

        private DateTime _lastLoginTime;
        /// <summary>最后登录时间</summary>
        public DateTime LastLoginTime
        {
            get => _lastLoginTime;
            set => Set(ref _lastLoginTime, value);
        }

        private string _remark;
        /// <summary>备注</summary>
        public string Remark
        {
            get => _remark;
            set => Set(ref _remark, value);
        }
    }
}
