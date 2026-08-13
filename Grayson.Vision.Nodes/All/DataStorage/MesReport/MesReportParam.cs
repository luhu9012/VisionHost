using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DataStorage.MesReport
{
    public class MesReportParam : ParamBase
    {
        private string _apiUrl = "http://192.168.1.100:8080/api/v1/mes/upload";
        /// <summary>
        /// MES HTTP WebAPI URL
        /// </summary>
        public string ApiUrl
        {
            get => _apiUrl;
            set => Set(ref _apiUrl, value);
        }

        private string _stationName = "Station_01";
        /// <summary>
        /// 站点名称
        /// </summary>
        public string StationName
        {
            get => _stationName;
            set => Set(ref _stationName, value);
        }

        private int _timeoutMs = 3000;
        /// <summary>
        /// 超时时间 (毫秒)
        /// </summary>
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => Set(ref _timeoutMs, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(ApiUrl) && string.IsNullOrWhiteSpace(ApiUrl))
                    return "MES 接口 URL 不能为空";
                return null;
            }
        }
        #endregion
    }
}