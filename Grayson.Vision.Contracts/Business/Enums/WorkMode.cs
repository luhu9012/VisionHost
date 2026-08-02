namespace Grayson.Vision.Contracts.Business.Enums
{
    /// <summary>
    /// 工位运行模式
    /// </summary>
    public enum WorkMode
    {
        /// <summary>
        /// 调试/编排模式：支持单步/单次触发，开启全量日志与图像渲染
        /// </summary>
        Debug = 0,

        /// <summary>
        /// 自动/产线模式：监听外部硬触发，极简渲染，追求极致性能
        /// </summary>
        Production = 1
    }
}