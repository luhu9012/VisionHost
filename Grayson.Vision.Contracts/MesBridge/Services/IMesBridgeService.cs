using System.Threading.Tasks;

namespace Grayson.Vision.Contracts.MesBridge.Services
{
    /// <summary>
    /// MES 对接服务接口占位，实际实现可替换为 HTTP/SOAP/数据库直连等
    /// </summary>
    public interface IMesBridgeService
    {
        /// <summary>
        /// 当前是否已连接 MES
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// MES 服务端地址
        /// </summary>
        string Endpoint { get; set; }

        /// <summary>
        /// 异步连接 MES
        /// </summary>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 断开 MES 连接
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// 发送心跳测试
        /// </summary>
        Task<bool> HeartbeatAsync();

        /// <summary>
        /// 上传检测结果
        /// </summary>
        Task<bool> UploadResultAsync(string stationId, string batchId, bool isOk, string recipeName, string errorMessage);
    }
}
