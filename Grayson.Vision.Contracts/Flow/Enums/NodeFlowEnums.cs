using Grayson.Vision.Contracts.Flow.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Flow.Enums
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
        //Exec,   // 控制流/执行流程端口（如：In, Out, OK, NG）
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
    /// 工业 2D 视觉平台的 10 大核心业务分类（参考 VisionMaster 架构，暗黑主题高辨识度系）
    /// </summary>
    public enum NodeCategory
    {
        // 1. 青绿/湖绿系 (Teal Cyan)
        [NodeFieldMeta("📷", "#00A896", "图像采集", "图像采集与输入")]
        ImageInput,

        // 2. 蓝绿/薄荷系 (Mint Green-Blue)
        [NodeFieldMeta("🎨", "#02C39A", "图像增强", "图像预处理与增强")]
        ImagePreprocess,

        // 3. 科技蓝系 (Tech Blue)
        [NodeFieldMeta("🎯", "#0077B6", "标定定位", "标定与位置跟随")]
        CalibrationLocation,

        // 4. 暖琥珀/金黄系 (Warm Amber)
        [NodeFieldMeta("📏", "#D97706", "几何测量", "2D 几何测量与检测")]
        Measurement2D,

        // 5. 亮橙/暖铜系 (Copper Orange)
        [NodeFieldMeta("🔍", "#EA580C", "识别读码", "识别与读码")]
        Identification,

        // 6. 电光紫系 (Electric Purple)
        [NodeFieldMeta("🧮", "#7C3AED", "逻辑运算", "逻辑与算术运算")]
        MathLogic,

        // 7. 洋红/品红系 (Magenta Violet)
        [NodeFieldMeta("🔀", "#C026D3", "流程控制", "流程控制与条件分支")]
        FlowControl,

        // 8. 柔粉紫系 (Soft Rose Orchid)
        [NodeFieldMeta("📦", "#DB2777", "子流程", "Group 子流程")]
        [Description("Group 子流程")]
        CompositeGroup,

        // 9. 蓝灰/钢蓝系 (Slate Steel Blue)
        [NodeFieldMeta("🔌", "#2563EB", "设备 IO", "设备通信与硬件控制")]
        DeviceIO,

        // 10. 炭晶/冷钛系 (Cold Titanium)
        [NodeFieldMeta("💾", "#475569", "数据 MES", "数据存储与系统交互")]
        DataStorage
    }

    /// <summary>
    /// 工业 2D 视觉节点全量类型枚举 (派生自父分类同色系渐变)
    /// </summary>
    public enum NodeType
    {
        // ==========================================
        // 1. 📷 图像采集与输入 (ImageInput - #00A896 青绿系)
        // ==========================================
        [NodeFieldMeta("📸", "#00A896", "相机采集", "相机采集 (触发/连续)")]
        AcquireImage,       // 海康/大恒等 SDK 触发采集[cite: 4]

        [NodeFieldMeta("🖼️", "#028090", "图像读取", "本地图像/序列读取")]
        ReadImageFile,      // 从磁盘读取单张/离线图片序列[cite: 4]

        [NodeFieldMeta("🎛️", "#05668D", "通道拆分", "RGB/HSV 通道拆分合成")]
        ImageChannel,       // RGB/HSV 拆分或多灰度图合成[cite: 4]

        // ==========================================
        // 2. 🎨 图像预处理与增强 (ImagePreprocess - #02C39A 蓝绿系)
        // ==========================================
        [NodeFieldMeta("🧹", "#02C39A", "图像滤波", "图像滤波 (平滑/去噪/形态学)")]
        ImageFilter,        // 高斯/中值/形态学膨胀腐蚀

        [NodeFieldMeta("🌓", "#00A884", "阈值分割", "阈值分割 (固定/自适应/Otsu)")]
        ImageThreshold,     // 固定/动态/Otsu 阈值二值化

        [NodeFieldMeta("✂️", "#008E73", "ROI 运算", "ROI 提取、掩膜与集合交并差运算")]
        ROIOperation,       // 裁剪/生成 Mask 掩膜及 ROI 集合运算

        [NodeFieldMeta("🔄", "#007562", "图像纠偏", "图像仿射变换与角度纠偏")]
        AffineImage,        // 基于旋转矩阵旋转/矫正图像

        // ==========================================
        // 3. 🎯 标定与位置跟随 (CalibrationLocation - #0077B6 科技蓝系)
        // ==========================================
        [NodeFieldMeta("🧩", "#0096C7", "形状匹配", "基于形状/边缘模板匹配定位")]
        ShapeMatch,         // 基于 Halcon Shape-Based 查找定位

        [NodeFieldMeta("🏁", "#03045E", "灰度匹配", "基于灰度/NCC 模板匹配")]
        NccMatch,           // 基于灰度纹理匹配

        [NodeFieldMeta("⚓", "#023E8A", "位置补正", "建立基准位置 (计算 ΔX, ΔY, Δθ)")]
        CreateFixture,      // 计算旋转平移偏差，用于后续 ROI 转换

        [NodeFieldMeta("🎯", "#0077B6", "位置跟随", "应用位置补正矩阵到后续检测 ROI")]
        ApplyFixture,       // 将基准下的 ROI 映射到当前来料实际坐标

        [NodeFieldMeta("📐", "#0077B6", "标定转换", "应用已保存的标定矩阵转换像素与物理坐标")]
        CalibrationApply,    // 标定映射转换 (Pixel -> World)

        // ==========================================
        // 4. 📏 2D 几何测量与检测 (Measurement2D - #D97706 暖琥珀系)
        // ==========================================
        [NodeFieldMeta("🔎", "#D97706", "卡尺测量", "单/多卡尺找边缘点")]
        CaliperMeasure,     // 单卡尺或卡尺线找边缘点提取

        [NodeFieldMeta("📏", "#B45309", "直线提取", "找线单元 (多卡尺拟合直线)")]
        FitLine,            // 沿着指定方向投射卡尺拟合直线

        [NodeFieldMeta("⭕", "#92400E", "圆弧提取", "找圆单元 (环形卡尺拟合圆)")]
        FitCircle,          // 环形/弧形卡尺拟合圆心与半径

        [NodeFieldMeta("📐", "#D97706", "几何测量", "几何建构 (交点/距离/角度计算)")]
        GeometryMeasure,    // 线线交点、点到线距离、夹角建构

        [NodeFieldMeta("⚪", "#F59E0B", "Blob 分析", "Blob 连通域斑点分析")]
        BlobAnalysis,       // 连通域面积、周长、圆度、质心提取

        [NodeFieldMeta("⚠️", "#EA580C", "缺陷检测", "表面缺陷与瑕疵检测")]
        DefectDetect,       // 黄金模板差分、划痕/脏污检测

        // ==========================================
        // 5. 🔍 识别与读码 (Identification - #EA580C 亮橙系)
        // ==========================================
        [NodeFieldMeta("🏁", "#EA580C", "条码识别", "一维码 / 二维码识别")]
        ReadBarcode,        // 一维码 (Code128 等) / 二维码 (QR/DM) 识别

        [NodeFieldMeta("🔤", "#C2410C", "字符识别", "字符识别 (OCR / OCV)")]
        ReadOCR,            // 文本字符识别与打印质量验证

        [NodeFieldMeta("🎨", "#9A3412", "颜色识别", "HSV 色彩空间提取与测量")]
        ColorIdentify,      // 线束排线颜色检测/有无检测

        [NodeFieldMeta("🤖", "#7C3AED", "深度学习", "深度学习 AI 推理 (YOLO/分类/分割)")]
        DlInference,        // AI 目标检测、分类与缺陷分割

        // ==========================================
        // 6. 🧮 逻辑与算术运算 (MathLogic - #7C3AED 电光紫系)
        // ==========================================
        [NodeFieldMeta("🗺️", "#7C3AED", "坐标转换", "像素坐标系 -> 机械手物理坐标转换")]
        OffsetMath,         // 像素坐标结合标定矩阵转换为机械手物理坐标

        [NodeFieldMeta("🧮", "#6D28D9", "公式计算", "表达式 / 动态公式计算")]
        ScriptMath,         // 利用 DynamicExpresso 进行复杂数学表达式计算

        [NodeFieldMeta("✅", "#05668D", "公差判定", "测量值公差上限/下限 OK/NG 判定")]
        ToleranceCheck,     // 数值公差比较，输出布尔 OK/NG 信号

        [NodeFieldMeta("📝", "#8B5CF6", "字符串格式", "字符串格式化与报文拼装")]
        StringFormat,       // 动态拼接 PLC 报文或 MES 字符串

        // ==========================================
        // 7. 🔀 流程控制与条件分支 (FlowControl - #C026D3 洋红系)
        // ==========================================
        [NodeFieldMeta("🔱", "#C026D3", "条件判断", "条件分支 (If / Else)")]
        ConditionIf,        // 根据测量结果判 OK/NG 走向不同分支

        [NodeFieldMeta("🔀", "#A21CAF", "多路分支", "多路条件分支 (Switch)")]
        SwitchCase,         // 根据物料类型/型号跳转对应逻辑

        [NodeFieldMeta("🔄", "#86198F", "循环控制", "循环控制 (For / While)")]
        ForLoop,            // 多工位重复检测或批量计算

        [NodeFieldMeta("🔁", "#86198F", "条件循环", "基于条件的循环控制 (While)")]
        WhileLoop,          // 满足条件时持续循环，直到条件不成立或超时退出

        [NodeFieldMeta("🔀", "#701A75", "分支汇聚", "数据与控制流合并 (Merge)")]
        Merge,              // 多条分支并行后收拢交汇点

        [NodeFieldMeta("⏳", "#D946EF", "延时等待", "延时等待 (Delay)")]
        Delay,              // 硬件到位等待或延时触发

        [NodeFieldMeta("🚥", "#E879F9", "等待信号", "等待外部状态/触发信号")]
        WaitSignal,         // 阻塞等待 PLC/IO 触发信号

        // ==========================================
        // 8. 📦 Group 子流程 (CompositeGroup - #DB2777 柔粉紫系)
        // ==========================================
        [NodeFieldMeta("📦", "#DB2777", "Group子流程", "Group 子流程 / 模块封装")]
        CompositeFlow,      // 类似 VisionMaster 的 Group 组，实现折叠与复用

        [NodeFieldMeta("🛑", "#9D174D", "终止流程", "流程强制终止 / 跳出")]
        TerminateFlow,      // 触发严重错误时强制终止当前运行

        // ==========================================
        // 9. 🔌 设备通信与硬件控制 (DeviceIO - #2563EB 蓝灰/钢蓝系)
        // ==========================================
        [NodeFieldMeta("📟", "#2563EB", "PLC 读写", "PLC 寄存器读写 (Siemens/Modbus)")]
        PlcReadWrite,       // Siemens/Modbus 寄存器位/字读写

        [NodeFieldMeta("💡", "#3B82F6", "光源控制", "光源控制器 (串口/网口调光)")]
        LightControl,       // 串口/网口动态调节光源亮度

        // ==========================================
        // 10. 💾 数据存储与系统交互 (DataStorage - #475569 炭晶系)
        // ==========================================
        [NodeFieldMeta("💾", "#475569", "图像存盘", "图像异步分类存盘 (OK/NG)")]
        SaveImage,          // OK/NG 图片异步分类保存

        [NodeFieldMeta("📊", "#334155", "数据写盘", "测量数据追加存盘 (CSV/DB)")]
        SaveData,           // 测量结果追加写入本地文件或数据库

        [NodeFieldMeta("🌐", "#64748B", "MES 上报", "MES 系统对接 (WebAPI/MQTT)")]
        MesReport           // WebAPI/HTTP/MQTT 对接 MES 上报数据
    }


}