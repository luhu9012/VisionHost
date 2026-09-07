# Grayson.Vision.Repository — 持久化层（LiteDB + JSON）

> 校正：2026-09-07。轻量本地文档数据库封装 + 配方/工位/历史/账号等仓储。

## 1. 目录结构

| 目录/文件 | 职责 |
|---|---|
| `Core\` | 仓储基类/通用实现（`IRepository<T, TKey>` 系） |
| `Interfaces\` | 各业务仓储接口（如配方存储服务） |
| `Implementations\` | 仓储具体实现 |
| `Entities\` | 持久化实体（引 Contracts 模型保持跨层一致） |
| `Services\` | 上层服务（配方存储/工位配置/历史记录） |
| `StorageFactory.cs` | 统一入口：LiteDB 初始化、默认账号种子化 |

依赖：仅 `Contracts`。

## 2. 定位与边界

- **工位配置**：已建档工位以 LiteDB `StationConfigModel`(ProcessConfigJson) 为准（改 .cs 默认值无效的根源在此）；
- **配方存储**：`IRecipeStorageService`（JSON 文件实现，`Recipes\{RecipeCode}.json`），UI 不再直接读写文件；`RecipeConverter` 负责 DTO↔Model，旧 JSON 多余属性静默忽略（兼容策略）；
- **标定矩阵**：JSON（`Recipes\Workstations\{Station}\Calib` 工位级共享 / `Recipes\{RecipeCode}\Calib` 配方级快照，发布时由标定服务写）；
- 历史/工单/日志走 LiteDB 集合。

## 3. 新增仓储流程（照模板抄）

```text
Entities 加 PO → Interfaces 加 IxxxRepository → Implementations 实现(继承基类)
→ StorageFactory 注册/暴露 → 上层经接口使用(禁止直接 new LiteDatabase)
```

## 4. 注意

- 删实体/字段前：检查旧 LiteDB 文档与旧 JSON 反序列化兼容；
- 模板源图/图片等大文件不落库，走文件系统路径引用（.gitignore 已忽略 Images/Templates/CalibFiles/*.png 等）；
- 引擎层改动跑 `.workbuddy/verify_recipe_publish_assert.py` 回归。
