namespace Grayson.Vision.Contracts.Business.Enums
{
    /// <summary>业务单元大类枚举，用于分类、过滤、UI分组</summary>
    public enum BusinessUnitType
    {
        /// <summary>图像采集</summary>
        ImageGrab,
        /// <summary>定位匹配</summary>
        Location,
        /// <summary>尺寸测量</summary>
        Measure,
        /// <summary>缺陷检测</summary>
        DefectInspect,
        /// <summary>有无检测</summary>
        PresenceCheck,
        /// <summary>运动控制</summary>
        MotionControl,
        /// <summary>PLC点位读写</summary>
        PlcIo,
        /// <summary>数据运算处理</summary>
        DataProcess,
        /// <summary>脚本胶水逻辑（非标兜底）</summary>
        ScriptGlue
    }
}