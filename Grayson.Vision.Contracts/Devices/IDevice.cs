using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Devices
{
    public enum DeviceCategory
    {
        Camera,         // 工业相机
        MotionCard,     // 运动控制卡
        PLC,            // PLC 通信
        LightController // 光源控制器
    }
    /// <summary>所有硬件顶层接口：相机、PLC、运动卡、机器人全部实现</summary>
    public interface IDevice : IDisposable
    {
        /// <summary>设备唯一标识ID，通常为序列号SN</summary>
        string DeviceId { get; set; }
        /// <summary>设备名称，用户自定义</summary>
        string DeviceName { get; set; }
        /// <summary>设备唯一标识Key，配置绑定用，不和IP绑定</summary>
        string DeviceKey { get; set; }

        /// <summary>硬件品牌名称：Hikvision/Basler/Siemens/Modbus</summary>
        string BrandName { get; set; }

        /// <summary>当前硬件在线状态</summary>
        DeviceState State { get; set; }
        // 设备大类
        DeviceCategory Category { get; set; }       

        /// <summary>连接硬件设备</summary>
        Result Connect();

        /// <summary>断开连接，释放SDK所有资源</summary>
        Result Disconnect();

        /// <summary>主动轮询硬件在线状态</summary>
        Result CheckStatus();

        /// <summary>设置硬件参数（曝光、波特率等）</summary>
        Result SetParam(string key, object value);

        /// <summary>读取硬件参数</summary>
        Result<object> GetParam(string key);
    }
}