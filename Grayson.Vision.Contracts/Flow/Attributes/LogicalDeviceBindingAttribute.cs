using System;
using Grayson.Vision.Contracts.Devices.Enums;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public class LogicalDeviceBindingAttribute : Attribute
{
    /// <summary>
    /// 设备类型 (如 "Camera", "Light", "PLC")
    /// </summary>
    public DeviceCategory DeviceType { get; }

    public string DeviceName { get; set; }

    /// <summary>
    /// 默认设备规格说明
    /// </summary>
    public string RequiredSpec { get; }

    public LogicalDeviceBindingAttribute(DeviceCategory deviceType, string deviceName="", string requiredSpec = "默认规格")
    {
        DeviceType = deviceType;
        RequiredSpec = requiredSpec;
        DeviceName = deviceName;
    }
}