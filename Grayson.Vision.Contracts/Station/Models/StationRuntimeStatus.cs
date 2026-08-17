//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationRuntimeStatus.cs
// 说 明: 工位实时运行状态模型
//        仅包含频繁变化的动态状态信息（连接状态、设备状态、最近更新时间等）
//        不包含配置信息与统计数据，确保单一职责
//        与 StationConfigModel 关键字形成 N:1 关系
//===================================================================================

using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 工位实时运行状态 
    /// 
    /// 此模型表示工位的动态运行状态信息，特点：
    /// - 频繁变化（秒级更新）
    /// - 非持久化或低频持久化
    /// - 可缓存在内存/Redis 中
    /// - 不包含配置或统计数据
    /// 
    /// 职责分离:
    /// - StationConfigModel: 工位配置（生命周期：创建-修改-删除）
    /// - StationRuntimeStatus: 工位运行状态（变化频繁：秒级）
    /// - StationStatistics: 工位统计数据（变化周期：分钟级）
    /// </summary>
    public class StationRuntimeStatus
    {
        /// <summary>
        /// 工位唯一标识 (外键关联到 StationConfigModel)
        /// </summary>
        public string StationId { get; set; }

        /// <summary>
        /// 工位硬件连接状态
        /// true   = 工位设备已连接，可正常交互
        /// false  = 工位离线或通讯中断
        /// </summary>
        public bool IsConnected { get; set; }

        /// <summary>
        /// 工位当前运行状态
        /// 
        /// 状态枚举:
        /// - Idle     : 空闲，没有工单在执行
        /// - Running  : 运行中，正在处理工单
        /// - Paused   : 暂停，工单已暂停未恢复
        /// - Faulted  : 故障，出现错误需人工介入
        /// - Stopped  : 停止，工位已关闭
        /// 
        /// 注意: 此字段应与 Contracts.Station.Enums.StationState 枚举保持同步
        /// </summary>
        public string State { get; set; } = "Stopped";

        /// <summary>
        /// 工位最后一次状态更新的时间戳
        /// 用于:
        /// - 监控工位是否心跳检测正常
        /// - UI 显示数据新鲜度
        /// - 故障诊断（状态长时间不更新表示通讯中断）
        /// </summary>
        public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// 工位最后一条错误消息 (当 State = "Faulted" 时有效)
        /// 示例: "相机连接超时", "PLC 通讯故障", "配方加载失败"
        /// 
        /// 设置时机:
        /// - 状态转为 Faulted 时，记录具体错误原因
        /// - 状态恢复为正常时，清空此字段
        /// </summary>
        public string LastErrorMessage { get; set; }

        /// <summary>
        /// 工位故障代码 (当 State = "Faulted" 时有效)
        /// -1      = 未定义的故障
        /// 1000-1999 = 硬件相关故障
        /// 2000-2999 = 通讯相关故障
        /// 3000-3999 = 配置相关故障
        /// 等等...
        /// 
        /// 用途: 结合日志系统快速查询和分类故障
        /// </summary>
        public int FaultCode { get; set; } = -1;

        /// <summary>
        /// 验证状态一致性
        /// 
        /// 规则:
        /// - 如果 IsConnected = false，State 应为 "Stopped" 或错误状态
        /// - 如果 State = "Faulted"，LastErrorMessage 不应为空
        /// - LastUpdateTime 应为最近的时间戳
        /// 
        /// 用途: 数据完整性检验，防止数据不一致
        /// </summary>
        public bool IsConsistent
        {
            get
            {
                // 离线状态下，运行状态应为 Stopped
                if (!IsConnected && State == "Running")
                    return false;

                // 故障状态下，应有错误信息
                if (State == "Faulted" && string.IsNullOrEmpty(LastErrorMessage))
                    return false;

                // 更新时间应该合理（不能是未来时间）
                if (LastUpdateTime > DateTime.UtcNow)
                    return false;

                return true;
            }
        }

        /// <summary>
        /// 工厂方法: 创建一个已连接、空闲的工位状态
        /// </summary>
        public static StationRuntimeStatus CreateConnectedIdle(string stationId)
        {
            return new StationRuntimeStatus
            {
                StationId = stationId,
                IsConnected = true,
                State = "Idle",
                LastUpdateTime = DateTime.UtcNow,
                FaultCode = -1
            };
        }

        /// <summary>
        /// 工厂方法: 创建一个离线的工位状态
        /// </summary>
        public static StationRuntimeStatus CreateOffline(string stationId, string reason = null)
        {
            return new StationRuntimeStatus
            {
                StationId = stationId,
                IsConnected = false,
                State = "Stopped",
                LastUpdateTime = DateTime.UtcNow,
                LastErrorMessage = reason ?? "工位离线",
                FaultCode = 2001  // 通讯故障代码
            };
        }

        /// <summary>
        /// 工厂方法: 创建一个故障状态的工位
        /// </summary>
        public static StationRuntimeStatus CreateFaulted(string stationId, string errorMsg, int faultCode = -1)
        {
            return new StationRuntimeStatus
            {
                StationId = stationId,
                IsConnected = true,
                State = "Faulted",
                LastUpdateTime = DateTime.UtcNow,
                LastErrorMessage = errorMsg,
                FaultCode = faultCode
            };
        }
    }
}
