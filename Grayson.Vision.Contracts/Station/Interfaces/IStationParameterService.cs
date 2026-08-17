using Grayson.Vision.Contracts.Core;
using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.Station.Interfaces
{
    /// <summary>
    /// 工位全局参数服务：标定矩阵、工位配置等只允许通过此服务写入。
    /// 业务节点对全局参数只读，写操作需要专门权限节点/服务调用。
    /// </summary>
    public interface IStationParameterService
    {
        /// <summary>
        /// 读取全局参数；节点可以直接读取。
        /// </summary>
        T GetParameter<T>(string key, T defaultValue = default);

        /// <summary>
        /// 写入全局参数；需要显式传入授权令牌或校验调用方身份。
        /// 普通业务节点不应直接调用。
        /// </summary>
        Result SetParameter<T>(string key, T value, string operatorToken = null);

        /// <summary>
        /// 判断当前调用是否拥有写入权限。
        /// </summary>
        bool CanWrite(string operatorToken);

        /// <summary>
        /// 保存全局参数到持久化（可选实现）。
        /// </summary>
        Task<Result> PersistAsync();
    }
}
