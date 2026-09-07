# Plugins.Camera.Hikvision

海康威视（Hikvision）工业相机插件，实现 `ICamera`（MvCamCtrl.NET / MVS SDK）。

- 工程：`Plugins.Camera.Hikvision.csproj`；依赖仅 `Contracts`
- 来源：早期工位在用（宿主通过 `IHardwarePlugin` 装配）

## 代码结构

| 文件 | 职责 |
|---|---|
| `HikvisionPlugin.cs` | `IHardwarePlugin` 入口 + `ICamera` 实现（取流/曝光/触发/参数） |
| `官方案例Demo_仅供参考功能.cs/.Designer.cs/.resx` | ⚠️ 海康官方示例残留（参考用），见治理 TODO A3：建议清理，避免误导 |

## 状态 / 注意

- 状态：可用；工程内残留官方案例文件（A3 待清理）。
- 与 Basler 插件的差异点：SDK 命名空间/回调模型不同，但宿主侧 `ICamera` 完全一致——插件化的收益所在。
