//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: I/O 设备控制模型
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;

namespace Grayson.Vision.Contracts.Devices.Models
{
    /// <summary>
    /// I/O 点类型枚举
    /// </summary>
    public enum IoType
    {
        /// <summary>数字输入 (DI)</summary>
        Input = 0,
        /// <summary>数字输出 (DO)</summary>
        Output = 1,
        /// <summary>模拟输入 (AI)</summary>
        AnalogInput = 2,
        /// <summary>模拟输出 (AO)</summary>
        AnalogOutput = 3
    }

    /// <summary>
    /// I/O 点定义模型 - 代表 PLC/控制卡上的单个 I/O 点
    /// </summary>
    public class IoPointModel : ViewModelBase
    {
        /// <summary>通道索引</summary>
        public int ChannelIndex { get; set; }

        /// <summary>I/O 点类型</summary>
        public IoType Type { get; set; } = IoType.Input;

        /// <summary>I/O 点名称（如"料斗压力"、"夹具释放"）</summary>
        public string Name { get; set; }

        /// <summary>硬件地址（如"X0.0"、"Y0.1"）</summary>
        public string Address { get; set; }

        private bool _isActive;
        /// <summary>当前状态（ON/OFF）</summary>
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (Set(ref _isActive, value))
                {
                    OnPropertyChanged(nameof(StateText));
                }
            }
        }

        /// <summary>状态文本展示</summary>
        public string StateText => IsActive ? "ON" : "OFF";
    }
}
