using System;
using System.Threading.Tasks;
using System.Windows;
using Grayson.Vision.Contracts.MesBridge.Services;

namespace Grayson.Vision.WpfUI.Service
{
    /// <summary>
    /// MES 对接服务模拟实现，用于 UI 界面占位与连接测试
    /// </summary>
    public class MockMesBridgeService : IMesBridgeService
    {
        private bool _isConnected;

        public bool IsConnected
        {
            get => _isConnected;
            private set
            {
                _isConnected = value;
                OnConnectionStateChanged?.Invoke(this, value);
            }
        }

        public string Endpoint { get; set; } = "http://127.0.0.1:5000/api/mes";

        public event EventHandler<bool> OnConnectionStateChanged;

        public async Task<bool> ConnectAsync()
        {
            await Task.Delay(300);
            IsConnected = !string.IsNullOrWhiteSpace(Endpoint);
            return IsConnected;
        }

        public async Task DisconnectAsync()
        {
            await Task.Delay(100);
            IsConnected = false;
        }

        public async Task<bool> HeartbeatAsync()
        {
            await Task.Delay(200);
            return IsConnected;
        }

        public async Task<bool> UploadResultAsync(string stationId, string batchId, bool isOk, string recipeName, string errorMessage)
        {
            if (!IsConnected)
            {
                MessageBox.Show("MES 未连接，无法上传数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            await Task.Delay(150);
            return true;
        }
    }
}
