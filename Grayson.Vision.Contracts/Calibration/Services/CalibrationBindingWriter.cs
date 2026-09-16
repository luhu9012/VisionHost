//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: CalibrationBindingWriter.cs
// 说 明: 标定档案「相机 / 运动卡」绑定的唯一写回实现（2026-09-15）。
//
// 背景（为什么要抽出来）：
//   绑定四字段（CameraId / AxisId / BoundDeviceId / BindingInfo）此前**只有向导第一步**
//   （CalibrationWizardViewModel.UpdateBindingInfo）会写真值；标定中心只写占位文本
//   （"工位: 站名 (站号) · 卡名"）。于是"按计划创建"出来的档案，其绑定既不是物理设备、
//   在标定中心**又没有任何编辑入口**（界面只读 TextBlock）。Cam_C 两份档案正是如此：
//     · CameraId = "Cam_C"   ← 槽名被当成了设备名（占位）
//     · AxisId   = "Axis_X"  ← 占位
//     · BoundDeviceId = null ← ★ 会让矩阵产物落到 Recipes\Devices\Default\Calib\
//                              而不是该相机自己的 Calib 目录（见 Wizard 生成矩阵路径处）
//
//   ⇒ 写回收敛到本类，**向导与标定中心共用同一份实现**。
//     （纪律：同一件事在两处各写一遍，分叉迟早出现在边界上。）
//
// ★ 预选语义（MatchBound）刻意**不兜底**：
//   向导原写法是 `FirstOrDefault(match) ?? List.FirstOrDefault()` —— 匹配不到就给"列表第一个"。
//   对本仓的复合工位这是**危险默认**：Cam_C 的 CameraId="Cam_C" 匹配不到任何设备，
//   于是会静默预选到列表第一个（往往是上相机）⇒ 用户点下一步就把上相机写进了下相机档案。
//   新写法：**匹配不到就返回 null（界面显示"未绑定"）**，绝不替用户猜。
//===================================================================================
using System;
using System.Collections.Generic;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Devices;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>标定档案设备绑定的唯一写回实现（向导 / 标定中心共用）。</summary>
    public static class CalibrationBindingWriter
    {
        /// <summary>
        /// 设备是否即档案里记录的那台。兼容 DeviceKey / DeviceId / DeviceName 三种历史写法
        /// （旧档存过设备名、也存过序列号）。
        /// </summary>
        public static bool IsBoundDevice(IDevice device, string profileId)
        {
            if (device == null || string.IsNullOrWhiteSpace(profileId)) return false;
            return string.Equals(device.DeviceKey, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceId, profileId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(device.DeviceName, profileId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>设备人读名：DeviceName 优先 → DeviceKey → DeviceId</summary>
        public static string DisplayName(IDevice device)
        {
            if (device == null) return "未选择";
            if (!string.IsNullOrWhiteSpace(device.DeviceName)) return device.DeviceName;
            if (!string.IsNullOrWhiteSpace(device.DeviceKey)) return device.DeviceKey;
            if (!string.IsNullOrWhiteSpace(device.DeviceId)) return device.DeviceId;
            return "未命名设备";
        }

        /// <summary>设备落档键：DeviceKey 优先（配置绑定用），空则退 DeviceId</summary>
        public static string KeyOf(IDevice device)
        {
            if (device == null) return null;
            return string.IsNullOrWhiteSpace(device.DeviceKey) ? device.DeviceId : device.DeviceKey;
        }

        /// <summary>
        /// 在设备集合里找档案记录的那一台。**匹配不到返回 null，不兜底取第一个**
        /// （理由见文件头 ★ 预选语义）。
        /// </summary>
        public static IDevice MatchBound(IEnumerable<IDevice> devices, string profileId)
        {
            if (devices == null || string.IsNullOrWhiteSpace(profileId)) return null;
            foreach (var d in devices)
            {
                if (IsBoundDevice(d, profileId)) return d;
            }
            return null;
        }

        /// <summary>
        /// 写回绑定四字段（与向导第一步同一口径）：
        /// CameraId / AxisId 取设备落档键；BoundDeviceId 与 CameraId 同步（矩阵落盘目录按它分）；
        /// BindingInfo 写成「相机: X | 运动卡: Y」人读串。
        /// ★ 只动这四项——**不碰 CameraSlotKey / PrimaryPath / Quantity**（槽与布局各有自己的编辑入口）。
        ///
        /// ★★ null 的语义是"这次没给替代设备"，**不是"要把它清空"**：
        ///   传 null 的那一项**原样保留**。理由有两个，都是本仓的真实路径：
        ///     1) 向导构造函数里 LoadDevices() 之后**无条件**调 UpdateBindingInfo()；
        ///        此时若设备池为空、或档案记的 CameraId 匹配不上（占位值/设备改名），
        ///        SelectedCameraDevice 就是 null。旧实现会照写 CameraId=null ——
        ///        **只是打开一次向导，档案的设备绑定就被抹掉了**，矩阵目录也随之退回
        ///        Recipes\Devices\Default\Calib\，而且全程无异常无日志。
        ///     2) 设备状态回调（OnCameraStateChanged / OnMotionStateChanged）也会调 UpdateBindingInfo()，
        ///        触发时机与"用户是否选过设备"完全无关。
        ///   ⇒ 想清空绑定必须**显式**（另设接口/按钮），不能靠"没选"顺带完成。
        /// </summary>
        public static void Apply(CalibrationProfile profile, IDevice camera, IDevice motion)
        {
            if (profile == null) return;
            if (camera == null && motion == null) return;   // 没给任何替代值 = 无事发生

            if (camera != null)
            {
                profile.CameraId = KeyOf(camera);
                profile.BoundDeviceId = profile.CameraId;   // 矩阵落盘目录按它分，必须同步
            }
            if (motion != null) profile.AxisId = KeyOf(motion);

            // ★ BindingInfo 从**写回后的档案状态**重建，而不是只拼传入的两个设备——
            //   否则"只改相机"时会把运动卡那一栏显示成"未选择"，与档案实际内容不符。
            string camText = camera != null
                ? DisplayName(camera)
                : (string.IsNullOrWhiteSpace(profile.CameraId) ? "（空）" : profile.CameraId);
            string motText = motion != null
                ? DisplayName(motion)
                : (string.IsNullOrWhiteSpace(profile.AxisId) ? "（空）" : profile.AxisId);
            profile.BindingInfo = "相机: " + camText + " | 运动卡: " + motText;
        }
    }
}
