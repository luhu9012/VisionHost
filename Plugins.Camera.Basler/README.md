# Plugins.Camera.Basler

巴斯勒（Basler）工业相机插件，实现 `Contracts\Devices\Interfaces\ICamera`。用于麻将双吸嘴工位（新工位相机来源）。

- 工程：`Plugins.Camera.Basler.csproj`（老式工程，新增 .cs 需加 `<Compile Include>`）
- 依赖：仅 `Grayson.Vision.Contracts`；运行期需 pylon 相机 SDK 原生库放入 exe 目录

## 代码结构

| 文件 | 职责 |
|---|---|
| `BaslerPlugin.cs` | `IHardwarePlugin` 入口：声明设备类型、工厂创建相机实例 |
| `PylonNetAdapter.cs` | 隔离 pylon .NET API 的薄适配层（取流/参数/触发） |
| `BaslerSdkAdapters.cs` | 相机行为到 `ICamera` 契约的桥接（打开/采集/软触发/参数读写） |

## 接入范式（其他相机插件照此写）

`IHardwarePlugin`(声明) → SDK 适配层(隔离厂商 API) → `ICamera`(契约) → 设备池统一管理。
优点：宿主/流程不感知厂商差异；换相机品牌只换插件工程。

## 状态 / 注意

- 状态：可用（主程序全插件版引用本插件；Epson/ZMC 同批接入）。
- 注意事项：相机句柄/采集回调严禁跨线程直接上 UI；原生 SDK 部署缺失时跑 `.workbuddy/deploy_halcon_runtime.py` 思路自查或检查 bin 目录原生 dll。
