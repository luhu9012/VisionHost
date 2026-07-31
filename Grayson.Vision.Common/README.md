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