//===================================================================================
//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationStateTexts.cs
// 说 明: 工位状态机（StationState）中文文案映射。
//        仅用于展示层文本转换，业务逻辑判断仍使用 StationState 枚举/英文字符串。
//===================================================================================

using Grayson.Vision.Contracts.Station.Interfaces;

namespace Grayson.Vision.WpfUI.Common
{
    /// <summary>
    /// 工位状态机中文文案映射（PackML 状态 → 中文显示文本）。
    /// 未连接（无可用 client 实例）统一显示为「未连接」。
    /// </summary>
    public static class StationStateTexts
    {
        /// <summary>未连接（无可用 client 实例）</summary>
        public const string NotConnectedText = "未连接";

        /// <summary>
        /// 将状态字符串转换为中文展示文本。
        /// </summary>
        /// <param name="state">状态字符串（如 Running / NotConnected / 空）</param>
        public static string ToDisplayText(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return NotConnectedText;

            switch (state)
            {
                case "Idle": return "空闲";
                case "Stopped": return "已停止";
                case "Running": return "运行中";
                case "Paused": return "已暂停";
                case "Faulted": return "故障";
                case "Resetting": return "复位中";
                case "ErrorLocked": return "急停锁定";
                case "NotConnected": return NotConnectedText;
                default: return state; // 未知状态原样显示，便于排查
            }
        }

        /// <summary>
        /// 将状态枚举转换为中文展示文本。
        /// </summary>
        public static string ToDisplayText(StationState? state)
        {
            return state.HasValue ? ToDisplayText(state.Value.ToString()) : NotConnectedText;
        }

        /// <summary>
        /// 将状态枚举转换为中文展示文本。
        /// </summary>
        public static string ToDisplayText(StationState state)
        {
            return ToDisplayText(state.ToString());
        }
    }
}
