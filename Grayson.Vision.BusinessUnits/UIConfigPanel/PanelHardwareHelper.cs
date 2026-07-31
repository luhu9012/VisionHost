using System.Collections.Generic;

namespace Grayson.Vision.BusinessUnits.UIConfigPanel
{
    /// <summary>
    /// 配置面板全局硬件静态工具类
    /// 替代WPF不可行的继承方案，所有面板共享硬件Key清单
    /// 宿主在打开任意配置面板前统一注入硬件列表，面板读取静态数据刷新下拉框
    /// </summary>
    public static class PanelHardwareHelper
    {
        /// <summary>系统全部相机DeviceKey集合</summary>
        public static List<string> GlobalCameraKeys { get; set; } = new List<string>();
        /// <summary>系统全部PLC DeviceKey集合</summary>
        public static List<string> GlobalPlcKeys { get; set; } = new List<string>();

        /// <summary>宿主全局批量设置硬件清单，所有面板共用</summary>
        /// <param name="cameraKeys">全部相机Key</param>
        /// <param name="plcKeys">全部PLC Key</param>
        public static void SetAllHardwareList(List<string> cameraKeys, List<string> plcKeys)
        {
            GlobalCameraKeys = cameraKeys ?? new List<string>();
            GlobalPlcKeys = plcKeys ?? new List<string>();
        }
    }
}