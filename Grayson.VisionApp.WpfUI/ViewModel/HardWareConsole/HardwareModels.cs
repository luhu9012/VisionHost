using Grayson.Vision.Contracts.Devices.Models;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.WpfUI.ViewModel.HardwareConsole
{
    // 所有硬件模型已迁移至 Grayson.Vision.Contracts.Devices.Models：
    // - AxisInfoModel → Contracts.Devices.Models.MotionModels
    // - IoType 枚举 → Contracts.Devices.Models.IoModels
    // - IoPointModel → Contracts.Devices.Models.IoModels
    // 此处通过 using 导入保持兼容性
}
