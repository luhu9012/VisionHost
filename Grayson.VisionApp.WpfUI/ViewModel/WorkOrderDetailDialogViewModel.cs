//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: WorkOrderDetailDialogViewModel.cs
// 说 明: 工单详情对话框 ViewModel
//        展示工单生命周期、时间分解、检验结果等完整信息
//===================================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Repository.Interfaces;
using Grayson.Vision.WpfUI.Service;

namespace Grayson.Vision.WpfUI.ViewModel
{
    /// <summary>
    /// 时间分解条形图项目（用于甘特图显示）
    /// </summary>
    public class TimelineBarItem : ViewModelBase
    {
        private string _label;
        public string Label
        {
            get => _label;
            set => Set(ref _label, value);
        }

        private double _durationMs;
        /// <summary>
        /// 此阶段的耗时(毫秒)
        /// </summary>
        public double DurationMs
        {
            get => _durationMs;
            set => Set(ref _durationMs, value);
        }

        private double _percentage;
        /// <summary>
        /// 占总耗时的百分比
        /// </summary>
        public double Percentage
        {
            get => _percentage;
            set => Set(ref _percentage, value);
        }

        private string _color;
        /// <summary>
        /// 条形图颜色代码 (例如 "#00C853" 绿色)
        /// </summary>
        public string Color
        {
            get => _color;
            set => Set(ref _color, value);
        }

        private string _durationText;
        /// <summary>
        /// 显示用的耗时文本 (例如 "120.5ms")
        /// </summary>
        public string DurationText
        {
            get => _durationText;
            set => Set(ref _durationText, value);
        }
    }

    /// <summary>
    /// 缺陷标签项目
    /// </summary>
    public class DefectTagItem : ViewModelBase
    {
        private string _defectCode;
        public string DefectCode
        {
            get => _defectCode;
            set => Set(ref _defectCode, value);
        }

        private string _severity;
        /// <summary>
        /// 缺陷严重程度 (None | Minor | Major | Error)
        /// </summary>
        public string Severity
        {
            get => _severity;
            set => Set(ref _severity, value);
        }

        private string _severityColor;
        /// <summary>
        /// 严重程度对应的颜色
        /// Minor → #FFFFA726 (橙色警告)
        /// Major → #FFFF3D00 (红色错误)
        /// </summary>
        public string SeverityColor
        {
            get => _severityColor;
            set => Set(ref _severityColor, value);
        }
    }

    /// <summary>
    /// 工单详情对话框 ViewModel
    /// 
    /// 职责：
    /// - 从 WorkOrder 和 WorkOrderResult 加载数据
    /// - 计算时间分解的显示数据
    /// - 格式化测量数据和缺陷信息
    /// - 管理图像列表
    /// </summary>
    public class WorkOrderDetailDialogViewModel : ViewModelBase
    {
        // 来自 WorkOrder 本身
        private string _workOrderId;
        public string WorkOrderId
        {
            get => _workOrderId;
            set => Set(ref _workOrderId, value);
        }

        private string _stationId;
        public string StationId
        {
            get => _stationId;
            set => Set(ref _stationId, value);
        }

        private string _batchId;
        public string BatchId
        {
            get => _batchId;
            set => Set(ref _batchId, value);
        }

        private string _recipeName;
        public string RecipeName
        {
            get => _recipeName;
            set => Set(ref _recipeName, value);
        }

        // 时间戳
        private DateTime _createdAt;
        public DateTime CreatedAt
        {
            get => _createdAt;
            set => Set(ref _createdAt, value);
        }

        private DateTime? _startedAt;
        public DateTime? StartedAt
        {
            get => _startedAt;
            set => Set(ref _startedAt, value);
        }

        private DateTime? _completedAt;
        public DateTime? CompletedAt
        {
            get => _completedAt;
            set => Set(ref _completedAt, value);
        }

        // WorkOrderResult 中的检验结果
        private string _resultStatus;
        /// <summary>
        /// 检验结论文本 (例如 "✓ OK" 或 "✗ NG")
        /// </summary>
        public string ResultStatus
        {
            get => _resultStatus;
            set => Set(ref _resultStatus, value);
        }

        private string _resultStatusColor;
        /// <summary>
        /// 结果状态的颜色 (OK → 绿色，NG → 红色)
        /// </summary>
        public string ResultStatusColor
        {
            get => _resultStatusColor;
            set => Set(ref _resultStatusColor, value);
        }

        private double _confidenceScore;
        /// <summary>
        /// 置信度分数 (0.0 - 1.0)
        /// </summary>
        public double ConfidenceScore
        {
            get => _confidenceScore;
            set => Set(ref _confidenceScore, value);
        }

        private string _confidenceScoreText;
        /// <summary>
        /// 置信度显示文本 (例如 "98.5%")
        /// </summary>
        public string ConfidenceScoreText
        {
            get => _confidenceScoreText;
            set => Set(ref _confidenceScoreText, value);
        }

        // 缺陷列表
        private ObservableCollection<DefectTagItem> _defectTags;
        public ObservableCollection<DefectTagItem> DefectTags
        {
            get => _defectTags;
            set => Set(ref _defectTags, value);
        }

        // 时间分解甘特图
        private ObservableCollection<TimelineBarItem> _timelineItems;
        public ObservableCollection<TimelineBarItem> TimelineItems
        {
            get => _timelineItems;
            set => Set(ref _timelineItems, value);
        }

        private double _totalElapsedMs;
        /// <summary>
        /// 工单总耗时(毫秒)
        /// </summary>
        public double TotalElapsedMs
        {
            get => _totalElapsedMs;
            set => Set(ref _totalElapsedMs, value);
        }

        private string _totalElapsedText;
        /// <summary>
        /// 总耗时文本显示 (例如 "250.5 ms")
        /// </summary>
        public string TotalElapsedText
        {
            get => _totalElapsedText;
            set => Set(ref _totalElapsedText, value);
        }

        // 图像列表
        private ObservableCollection<string> _imagePaths;
        public ObservableCollection<string> ImagePaths
        {
            get => _imagePaths;
            set => Set(ref _imagePaths, value);
        }

        private string _selectedImagePath;
        public string SelectedImagePath
        {
            get => _selectedImagePath;
            set => Set(ref _selectedImagePath, value);
        }

        // 错误消息
        private string _errorMessage;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => Set(ref _errorMessage, value);
        }

        private string _resultComment;
        public string ResultComment
        {
            get => _resultComment;
            set => Set(ref _resultComment, value);
        }

        // 测量数据
        private ObservableCollection<MeasurementDataItem> _measurementData;
        public ObservableCollection<MeasurementDataItem> MeasurementData
        {
            get => _measurementData;
            set => Set(ref _measurementData, value);
        }

        public WorkOrderDetailDialogViewModel()
        {
            DefectTags = new ObservableCollection<DefectTagItem>();
            TimelineItems = new ObservableCollection<TimelineBarItem>();
            ImagePaths = new ObservableCollection<string>();
            MeasurementData = new ObservableCollection<MeasurementDataItem>();
        }

        /// <summary>
        /// 使用工单数据初始化对话框
        /// </summary>
        public void LoadWorkOrder(WorkOrder workOrder)
        {
            if (workOrder == null) return;

            // 基本信息
            WorkOrderId = workOrder.WorkOrderId;
            StationId = workOrder.StationId;
            BatchId = workOrder.BatchId;
            CreatedAt = workOrder.CreatedAt;
            StartedAt = workOrder.StartedAt;
            CompletedAt = workOrder.CompletedAt;

            // 如果没有 Result 则无法显示详情
            if (workOrder.Result == null) return;

            var result = workOrder.Result;

            // 检验结果
            ResultStatus = result.Result.IsOk ? "✓ OK" : "✗ NG";
            ResultStatusColor = result.Result.IsOk ? "#FF00C853" : "#FFFF3D00";

            // 置信度
            ConfidenceScore = result.Measurements?.ConfidenceScore ?? 0;
            ConfidenceScoreText = $"{ConfidenceScore * 100:F1}%";

            // 缺陷标签
            PopulateDefectTags(result.Result.DefectCodes, result.Result.SeverityLevel);

            // 时间分解
            if (result.Timing != null)
            {
                PopulateTimeline(result.Timing);
            }

            // 图像路径
            if (result.ImagePaths != null)
            {
                ImagePaths.Clear();
                foreach (var imagePath in result.ImagePaths)
                {
                    ImagePaths.Add(imagePath);
                }
                if (ImagePaths.Count > 0)
                {
                    SelectedImagePath = ImagePaths[0];
                }
            }

            // 错误与备注
            ErrorMessage = result.ErrorMessage;
            ResultComment = result.ResultComment;

            // 测量数据
            PopulateMeasurementData(result.Measurements);

            TotalElapsedMs = result.Timing?.TotalElapsedMs ?? 0;
            TotalElapsedText = $"{TotalElapsedMs:F1} ms";
        }

        /// <summary>
        /// 填充缺陷标签列表
        /// </summary>
        private void PopulateDefectTags(List<string> defectCodes, string severity)
        {
            DefectTags.Clear();

            if (defectCodes == null || defectCodes.Count == 0)
            {
                return;
            }

            foreach (var code in defectCodes)
            {
                var tag = new DefectTagItem
                {
                    DefectCode = code,
                    Severity = severity
                };

                // 根据严重程度分配颜色
                if (severity == "Minor")
                {
                    tag.SeverityColor = "#FFFFA726";    // 橙色
                }
                else if (severity == "Major")
                {
                    tag.SeverityColor = "#FFFF3D00";    // 红色
                }
                else
                {
                    tag.SeverityColor = "#FFB0B0B0";    // 灰色
                }

                DefectTags.Add(tag);
            }
        }

        /// <summary>
        /// 填充时间分解甘特图数据
        /// </summary>
        private void PopulateTimeline(WorkOrderTiming timing)
        {
            TimelineItems.Clear();

            if (timing == null) return;

            var total = timing.TotalElapsedMs;

            if (total <= 0)
                return;

            // 队列等待
            TimelineItems.Add(new TimelineBarItem
            {
                Label = "队列等待",
                DurationMs = timing.QueueWaitTimeMs,
                Percentage = (timing.QueueWaitTimeMs / total) * 100,
                Color = "#FFFFA726",  // 橙色
                DurationText = $"{timing.QueueWaitTimeMs:F1} ms"
            });

            // 硬件交互总计
            TimelineItems.Add(new TimelineBarItem
            {
                Label = "硬件交互",
                DurationMs = timing.Hardware.Total,
                Percentage = (timing.Hardware.Total / total) * 100,
                Color = "#FF00C853",  // 绿色
                DurationText = $"{timing.Hardware.Total:F1} ms"
            });

            // 后处理
            TimelineItems.Add(new TimelineBarItem
            {
                Label = "后处理",
                DurationMs = timing.PostProcessTimeMs,
                Percentage = (timing.PostProcessTimeMs / total) * 100,
                Color = "#FF0091EA",  // 蓝色
                DurationText = $"{timing.PostProcessTimeMs:F1} ms"
            });
        }

        /// <summary>
        /// 填充测量数据
        /// </summary>
        private void PopulateMeasurementData(WorkOrderResult.MeasurementData measurements)
        {
            MeasurementData.Clear();

            if (measurements?.Values == null || measurements.Values.Count == 0)
            {
                return;
            }

            foreach (var kvp in measurements.Values)
            {
                MeasurementData.Add(new MeasurementDataItem
                {
                    ItemName = kvp.Key,
                    Value = kvp.Value.ToString("F4"),
                    Unit = "mm"  // 默认单位，可根据需要扩展
                });
            }
        }
    }

    /// <summary>
    /// 测量数据显示项目
    /// </summary>
    public class MeasurementDataItem : ViewModelBase
    {
        private string _itemName;
        public string ItemName
        {
            get => _itemName;
            set => Set(ref _itemName, value);
        }

        private string _value;
        public string Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        private string _unit;
        public string Unit
        {
            get => _unit;
            set => Set(ref _unit, value);
        }

        private string _status;
        /// <summary>
        /// 测量值状态 (OK | OOT | Warning)
        /// OOT = Out of Tolerance (超公差)
        /// </summary>
        public string Status
        {
            get => _status;
            set => Set(ref _status, value);
        }
    }
}
