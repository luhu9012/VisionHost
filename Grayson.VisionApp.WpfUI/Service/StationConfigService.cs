using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Devices;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.ViewModel;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// 工位配置领域服务
    /// 负责：产线工位配置的持久化加载/保存、真实硬件池映射、与 UI 本地模型转换。
    /// </summary>
    public class StationConfigService
    {
        private readonly IStationRepository _stationRepository;

        public StationConfigService(IStationRepository stationRepository = null)
        {
            _stationRepository = stationRepository ?? StorageFactory.CreateStationRepository();
        }

        /// <summary>
        /// 从数据库加载全部产线及下属工位
        /// </summary>
        public List<LineConfigModel> LoadAllLines()
        {
            return _stationRepository.GetAllLines();
        }

        /// <summary>
        /// 保存产线（含下属工位）
        /// </summary>
        public bool SaveLine(LineConfigModel line)
        {
            return _stationRepository.SaveLine(line);
        }

        /// <summary>
        /// 保存单个工位
        /// </summary>
        public bool SaveStation(StationConfigModel station)
        {
            return _stationRepository.SaveStation(station);
        }

        /// <summary>
        /// 删除产线及其下属工位
        /// </summary>
        public bool DeleteLine(string lineId)
        {
            return _stationRepository.DeleteLine(lineId);
        }

        /// <summary>
        /// 删除单个工位
        /// </summary>
        public bool DeleteStation(string stationId)
        {
            return _stationRepository.DeleteStation(stationId);
        }

        /// <summary>
        /// 从 DevicePoolManager 获取当前所有可用物理设备，转换为 UI 模型
        /// </summary>
        public List<HardwareDeviceModel> LoadAvailableHardwareDevices()
        {
            var devices = DevicePoolManager.Instance.GetAllDevices();
            var result = new List<HardwareDeviceModel>();

            foreach (var device in devices)
            {
                result.Add(MapToHardwareDeviceModel(device));
            }

            return result;
        }

        /// <summary>
        /// 将 IDevice 实例转换为 UI 展示模型
        /// </summary>
        public HardwareDeviceModel MapToHardwareDeviceModel(IDevice device)
        {
            if (device == null) return null;

            string brand = device.GetType().Name;
            string category = "通用设备";

            // 优先读取用户持久化的配置信息
            var configRepo = StorageFactory.CreateDeviceConfigRepository();
            var config = configRepo.GetByKey(device.DeviceKey);
            if (config != null)
            {
                brand = config.BrandName;
                category = config.Category.ToString();
            }

            return new HardwareDeviceModel
            {
                DeviceId = device.DeviceKey ?? Guid.NewGuid().ToString("N"),
                DeviceCode = config?.DeviceId ?? device.DeviceKey,
                DeviceName = $"{device.DeviceKey}",
                DeviceType = category,
                BrandName = brand,
                ConnectionString = config?.ConnectionString ?? string.Empty,
                IsConnected = false,
                Remark = $"{brand} / {category}"
            };
        }

        /// <summary>
        /// 根据 RecipeModel 生成默认的设备映射表
        /// </summary>
        public List<DeviceMappingModel> BuildDefaultMappings(RecipeModel recipe)
        {
            if (recipe?.LogicalDevices == null) return new List<DeviceMappingModel>();

            var availableDevices = LoadAvailableHardwareDevices();
            var defaultDevice = availableDevices.FirstOrDefault();

            return recipe.LogicalDevices.Select(logical => new DeviceMappingModel
            {
                LogicalDeviceId = logical.LogicalDeviceId,
                LogicalDeviceName = logical.LogicalDeviceName,
                LogicalDeviceType = logical.LogicalDeviceType,
                RequiredSpec = logical.RequiredSpec,
                MappedDeviceKey = defaultDevice?.DeviceId,
                MappedDeviceName = defaultDevice?.DeviceName
            }).ToList();
        }

        /// <summary>
        /// 生成新的工位ID
        /// </summary>
        public string GenerateStationId()
        {
            return $"ST_{DateTime.Now:yyyyMMdd}_{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}";
        }

        /// <summary>
        /// 生成新的产线ID
        /// </summary>
        public string GenerateLineId()
        {
            return $"LINE_{DateTime.Now:yyyyMMdd}_{Guid.NewGuid().ToString("N").Substring(0, 6).ToUpper()}";
        }
    }
}
