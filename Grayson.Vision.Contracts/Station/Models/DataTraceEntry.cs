//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 说 明: 工单检验追溯记录
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Mvvm;
using System;

namespace Grayson.Vision.Contracts.Station.Models
{
    /// <summary>
    /// 检验数据追溯条目 - 记录单个工单在某工位的检验结果
    /// </summary>
    public class DataTraceEntry : ViewModelBase
    {
        /// <summary>检验时间</summary>
        public DateTime InspectTime { get; set; }

        /// <summary>工位 ID</summary>
        public string StationId { get; set; }

        /// <summary>批次 ID（工单 ID）</summary>
        public string BatchId { get; set; }

        /// <summary>关联的配方名称</summary>
        public string RecipeName { get; set; }

        /// <summary>检验结果代码（OK/NG/Error/Skip）</summary>
        public string Result { get; set; }

        /// <summary>错误信息或异常描述</summary>
        public string ErrorMessage { get; set; }

        /// <summary>本工位周期时间（毫秒）</summary>
        public double CycleTimeMs { get; set; }

        /// <summary>检验结果图像路径</summary>
        public string ImagePath { get; set; }

        /// <summary>结果文本呈现</summary>
        public string ResultText => Result;

        /// <summary>结果对应的 UI 颜色键：OK→SuccessBrush、NG→DangerBrush、其余(Error/Skip/—)=中性灰。</summary>
        public string ResultBrushKey
        {
            get
            {
                switch (Result)
                {
                    case "OK": return "SuccessBrush";
                    case "NG": return "DangerBrush";
                    default: return "TextSecondaryBrush"; // Error/Skip/未知不做误红处理
                }
            }
        }

        /// <summary>是否 NG 结果（供整行红色高亮触发）。</summary>
        public bool IsNg => Result == "NG";
    }
}
