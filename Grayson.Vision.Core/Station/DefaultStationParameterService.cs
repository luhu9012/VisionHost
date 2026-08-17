using Grayson.Vision.Contracts.Core;
using Grayson.Vision.Contracts.Station.Interfaces;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Grayson.Vision.Core.Station
{
    /// <summary>
    /// 工位全局参数的默认实现：运行时内存字典 + 可选持久化。
    /// 注意：写入需要 operatorToken 校验，防止普通业务节点误改标定矩阵。
    /// </summary>
    public class DefaultStationParameterService : IStationParameterService
    {
        private readonly ConcurrentDictionary<string, object> _parameters
            = new ConcurrentDictionary<string, object>();

        public string StationId { get; }

        public DefaultStationParameterService(string stationId)
        {
            StationId = stationId;
        }

        public T GetParameter<T>(string key, T defaultValue = default)
        {
            if (string.IsNullOrEmpty(key)) return defaultValue;
            if (_parameters.TryGetValue(key, out var value) && value is T typedValue)
            {
                return typedValue;
            }
            return defaultValue;
        }

        public Result SetParameter<T>(string key, T value, string operatorToken = null)
        {
            if (string.IsNullOrEmpty(key))
                return Result.Fail("参数键不能为空");

            if (!CanWrite(operatorToken))
                return Result.Fail("当前调用方没有写入工位全局参数的权限");

            _parameters[key] = value;
            return Result.Ok();
        }

        public bool CanWrite(string operatorToken)
        {
            // 默认宽松：未启用 token 时允许写入；实际项目可接入权限服务
            return string.IsNullOrEmpty(operatorToken) || operatorToken.StartsWith("SYS_");
        }

        public Task<Result> PersistAsync()
        {
            // 当前为内存实现，持久化留给后续 Repository/工艺参数集实现
            return Task.FromResult(Result.Ok());
        }
    }
}
