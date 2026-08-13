# Grayson.Vision.Repository

Grayson.Vision.Repository 是 Grayson.Vision 解决方案中的数据持久化与仓储层，基于 **LiteDB** 提供轻量化的本地文档数据库能力。该层负责配方、设备配置、用户账号及检测日志等核心业务数据的存储、查询和索引管理，并通过工厂模式对外暴露仓储实例。

---

## 设计目标

- **零配置本地存储**：使用 LiteDB 单文件数据库，无需安装独立数据库服务。
- **依赖 Contracts 层**：实体直接引用 `Grayson.Vision.Contracts` 中的枚举与模型，保持跨层一致。
- **接口隔离**：通过 `IRepository<T, TKey>` 与具体仓储接口解耦，便于后续替换底层存储实现。
- **统一入口**：`StorageFactory` 负责数据库初始化与默认账号种子化，简化上层调用。

---

## 技术栈

- 目标框架：`.NET Framework 4.7.2`
- 文档数据库：`LiteDB 5.0.21`
- 辅助包：`System.Buffers 4.5.1`

---

## 目录结构

```
Grayson.Vision.Repository/
├── Core/
│   ├── BaseEntity.cs                 # 数据库主键与审计基类
│   └── DbContext.cs                  # LiteDB 实例与连接管理
├── Entities/                         # 持久化对象 (PO)
│   ├── RecipePo.cs                   # 配方表实体
│   ├── DeviceConfigPo.cs             # 设备注册表实体
│   ├── UserPo.cs                     # 用户表实体
│   └── InspectionLogPo.cs            # 检测统计/历史数据表实体
├── Interfaces/                       # 仓储接口定义
│   ├── IRepository.cs                # 通用泛型接口
│   ├── IRecipeRepository.cs          # 配方仓储
│   ├── IDeviceConfigRepository.cs    # 设备配置仓储
│   ├── IUserRepository.cs            # 用户仓储
│   └── IInspectionLogRepository.cs   # 统计报表仓储
├── Implementations/                  # LiteDB 仓储实现
│   ├── LiteDbRepositoryBase.cs       # 通用 CRUD 抽象基类
│   ├── LiteDbRecipeRepository.cs
│   ├── LiteDbDeviceConfigRepository.cs
│   ├── LiteDbUserRepository.cs
│   └── LiteDbInspectionLogRepository.cs
└── StorageFactory.cs                 # 仓储工厂统一入口
```

---

## 核心组件

### 1. `BaseEntity`

所有持久化实体的抽象基类，定义了主键与审计字段：

```csharp
public abstract class BaseEntity
{
	public string Id { get; set; } = Guid.NewGuid().ToString("N");
	public DateTime CreatedTime { get; set; } = DateTime.Now;
	public DateTime UpdatedTime { get; set; } = DateTime.Now;
}
```

LiteDB 默认使用名为 `Id` 的字段作为主键，因此无需额外 `[BsonId]` 标注。

### 2. `DbContext`

静态数据库上下文，提供 `LiteDatabase` 实例：

- 默认数据文件：`{BaseDirectory}/Data/GraysonVision.db`
- 支持通过 `Initialize(customDbPath)` 自定义数据库路径
- 使用 `ConnectionType.Shared` 连接模式，支持多进程/多线程共享访问
- 自动创建数据库文件父目录

示例：

```csharp
DbContext.Initialize();                         // 使用默认路径
DbContext.Initialize(@"D:\Data\Vision.db");    // 使用自定义路径
using (var db = DbContext.GetDatabase())
{
	var col = db.GetCollection<RecipePo>("recipes");
}
```

### 3. `IRepository<T, TKey>` 与 `LiteDbRepositoryBase<T>`

通用仓储接口与 LiteDB 抽象基类，提供标准 CRUD：

| 操作 | 方法 |
|------|------|
| 按主键查询 | `GetById(TKey id)` |
| 查询全部 | `GetAll()` |
| 条件查询 | `Find(Expression<Func<T, bool>> predicate)` |
| 插入 | `Insert(T entity)` |
| 更新 | `Update(T entity)` |
| 删除 | `Delete(TKey id)` |

基类会在 `Insert`/`Update` 时自动维护 `CreatedTime` 与 `UpdatedTime`。

### 4. 业务仓储

#### IRecipeRepository / LiteDbRecipeRepository

集合名：`recipes`

扩展方法：

- `GetByName(string recipeName)`：按配方名称查询。
- `GetActiveRecipe(string productCategory)`：按产品类别查询当前激活配方。
- `SetActive(string id)`：将指定配方设为激活，同时将该类别下其他配方置为非激活。

实体 `RecipePo` 直接嵌套 `RecipeModel`，用于保存完整配方结构。

#### IDeviceConfigRepository / LiteDbDeviceConfigRepository

集合名：`device_configs`

扩展方法：

- `GetByKey(string deviceKey)`：按逻辑设备名称查询。
- `GetByDeviceId(string deviceId)`：按物理硬件 ID 查询。
- `GetAllEnabled()`：查询所有启用状态的设备。

索引：

- `DeviceKey` 唯一索引
- `DeviceId` 普通索引

#### IUserRepository / LiteDbUserRepository

集合名：`users`

扩展方法：

- `GetByUsername(string username)`：按用户名查询。
- `ValidateUser(string username, string passwordHash)`：校验用户名与密码哈希，并检查账号是否锁定。

索引：

- `Username` 唯一索引

#### IInspectionLogRepository / LiteDbInspectionLogRepository

集合名：`inspection_logs`

扩展方法：

- `GetLogsByDate(string stationId, DateTime startTime, DateTime endTime)`：按工位与时间段查询检测记录。
- `GetYieldStats(...)`：统计指定时间段内的总数量、OK 数量、NG 数量。

### 5. `StorageFactory`

统一工厂入口，负责：

1. 调用 `DbContext.Initialize` 初始化数据库路径。
2. 种子化默认内置用户（admin / engineer / operator）。
3. 对外提供各仓储实例的创建方法。

示例：

```csharp
// 在程序启动时调用一次
StorageFactory.Initialize();

// 后续通过工厂获取仓储实例
var recipeRepo = StorageFactory.CreateRecipeRepository();
var deviceRepo = StorageFactory.CreateDeviceConfigRepository();
var userRepo = StorageFactory.CreateUserRepository();
var logRepo = StorageFactory.CreateInspectionLogRepository();
```

---

## 默认用户种子

`StorageFactory.Initialize()` 会自动创建以下默认账号（首次运行时）：

| 用户名 | 密码哈希 | 角色 | 显示名称 |
|--------|----------|------|----------|
| admin | admin123 | Administrator | 系统管理员 |
| engineer | eng123 | Engineer | 视觉工程师 |
| operator | op123 | Operator | 产线操作员 |

> 注意：当前 `PasswordHash` 存储的是明文或简单字符串，建议使用更安全的哈希算法（如 PBKDF2 / SHA-256）并加盐。

---

## 实体说明

### RecipePo

| 字段 | 说明 |
|------|------|
| `RecipeCode` | 配方编码 |
| `RecipeName` | 配方名称 |
| `ProductCategory` | 产品类别 |
| `Version` | 版本 |
| `IsActive` | 是否激活 |
| `Description` | 描述 |
| `Model` | 来自 Contracts 的完整配方模型 |

### DeviceConfigPo

| 字段 | 说明 |
|------|------|
| `DeviceKey` | 用户定义的逻辑唯一名称 |
| `DeviceId` | 物理硬件 ID |
| `BrandName` | 硬件品牌 |
| `Category` | 设备大类（Camera / PLC / MotionCard） |
| `IsEnabled` | 是否启用 |
| `ConnectionString` | 扩展连接参数（JSON 字符串，预留） |

### UserPo

| 字段 | 说明 |
|------|------|
| `Username` | 用户名 |
| `PasswordHash` | 密码哈希 |
| `RealName` | 显示名称 |
| `Role` | 用户角色（引用 Contracts 枚举） |
| `IsLocked` | 是否锁定 |

### InspectionLogPo

| 字段 | 说明 |
|------|------|
| `StationId` | 工位 ID |
| `BatchId` | 批次号 |
| `RecipeName` | 配方名称 |
| `IsOk` | 检测结果 |
| `CycleTimeMs` | 节拍时间（毫秒） |
| `ErrorCode` | 错误码 |
| `ErrorMessage` | 错误信息 |
| `ImagePath` | 存盘图片路径 |
| `InspectTime` | 检测时间 |

---

## 使用建议

1. **初始化时机**：在应用启动时尽早调用 `StorageFactory.Initialize()`，确保数据库路径正确且默认账号已写入。
2. **生命周期**：每个仓储方法内部都会 `using` 数据库实例，无需手动释放；仓储实例本身可长期持有或按需创建。
3. **索引管理**：索引创建放在 `Insert` 方法中，首次插入时自动建立。如果数据量较大或索引策略变更，可考虑在初始化时统一创建。
4. **多路径切换**：如需在运行中切换数据库路径，应调用 `DbContext.Initialize(newPath)` 重新初始化。
5. **备份与迁移**：LiteDB 为单文件数据库，可直接复制 `.db` 文件进行备份；迁移时只需替换文件路径。

---

## 后续可扩展点

- 将 `PasswordHash` 升级为加盐哈希。
- 抽象 `IUnitOfWork` 以支持事务性批量写入。
- 为检测日志添加按 `BatchId` 与 `RecipeName` 的索引，优化报表查询性能。
- 支持数据库版本升级/迁移脚本机制。
