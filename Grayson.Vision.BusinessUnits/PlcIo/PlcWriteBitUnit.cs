using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Grayson.Vision.Contracts.Business;
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Common.Extensions;


namespace Grayson.Vision.BusinessUnits.PlcIo
{
    /// <summary>
    /// PLC布尔点位写入单元
    /// 可固定写入True/False，也可读取上下文布尔变量写入点位
    /// 适配OK/NG信号上报、触发外部设备
    /// </summary>
    [BusinessUnitMeta(
        ModuleId = "PlcWriteBitUnit",
        DisplayName = "PLC写入布尔点位",
        UnitType = BusinessUnitType.PlcIo,
        Category = "PLC通讯",
        Description = "指定PLC设备、地址，写入固定值或上下文的布尔变量")]
    public class PlcWriteBitUnit : IBusinessUnit
    {
        public string PlcDeviceKey { get; set; } = string.Empty;
        /// <summary>PLC点位地址，如M0.0、DB10.DBX0.0</summary>
        public string PlcAddress { get; set; } = string.Empty;
        /// <summary>是否使用上下文变量；false=写固定值</summary>
        public bool UseContextBool { get; set; }
        /// <summary>UseContextBool=false时的固定写入值</summary>
        public bool FixedWriteValue { get; set; }
        /// <summary>UseContextBool=true时读取的上下文Key</summary>
        public string ContextBoolKey { get; set; } = string.Empty;

        public bool Enable { get; set; } = true;
        public string ModuleId => "PlcWriteBitUnit";
        public string ModuleName => "PLC写入布尔点位";
        public BusinessUnitType UnitType => BusinessUnitType.PlcIo;

        public IReadOnlyList<string> InputKeys
        {
            get
            {
                if (UseContextBool && !string.IsNullOrEmpty(ContextBoolKey))
                    return new List<string> { ContextBoolKey };
                return Array.Empty<string>();
            }
        }
        public IReadOnlyList<string> OutputKeys => Array.Empty<string>();

        public Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 校验基础配置
            if (string.IsNullOrWhiteSpace(PlcDeviceKey) || string.IsNullOrWhiteSpace(PlcAddress))
            {
                runtime.LogError("PLC Key或点位地址为空", null, ModuleId);
                return Result.Fail("PLC配置不全");
            }

            // 获取PLC硬件
            var plcQuery = runtime.GetDevice(PlcDeviceKey);
            if (!plcQuery.Success || !(plcQuery.Data is IPlc plc))
            {
                runtime.LogError($"获取PLC失败 {PlcDeviceKey}", null, ModuleId);
                return Result.Fail("PLC硬件异常");
            }

            // 判断最终写入的布尔值
            bool writeVal;
            if (UseContextBool)
            {
                writeVal = context.SharedData.SafeGet(ContextBoolKey, false);
            }
            else
            {
                writeVal = FixedWriteValue;
            }

            // 执行写入
            Result writeRes = plc.WriteBit(PlcAddress, writeVal);
            if (!writeRes.Success)
            {
                runtime.LogError($"点位{PlcAddress}写入失败:{writeRes.Message}", null, ModuleId);
                return writeRes;
            }
            runtime.LogInfo($"PLC {PlcAddress} 成功写入{writeVal}", ModuleId);
            return Result.Ok();
        }

        public Dictionary<string, object> SaveRecipe()
        {
            return new Dictionary<string, object>()
            {
                {"PlcDeviceKey",PlcDeviceKey},
                {"PlcAddress",PlcAddress},
                {"UseContextBool",UseContextBool},
                {"FixedWriteValue",FixedWriteValue},
                {"ContextBoolKey",ContextBoolKey},
                {"Enable",Enable}
            };
        }

        public void LoadRecipe(Dictionary<string, object> recipeDict)
        {
            PlcDeviceKey = recipeDict.SafeGet("PlcDeviceKey", "");
            PlcAddress = recipeDict.SafeGet("PlcAddress", "");
            UseContextBool = recipeDict.SafeGet("UseContextBool", false);
            FixedWriteValue = recipeDict.SafeGet("FixedWriteValue", false);
            ContextBoolKey = recipeDict.SafeGet("ContextBoolKey", "");
            Enable = recipeDict.SafeGet("Enable", true);
        }

        //public UserControl GetConfigPanel()
        //{
        //    return new PlcWriteBitPanel(this);
        //}
    }
}