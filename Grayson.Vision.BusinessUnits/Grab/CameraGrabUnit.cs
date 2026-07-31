using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Common.Extensions;
using Grayson.Vision.BusinessUnits.UIConfigPanel.Grab;

namespace Grayson.Vision.BusinessUnits.Grab
{
    /// <summary>
    /// 工业相机软触发拍照单元
    /// 负责指定Key的相机单次抓拍，图像写入上下文Grab.SourceImage
    /// 可配置相机设备Key，支持拍照超时设置
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "CameraGrabUnit",
        DisplayName = "相机图像采集",
        UnitType = BusinessUnitType.ImageGrab,
        Category = "图像采集",
        Description = "根据绑定的相机DeviceKey软触发拍照，原图存入全局上下文")]
    public class CameraGrabUnit : IBusinessUnit
    {
        #region 单元私有配方参数（全部可序列化）
        /// <summary>绑定的相机硬件唯一Key</summary>
        public string CameraDeviceKey { get; set; } = string.Empty;

        /// <summary>拍照超时毫秒，超时判定采集失败</summary>
        public int GrabTimeoutMs { get; set; } = 3000;
        #endregion

        #region IBusinessUnit 固定实现
        public string ModuleId => "CameraGrabUnit";
        public string ModuleName => "相机图像采集";
        public BusinessUnitType UnitType => BusinessUnitType.ImageGrab;
        public bool Enable { get; set; } = true;

        /// <summary>本单元无前置依赖，不需要读取任何上下文数据</summary>
        public IReadOnlyList<string> InputKeys => Array.Empty<string>();

        /// <summary>执行完成写入原图、触发时间戳</summary>
        public IReadOnlyList<string> OutputKeys => new List<string>
        {
            ContextDataKeys.Grab_SourceImage,
            ContextDataKeys.Grab_TriggerTime
        };

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 1. 基础参数校验
            if (string.IsNullOrWhiteSpace(CameraDeviceKey))
            {
                runtime.LogError("相机DeviceKey为空，请在配置面板选择相机");
                return Result.Fail("未绑定相机");
            }

            // 2. 通过运行时门面获取相机硬件，不能直接new硬件类
            var camQuery = runtime.GetDevice(CameraDeviceKey);
            if (!camQuery.Success || !(camQuery.Data is ICamera camera))
            {
                runtime.LogError($"获取相机失败，Key:{CameraDeviceKey}，{camQuery.Message}");
                return Result.Fail("相机硬件获取失败");
            }

            // 3. 软触发拍照
            runtime.LogInfo($"开始触发相机{CameraDeviceKey}拍照", ModuleId);
            Result grabResult = camera.GrabImage(out var captureImage);
            if (!grabResult.Success)
            {
                runtime.LogError($"相机抓拍失败：{grabResult.Message}", null,ModuleId);
                return grabResult;
            }

            // 4. 图像写入上下文，记录时间戳
            context.SourceImage = captureImage;
            context.TriggerTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            context.SharedData[ContextDataKeys.Grab_TriggerTime] = context.TriggerTimestamp;

            // 5. 推送UI显示原图
            runtime.PublishUiEvent("Ui.ShowMainImage", captureImage);
            runtime.LogInfo("相机采集完成", ModuleId);

            return Result.Ok();
        }

        /// <summary>参数打包存入配方字典</summary>
        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"CameraDeviceKey", CameraDeviceKey},
                {"GrabTimeoutMs", GrabTimeoutMs},
                {"Enable", Enable}
            };
        }

        /// <summary>从配方字典加载参数</summary>
        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            CameraDeviceKey = recipeDict.SafeGet("CameraDeviceKey", string.Empty);
            GrabTimeoutMs = recipeDict.SafeGet("GrabTimeoutMs", 3000);
            Enable = recipeDict.SafeGet("Enable", true);
        }

        /// <summary>返回WPF配置面板，双击节点弹出参数配置窗口</summary>
        public UserControl GetConfigPanel()
        {
            return new CameraGrabUnitPanel(this);
        }
        #endregion
    }
}