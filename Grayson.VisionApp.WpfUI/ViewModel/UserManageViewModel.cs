//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: UserManageViewModel.cs
// 创 建: 2026-07-18
// 说 明: 用户管理界面的 ViewModel，包含用户增删改查和权限分配功能
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 用户信息数据模型
    /// </summary>
    public class UserModel : ViewModelBase
    {
        private string _userName;
        public string UserName
        {
            get => _userName;
            set => Set(ref _userName, value);
        }

        private string _displayName;
        public string DisplayName
        {
            get => _displayName;
            set => Set(ref _displayName, value);
        }

        private UserRole _role;
        public UserRole Role
        {
            get => _role;
            set => Set(ref _role, value);
        }

        private string _roleDisplayName;
        public string RoleDisplayName
        {
            get => _roleDisplayName;
            set => Set(ref _roleDisplayName, value);
        }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        private DateTime _createTime;
        public DateTime CreateTime
        {
            get => _createTime;
            set => Set(ref _createTime, value);
        }

        private DateTime _lastLoginTime;
        public DateTime LastLoginTime
        {
            get => _lastLoginTime;
            set => Set(ref _lastLoginTime, value);
        }

        private string _remark;
        public string Remark
        {
            get => _remark;
            set => Set(ref _remark, value);
        }
    }

    /// <summary>
    /// 用户管理 ViewModel，提供用户增删改查和权限分配功能
    /// </summary>
    public class UserManageViewModel : ViewModelBase
    {
        public UserManageViewModel()
        {
            AddUserCommand = new RelayCommand(_ => OnAddUser());
            EditUserCommand = new RelayCommand(OnEditUser);
            DeleteUserCommand = new RelayCommand(OnDeleteUser);
            ResetPasswordCommand = new RelayCommand(OnResetPassword);
            EnableUserCommand = new RelayCommand(OnEnableUser);
            DisableUserCommand = new RelayCommand(OnDisableUser);
            SaveEditCommand = new RelayCommand(_ => OnSaveEdit());
            CancelEditCommand = new RelayCommand(_ => OnCancelEdit());
            ExportLogCommand = new RelayCommand(_ => OnExportLog());

            RoleOptions = new ObservableCollection<string> { "全部", "操作员", "工程师", "管理员" };
            SelectedRoleFilter = "全部";

            InitializeMockData();
            ApplyFilter();
        }

        #region 属性

        private ObservableCollection<UserModel> _allUsers;

        private ObservableCollection<UserModel> _userList;
        /// <summary>过滤后的用户列表</summary>
        public ObservableCollection<UserModel> UserList
        {
            get => _userList;
            set => Set(ref _userList, value);
        }

        private UserModel _selectedUser;
        public UserModel SelectedUser
        {
            get => _selectedUser;
            set => Set(ref _selectedUser, value);
        }

        private bool _isEditing;
        /// <summary>是否处于编辑/新增模式</summary>
        public bool IsEditing
        {
            get => _isEditing;
            set => Set(ref _isEditing, value);
        }

        private bool _isAddingNew;
        public bool IsAddingNew
        {
            get => _isAddingNew;
            set => Set(ref _isAddingNew, value);
        }

        // 编辑表单字段
        private string _editUserName;
        public string EditUserName
        {
            get => _editUserName;
            set => Set(ref _editUserName, value);
        }

        private string _editDisplayName;
        public string EditDisplayName
        {
            get => _editDisplayName;
            set => Set(ref _editDisplayName, value);
        }

        private UserRole _editRole;
        public UserRole EditRole
        {
            get => _editRole;
            set
            {
                if (Set(ref _editRole, value))
                {
                    OnPropertyChanged(nameof(EditRoleOperator));
                    OnPropertyChanged(nameof(EditRoleEngineer));
                    OnPropertyChanged(nameof(EditRoleAdmin));
                }
            }
        }

        public bool EditRoleOperator
        {
            get => EditRole == UserRole.Operator;
            set { if (value) EditRole = UserRole.Operator; }
        }
        public bool EditRoleEngineer
        {
            get => EditRole == UserRole.Engineer;
            set { if (value) EditRole = UserRole.Engineer; }
        }
        public bool EditRoleAdmin
        {
            get => EditRole == UserRole.Administrator;
            set { if (value) EditRole = UserRole.Administrator; }
        }

        private bool _editIsEnabled;
        public bool EditIsEnabled
        {
            get => _editIsEnabled;
            set => Set(ref _editIsEnabled, value);
        }

        private string _editRemark;
        public string EditRemark
        {
            get => _editRemark;
            set => Set(ref _editRemark, value);
        }

        // 角色筛选
        public ObservableCollection<string> RoleOptions { get; }

        private string _selectedRoleFilter;
        public string SelectedRoleFilter
        {
            get => _selectedRoleFilter;
            set
            {
                if (Set(ref _selectedRoleFilter, value))
                {
                    ApplyFilter();
                }
            }
        }

        private string _searchKeyword;
        public string SearchKeyword
        {
            get => _searchKeyword;
            set
            {
                if (Set(ref _searchKeyword, value))
                {
                    ApplyFilter();
                }
            }
        }

        // 统计
        public int TotalUsers => _allUsers?.Count ?? 0;
        public int AdminCount => _allUsers?.Count(u => u.Role == UserRole.Administrator) ?? 0;
        public int EngineerCount => _allUsers?.Count(u => u.Role == UserRole.Engineer) ?? 0;
        public int OperatorCount => _allUsers?.Count(u => u.Role == UserRole.Operator) ?? 0;

        // 操作日志
        private ObservableCollection<string> _operationLogs;
        public ObservableCollection<string> OperationLogs
        {
            get => _operationLogs;
            set => Set(ref _operationLogs, value);
        }

        #endregion

        #region 命令

        public ICommand AddUserCommand { get; }
        public ICommand EditUserCommand { get; }
        public ICommand DeleteUserCommand { get; }
        public ICommand ResetPasswordCommand { get; }
        public ICommand EnableUserCommand { get; }
        public ICommand DisableUserCommand { get; }
        public ICommand SaveEditCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand ExportLogCommand { get; }

        #endregion

        #region 方法

        private void InitializeMockData()
        {
            _allUsers = new ObservableCollection<UserModel>
            {
                new UserModel
                {
                    UserName = "admin",
                    DisplayName = "系统管理员",
                    Role = UserRole.Administrator,
                    RoleDisplayName = "管理员",
                    IsEnabled = true,
                    CreateTime = new DateTime(2024, 1, 1),
                    LastLoginTime = DateTime.Now.AddMinutes(-30),
                    Remark = "系统内置管理员账号"
                },
                new UserModel
                {
                    UserName = "engineer",
                    DisplayName = "张工",
                    Role = UserRole.Engineer,
                    RoleDisplayName = "工程师",
                    IsEnabled = true,
                    CreateTime = new DateTime(2024, 3, 10),
                    LastLoginTime = DateTime.Now.AddHours(-2),
                    Remark = "视觉算法工程师"
                },
                new UserModel
                {
                    UserName = "engineer2",
                    DisplayName = "李工",
                    Role = UserRole.Engineer,
                    RoleDisplayName = "工程师",
                    IsEnabled = true,
                    CreateTime = new DateTime(2024, 5, 20),
                    LastLoginTime = DateTime.Now.AddDays(-1),
                    Remark = "机械工程师"
                },
                new UserModel
                {
                    UserName = "operator",
                    DisplayName = "王操作员",
                    Role = UserRole.Operator,
                    RoleDisplayName = "操作员",
                    IsEnabled = true,
                    CreateTime = new DateTime(2024, 6, 1),
                    LastLoginTime = DateTime.Now.AddHours(-5),
                    Remark = "A班操作员"
                },
                new UserModel
                {
                    UserName = "op2",
                    DisplayName = "陈操作员",
                    Role = UserRole.Operator,
                    RoleDisplayName = "操作员",
                    IsEnabled = true,
                    CreateTime = new DateTime(2024, 7, 15),
                    LastLoginTime = DateTime.Now.AddDays(-2),
                    Remark = "B班操作员"
                },
                new UserModel
                {
                    UserName = "op_old",
                    DisplayName = "刘操作员（离职）",
                    Role = UserRole.Operator,
                    RoleDisplayName = "操作员",
                    IsEnabled = false,
                    CreateTime = new DateTime(2023, 9, 1),
                    LastLoginTime = new DateTime(2024, 12, 31),
                    Remark = "已离职，账号已禁用"
                }
            };

            OperationLogs = new ObservableCollection<string>
            {
                $"[{DateTime.Now.AddMinutes(-10):HH:mm:ss}] admin 登录系统",
                $"[{DateTime.Now.AddMinutes(-8):HH:mm:ss}] admin 查看用户管理模块",
                $"[{DateTime.Now.AddDays(-1):yyyy-MM-dd HH:mm:ss}] admin 禁用用户 op_old",
                $"[{DateTime.Now.AddDays(-3):yyyy-MM-dd HH:mm:ss}] admin 新增用户 op2",
                $"[{DateTime.Now.AddDays(-5):yyyy-MM-dd HH:mm:ss}] admin 重置用户 engineer 密码"
            };
        }

        private void ApplyFilter()
        {
            if (_allUsers == null) return;

            var filtered = _allUsers.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchKeyword))
            {
                filtered = filtered.Where(u =>
                    u.UserName.Contains(SearchKeyword) ||
                    u.DisplayName.Contains(SearchKeyword));
            }

            if (SelectedRoleFilter != "全部")
            {
                var roleMap = new Dictionary<string, UserRole>
                {
                    { "操作员", UserRole.Operator },
                    { "工程师", UserRole.Engineer },
                    { "管理员", UserRole.Administrator }
                };
                if (roleMap.TryGetValue(SelectedRoleFilter, out var role))
                    filtered = filtered.Where(u => u.Role == role);
            }

            UserList = new ObservableCollection<UserModel>(filtered);
        }

        private void OnAddUser()
        {
            IsAddingNew = true;
            IsEditing = true;
            EditUserName = string.Empty;
            EditDisplayName = string.Empty;
            EditRole = UserRole.Operator;
            EditIsEnabled = true;
            EditRemark = string.Empty;
        }

        private void OnEditUser(object parameter)
        {
            if (parameter is UserModel user)
            {
                SelectedUser = user;
                IsAddingNew = false;
                IsEditing = true;
                EditUserName = user.UserName;
                EditDisplayName = user.DisplayName;
                EditRole = user.Role;
                EditIsEnabled = user.IsEnabled;
                EditRemark = user.Remark;
            }
        }

        private void OnSaveEdit()
        {
            if (string.IsNullOrWhiteSpace(EditUserName))
            {
                ShowWarning("用户名不能为空！");
                return;
            }
            if (string.IsNullOrWhiteSpace(EditDisplayName))
            {
                ShowWarning("显示名称不能为空！");
                return;
            }

            string roleDisplay = EditRole == UserRole.Administrator ? "管理员"
                               : EditRole == UserRole.Engineer ? "工程师" : "操作员";

            if (IsAddingNew)
            {
                if (_allUsers.Any(u => u.UserName == EditUserName))
                {
                    ShowWarning($"用户名 '{EditUserName}' 已存在！");
                    return;
                }
                var newUser = new UserModel
                {
                    UserName = EditUserName,
                    DisplayName = EditDisplayName,
                    Role = EditRole,
                    RoleDisplayName = roleDisplay,
                    IsEnabled = EditIsEnabled,
                    CreateTime = DateTime.Now,
                    LastLoginTime = DateTime.MinValue,
                    Remark = EditRemark
                };
                _allUsers.Add(newUser);
                AddLog($"admin 新增用户 {EditUserName}（{roleDisplay}）");
                ShowInfo($"用户 '{EditDisplayName}' 创建成功，初始密码: 123456");
            }
            else if (SelectedUser != null)
            {
                SelectedUser.DisplayName = EditDisplayName;
                SelectedUser.Role = EditRole;
                SelectedUser.RoleDisplayName = roleDisplay;
                SelectedUser.IsEnabled = EditIsEnabled;
                SelectedUser.Remark = EditRemark;
                AddLog($"admin 修改用户 {SelectedUser.UserName} 信息");
                ShowInfo("用户信息保存成功！");
            }

            IsEditing = false;
            ApplyFilter();
            UpdateStatistics();
        }

        private void OnCancelEdit()
        {
            IsEditing = false;
        }

        private void OnDeleteUser(object parameter)
        {
            var user = parameter as UserModel ?? SelectedUser;
            if (user == null) return;

            if (user.UserName == GlobalData.Instance.CurrentUserName)
            {
                ShowWarning("不能删除当前登录用户！");
                return;
            }
            if (user.UserName == "admin")
            {
                ShowWarning("系统管理员账号不能删除！");
                return;
            }

            if (ShowConfirm($"确定要删除用户 '{user.DisplayName}'（{user.UserName}）吗？"))
            {
                _allUsers.Remove(user);
                AddLog($"admin 删除用户 {user.UserName}");
                ApplyFilter();
                UpdateStatistics();
                ShowInfo("用户已删除。");
            }
        }

        private void OnResetPassword(object parameter)
        {
            var user = parameter as UserModel ?? SelectedUser;
            if (user == null) return;

            if (ShowConfirm($"确定要重置用户 '{user.DisplayName}' 的密码吗？\n重置后密码为: 123456"))
            {
                AddLog($"admin 重置用户 {user.UserName} 的密码");
                ShowInfo($"密码已重置为 123456，请提醒用户及时修改。");
            }
        }

        private void OnEnableUser(object parameter)
        {
            var user = parameter as UserModel ?? SelectedUser;
            if (user == null) return;
            user.IsEnabled = true;
            AddLog($"admin 启用用户 {user.UserName}");
            ApplyFilter();
        }

        private void OnDisableUser(object parameter)
        {
            var user = parameter as UserModel ?? SelectedUser;
            if (user == null) return;
            if (user.UserName == "admin")
            {
                ShowWarning("不能禁用管理员账号！");
                return;
            }
            user.IsEnabled = false;
            AddLog($"admin 禁用用户 {user.UserName}");
            ApplyFilter();
        }

        private void OnExportLog()
        {
            ShowInfo("导出操作日志 (TODO: 对接 Excel/CSV 导出功能)");
        }

        private void AddLog(string message)
        {
            OperationLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        private void UpdateStatistics()
        {
            OnPropertyChanged(nameof(TotalUsers));
            OnPropertyChanged(nameof(AdminCount));
            OnPropertyChanged(nameof(EngineerCount));
            OnPropertyChanged(nameof(OperatorCount));
        }

        #region 辅助弹窗方法

        private void ShowWarning(string message, string title = "警告")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void ShowInfo(string message, string title = "提示")
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool ShowConfirm(string message, string title = "确认操作")
        {
            return MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        #endregion

        #endregion
    }
}