# Grayson.Vision.Contracts — 契约层（架构最底层）

> 校正：2026-09-07。全仓依赖的唯一底座：接口 / 领域模型 / DTO / 枚举 / 基础工具。
> **纪律：本工程不引用 WPF / HALCON / 任何硬件与厂商 SDK**（MVVM 基类例外——仅依赖 INotifyPropertyChanged/ICommand）。

## 1. 在架构中的位置

```text
Contracts <── 所有人(Plugins.* / Repository / HalconWrapper / Core / Nodes / FlowEdit / WpfUI)
规则：没有人反向依赖"具体实现"；具体实现都在接口背后。
```

规模：128 个 .cs；.NET Framework 4.7.2。

## 2. 领域目录地图

| 目录 | 承载内容（代表类型） |
|---|---|
| `Devices\Interfaces` | `IHardwarePlugin / ICamera / IPlc / IMotionCard / ILightController / IIoDevice / IDevice(Lease/Manager/PoolService)` |
| `Station` | 工位配置模型 / 状态 / 触发源 / 运行参数 |
| `Recipe` | 配方 Model / DTO / Converter（流程 + 工艺 + 逻辑设备） |
| `Calibration` | 标定 v2：`CalibrationQuantity`（HandEye/ToolRotation/ToolOffset/PixelScale/LensDistortion…）、Artifact、任务卡、状态机、标定档案模型 |
| `Templates` | 模板模型：ROI / 参数 / 源图留档路径 / 平场开关 |
| `Imaging` | 图像上下文 / 采集结果 / 成像指标模型 |
| `Flow` | 节点与图契约：节点定义 / 参数描述 / 执行上下文 |
| `Communication` | 通信消息基元 |
| `Ipc` | 进程/线程间通信与事件订阅接口 |
| `MesBridge` | MES 桥接模型 |
| `Ai` | AI/推理契约（IInferenceProvider、结果映射） |
| `Infrastructure` | 指标(Metrics)/日志接口/通用工具；`Metrics` 近期增补工位指标 |
| `Core` | 领域通用基础（值对象 / 基类，如 MVVM 基类） |

## 3. 改契约的连锁影响（动手前必读）

契约是被 16+ 工程共享的地基，改动波及面最大，遵守：

1. **加新契约**：放对领域目录；默认给接口 + 最少实现；同步更新本 README 目录地图；
2. **删/改名类型或成员**：
   - 先全仓 grep 引用（含 XAML Binding 路径、插件、断言脚本）；
   - 模型删除必须同步 5 处：Model→DTO→Converter→XAML/VM 门禁→回归断言（Recipe 教训，见根 STATUS_TODO）；
   - 持久化对象删除前确认旧 JSON/LiteDB 文档兼容（反序列化静默忽略多余属性是既定策略，勿反向破坏）；
3. **语义变更**（如"发布域""偏心方向符号"）：必须同步 `ARCHITECTURE.md` 与相关设计文档，防止口头契约漂移。

## 4. 使用示例

```csharp
// 新增设备契约 → 插件实现 → 宿主装配（示例路径）
public interface ILightController : IDevice { bool SetBrightness(int ch, double v); }
// 实现放 Plugins.*，宿主只依赖 IHardwarePlugin 遍历创建
```

## 5. 关联文档

- 根 `ARCHITECTURE.md` §3（领域地图全景）
- 根 `DOCS_INDEX.md` §3（契约层文档索引）
- `STATUS_TODO.md`（近期在删 Recipe 孤儿字段——详见 §5 未提交改动）
