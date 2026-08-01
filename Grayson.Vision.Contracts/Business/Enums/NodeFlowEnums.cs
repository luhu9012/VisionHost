using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Business.Enums
{
    // 画布内节点的端口类型枚举
    public enum PortType
    {
        In,     // 输入端口
        Out     // 输出端口
    }
    /// <summary>
    /// 端口物理类型：控制流端口 (Exec) 还是 数据流端口 (Data)
    /// </summary>
    public enum PortCategory
    {
        Exec,   // 控制流/执行流程端口（如：In, Out, OK, NG）
        Data    // 数据传递端口（如：Image, Region, Point, String, Double）
    }
    public enum PortPosition
    {
        Top,    // 顶部主输入
        Bottom, // 底部主输出
        Left,   // 左侧扩展输入
        Right   // 右侧扩展输出
    }

    /// <summary>
    /// 端口连接类型枚举（对应连线逻辑）
    /// </summary>
    public enum ConnectorType
    {
        Input,          // 默认输入
        OutputDefault,  // 默认单线输出
        OutputTrue,     // 条件满足 / OK 管道
        OutputFalse,    // 条件不满足 / NG 管道
        OutputError     // 异常通道
    }



    /// <summary>
    /// 工业视觉节点的 6 大核心业务分类
    /// </summary>
    public enum NodeCategory
    {
        [Description("⚙️ 设备与 IO 控制类")]
        DeviceIO,

        [Description("👁️ Halcon 算法与视觉处理类")]
        Vision,

        [Description("🧠 逻辑控制与数据流类")]
        Logic,

        [Description("📊 数据处理与转换类")]
        DataProcess,

        [Description("🏭 生产与数据对接类")]
        SystemMES,

        [Description("🛑 复合子流程与异常处理类")]
        CompositeEx
    }


    /// <summary>
    /// 23 种全量具体节点类型枚举
    /// </summary>
    public enum NodeType
    {
        // --- 1. 设备与 IO 控制类 ---
        AcquireImage,       // 相机采集
        PlcReadWrite,       // PLC 读写
        AxisMove,           // 运动轴移动
        DigitalOutput,      // 数字 IO 输出
        LightControl,       // 光照控制

        // --- 2. Halcon 算法与视觉处理类 ---
        TemplateMatch,      // 模板匹配
        Calib2D,            // 九点标定/手眼标定
        Measurement,        // 几何测量
        DefectDetect,       // 缺陷检测
        ReadBarcode,        // 条码/二维码识别
        DlInference,        // 深度学习推理

        // --- 3. 逻辑控制与数据流类 ---
        ConditionIf,        // 条件分支 (If/Else)
        SwitchCase,         // 多路分支
        ForLoop,            // 循环控制
        Delay,              // 延时等待
        WaitSignal,         // 状态信号等待

        // --- 4. 数据处理与转换类 ---
        OffsetMath,         // 坐标计算/偏移
        ScriptMath,         // 公式计算
        VarMapper,          // 变量映射
        StringFormat,       // 字符串格式化

        // --- 5. 生产与数据对接类 ---
        MesReport,          // MES 上报
        SaveData,           // 数据存盘
        SaveImage,          // 图像保存

        // --- 6. 复合子流程与异常处理类 ---
        CompositeFlow,      // 子流程节点
        TryCatch,           // 异常捕获
        TerminateFlow       // 流程终止
    }
}
