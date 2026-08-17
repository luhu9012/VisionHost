using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grayson.Vision.Contracts.Recipe.Models;

namespace Grayson.Vision.WpfUI.Model
{
    public static class MockDataFactory
    {
        /// <summary>
        /// 集中生成全套交互演示Mock数据
        /// </summary>
        public static void SeedData(
            out ObservableCollection<LineModel> lines,
            out ObservableCollection<HardwareDeviceModel> globalHardwarePool,
            out ObservableCollection<RecipeModel> recipes)
        {
            // 1. 全局硬件池
            globalHardwarePool = new ObservableCollection<HardwareDeviceModel>
            {
                new HardwareDeviceModel { DeviceId = "DEV_01", DeviceCode = "CAM_TOP_01", DeviceName = "顶视扫码相机", DeviceType = "HikVision Camera", ConnectionString = "192.168.1.101", IsConnected = true, Remark = "海康 500万" },
                new HardwareDeviceModel { DeviceId = "DEV_02", DeviceCode = "CAM_POS_01", DeviceName = "高精度定位相机", DeviceType = "Cognex Camera", ConnectionString = "192.168.1.102", IsConnected = true, Remark = "康耐视 智能相机" },
                new HardwareDeviceModel { DeviceId = "DEV_03", DeviceCode = "PLC_MAIN_01", DeviceName = "主控线PLC", DeviceType = "Siemens S7-1200", ConnectionString = "192.168.1.200", IsConnected = true, Remark = "IO控制" },
                new HardwareDeviceModel { DeviceId = "DEV_04", DeviceCode = "MC_CARD_01", DeviceName = "三轴运动控制卡", DeviceType = "Gts Motion Card", ConnectionString = "Slot: 0", IsConnected = false, Remark = "对位模组" }
            };

            // 2. 预设配方列表 (含逻辑设备 demand 与多对多工位关联)
            var rcp1 = new RecipeModel
            {
                RecipeCode = "RCP_01",
                RecipeName = "手机中框外观检测配方",
                ProductCategory = "3C电子",
                Version = "V1.0.2",
                FlowName = "Main_Frame_Inspection_v2",
                IsActive = true,
                ExposureTime = 1200,
                Gain = 2.0,
                ToleranceMm = 0.05,
                ApplicableStationCodes = new ObservableCollection<string> { "ST_01", "ST_02" }
            };
            rcp1.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_01", LogicalDeviceName = "扫码识别相机", LogicalDeviceType = "2D Camera", RequiredSpec = "分辨率 >= 1080P" });
            rcp1.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_PLC_01", LogicalDeviceName = "工位顶升PLC", LogicalDeviceType = "PLC IO", RequiredSpec = "ModbusTCP" });

            var rcp2 = new RecipeModel
            {
                RecipeCode = "RCP_02",
                RecipeName = "电池贴合高精度配方",
                ProductCategory = "新能源锂电",
                Version = "V2.1.0",
                FlowName = "Battery_Align_Flow",
                IsActive = false,
                ExposureTime = 800,
                Gain = 1.5,
                ToleranceMm = 0.02,
                ApplicableStationCodes = new ObservableCollection<string> { "ST_02" }
            };
            rcp2.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_POS", LogicalDeviceName = "引导定位相机", LogicalDeviceType = "2D Camera", RequiredSpec = "500万黑白" });
            rcp2.LogicalDevices.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_AXIS_XYZ", LogicalDeviceName = "对位模组控制卡", LogicalDeviceType = "Motion Card", RequiredSpec = "支持3轴插补" });

            recipes = new ObservableCollection<RecipeModel> { rcp1, rcp2 };

            // 3. 产线与工位
            var st1 = new StationModel
            {
                StationCode = "ST_01",
                StationName = "上料扫码工位",
                IsEnabled = true,
                TimeoutMs = 3000,
                BoundRecipe = rcp1
            };
            st1.HardwareDevices.Add(globalHardwarePool[0]); // CAM_TOP_01
            st1.HardwareDevices.Add(globalHardwarePool[2]); // PLC_MAIN_01

            // 映射逻辑设备到工位已领用的硬件
            st1.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_CAM_01", LogicalDeviceName = "扫码识别相机", LogicalDeviceType = "2D Camera", RequiredSpec = "分辨率 >= 1080P", MappedDeviceId = "DEV_01" });
            st1.RecipeDeviceMappings.Add(new RecipeDeviceMappingModel { LogicalDeviceId = "LOG_PLC_01", LogicalDeviceName = "工位顶升PLC", LogicalDeviceType = "PLC IO", RequiredSpec = "ModbusTCP", MappedDeviceId = "DEV_03" });

            var st2 = new StationModel
            {
                StationCode = "ST_02",
                StationName = "视觉定位贴合工位",
                IsEnabled = true,
                TimeoutMs = 5000,
                BoundRecipe = rcp2
            };
            st2.HardwareDevices.Add(globalHardwarePool[1]); // CAM_POS_01
            st2.HardwareDevices.Add(globalHardwarePool[3]); // MC_CARD_01

            var line1 = new LineModel { LineId = "LINE_01", LineName = "A线 - 模组组装产线" };
            line1.Stations.Add(st1);
            line1.Stations.Add(st2);

            lines = new ObservableCollection<LineModel> { line1 };
        }
    }
}
