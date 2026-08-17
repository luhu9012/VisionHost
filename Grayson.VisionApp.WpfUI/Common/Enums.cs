//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: Enums.cs
// 创 建: 2026-07-18
// 说 明: UI 层相关的枚举定义
//===================================================================================

namespace Grayson.Vision.WpfUI.Common
{
    // 注意：UserRole 统一使用 Grayson.Vision.Contracts.Infrastructure.Permission.UserRole，
    // 避免 UI 层与 Contracts 层重复定义造成歧义与维护负担。

    /// <summary>
    /// 报警等级
    /// </summary>
    public enum AlarmLevel
    {
        /// <summary>信息提示</summary>
        Info = 0,

        /// <summary>警告 - 不影响生产但需关注</summary>
        Warning = 1,

        /// <summary>错误 - 影响生产,需立即处理</summary>
        Error = 2,

        /// <summary>严重错误 - 系统停机</summary>
        Critical = 3
    }

    /// <summary>
    /// 设备运行状态
    /// </summary>
    public enum DeviceStatus
    {
        /// <summary>未连接</summary>
        Disconnected = 0,

        /// <summary>已连接待机</summary>
        Connected = 1,

        /// <summary>运行中</summary>
        Running = 2,

        /// <summary>暂停</summary>
        Paused = 3,

        /// <summary>故障</summary>
        Error = 4
    }

    /// <summary>
    /// 检测结果
    /// </summary>
    public enum InspectionResult
    {
        /// <summary>未检测</summary>
        None = 0,

        /// <summary>合格 OK</summary>
        OK = 1,

        /// <summary>不合格 NG</summary>
        NG = 2,

        /// <summary>检测异常</summary>
        Error = 3
    }

    /// <summary>
    /// 导航页面枚举
    /// </summary>
    public enum PageType
    {
        Login,
        Alarm,
        UserManage,
        FlowEdit,
        StationManage,
        LineOverview,
        StationMonitor,
        RecipeManage,
        CalibrationManage,
        DevicePool,
        HardwareConsole,
        PluginManage,
        DataTrace,
        MesBridge,
        SystemSetting

    }
}
