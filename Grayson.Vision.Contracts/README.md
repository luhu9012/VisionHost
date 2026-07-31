# 0~1全流程分步搭建方案
整体开发环境：VS2026、.NET Framework 4.7、C# 7.4、WPF、Halcon 19.11 C#、Windows；仅做2D视觉。
## 整体解决方案目录规划（最终完整Solution）
```
Grayson.Vision.Host.sln
├─ 【契约层，无任何第三方依赖，绝对稳定】Grayson.Vision.Contracts
├─ 【底层通用工具】Grayson.Vision.Common
├─ 【Halcon封装层，隔离原生HObject】Grayson.Vision.HalconWrapper
├─ 【硬件插件合集（分多个独立dll）】
│   ├─ Plugins.Camera.Hikvision
│   ├─ Plugins.Camera.Basler
│   ├─ Plugins.PLC.S7
│   ├─ Plugins.PLC.Modbus
│   ├─ Plugins.Motion.Zmc
│   └─ Plugins.Robot.Epson
├─ 【视觉业务单元实现库】Grayson.Vision.BusinessUnits
├─ 【宿主WPF主程序（核心调度、UI、执行引擎）】Grayson.Vision.WpfUI
└─ 【辅助工具】Grayson.Vision.BuildHelper（打包编译裁剪工具，可选后期做）
```
开发严格遵守顺序：**契约先行 → 通用工具 → Halcon封装 → 硬件插件 → 业务单元 → 宿主主程序**，严禁反向依赖。

# 阶段1：新建全部项目，配置基础引用
## 步骤1：创建所有项目，统一框架版本
全部项目右键属性：
- 目标框架：.NET Framework 4.7
- C#语言版本：C# 7.4
- 输出：Any CPU / x64（推荐全工程x64，适配Halcon x64）

逐个新建6个基础项目：
1. 类库 Grayson.Vision.Contracts（最顶层，无第三方）
2. 类库 Grayson.Vision.Common
3. 类库 Grayson.Vision.HalconWrapper（引用HalconDotNet）
4. 类库 Grayson.Vision.BusinessUnits（引用Contracts、Common、HalconWrapper）
5. WPF应用 Grayson.Vision.Shell（主程序，引用所有上层类库）
6. 多个硬件插件类库：Plugins.Camera.Hikvision、Plugins.PLC.S7等，全部引用Contracts、Common

### 引用依赖约束（强制）
1. Contracts：**不引用任何第三方dll**（Halcon、相机SDK、PLC SDK一律不能引用）
2. 硬件插件：仅引用 Contracts + Common + 对应厂商SDK，不引用Shell、BusinessUnits
3. BusinessUnits：引用Contracts、Common、HalconWrapper，不引用Shell
4. Shell：引用全部类库，是唯一顶层入口；插件通过反射动态加载，不用项目引用

## 步骤2：Halcon环境统一配置
1. 全部项目引用 `halcondotnet.dll`（Halcon19.11 x64），设置属性：复制到输出目录=如果较新则复制
2. 系统环境变量配置HALCONROOT，保证所有进程可正常加载halcon依赖；
3. 所有硬件SDK统一x64，杜绝AnyCPU导致兼容报错。

# 阶段2：完整编写 Grayson.Vision.Contracts（全量可复制代码）
按之前分层完整落地，一次性写完所有接口、枚举、模型。
文件夹层级：
```
Grayson.Vision.Contracts
├─ Core
│  ├─ Result.cs
│  ├─ Pose3D.cs
│  ├─ VisionContext.cs
│  ├─ ContextDataKeys.cs // 全局共享变量常量
│  └─ Runtime
│     └─ IWorkflowRuntime.cs
├─ Core/Message
│  ├─ MessageBase.cs
│  └─ IMessageBus.cs
├─ Devices
│  ├─ IDevice.cs
│  ├─ ICamera.cs
│  ├─ IPlc.cs
│  ├─ IMotionController.cs
│  └─ IRobot.cs
├─ Business
│  ├─ Enums
│  │  ├─ DeviceState.cs
│  │  └─ BusinessUnitType.cs
│  ├─ IBusinessUnit.cs
│  ├─ ICompositeBusinessUnit.cs
│  ├─ BusinessUnitMetaAttribute.cs
│  └─ Flow                           //流程节点基类与分支 / 循环 / 并行节点
│     ├─ FlowNodeBase.cs
│     ├─ AtomicFlowNode.cs
│     ├─ ConditionFlowNode.cs
│     ├─ LoopFlowNode.cs
│     └─ ParallelFlowNode.cs
├─ Registry                          // 单元注册表模型
│  ├─ BusinessUnitMeta.cs
│  └─ IBusinessUnitRegistry.cs
├─ Recipe                           //Recipe 配方、工位重启持久化模型
│  ├─ IRecipeProvider.cs
│  ├─ StationConfigModel.cs // 工位持久化配置（重启恢复用）
│  └─ RecipeRootModel.cs // 完整配方根实体
└─ Permission
   └─ UserRole.cs // 角色枚举
```

# 全流程带中文注释分阶落地代码
整体环境：VS2026、.NET Framework 4.7、C#7.4、WPF、Halcon19.11 x64，仅2D视觉，全文件添加详细中文注释。
严格按依赖顺序开发：契约层 → 通用层 → Halcon封装 → 硬件插件 → 业务单元 → 宿主Shell。
## 一、解决方案整体目录结构（固定）
```
Grayson.Vision.Host.sln
├─ Grayson.Vision.Contracts 【契约层，无第三方依赖，所有项目依赖此】
├─ Grayson.Vision.Common 【通用工具、序列化、日志、表达式】
├─ Grayson.Vision.HalconWrapper 【Halcon隔离封装，禁止上层直接调用原生Halcon】
├─ Grayson.Vision.BusinessUnits 【所有视觉/PLC/运动业务单元实现】
├─ Plugins.Camera.Hikvision 【海康相机硬件插件】
├─ Plugins.PLC.Modbus 【Modbus PLC插件】
├─ Grayson.Vision.Shell 【WPF主宿主程序，唯一入口】
```
全部项目统一配置：
1. 目标框架：.NET Framework 4.7
2. 语言版本：C# 7.4
3. 平台目标：x64（适配Halcon与各类硬件SDK）
4. 输出路径统一配置，插件编译输出到`./bin/x64/Debug/Plugins`

# 二、Grayson.Vision.Contracts 完整带注释代码
## 2.1 Core/Result.cs 通用返回模型
```csharp
using System;

namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 通用无返回值执行结果
    /// 全系统统一用该对象承载方法调用成功/失败、错误信息、异常
    /// </summary>
    public class Result
    {
        /// <summary>执行是否成功</summary>
        public bool Success { get; set; }

        /// <summary>自定义错误码，0=正常，负数为各类故障</summary>
        public int ErrorCode { get; set; }

        /// <summary>可读中文消息，用于日志与界面提示</summary>
        public string Message { get; set; }

        /// <summary>原始异常对象，用于日志详细排查</summary>
        public Exception Exception { get; set; }

        /// <summary>快捷构建成功结果</summary>
        public static Result Ok()
        {
            return new Result { Success = true, ErrorCode = 0, Message = "执行正常" };
        }

        /// <summary>快捷构建失败结果</summary>
        /// <param name="msg">失败说明</param>
        /// <param name="code">错误编号</param>
        /// <param name="ex">捕获的异常</param>
        public static Result Fail(string msg, int code = -1, Exception ex = null)
        {
            return new Result
            {
                Success = false,
                Message = msg,
                ErrorCode = code,
                Exception = ex
            };
        }
    }

    /// <summary>带泛型返回数据的结果封装</summary>
    /// <typeparam name="T">返回承载的数据类型</typeparam>
    public class Result<T> : Result
    {
        /// <summary>成功时返回的业务数据</summary>
        public T Data { get; set; }

        /// <summary>带返回数据的成功构建</summary>
        public static Result<T> Ok(T data)
        {
            return new Result<T>
            {
                Success = true,
                ErrorCode = 0,
                Message = "执行正常",
                Data = data
            };
        }

        /// <summary>带返回数据的失败构建</summary>
        public new static Result<T> Fail(string msg, int code = -1, Exception ex = null)
        {
            return new Result<T>
            {
                Success = false,
                Message = msg,
                ErrorCode = code,
                Exception = ex
            };
        }
    }
}
```

## 2.2 Core/Pose3D.cs 统一位姿结构体
```csharp
namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 全局统一位姿结构体
    /// 单位：XYZ mm；Rx/Ry/Rz 角度制°，对接Halcon Pose、机器人、运动轴坐标
    /// 所有定位、机械手移动全部使用该结构，避免多套坐标格式
    /// </summary>
    public struct Pose3D
    {
        /// <summary>X轴平移</summary>
        public double X;
        /// <summary>Y轴平移</summary>
        public double Y;
        /// <summary>Z轴平移</summary>
        public double Z;
        /// <summary>绕X旋转</summary>
        public double Rx;
        /// <summary>绕Y旋转</summary>
        public double Ry;
        /// <summary>绕Z旋转</summary>
        public double Rz;

        public Pose3D(double x, double y, double z, double rx, double ry, double rz)
        {
            X = x;
            Y = y;
            Z = z;
            Rx = rx;
            Ry = ry;
            Rz = rz;
        }
    }
}
```

## 2.3 Core/ContextDataKeys.cs 全局上下文常量（杜绝魔法字符串）
```csharp
namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// VisionContext.SharedData 所有Key常量统一管理
    /// 所有业务单元读写共享数据必须使用此处常量，禁止手写字符串
    /// 便于全项目统一维护、编辑器校验、文档梳理
    /// </summary>
    public static class ContextDataKeys
    {
        #region 图像采集相关
        /// <summary>本次相机采集原始图像HObject</summary>
        public const string Grab_SourceImage = "Grab.SourceImage";
        /// <summary>相机触发时间戳</summary>
        public const string Grab_TriggerTime = "Grab.TriggerTimestamp";
        #endregion

        #region 定位匹配相关
        /// <summary>匹配是否成功 bool</summary>
        public const string Match_IsSuccess = "Match.IsSuccess";
        /// <summary>工件定位位姿 Pose3D</summary>
        public const string Match_WorkPose = "Match.Pose3D";
        #endregion

        #region 尺寸测量
        /// <summary>尺寸测量结果集合</summary>
        public const string Measure_DimensionResult = "Measure.DimensionData";
        #endregion

        #region 缺陷检测
        /// <summary>缺陷总数量 int</summary>
        public const string Defect_Count = "Defect.TotalCount";
        /// <summary>缺陷明细列表</summary>
        public const string Defect_List = "Defect.ItemList";
        #endregion

        #region 机器人运动
        /// <summary>机器人目标移动位姿 Pose3D</summary>
        public const string Robot_TargetPose = "Robot.TargetPose";
        #endregion

        #region PLC交互
        /// <summary>PLC输入信号集合</summary>
        public const string Plc_InputSignal = "Plc.InputSignal";
        /// <summary>PLC待写入输出数据</summary>
        public const string Plc_OutputSignal = "Plc.OutputSignal";
        #endregion
    }
}
```

## 2.4 Core/VisionContext.cs 单次流程全局上下文
```csharp
using System.Collections.Generic;
using HalconDotNet;

namespace Grayson.Vision.Contracts.Core
{
    /// <summary>
    /// 单次工位触发流程的运行上下文
    /// 规则：每次相机触发新建独立实例，流程结束必须调用CleanAllResource释放资源
    /// 禁止全局单例复用，防止多工位数据串扰、内存堆积
    /// </summary>
    public class VisionContext
    {
        /// <summary>本次检测原始图像，单次流程唯一主图</summary>
        public HObject SourceImage { get; set; }

        /// <summary>工件定位输出的基准位姿</summary>
        public Pose3D WorkpiecePose { get; set; }

        /// <summary>跨单元共享数据字典，所有临时变量存放位置</summary>
        public Dictionary<string, object> SharedData { get; set; } = new Dictionary<string, object>();

        /// <summary>本次触发Unix时间戳</summary>
        public long TriggerTimestamp { get; set; }

        /// <summary>
        /// 流程执行完毕强制资源清理
        /// 释放Halcon图像、清空临时字典，杜绝内存泄漏
        /// </summary>
        public void CleanAllResource()
        {
            // 释放Halcon图像资源
            if (SourceImage != null)
            {
                SourceImage.Dispose();
                SourceImage = null;
            }
            // 清空所有临时业务变量
            SharedData.Clear();
        }
    }
}
```

## 2.5 Core/Runtime/IWorkflowRuntime.cs 运行时门面（单元访问硬件/日志/UI唯一入口）
```csharp
using Grayson.Vision.Contracts.Core.Message;
using Grayson.Vision.Contracts.Devices;
using System;

namespace Grayson.Vision.Contracts.Core.Runtime
{
    /// <summary>
    /// 业务单元运行时服务门面
    /// 所有IBusinessUnit禁止直接引用宿主、硬件实例、日志类
    /// 全部通过此接口获取硬件、打印日志、推送UI消息，实现彻底解耦
    /// 每个流程执行器持有独立Runtime实例
    /// </summary>
    public interface IWorkflowRuntime
    {
        /// <summary>根据配置的DeviceKey获取硬件设备</summary>
        Result<IDevice> GetDevice(string deviceKey);

        /// <summary>全局消息总线</summary>
        IMessageBus MessageBus { get; }

        #region 分级日志输出
        void LogTrace(string msg);
        void LogInfo(string msg);
        void LogWarn(string msg);
        void LogError(string msg, Exception ex = null);
        #endregion

        /// <summary>推送UI事件，单元只发消息，不操作任何WPF控件</summary>
        void PublishUiEvent(string eventKey, object payload);
    }
}
```

## 2.6 Core/Message 消息总线相关
### MessageBase.cs
```csharp
namespace Grayson.Vision.Contracts.Core.Message
{
    /// <summary>所有总线消息父类</summary>
    public abstract class MessageBase
    {
        /// <summary>消息发送方标识（工位ID/单元名称）</summary>
        public string Sender { get; set; }
        /// <summary>消息发生时间戳</summary>
        public long Timestamp { get; set; }
    }

    /// <summary>硬件设备状态变更消息</summary>
    public class DeviceStateChangedMessage : MessageBase
    {
        public string DeviceKey { get; set; }
        public Business.Enums.DeviceState NewState { get; set; }
    }

    /// <summary>单条视觉流程执行完成消息</summary>
    public class WorkflowFinishMessage : MessageBase
    {
        public string StationId { get; set; }
        public bool IsPass { get; set; }
    }
}
```
### IMessageBus.cs
```csharp
using System;

namespace Grayson.Vision.Contracts.Core.Message
{
    /// <summary>进程内消息总线，模块/插件/宿主解耦通信</summary>
    public interface IMessageBus
    {
        /// <summary>发布一条消息</summary>
        void Publish<T>(T msg) where T : MessageBase;

        /// <summary>订阅指定类型消息</summary>
        void Subscribe<T>(Action<T> callback) where T : MessageBase;

        /// <summary>取消订阅</summary>
        void Unsubscribe<T>(Action<T> callback) where T : MessageBase;
    }
}
```

## 2.7 Business/Enums 枚举
### DeviceState.cs
```csharp
namespace Grayson.Vision.Contracts.Business.Enums
{
    /// <summary>所有硬件通用状态</summary>
    public enum DeviceState
    {
        /// <summary>未连接</summary>
        Disconnected,
        /// <summary>连接中</summary>
        Connecting,
        /// <summary>正常在线</summary>
        Connected,
        /// <summary>故障异常</summary>
        Error
    }
}
```
### BusinessUnitType.cs
```csharp
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
```

## 2.8 Business/BusinessUnitMetaAttribute.cs 单元标记特性
```csharp
using Grayson.Vision.Contracts.Business.Enums;
using System;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 业务单元标记特性
    /// 所有IBusinessUnit实现类必须加此标记，程序启动靠反射读取元数据
    /// 自动生成UI工具箱、自动创建实例、分类展示
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class BusinessUnitMetaAttribute : Attribute
    {
        /// <summary>全局唯一模块ID，序列化、创建实例依靠该ID</summary>
        public string ModuleId { get; }

        /// <summary>UI显示友好名称</summary>
        public string DisplayName { get; }

        /// <summary>单元所属大类</summary>
        public BusinessUnitType UnitType { get; }

        /// <summary>UI分组名称：视觉算法/PLC/运动/脚本</summary>
        public string Category { get; }

        /// <summary>单元功能描述，鼠标悬浮提示</summary>
        public string Description { get; }

        public BusinessUnitMetaAttribute(string moduleId, string displayName, BusinessUnitType unitType, string category, string desc)
        {
            ModuleId = moduleId;
            DisplayName = displayName;
            UnitType = unitType;
            Category = category;
            Description = desc;
        }
    }
}
```

## 2.9 Business/IBusinessUnit.cs 所有业务单元顶层接口
```csharp
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;
using Grayson.Vision.Contracts.Business.Enums;
using System.Collections.Generic;
using System.Windows.Controls;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 所有原子视觉、PLC、运动、数据单元统一顶层接口
    /// 所有可拖拽流程节点的业务逻辑都实现此接口
    /// 支持执行、参数序列化、独立配置面板
    /// </summary>
    public interface IBusinessUnit
    {
        /// <summary>唯一模块ID，和特性标记保持一致</summary>
        string ModuleId { get; }

        /// <summary>界面展示名称</summary>
        string ModuleName { get; }

        /// <summary>单元业务分类</summary>
        BusinessUnitType UnitType { get; }

        /// <summary>当前节点是否启用，可在画布临时禁用不走逻辑</summary>
        bool Enable { get; set; }

        /// <summary>本单元需要读取的上下文Key列表，编辑器做依赖校验</summary>
        IReadOnlyList<string> InputKeys { get; }

        /// <summary>本单元执行完成写入的上下文Key列表</summary>
        IReadOnlyList<string> OutputKeys { get; }

        /// <summary>
        /// 单元核心执行逻辑
        /// </summary>
        /// <param name="context">单次流程上下文</param>
        /// <param name="runtime">运行时服务，用来拿硬件、打日志、推UI</param>
        Result Execute(VisionContext context, IWorkflowRuntime runtime);

        /// <summary>把单元当前所有配置参数导出字典，存入配方</summary>
        Dictionary<string, object> SaveRecipe();

        /// <summary>从配方字典加载参数</summary>
        void LoadRecipe(Dictionary<string, object> recipeDict);

        /// <summary>返回WPF配置面板，双击节点弹出参数配置</summary>
        UserControl GetConfigPanel();
    }
}
```

## 2.10 Business/ICompositeBusinessUnit.cs 复合子流程单元
```csharp
using Grayson.Vision.Contracts.Business.Flow;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Business
{
    /// <summary>
    /// 复合业务单元：内部嵌套完整子工作流
    /// 对外依旧是IBusinessUnit，实现组合模式，兼顾灵活与复用
    /// 适用于粗定位+ROI+精定位这种多工位重复固定组合
    /// </summary>
    public interface ICompositeBusinessUnit : IBusinessUnit
    {
        /// <summary>内部嵌套子流程节点树</summary>
        List<FlowNodeBase> SubFlowNodes { get; set; }

        /// <summary>打开子流程独立编辑器画布</summary>
        void OpenSubFlowEditor();
    }
}
```

## 2.11 Business.Flow 流程节点基类与分支/循环/并行节点
### FlowNodeBase.cs
```csharp
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>所有流程节点父类，分为业务节点、控制节点</summary>
    public abstract class FlowNodeBase
    {
        /// <summary>节点全局唯一ID，配方序列化标识节点</summary>
        public string NodeId { get; set; }

        /// <summary>节点是否启用，关闭则跳过执行</summary>
        public bool Enable { get; set; }

        /// <summary>递归执行当前节点逻辑</summary>
        public abstract Result Execute(VisionContext context, IWorkflowRuntime runtime);
    }
}
```
### AtomicFlowNode.cs 普通业务单元节点
```csharp
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>承载IBusinessUnit的普通执行节点</summary>
    public class AtomicFlowNode : FlowNodeBase
    {
        /// <summary>绑定的业务单元ModuleId</summary>
        public string ModuleId { get; set; }

        /// <summary>运行时实例化后的单元对象</summary>
        public IBusinessUnit BindUnit { get; set; }

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 节点关闭、单元不存在、单元自身关闭，直接跳过
            if (!Enable || BindUnit == null || !BindUnit.Enable)
                return Result.Ok();
            return BindUnit.Execute(context, runtime);
        }
    }
}
```
### ConditionFlowNode.cs 条件分支节点
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>If Else条件分支控制节点</summary>
    public class ConditionFlowNode : FlowNodeBase
    {
        /// <summary>C#风格条件表达式，DynamicExpresso解析</summary>
        public string ConditionExpr { get; set; }

        /// <summary>条件成立执行的子节点列表</summary>
        public List<FlowNodeBase> TrueBranch { get; set; } = new List<FlowNodeBase>();

        /// <summary>条件不成立执行分支</summary>
        public List<FlowNodeBase> FalseBranch { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            // 执行逻辑宿主WorkflowExecutor内部实现，契约只定义结构
            return Result.Ok();
        }
    }
}
```
### LoopFlowNode.cs 循环重试节点
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>循环执行节点，多用于拍照重试3次</summary>
    public class LoopFlowNode : FlowNodeBase
    {
        /// <summary>最大循环次数</summary>
        public int MaxLoopTimes { get; set; }

        /// <summary>跳出循环的条件表达式</summary>
        public string BreakExpr { get; set; }

        /// <summary>循环体内执行节点</summary>
        public List<FlowNodeBase> BodyNodes { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            return Result.Ok();
        }
    }
}
```
### ParallelFlowNode.cs 并行多节点（多相机同时拍照）
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Core.Runtime;

namespace Grayson.Vision.Contracts.Business.Flow
{
    /// <summary>并行执行节点，多相机同步抓拍使用</summary>
    public class ParallelFlowNode : FlowNodeBase
    {
        public List<FlowNodeBase> ParallelNodes { get; set; } = new List<FlowNodeBase>();

        public override Result Execute(VisionContext context, IWorkflowRuntime runtime)
        {
            return Result.Ok();
        }
    }
}
```

## 2.12 Devices 硬件统一接口
### IDevice.cs 硬件根接口
```csharp
using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>所有硬件顶层接口：相机、PLC、运动卡、机器人全部实现</summary>
    public interface IDevice
    {
        /// <summary>设备唯一标识Key，配置绑定用，不和IP绑定</summary>
        string DeviceKey { get; }

        /// <summary>硬件品牌名称：Hikvision/Basler/Siemens/Modbus</summary>
        string BrandName { get; }

        /// <summary>当前硬件在线状态</summary>
        DeviceState State { get; }

        /// <summary>连接硬件设备</summary>
        Result Connect();

        /// <summary>断开连接，释放SDK所有资源</summary>
        Result Disconnect();

        /// <summary>主动轮询硬件在线状态</summary>
        Result CheckStatus();

        /// <summary>设置硬件参数（曝光、波特率等）</summary>
        Result SetParam(string key, object value);

        /// <summary>读取硬件参数</summary>
        Result<object> GetParam(string key);
    }
}
```
### ICamera.cs 工业相机接口
```csharp
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>工业相机通用接口，所有品牌相机统一规范</summary>
    public interface ICamera : IDevice
    {
        /// <summary>软触发单次拍照</summary>
        Result SoftTrigger();

        /// <summary>同步采集一张图像返回HObject</summary>
        Result GrabImage(out HObject image);

        /// <summary>开启连续流采集</summary>
        Result StartContinuousGrab();

        /// <summary>停止连续采集</summary>
        Result StopContinuousGrab();
    }
}
```
### IPlc.cs PLC读写接口
```csharp
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>PLC通用点位读写接口，适配Modbus、S7等协议</summary>
    public interface IPlc : IDevice
    {
        Result ReadBit(string addr, out bool val);
        Result WriteBit(string addr, bool val);

        Result ReadInt(string addr, out int val);
        Result WriteInt(string addr, int val);

        Result ReadFloat(string addr, out float val);
        Result WriteFloat(string addr, float val);
    }
}
```
### IMotionController.cs 运动控制卡
```csharp
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>多轴运动控制卡通用接口</summary>
    public interface IMotionController : IDevice
    {
        /// <summary>轴绝对定位移动</summary>
        Result MoveAbsolute(int axis, double pos, double vel);

        /// <summary>相对位移移动</summary>
        Result MoveRelative(int axis, double delta, double vel);

        /// <summary>轴急停</summary>
        Result EmergencyStop(int axis);

        /// <summary>读取当前轴实际位置</summary>
        Result<double> GetAxisPos(int axis);
    }
}
```
### IRobot.cs 六轴机械手接口
```csharp
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>工业六轴机器人通用接口</summary>
    public interface IRobot : IDevice
    {
        /// <summary>移动到目标世界位姿</summary>
        Result MoveToPose(Pose3D pose, double speedPercent);

        /// <summary>读取机器人当前末端位姿</summary>
        Result<Pose3D> GetCurrentPose();

        /// <summary>设置工具坐标系偏移补偿</summary>
        Result SetToolOffset(Pose3D offset);
    }
}
```

## 2.13 Registry 单元注册表模型
### BusinessUnitMeta.cs
```csharp
using Grayson.Vision.Contracts.Business.Enums;
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Registry
{
    /// <summary>单元反射元数据实体，注册表扫描后缓存该信息</summary>
    public class BusinessUnitMeta
    {
        public string ModuleId { get; set; }
        public string DisplayName { get; set; }
        public BusinessUnitType UnitType { get; set; }
        public string Category { get; set; }
        public string Description { get; set; }
        /// <summary>单元实现完整类型，反射实例化使用</summary>
        public Type ImplementType { get; set; }
    }
}
```
### IBusinessUnitRegistry.cs
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Business;

namespace Grayson.Vision.Contracts.Registry
{
    /// <summary>全局业务单元注册表，负责扫描、创建所有业务单元</summary>
    public interface IBusinessUnitRegistry
    {
        /// <summary>获取所有单元元数据，用于WPF工具箱渲染</summary>
        List<BusinessUnitMeta> GetAllMetaList();

        /// <summary>根据ModuleId反射创建单元实例</summary>
        Result<IBusinessUnit> CreateUnit(string moduleId);

        /// <summary>扫描指定目录下所有dll，加载带标记的业务单元</summary>
        void ScanAllAssembly(string pluginDir);
    }
}
```

## 2.14 Recipe 配方、工位重启持久化模型
### StationConfigModel.cs
```csharp
using System.Collections.Generic;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>工位运行状态枚举，用于重启恢复判断</summary>
    public enum StationRunStatus
    {
        Stopped,
        Running,
        Paused,
        Error
    }

    /// <summary>全局工位配置，独立持久化，不受配方变更影响，软件重启恢复核心</summary>
    public class StationConfigModel
    {
        /// <summary>工位唯一ID</summary>
        public string StationId { get; set; }

        /// <summary>工位页面展示名称</summary>
        public string StationDisplayName { get; set; }

        /// <summary>该工位绑定的硬件Key列表</summary>
        public List<string> BindDeviceKeys { get; set; } = new List<string>();

        /// <summary>上次运行加载的配方ID，重启自动加载该配方</summary>
        public string LastActiveRecipeId { get; set; }

        /// <summary>软件正常退出时记录的工位运行状态</summary>
        public StationRunStatus LastRunStatus { get; set; }

        /// <summary>开机是否自动恢复上次运行状态，可手动关闭</summary>
        public bool AutoRestoreOnStartup { get; set; } = true;
    }
}
```
### RecipeRootModel.cs 完整配方结构
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Business.Flow;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>整套产品配方实体：包含流程拓扑+所有节点参数</summary>
    public class RecipeRootModel
    {
        /// <summary>配方唯一ID</summary>
        public string RecipeId { get; set; }

        /// <summary>适配产品型号</summary>
        public string ProductModel { get; set; }

        /// <summary>配方版本号，用于迭代管理</summary>
        public string Version { get; set; }

        /// <summary>创建时间戳</summary>
        public long CreateTime { get; set; }

        /// <summary>完整工位流程节点树</summary>
        public List<FlowNodeBase> WholeFlowNodes { get; set; } = new List<FlowNodeBase>();

        /// <summary>键：NodeId，值：当前节点所有单元参数字典</summary>
        public Dictionary<string, Dictionary<string, object>> NodeRecipeDict { get; set; } = new Dictionary<string, Dictionary<string, object>>();
    }
}
```
### IRecipeProvider.cs 配方读写抽象
```csharp
using System.Collections.Generic;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Recipe
{
    /// <summary>配方、工位配置持久化读写接口，宿主用JSON/LiteDB实现</summary>
    public interface IRecipeProvider
    {
        /// <summary>保存全部工位全局配置（重启恢复用）</summary>
        Result SaveStationConfig(List<StationConfigModel> allStations);

        /// <summary>加载所有工位配置</summary>
        Result<List<StationConfigModel>> LoadAllStationConfigs();

        /// <summary>保存单条产品配方</summary>
        Result SaveRecipe(RecipeRootModel recipe);

        /// <summary>根据ID读取配方</summary>
        Result<RecipeRootModel> LoadRecipe(string recipeId);

        Result DeleteRecipe(string recipeId);

        /// <summary>获取全部配方ID清单</summary>
        Result<List<string>> GetAllRecipeIds();
    }
}
```

## 2.15 Permission/UserRole.cs 三级权限枚举
```csharp
namespace Grayson.Vision.Contracts.Permission
{
    /// <summary>系统三级角色权限划分，控制菜单、编辑功能显隐</summary>
    public enum UserRole
    {
        /// <summary>操作员：仅可切换已有配方，不能编辑流程、参数</summary>
        Operator,
        /// <summary>工程师：可编辑流程、调参、标定，不可修改账号权限</summary>
        Engineer,
        /// <summary>管理员：全功能开放，权限、硬件配置均可修改</summary>
        Admin
    }
}
```






# 阶段3：搭建 Grayson.Vision.Common 通用工具层
内置通用能力，全工程复用，文件夹：
```
Grayson.Vision.Common
├─ Logging // 日志封装
├─ JsonHelper // Newtonsoft.Json序列化（统一全局序列化规则）
├─ FileHelper // 文件目录操作
├─ ThreadSafe // 线程锁、任务封装
└─ ExpressionEval // DynamicExpresso 表达式工具（条件节点用）
```
1. 引入NuGet：Newtonsoft.Json、DynamicExpresso
2. 统一Json序列化配置：忽略空值、支持结构体序列化、兼容Pose3D
3. 日志封装提供静态调用，统一日志格式，可输出文件+控制台
4. 文件工具封装目录创建、自动清理、路径拼接

# 阶段4：Grayson.Vision.HalconWrapper（Halcon隔离层）
目标：**所有原生HObject操作收拢在此层，上层业务禁止直接调用Halcon原生API**
文件夹：
```
Grayson.Vision.HalconWrapper
├─ ImageTool // 图像采集、滤波、阈值、形态学
├─ MatchTool // 模板匹配、形状匹配2D
├─ MeasureTool // 边缘、距离、角度测量
├─ DefectTool // 斑点、划痕检测
├─ CalibTool // 九点标定、手眼标定
└─ HalconMemoryHelper // HObject安全释放、内存池简易封装
```
规范：
1. 所有方法接收/返回封装后的图像类，自带Dispose管理；
2. 封装统一异常捕获，返回Result结构；
3. BusinessUnits只调用Wrapper，不using HalconDotNet。

# 阶段5：硬件插件开发（逐个开发，以海康相机举例）
以Plugins.Camera.Hikvision为例：
1. 引用：Contracts、Common、海康相机SDK、x64编译；
2. 新建HikCameraDevice : ICamera，实现全部接口Connect/GrabImage；
3. 所有SDK资源在Disconnect里彻底释放；
4. 其他PLC、运动卡、机器人插件全部一套范式实现。

> 插件统一输出到主程序Plugins文件夹，宿主启动时扫描该目录反射加载。

# 阶段6：视觉业务单元库 Grayson.Vision.BusinessUnits
所有原子单元、脚本单元写在此项目，每个单元加`[BusinessUnitMeta]`标记。
文件夹划分：
```
BusinessUnits
├─ Grab
│  └─ CameraGrabUnit.cs
├─ Preprocess
│  ├─ GaussFilterUnit.cs
│  ├─ ThresholdUnit.cs
│  └─ MorphologyUnit.cs
├─ Location
│  ├─ TemplateMatch2DUnit.cs
│  └─ ShapeMatchUnit.cs
├─ Measure
│  ├─ DistanceMeasureUnit.cs
│  └─ EdgeSizeUnit.cs
├─ Defect
│  ├─ BlobDetectUnit.cs
│  └─ ScratchUnit.cs
├─ PlcIo
│  ├─ PlcReadUnit.cs
│  └─ PlcWriteUnit.cs
├─ MotionRobot
│  ├─ RobotPoseMoveUnit.cs
│  └─ MotionAbsUnit.cs
├─ DataProcess
│  ├─ DataMappingUnit.cs
│  └─ ScriptGlueUnit.cs
└─ Composite // 常用复合子流程单元
```
每个单元必须实现：
InputKeys、OutputKeys、SaveRecipe/LoadRecipe、GetConfigPanel(WPF UserControl)。

# 阶段7：宿主主程序 Grayson.Vision.Shell（WPF核心）
## 7.1 宿主内部模块划分（后台逻辑层）
```
Shell
├─ App.xaml 程序入口，启动加载、异常全局捕获
├─ CoreEngine
│  ├─ HardwareManager.cs 硬件总管理器，维护所有IDevice实例池
│  ├─ PluginLoader.cs 扫描Plugins目录加载硬件插件
│  ├─ UnitRegistryService 实现IBusinessUnitRegistry
│  ├─ WorkflowExecutor.cs 流程递归执行引擎（核心）
│  ├─ RecipeService 实现IRecipeProvider，使用Json+LiteDB落地
│  ├─ AppMessageBus 实现IMessageBus
│  └─ WorkflowRuntime 实现IWorkflowRuntime，每个执行器独立实例
├─ StationRuntime
│  └─ StationInstance.cs 单个工位运行容器：配方、执行器、状态、硬件绑定
├─ Permission
│  └─ UserAuthService 登录、角色权限判断
├─ AppConfig
│  └─ GlobalAppSetting.cs 全局硬件配置、路径、MES参数
└─ UI 分层MVVM
```

## 7.2 UI分层（严格MVVM，无后台代码逻辑）
### 布局双模式实现（AvalonDock）
1. NuGet引入AvalonDock；
2. 两套布局配置文件：RuntimeLayout.config（操作工简洁）、DebugLayout.config（工程师HDevelop停靠布局）；
3. 登录角色/手动按钮切换布局，自动加载对应停靠配置；

### 一级菜单7个，权限动态显隐
1. 工位总览监控
2. 工作流编辑器（工程师/管理员）
3. 配方管理
4. 数据追溯
5. 工具集（标定、模板训练）
6. 报警日志
7. 系统设置（仅管理员）

### 页面拆分
1. OverviewView：多工位卡片总览首页
2. SingleStationView：单工位大图监控
3. WorkflowEditorView：Dock停靠流程画布、工具箱、变量监视器、日志
4. RecipeManageView：配方列表、导入导出、型号切换
5. DataQueryView：NG图片、报表、良率统计
6. CalibrationToolView：各类标定工具
7. AlarmView：报警列表
8. SystemSettingView：硬件配置、账号、存储路径

## 7.3 重启自动恢复完整逻辑（宿主StationInstance启动逻辑）
1. App启动读取StationConfigModel配置文件；
2. 循环实例化StationInstance；
3. 按LastActiveRecipeId加载配方；
4. HardwareManager重连绑定的DeviceKey硬件；
5. 判断LastRunStatus+AutoRestore：
   运行状态则调用StationInstance.StartRun()；停止则只加载不启动；
6. 单个工位硬件失败仅本工位告警，其余工位正常恢复。

### 持久化写入时机
- 正常关闭软件；
- 人工启停工位；
- 手动切换配方；
仅这三类场景写StationConfigModel到本地json，不高频落盘。

# 阶段8：分步联调测试顺序（不能乱）
1. 单元注册表调试：启动自动扫描所有BusinessUnits，工具箱正常列出全部单元；
2. 硬件插件调试：单独写测试代码实例化海康/PLC，保证Connect/Grab正常；
3. 单个业务单元单元测试：CameraGrabUnit单独调用Execute跑通拍照；
4. 流程引擎线性执行：搭建采集→匹配→PLC输出串行流程，完整跑通；
5. 分支、循环节点测试，DynamicExpresso表达式正常判断；
6. 配方完整序列化：保存配方json，重新加载参数无丢失；
7. 多工位隔离：工位A报错，工位B不受影响；
8. 重启断电测试：关闭软件修改工位状态，重启自动复原运行；
9. 权限测试：操作员无法打开编辑器，仅能切换配方；
10. 长期稳定性72h：监控内存，图像正常Dispose，无泄漏。

# 阶段9：收尾配套
1. 日志滚动、NG图片自动按日期归档、磁盘容量预警；
2. MES对接补充TCP/OPCUA上传结果；
3. 打包工具：BuildHelper实现勾选模块，MSBuild条件编译裁剪dll，产出最小交付包；
4. 制作部署文档：dll依赖清单、Halcon环境、SDK安装清单。

# 交付扩展建议
你需要我接下来优先输出哪一份可直接复制落地代码？
1. WorkflowExecutor完整递归执行代码（分支/循环/并行全部实现）
2. StationInstance工位运行容器完整C#代码
3. AvalonDock双布局XAML完整骨架
4. 海康相机插件完整实现Demo
5. 2D模板匹配业务单元完整实现Demo

















# 分步实现明细

# 阶段3：Grayson.Vision.Common 完整搭建（全中文注释，适配 .NET Framework 4.7、C#7.4）
## 一、项目基础配置
1. 新建类库项目 `Grayson.Vision.Common`
2. 目标框架：**.NET Framework 4.7**，C#语言版本：7.4，平台目标：x64
3. NuGet 必装包：
   - Newtonsoft.Json 13.0.3（序列化统一标准）
   - DynamicExpresso 2.11.0（流程条件表达式解析）
4. 项目引用：仅依赖 `Grayson.Vision.Contracts`，无其他业务层引用
5. 编译输出路径统一配置到解决方案公共bin目录

## 二、完整目录结构
```
Grayson.Vision.Common
├─ Helpers
│  ├─ FileHelper.cs         文件、目录、路径工具
│  ├─ JsonSerializerHelper  全局统一JSON序列化工具
│  ├─ ExpressionHelper.cs   DynamicExpresso表达式解析封装
│  └─ ThreadSafeHelper.cs   线程锁、异步任务通用封装
├─ Logging
│  ├─ LogLevel.cs           日志分级枚举
│  └─ GlobalLogger.cs       全局静态日志管理器（文件+控制台双输出）
└─ Extensions
   └─ ObjectExtension.cs    通用对象扩展方法（判空、类型转换）
```

## 三、逐个完整可复制代码（全量中文注释）
### 1. Logging/LogLevel.cs 日志分级
```csharp
namespace Grayson.Vision.Common.Logging
{
    /// <summary>
    /// 日志分级，和宿主、单元日志输出一一对应
    /// Trace<Info<Warn<Error，可配置日志级别过滤低级日志
    /// </summary>
    public enum LogLevel
    {
        /// <summary>跟踪日志，仅调试排查用，生产环境默认关闭</summary>
        Trace,
        /// <summary>正常运行信息，拍照成功、设备连接成功等常规记录</summary>
        Info,
        /// <summary>警告，非阻断故障，相机偶尔闪断、参数接近阈值</summary>
        Warn,
        /// <summary>错误，流程执行失败、硬件通讯报错，影响单次检测</summary>
        Error
    }
}
```

### 2. Logging/GlobalLogger.cs 全局日志工具
```csharp
using System;
using System.IO;
using System.Text;
using System.Threading;
using Grayson.Vision.Common.Helpers;

namespace Grayson.Vision.Common.Logging
{
    /// <summary>
    /// 全局静态日志工具类
    /// 统一所有模块日志输出格式，支持落地本地文件+控制台打印
    /// 按日期分文件夹存储日志，自动滚动，防止单个日志文件过大
    /// 所有业务单元、硬件插件、宿主禁止自建日志写入，统一调用此类
    /// </summary>
    public static class GlobalLogger
    {
        #region 静态配置项
        /// <summary>日志根目录路径</summary>
        private static string _logRootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

        /// <summary>当前生效最低日志级别，低于该级别不会输出</summary>
        public static LogLevel MinLogLevel { get; set; } = LogLevel.Trace;

        /// <summary>是否开启控制台打印日志，调试开，产线可关闭</summary>
        public static bool EnableConsoleOutput { get; set; } = true;

        /// <summary>是否写入本地日志文件</summary>
        public static bool EnableFileOutput { get; set; } = true;

        /// <summary>多线程写入文件锁，防止并发日志错乱、文件占用</summary>
        private static readonly object _fileWriteLock = new object();
        #endregion

        #region 对外静态打印方法（全系统统一入口）
        public static void Trace(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Trace, sender, message, null);
        }

        public static void Info(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Info, sender, message, null);
        }

        public static void Warn(string message, string sender = "Unknown")
        {
            WriteLog(LogLevel.Warn, sender, message, null);
        }

        public static void Error(string message, Exception ex = null, string sender = "Unknown")
        {
            WriteLog(LogLevel.Error, sender, message, ex);
        }
        #endregion

        #region 内部日志拼接写入逻辑
        /// <summary>统一日志组装、分发到控制台/文件</summary>
        private static void WriteLog(LogLevel level, string sender, string msg, Exception ex)
        {
            // 低于配置最低级别，直接丢弃日志
            if (level < MinLogLevel)
                return;

            // 拼装完整日志内容
            DateTime now = DateTime.Now;
            StringBuilder sb = new StringBuilder();
            sb.Append($"[{now:yyyy-MM-dd HH:mm:ss.fff}]");
            sb.Append($"[{level.ToString().ToUpper()}]");
            sb.Append($"[{sender}] ");
            sb.Append(msg);

            // 追加异常堆栈
            if (ex != null)
            {
                sb.AppendLine();
                sb.Append($"异常详情：{ex.Message}");
                sb.AppendLine();
                sb.Append($"堆栈：{ex.StackTrace}");
            }

            string fullLogText = sb.ToString();

            // 控制台输出
            if (EnableConsoleOutput)
            {
                Console.WriteLine(fullLogText);
            }

            // 文件落地，加锁保证线程安全
            if (EnableFileOutput)
            {
                WriteLogToFile(now, fullLogText);
            }
        }

        /// <summary>按天拆分日志文件写入</summary>
        private static void WriteLogToFile(DateTime logTime, string content)
        {
            try
            {
                lock (_fileWriteLock)
                {
                    // 按日期创建子目录
                    string dayFolder = Path.Combine(_logRootPath, logTime.ToString("yyyy-MM-dd"));
                    FileHelper.EnsureDirectoryExists(dayFolder);
                    // 每日一个日志文件
                    string logFilePath = Path.Combine(dayFolder, "Runtime.log");

                    // 追加写入
                    File.AppendAllText(logFilePath, content + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                // 日志写入失败兜底，防止日志异常导致主程序报错
                Console.WriteLine($"日志文件写入失败：{ex.Message}");
            }
        }
        #endregion

        #region 外部配置修改接口
        /// <summary>动态修改日志根路径</summary>
        public static void SetLogRootPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _logRootPath = path;
            }
        }
        #endregion
    }
}
```

### 3. Helpers/FileHelper.cs 文件目录工具
```csharp
using System;
using System.IO;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 文件、文件夹路径通用工具
    /// 封装目录创建、文件删除、过期文件清理、路径拼接，全项目统一调用
    /// 适配日志、NG图片、配方文件、模板文件管理
    /// </summary>
    public static class FileHelper
    {
        /// <summary>
        /// 路径不存在则创建目录，存在无操作
        /// </summary>
        public static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        /// <summary>
        /// 删除指定文件，文件不存在不抛异常
        /// </summary>
        public static void SafeDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception ex)
            {
                GlobalLogger.GlobalLogger.Warn($"文件删除失败：{filePath}，{ex.Message}", nameof(FileHelper));
            }
        }

        /// <summary>
        /// 清理文件夹内N天前的过期文件，用于自动清理老旧NG图片、日志
        /// </summary>
        /// <param name="folderPath">目标文件夹</param>
        /// <param name="keepDay">保留天数，超出天数删除</param>
        public static void CleanExpiredFiles(string folderPath, int keepDay)
        {
            if (!Directory.Exists(folderPath))
                return;

            DateTime expireTime = DateTime.Now.AddDays(-keepDay);
            string[] allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);

            foreach (string file in allFiles)
            {
                try
                {
                    FileInfo fi = new FileInfo(file);
                    // 按文件最后修改时间判断过期
                    if (fi.LastWriteTime < expireTime)
                    {
                        fi.Delete();
                    }
                }
                catch
                {
                    // 单个文件删除失败不阻断整体清理
                }
            }
        }

        /// <summary>
        /// 获取程序运行根目录（exe所在目录）
        /// </summary>
        public static string GetAppBasePath()
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// 拼接完整路径，自动兼容正反斜杠
        /// </summary>
        public static string CombinePath(params string[] paths)
        {
            return Path.Combine(paths);
        }
    }
}
```

### 4. Helpers/JsonSerializerHelper.cs JSON序列化统一封装
```csharp
using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 全局统一JSON序列化工具
    /// 配方、工位配置、流程节点全部使用此类序列化/反序列化
    /// 统一配置格式、时间、浮点、结构体处理，保证全系统JSON格式一致
    /// 兼容Pose3D结构体、枚举、DateTime
    /// </summary>
    public static class JsonSerializerHelper
    {
        /// <summary>全局固定序列化配置</summary>
        private static readonly JsonSerializerSettings _globalSettings;

        static JsonSerializerHelper()
        {
            _globalSettings = new JsonSerializerSettings
            {
                // 缩进格式化，方便人工打开JSON修改配方
                Formatting = Formatting.Indented,
                // 忽略null空字段，减小文件体积
                NullValueHandling = NullValueHandling.Ignore,
                // 枚举存字符串，可读性强，不要存数字
                Converters = { new StringEnumConverter() },
                // 日期统一格式
                DateFormatString = "yyyy-MM-dd HH:mm:ss.fff",
                // 循环引用规避（流程节点嵌套子流程必开）
                ReferenceLoopHandling = ReferenceLoopHandling.Serialize,
                PreserveReferencesHandling = PreserveReferencesHandling.None
            };
        }

        /// <summary>对象序列化为JSON字符串</summary>
        public static string SerializeObject(object obj)
        {
            if (obj == null)
                return string.Empty;
            try
            {
                return JsonConvert.SerializeObject(obj, _globalSettings);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("JSON序列化失败", ex, nameof(JsonSerializerHelper));
                return string.Empty;
            }
        }

        /// <summary>JSON字符串反序列化为指定实体</summary>
        public static T DeserializeObject<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;
            try
            {
                return JsonConvert.DeserializeObject<T>(json, _globalSettings);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"JSON反序列化{typeof(T).Name}失败", ex, nameof(JsonSerializerHelper));
                return default;
            }
        }

        /// <summary>将实体直接保存为本地JSON文件</summary>
        public static void SaveToFile<T>(T data, string filePath)
        {
            try
            {
                string json = SerializeObject(data);
                FileHelper.EnsureDirectoryExists(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"保存JSON文件失败:{filePath}", ex, nameof(JsonSerializerHelper));
            }
        }

        /// <summary>从本地JSON文件读取并反序列化实体</summary>
        public static T LoadFromFile<T>(string filePath)
        {
            if (!File.Exists(filePath))
            {
                GlobalLogger.Warn($"JSON配置文件不存在：{filePath}", nameof(JsonSerializerHelper));
                return default;
            }
            try
            {
                string json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                return DeserializeObject<T>(json);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"读取JSON文件失败:{filePath}", ex, nameof(JsonSerializerHelper));
                return default;
            }
        }
    }
}
```

### 5. Helpers/ExpressionHelper.cs 条件表达式解析（适配流程Condition/Loop）
```csharp
using System;
using DynamicExpresso;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Common.Logging;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 流程条件表达式执行工具
    /// 专门给ConditionFlowNode、LoopFlowNode做C#语法字符串表达式计算
    /// 运行时注入VisionContext.SharedData，支持读写上下文字典变量
    /// 语法完全贴近C#，不用学习新语法，工程师上手无门槛
    /// </summary>
    public static class ExpressionHelper
    {
        /// <summary>
        /// 执行布尔表达式，返回true/false
        /// </summary>
        /// <param name="expr">表达式字符串，例：SharedData["Match.IsSuccess"] == true</param>
        /// <param name="context">当前流程上下文，表达式可读取SharedData</param>
        public static bool ExecuteBoolExpression(string expr, VisionContext context)
        {
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            try
            {
                Interpreter interpreter = new Interpreter();
                // 把共享字典注入表达式环境，表达式内可直接使用SharedData
                interpreter.SetVariable("SharedData", context.SharedData);
                // 执行表达式强制转布尔
                object resultObj = interpreter.Eval(expr);
                if (resultObj is bool resBool)
                {
                    return resBool;
                }
                GlobalLogger.Warn($"表达式返回非布尔值，表达式：{expr}", nameof(ExpressionHelper));
                return false;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"条件表达式解析失败：{expr}", ex, nameof(ExpressionHelper));
                return false;
            }
        }
    }
}
```

### 6. Helpers/ThreadSafeHelper.cs 线程同步工具
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Grayson.Vision.Common.Logging;

namespace Grayson.Vision.Common.Helpers
{
    /// <summary>
    /// 多线程、异步任务通用工具
    /// 适配多工位并行执行、硬件异步读写、UI调度安全
    /// 提供锁封装、安全异步执行、超时控制
    /// </summary>
    public static class ThreadSafeHelper
    {
        /// <summary>
        /// 带超时的线程锁，防止死锁永久等待
        /// </summary>
        /// <param name="lockObj">锁对象</param>
        /// <param name="timeoutMs">等待超时毫秒</param>
        /// <param name="action">拿到锁后执行逻辑</param>
        /// <returns>true拿到锁执行；false超时未获取锁</returns>
        public static bool LockWithTimeout(object lockObj, int timeoutMs, Action action)
        {
            bool acquire = Monitor.TryEnter(lockObj, timeoutMs);
            if (!acquire)
            {
                GlobalLogger.Warn($"线程获取锁超时{timeoutMs}ms", nameof(ThreadSafeHelper));
                return false;
            }
            try
            {
                action.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("锁内业务执行异常", ex, nameof(ThreadSafeHelper));
                return false;
            }
            finally
            {
                Monitor.Exit(lockObj);
            }
        }

        /// <summary>
        /// 安全启动后台任务，统一异常捕获，防止工位线程崩溃无日志
        /// </summary>
        public static void RunSafeTask(Action taskAction, string taskName = "BackgroundTask")
        {
            Task.Run(() =>
            {
                try
                {
                    taskAction.Invoke();
                }
                catch (Exception ex)
                {
                    GlobalLogger.Error($"后台任务[{taskName}]异常终止", ex, nameof(ThreadSafeHelper));
                }
            });
        }
    }
}
```

### 7. Extensions/ObjectExtension.cs 通用扩展方法
```csharp
using System;
using System.Collections.Generic;

namespace Grayson.Vision.Common.Extensions
{
    /// <summary>
    /// 全项目通用对象扩展方法，简化判空、类型转换代码
    /// </summary>
    public static class ObjectExtension
    {
        /// <summary>判断引用类型为空</summary>
        public static bool IsNull(this object obj)
        {
            return obj == null;
        }

        /// <summary>判断不为空</summary>
        public static bool NotNull(this object obj)
        {
            return obj != null;
        }

        /// <summary>安全字典取值，无Key返回默认值，不抛KeyNotFound</summary>
        public static T SafeGet<T>(this Dictionary<string, object> dict, string key, T defaultValue = default)
        {
            if (dict.IsNull() || !dict.ContainsKey(key))
                return defaultValue;

            object val = dict[key];
            try
            {
                return (T)Convert.ChangeType(val, typeof(T));
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
```

## 四、使用规范强制约束（写入开发文档）
1. 日志：所有代码必须调用`GlobalLogger`，禁止`Console.WriteLine`、自行写文件日志；
2. JSON：配方、工位配置、流程节点**只允许用JsonSerializerHelper**，不能手写Newtonsoft逻辑；
3. 文件路径：全部使用FileHelper拼接、创建、清理，禁止手写绝对路径；
4. 表达式：流程所有if/loop判断统一走ExpressionHelper，禁止自行写DynamicExpresso；
5. 多工位并发硬件读写：必须使用ThreadSafeHelper加超时锁，杜绝死锁；
6. 字典读取上下文SharedData，一律用`SafeGet`扩展，避免键不存在报错；
7. Common层禁止引用任何硬件SDK、Halcon、WPF控件，保持纯通用底层。

## 五、下一步可选交付
1. Grayson.Vision.HalconWrapper 全套带注释封装代码（2D图像、匹配、测量、缺陷、标定）；
2. 海康相机硬件插件完整实现代码；
3. CameraGrabUnit采集业务单元+WPF配置面板完整代码。





# 阶段4：Grayson.Vision.HalconWrapper 完整落地（全中文注释）
## 基础配置说明
1. 项目类型：.NET Framework 4.7 类库，C#7.4，平台目标**x64**，适配Halcon19.11 x64
2. 必须引用：
   - Grayson.Vision.Contracts
   - Grayson.Vision.Common
   - HalconDotNet（19.11 x64，属性：复制到输出目录=如果较新则复制）
3. 红线约束：上层`BusinessUnits`、插件**禁止直接using HalconDotNet**，所有图像处理必须调用本层封装；本层是全系统唯一允许直接操作HObject的项目。
4. 核心设计：封装内存自动管控、统一返回`Result`、全部异常捕获，对外屏蔽原生Halcon报错细节。

## 完整目录结构
```
Grayson.Vision.HalconWrapper
├─ Core
│  ├─ HalconMemoryGuard.cs     HObject内存安全托管，防止泄漏
│  └─ HalconGlobalHelper.cs    全局算子公共工具、坐标系转换
├─ ImageProc
│  ├─ ImageBasicTool.cs        图像加载、保存、通道转换、裁剪
│  ├─ ImageFilterTool.cs       滤波、平滑、形态学运算
│  └─ ImageThresholdTool.cs    灰度阈值、动态阈值分割
├─ Match2D
│  ├─ TemplateMatchTool.cs     2D模板匹配（基于create_shape_model）
│  └─ ShapeMatchTool.cs        形状匹配进阶封装
├─ Measure2D
│  ├─ EdgeMeasureTool.cs       边缘检测、距离、角度、尺寸测量
└─ Calibration
   └─ Calib2DTool.cs           九点标定、像素转物理毫米换算
```

# 完整可复制代码（全部带中文注释）
## 1. Core/HalconMemoryGuard.cs 内存托管核心（解决HObject内存泄漏）
```csharp
using System;
using System.Collections.Generic;
using Grayson.Vision.Common.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// Halcon资源托管守卫
    /// 统一管理临时HObject生命周期，批量释放，避免零散忘记Dispose造成内存持续上涨
    /// 业务单元执行完毕统一调用Clean，所有临时图像全部回收
    /// </summary>
    public class HalconMemoryGuard : IDisposable
    {
        /// <summary>托管的所有临时图像容器</summary>
        private readonly List<HObject> _managedImages = new List<HObject>();
        private bool _disposed = false;

        /// <summary>将需要管控的HObject加入托管列表</summary>
        public void Register(HObject hoObj)
        {
            if (hoObj == null || hoObj.IsInitialized == false)
                return;
            _managedImages.Add(hoObj);
        }

        /// <summary>批量释放所有托管图像，清空列表</summary>
        public void CleanAll()
        {
            foreach (var img in _managedImages)
            {
                try
                {
                    if (img.IsInitialized)
                        img.Dispose();
                }
                catch (Exception ex)
                {
                    GlobalLogger.Warn($"Halcon图像释放异常", ex.Message, nameof(HalconMemoryGuard));
                }
            }
            _managedImages.Clear();
        }

        #region 标准IDisposable实现
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                CleanAll();
            }
            _disposed = true;
        }

        ~HalconMemoryGuard()
        {
            Dispose(false);
        }
        #endregion
    }
}
```

## 2. Core/HalconGlobalHelper.cs 全局公共工具
```csharp
using System;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Common.Logging;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Core
{
    /// <summary>
    /// 全局通用工具：Pose互转、像素/毫米换算、HWindow绘图基础封装
    /// 所有跨模块通用底层逻辑收拢在此
    /// </summary>
    public static class HalconGlobalHelper
    {
        /// <summary>
        /// Halcon原生HTuple Pose 转为系统统一Pose3D结构体
        /// Halcon姿态：X,Y,Z,Rx,Ry,Rz（角度）
        /// </summary>
        public static Pose3D HtuplePoseToPose3D(HTuple hPose)
        {
            if (hPose == null || hPose.Length < 6)
            {
                GlobalLogger.Warn("Halcon Pose数组长度不足", nameof(HalconGlobalHelper));
                return new Pose3D(0, 0, 0, 0, 0, 0);
            }

            Pose3D pose = new Pose3D(
                hPose[0].D,
                hPose[1].D,
                hPose[2].D,
                hPose[3].D,
                hPose[4].D,
                hPose[5].D
            );
            return pose;
        }

        /// <summary>
        /// 系统Pose3D转为Halcon HTuple，用于算子入参
        /// </summary>
        public static HTuple Pose3DToHtuplePose(Pose3D pose)
        {
            HTuple hPose = new HTuple();
            hPose.Append(pose.X);
            hPose.Append(pose.Y);
            hPose.Append(pose.Z);
            hPose.Append(pose.Rx);
            hPose.Append(pose.Ry);
            hPose.Append(pose.Rz);
            return hPose;
        }

        /// <summary>
        /// 像素坐标转物理毫米坐标（依赖九点标定完成的标定矩阵）
        /// </summary>
        /// <param name="pixelX">图像像素X</param>
        /// <param name="pixelY">图像像素Y</param>
        /// <param name="calibHomMat2D">标定HomMat2D矩阵</param>
        /// <returns>物理XY毫米</returns>
        public static (double worldX, double worldY) PixelToWorldMm(double pixelX, double pixelY, HTuple calibHomMat2D)
        {
            try
            {
                HHomMat2D.HomMat2dProjectPoint(calibHomMat2D, pixelX, pixelY, out HTuple wx, out HTuple wy);
                return (wx.D, wy.D);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("像素转世界坐标失败", ex, nameof(HalconGlobalHelper));
                return (0, 0);
            }
        }

        /// <summary>
        /// 世界毫米坐标转回图像像素坐标
        /// </summary>
        public static (double pixelX, double pixelY) WorldMmToPixel(double worldX, double worldY, HTuple calibHomMat2D)
        {
            try
            {
                HHomMat2D.HomMat2dProjectPointInv(calibHomMat2D, worldX, worldY, out HTuple px, out HTuple py);
                return (px.D, py.D);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("世界坐标转回像素失败", ex, nameof(HalconGlobalHelper));
                return (0, 0);
            }
        }

        /// <summary>
        /// 在HWindow绘制十字定位标记（统一视觉标记样式）
        /// </summary>
        public static void DrawCross(HWindow window, double x, double y, double crossLen = 20, string color = "red")
        {
            if (window == null || window.IsInitialized == false) return;
            try
            {
                window.SetColor(color);
                window.DispLine(y - crossLen, x, y + crossLen, x);
                window.DispLine(y, x - crossLen, y, x + crossLen);
            }
            catch
            {
                // 绘图异常不阻断主流程
            }
        }
    }
}
```

## 3. ImageProc/ImageBasicTool.cs 图像基础IO、裁剪、通道处理
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像基础操作：文件读写、ROI裁剪、灰度/彩色互转、尺寸缩放
    /// 所有对外返回Result封装，托管HObject资源
    /// </summary>
    public static class ImageBasicTool
    {
        /// <summary>
        /// 读取本地图片文件返回HObject
        /// </summary>
        public static Result<HObject> ReadImageFile(string filePath)
        {
            try
            {
                HObject img;
                HOperatorSet.ReadImage(out img, filePath);
                return Result<HObject>.Ok(img);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"读取图片失败:{filePath}", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("图片读取异常", -1, ex);
            }
        }

        /// <summary>
        /// HObject保存到本地文件（png/jpg/tif）
        /// </summary>
        public static Result SaveImageToFile(HObject image, string filePath, string format = "png")
        {
            if (image == null || !image.IsInitialized)
                return Result.Fail("无效图像");
            try
            {
                HOperatorSet.WriteImage(image, format, 0, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"保存图片失败:{filePath}", ex, nameof(ImageBasicTool));
                return Result.Fail("图像保存失败", -1, ex);
            }
        }

        /// <summary>
        /// 彩色图转灰度图
        /// </summary>
        public static Result<HObject> RgbToGray(HObject colorImage)
        {
            if (colorImage == null || !colorImage.IsInitialized)
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject grayImg;
                HOperatorSet.Rgb1ToGray(colorImage, out grayImg);
                return Result<HObject>.Ok(grayImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("彩色转灰度失败", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("灰度转换异常", -1, ex);
            }
        }

        /// <summary>
        /// ROI矩形裁剪图像
        /// </summary>
        /// <param name="row1">起始行Y</param>
        /// <param name="col1">起始列X</param>
        /// <param name="row2">结束行Y</param>
        /// <param name="col2">结束列X</param>
        public static Result<HObject> CropImage(HObject srcImage, double row1, double col1, double row2, double col2)
        {
            if (srcImage == null || !srcImage.IsInitialized)
                return Result<HObject>.Fail("原图无效");
            try
            {
                HObject roiRect, cropImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, row1, col1, row2, col2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(srcImage, roiRect, out cropImg);
                }
                return Result<HObject>.Ok(cropImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("图像ROI裁剪失败", ex, nameof(ImageBasicTool));
                return Result<HObject>.Fail("裁剪异常", -1, ex);
            }
        }
    }
}
```

## 4. ImageProc/ImageFilterTool.cs 滤波、形态学预处理（产线高频使用）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 图像滤波平滑 + 形态学开闭运算，适配划痕、斑点、脏点预处理
    /// 封装常用算子：高斯、均值、中值、开运算、闭运算、膨胀腐蚀
    /// </summary>
    public static class ImageFilterTool
    {
        /// <summary>高斯平滑滤波，消除相机噪点</summary>
        /// <param name="maskSize">卷积核尺寸，推荐3/5/7</param>
        public static Result<HObject> GaussFilter(HObject srcImg, int maskSize = 5)
        {
            if (srcImg == null || !srcImg.IsInitialized)
                return Result<HObject>.Fail("输入图像为空");
            try
            {
                HObject outImg;
                HOperatorSet.GaussFilter(srcImg, out outImg, maskSize, maskSize);
                return Result<HObject>.Ok(outImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("高斯滤波执行失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("高斯滤波异常", -1, ex);
            }
        }

        /// <summary>中值滤波，去除椒盐噪点、白点黑点脏污</summary>
        public static Result<HObject> MedianFilter(HObject srcImg, int mask = 3)
        {
            try
            {
                HObject res;
                HOperatorSet.MedianFilter(srcImg, out res, mask, mask, "circle");
                return Result<HObject>.Ok(res);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("中值滤波失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("中值滤波异常", -1, ex);
            }
        }

        /// <summary>腐蚀运算：收缩白色区域，去除细小白点杂讯</summary>
        public static Result<HObject> Erode(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Erode);
        }

        /// <summary>膨胀运算：扩大白色区域，填补细小缝隙</summary>
        public static Result<HObject> Dilate(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Dilate);
        }

        /// <summary>开运算：先腐蚀后膨胀，去除小白点，整体轮廓不变</summary>
        public static Result<HObject> OpenMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Open);
        }

        /// <summary>闭运算：先膨胀后腐蚀，填补小黑洞、缝隙</summary>
        public static Result<HObject> CloseMorph(HObject srcImg, int kernelSize = 3)
        {
            return MorphologyBase(srcImg, kernelSize, MorphType.Close);
        }

        #region 内部形态学统一入口
        private enum MorphType
        {
            Erode, Dilate, Open, Close
        }

        private static Result<HObject> MorphologyBase(HObject srcImg, int kernel, MorphType type)
        {
            if (srcImg == null || !srcImg.IsInitialized)
                return Result<HObject>.Fail("图像无效");
            try
            {
                HObject kernelRegion, dstImg;
                HOperatorSet.GenCircle(out kernelRegion, kernel, kernel, kernel);
                switch (type)
                {
                    case MorphType.Erode:
                        HOperatorSet.Erosion1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Dilate:
                        HOperatorSet.Dilation1(srcImg, kernelRegion, out dstImg, 1);
                        break;
                    case MorphType.Open:
                        HOperatorSet.Opening(srcImg, kernelRegion, out dstImg);
                        break;
                    case MorphType.Close:
                        HOperatorSet.Closing(srcImg, kernelRegion, out dstImg);
                        break;
                    default:
                        return Result<HObject>.Fail("不支持的形态学类型");
                }
                kernelRegion.Dispose();
                return Result<HObject>.Ok(dstImg);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error($"形态学运算{type}失败", ex, nameof(ImageFilterTool));
                return Result<HObject>.Fail("形态学处理异常", -1, ex);
            }
        }
        #endregion
    }
}
```

## 5. ImageProc/ImageThresholdTool.cs 阈值分割（缺陷、有无检测核心）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.ImageProc
{
    /// <summary>
    /// 灰度阈值分割工具
    /// 固定阈值、反阈值、动态自适应阈值，用于提取亮缺陷、暗缺陷、物料轮廓
    /// </summary>
    public static class ImageThresholdTool
    {
        /// <summary>
        /// 固定灰度阈值分割：低于minGray、高于maxGray保留
        /// 输出：满足灰度区间的Region区域
        /// </summary>
        public static Result<HObject> FixedThreshold(HObject grayImage, int minGray, int maxGray)
        {
            if (grayImage == null || !grayImage.IsInitialized)
                return Result<HObject>.Fail("灰度图无效");
            try
            {
                HObject regionOut;
                HOperatorSet.Threshold(grayImage, out regionOut, minGray, maxGray);
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("固定阈值分割失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("阈值分割异常", -1, ex);
            }
        }

        /// <summary>
        /// 自适应动态阈值（明暗不均匀工况必备）
        /// 用局部均值做参考，亮于均值offset则检出
        /// </summary>
        /// <param name="maskSize">局部窗口尺寸</param>
        /// <param name="offset">亮度偏移</param>
        public static Result<HObject> AutoThreshold(HObject grayImage, int maskSize = 15, int offset = 5)
        {
            try
            {
                HObject regionOut;
                HOperatorSet.DynThreshold(grayImage, grayImage, out regionOut, maskSize, maskSize, offset, "light");
                return Result<HObject>.Ok(regionOut);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("动态阈值分割失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("动态阈值异常", -1, ex);
            }
        }

        /// <summary>
        /// 区域筛选：按面积过滤小噪点，保留指定面积区间轮廓
        /// </summary>
        public static Result<HObject> SelectRegionByArea(HObject inputRegion, double areaMin, double areaMax)
        {
            try
            {
                HObject regionFiltered;
                HOperatorSet.SelectShape(inputRegion, out regionFiltered, "area", "and", areaMin, areaMax);
                return Result<HObject>.Ok(regionFiltered);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("区域面积筛选失败", ex, nameof(ImageThresholdTool));
                return Result<HObject>.Fail("区域筛选异常", -1, ex);
            }
        }
    }
}
```

## 6. Match2D/TemplateMatchTool.cs 2D模板匹配（定位核心）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Match2D
{
    /// <summary>
    /// 基于shape_model的2D刚性模板匹配
    /// 工业最常用工件定位：平移+旋转匹配，输出坐标、角度、匹配分数
    /// 统一封装模板创建、在线匹配、参数归一化
    /// </summary>
    public static class TemplateMatchTool
    {
        /// <summary>
        /// 创建形状模板（离线训练模板使用）
        /// </summary>
        /// <param name="templateImage">模板原图</param>
        /// <param name="roiRow1/Col1/Row2/Col2">模板ROI范围</param>
        /// <param name="angleStart">起始旋转角度(°)</param>
        /// <param name="angleEnd">终止旋转角度(°)</param>
        /// <returns>模板ID，匹配时传入使用</returns>
        public static Result<int> CreateShapeModel(HObject templateImage, double roiRow1, double roiCol1, double roiRow2, double roiCol2, double angleStart, double angleEnd)
        {
            if (templateImage == null || !templateImage.IsInitialized)
                return Result<int>.Fail("模板图像为空");
            try
            {
                HTuple modelId;
                // 截取ROI区域创建模板
                HObject roiRect, roiImg;
                using (var guard = new HalconMemoryGuard())
                {
                    HOperatorSet.GenRectangle1(out roiRect, roiRow1, roiCol1, roiRow2, roiCol2);
                    guard.Register(roiRect);
                    HOperatorSet.ReduceDomain(templateImage, roiRect, out roiImg);
                    guard.Register(roiImg);

                    // 创建形状模板，角度转弧度
                    HOperatorSet.CreateShapeModel(roiImg, 4, angleStart / 180 * Math.PI, (angleEnd - angleStart) / 180 * Math.PI,
                        "auto", "use_polarity", 30, 0, out modelId);
                }
                return Result<int>.Ok(modelId.I);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("创建Shape模板失败", ex, nameof(TemplateMatchTool));
                return Result<int>.Fail("模板创建异常", -1, ex);
            }
        }

        /// <summary>
        /// 执行模板搜索匹配
        /// </summary>
        /// <param name="searchImage">待搜索大图</param>
        /// <param name="modelId">模板ID</param>
        /// <param name="minScore">最低匹配分数（0~1）</param>
        /// <returns>匹配结果集合：坐标、角度、分数</returns>
        public static Result<TemplateMatchResult[]> FindShapeModel(HObject searchImage, int modelId, double minScore = 0.7)
        {
            if (searchImage == null || !searchImage.IsInitialized)
                return Result<TemplateMatchResult[]>.Fail("搜索图像无效");
            try
            {
                HTuple rows, cols, angles, scores;
                HOperatorSet.FindShapeModel(searchImage, modelId, 0, 0, 0, 0, minScore, 0, 0, out rows, out cols, out angles, out scores);

                int count = rows.Length;
                TemplateMatchResult[] resultArr = new TemplateMatchResult[count];
                for (int i = 0; i < count; i++)
                {
                    resultArr[i] = new TemplateMatchResult
                    {
                        PixelRow = rows[i].D,
                        PixelCol = cols[i].D,
                        RotateDegree = angles[i].D / Math.PI * 180,
                        Score = scores[i].D
                    };
                }
                return Result<TemplateMatchResult[]>.Ok(resultArr);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("模板匹配查找失败", ex, nameof(TemplateMatchTool));
                return Result<TemplateMatchResult[]>.Fail("匹配运算异常", -1, ex);
            }
        }

        /// <summary>释放模板内存，用完必须销毁</summary>
        public static Result ClearShapeModel(int modelId)
        {
            try
            {
                HOperatorSet.ClearShapeModel(modelId);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("销毁模板失败", ex, nameof(TemplateMatchTool));
                return Result.Fail("模板释放异常", -1, ex);
            }
        }
    }

    /// <summary>单条模板匹配结果实体</summary>
    public class TemplateMatchResult
    {
        /// <summary>匹配中心行像素Y</summary>
        public double PixelRow { get; set; }
        /// <summary>匹配中心列像素X</summary>
        public double PixelCol { get; set; }
        /// <summary>旋转角度 角度制°</summary>
        public double RotateDegree { get; set; }
        /// <summary>匹配相似度 0~1</summary>
        public double Score { get; set; }
    }
}
```

## 7. Measure2D/EdgeMeasureTool.cs 边缘尺寸测量（长宽、间距、角度）
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Measure2D
{
    /// <summary>
    /// 基于measure工具的亚像素级边缘测量
    /// 测长度、宽度、孔间距、两条边夹角，精度优于普通轮廓计算
    /// </summary>
    public static class EdgeMeasureTool
    {
        /// <summary>
        /// 直线矩形测量卡尺，查找两侧边缘，返回两点距离
        /// </summary>
        /// <param name="grayImg">灰度原图</param>
        /// <param name="lineRow1/Col1">卡尺起点</param>
        /// <param name="lineRow2/Col2">卡尺终点</param>
        /// <param name="width">卡尺横向宽度</param>
        /// <param name="edgeSelect">first/last/all 取第一条/最后一条边缘</param>
        public static Result<double> MeasureLineDistance(HObject grayImg, double lineRow1, double lineCol1, double lineRow2, double lineCol2, double width, string edgeSelect = "all")
        {
            if (grayImg == null || !grayImg.IsInitialized)
                return Result<double>.Fail("灰度图无效");
            try
            {
                HTuple measureHandle;
                // 创建测量句柄
                HOperatorSet.GenMeasureRectangle2(lineRow1, lineCol1, lineRow2, lineCol2, width, out measureHandle);
                HTuple edgeRows, edgeCols, amplitudes, distances;
                // 执行边缘查找
                HOperatorSet.MeasurePos(grayImg, measureHandle, 10, "all", edgeSelect, out edgeRows, out edgeCols, out amplitudes, out distances);
                // 释放句柄
                HOperatorSet.CloseMeasure(measureHandle);

                if (distances.Length >= 2)
                {
                    return Result<double>.Ok(distances[1].D - distances[0].D);
                }
                return Result<double>.Fail("未找到足够边缘点");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("直线距离测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("测量异常", -1, ex);
            }
        }

        /// <summary>
        /// 圆形卡尺测量圆孔内径，返回直径
        /// </summary>
        public static Result<double> MeasureCircleDiameter(HObject grayImg, double centerRow, double centerCol, double radiusMin, double radiusMax)
        {
            try
            {
                HTuple rows, cols, radii;
                HOperatorSet.FindCircle(grayImg, centerRow, centerCol, radiusMin, radiusMax, out rows, out cols, out radii);
                if (radii.Length > 0)
                {
                    return Result<double>.Ok(radii[0].D * 2);
                }
                return Result<double>.Fail("未检出圆孔");
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("圆孔测量失败", ex, nameof(EdgeMeasureTool));
                return Result<double>.Fail("圆孔测量异常", -1, ex);
            }
        }
    }
}
```

## 8. Calibration/Calib2DTool.cs 九点标定工具
```csharp
using System;
using Grayson.Vision.Common.Logging;
using Grayson.Vision.Contracts.Core;
using Grayson.Vision.HalconWrapper.Core;
using HalconDotNet;

namespace Grayson.Vision.HalconWrapper.Calibration
{
    /// <summary>
    /// 2D九点手眼标定（相机平面对标工作台）
    /// 采集9个特征点像素坐标+物理坐标，生成HomMat2D矩阵
    /// 后续所有定位结果依靠矩阵完成像素↔毫米换算
    /// </summary>
    public static class Calib2DTool
    {
        /// <summary>
        /// 九点标定计算HomMat2D单应矩阵
        /// </summary>
        /// <param name="pixelXList">9个点像素X数组</param>
        /// <param name="pixelYList">9个点像素Y数组</param>
        /// <param name="worldXList">9个点实际物理X mm</param>
        /// <param name="worldYList">9个点实际物理Y mm</param>
        /// <returns>HomMat2D标定矩阵</returns>
        public static Result<HTuple> CalcNinePointHomMat(HTuple pixelXList, HTuple pixelYList, HTuple worldXList, HTuple worldYList)
        {
            try
            {
                HTuple homMat;
                HOperatorSet.VectorToHomMat2d(pixelXList, pixelYList, worldXList, worldYList, out homMat);
                return Result<HTuple>.Ok(homMat);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("九点标定矩阵计算失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("标定计算异常", -1, ex);
            }
        }

        /// <summary>保存标定矩阵到本地文件</summary>
        public static Result SaveHomMatToFile(HTuple homMat, string filePath)
        {
            try
            {
                HOperatorSet.WriteTuple(homMat, filePath);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("标定矩阵保存失败", ex, nameof(Calib2DTool));
                return Result.Fail("矩阵保存异常", -1, ex);
            }
        }

        /// <summary>从文件读取标定矩阵</summary>
        public static Result<HTuple> LoadHomMatFromFile(string filePath)
        {
            try
            {
                HTuple mat;
                HOperatorSet.ReadTuple(out mat, filePath);
                return Result<HTuple>.Ok(mat);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("标定矩阵读取失败", ex, nameof(Calib2DTool));
                return Result<HTuple>.Fail("矩阵读取异常", -1, ex);
            }
        }
    }
}
```

# 强制开发规范（必须遵守）
1. **上层业务绝对禁止using HalconDotNet**
BusinessUnits、硬件插件、Shell全部不能直接引用原生Halcon类；所有图像、匹配、测量全部调用本层静态工具。
2. 临时HObject必须纳入`HalconMemoryGuard`托管，函数返回的HObject由调用方自行Dispose。
3. 所有算子执行包裹try-catch，日志完整记录，不会因为算子报错直接崩溃整条流程。
4. 坐标统一流转规则：
   算法内部用像素计算；最终定位结果用HomMat转毫米，转为`Pose3D`给运动/机器人单元。
5. 模板ID、测量句柄必须手动释放，封装层提供Clear方法兜底。

# 下一步可选交付清单
1. Plugins.Camera.Hikvision 海康相机完整插件代码；
2. 第一个业务单元：CameraGrabUnit + WPF配置面板全套带注释；
3. 宿主硬件管理器HardwareManager完整代码。
