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