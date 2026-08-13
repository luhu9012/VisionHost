//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: MesBridgeViewModel.cs
// 说 明: MES 对接状态：连接配置、心跳测试、手动上传
//===================================================================================
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Grayson.Vision.Contracts.MesBridge.Services;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.WpfUI.Common;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    public class MesBridgeViewModel : ViewModelBase
    {
        private readonly IMesBridgeService _mesService;

        public MesBridgeViewModel(IMesBridgeService mesService = null)
        {
            _mesService = mesService ?? new MockMesBridgeService();

            ConnectCommand = new RelayCommand(async _ => await ConnectAsync(), _ => !IsConnected);
            DisconnectCommand = new RelayCommand(async _ => await DisconnectAsync(), _ => IsConnected);
            HeartbeatCommand = new RelayCommand(async _ => await HeartbeatAsync());
            UploadTestCommand = new RelayCommand(async _ => await UploadTestAsync(), _ => IsConnected);

            Endpoint = _mesService.Endpoint;
            IsConnected = _mesService.IsConnected;
        }

        private string _endpoint;
        public string Endpoint
        {
            get => _endpoint;
            set
            {
                if (Set(ref _endpoint, value) && _mesService != null)
                {
                    _mesService.Endpoint = value;
                }
            }
        }

        private bool _isConnected;
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (Set(ref _isConnected, value))
                {
                    ConnectionStatusText = value ? "🟢 已连接" : "🔴 未连接";
                    ConnectionStatusBrushKey = value ? "SuccessBrush" : "DangerBrush";
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        private string _connectionStatusText = "🔴 未连接";
        public string ConnectionStatusText
        {
            get => _connectionStatusText;
            set => Set(ref _connectionStatusText, value);
        }

        private string _connectionStatusBrushKey = "DangerBrush";
        public string ConnectionStatusBrushKey
        {
            get => _connectionStatusBrushKey;
            set => Set(ref _connectionStatusBrushKey, value);
        }

        private string _lastMessage;
        public string LastMessage
        {
            get => _lastMessage;
            set => Set(ref _lastMessage, value);
        }

        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        public ICommand HeartbeatCommand { get; }
        public ICommand UploadTestCommand { get; }

        private async Task ConnectAsync()
        {
            try
            {
                bool result = await _mesService.ConnectAsync();
                IsConnected = result;
                LastMessage = result ? $"成功连接到 {Endpoint}" : "连接失败，请检查服务端地址。";
            }
            catch (Exception ex)
            {
                LastMessage = $"连接异常: {ex.Message}";
                IsConnected = false;
            }
        }

        private async Task DisconnectAsync()
        {
            try
            {
                await _mesService.DisconnectAsync();
                IsConnected = false;
                LastMessage = "已断开 MES 连接。";
            }
            catch (Exception ex)
            {
                LastMessage = $"断开异常: {ex.Message}";
            }
        }

        private async Task HeartbeatAsync()
        {
            try
            {
                bool result = await _mesService.HeartbeatAsync();
                LastMessage = result ? "心跳测试成功。" : "心跳测试失败，MES 未连接。";
            }
            catch (Exception ex)
            {
                LastMessage = $"心跳异常: {ex.Message}";
            }
        }

        private async Task UploadTestAsync()
        {
            try
            {
                bool result = await _mesService.UploadResultAsync("ST_01", "TEST_BATCH_001", true, "TestRecipe", string.Empty);
                LastMessage = result ? "测试上传成功。" : "测试上传失败。";
            }
            catch (Exception ex)
            {
                LastMessage = $"上传异常: {ex.Message}";
            }
        }
    }
}
