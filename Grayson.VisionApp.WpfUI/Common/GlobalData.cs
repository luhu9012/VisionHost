//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: GlobalData.cs
// 创 建: 2026-07-18
// 修 改: 2026-07-28
// 说 明: 全局单例数据上下文,供所有 ViewModel 访问和订阅 (继承自 Contracts ViewModelBase)
//===================================================================================

using Grayson.Vision.Contracts.ViewModels;

using System;

namespace Grayson.Vision.WpfUI.Common
{
    /// <summary>
    /// 全局单例数据上下文,提供跨模块的数据共享和事件通知
    /// </summary>
    public class GlobalData : ViewModelBase
    {
        private static readonly Lazy<GlobalData> _instance = new Lazy<GlobalData>(() => new GlobalData());
        public static GlobalData Instance => _instance.Value;

        private GlobalData() { }

        #region 用户信息

        private string _currentUserName;
        /// <summary>
        /// 当前登录用户名
        /// </summary>
        public string CurrentUserName
        {
            get => _currentUserName;
            set => Set(ref _currentUserName, value);
        }

        private UserRole _currentUserRole = UserRole.Operator;
        /// <summary>
        /// 当前用户权限等级
        /// </summary>
        public UserRole CurrentUserRole
        {
            get => _currentUserRole;
            set => Set(ref _currentUserRole, value);
        }

        #endregion

        #region 生产数据

        private int _totalCount;
        /// <summary>
        /// 生产总数
        /// </summary>
        public int TotalCount
        {
            get => _totalCount;
            set => Set(ref _totalCount, value);
        }

        private int _okCount;
        /// <summary>
        /// 合格数
        /// </summary>
        public int OKCount
        {
            get => _okCount;
            set => Set(ref _okCount, value);
        }

        private int _ngCount;
        /// <summary>
        /// 不合格数
        /// </summary>
        public int NGCount
        {
            get => _ngCount;
            set => Set(ref _ngCount, value);
        }

        private double _yieldRate;
        /// <summary>
        /// 良率 (百分比)
        /// </summary>
        public double YieldRate
        {
            get => _yieldRate;
            set => Set(ref _yieldRate, value);
        }

        #endregion

        #region 系统状态

        private DeviceStatus _systemStatus = DeviceStatus.Disconnected;
        /// <summary>
        /// 系统运行状态
        /// </summary>
        public DeviceStatus SystemStatus
        {
            get => _systemStatus;
            set => Set(ref _systemStatus, value);
        }

        private string _currentRecipeName;
        /// <summary>
        /// 当前加载的配方名称
        /// </summary>
        public string CurrentRecipeName
        {
            get => _currentRecipeName;
            set => Set(ref _currentRecipeName, value);
        }

        #endregion

        #region 产线上下文


        /// <summary>当前产线显示名称（快捷属性，供 UI 绑定）</summary>


        private int _alarmCount;
        /// <summary>
        /// 未处理报警数量
        /// </summary>
        public int AlarmCount
        {
            get => _alarmCount;
            set
            {
                if (Set(ref _alarmCount, value))
                    OnPropertyChanged(nameof(HasAlarm));
            }
        }

        /// <summary>
        /// 是否有未处理报警（标题栏徽章可见性绑定）
        /// </summary>
        public bool HasAlarm => _alarmCount > 0;

        private bool _hasCriticalAlarm;
        /// <summary>
        /// 是否存在严重报警（触发全局 Banner 浮现，跨页面打断操作）
        /// </summary>
        public bool HasCriticalAlarm
        {
            get => _hasCriticalAlarm;
            set => Set(ref _hasCriticalAlarm, value);
        }

        private string _criticalAlarmMessage;
        /// <summary>
        /// 严重报警消息文字（显示在 Banner 上）
        /// </summary>
        public string CriticalAlarmMessage
        {
            get => _criticalAlarmMessage;
            set => Set(ref _criticalAlarmMessage, value);
        }

        #endregion

        #region 事件定义 (用于模块间通信)

        /// <summary>
        /// 用户登录事件
        /// </summary>
        public event EventHandler<string> UserLoggedIn;

        /// <summary>
        /// 用户登出事件
        /// </summary>
        public event EventHandler UserLoggedOut;

        /// <summary>
        /// 新增报警事件
        /// </summary>
        public event EventHandler<string> NewAlarmRaised;

        /// <summary>
        /// 触发用户登录事件
        /// </summary>
        public void RaiseUserLoggedIn(string userName)
        {
            UserLoggedIn?.Invoke(this, userName);
        }

        /// <summary>
        /// 触发用户登出事件
        /// </summary>
        public void RaiseUserLoggedOut()
        {
            UserLoggedOut?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 触发新增报警事件
        /// </summary>
        public void RaiseNewAlarm(string alarmMessage)
        {
            NewAlarmRaised?.Invoke(this, alarmMessage);
        }

        /// <summary>
        /// 触发严重报警（同时展开全局 Banner）
        /// </summary>
        public void RaiseCriticalAlarm(string message)
        {
            CriticalAlarmMessage = message;
            HasCriticalAlarm = true;
            AlarmCount++;
            NewAlarmRaised?.Invoke(this, message);
        }

        /// <summary>
        /// 消除严重报警 Banner（用户确认或自动清除后调用）
        /// </summary>
        public void DismissCriticalAlarm()
        {
            HasCriticalAlarm = false;
            CriticalAlarmMessage = null;
        }

        #endregion
    }
}