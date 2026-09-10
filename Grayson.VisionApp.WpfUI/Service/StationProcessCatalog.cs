// Grayson.Vision.WpfUI/Service/StationProcessCatalog.cs
// 执行方案（业务过程引擎）目录 —— 替代旧的"ProcessKey 裸字符串 + JSON 手改"装配方式。
//
// 设计动机（2026-09-06 定稿）：
//   · 运行时仍以 StationConfigModel.ProcessKey + ProcessConfigJson 装配（Core 单轨，不动）；
//   · 但 UI 不再让用户面对魔法字符串/JSON 文本：工位装配 = 从目录选"执行方案"（友好名+摘要+适用），
//     参数在方案自带的面板（监视页扩展面板）以表单示教写回，JSON 只在库内承载。
//   · 目录的键集合动态对账 Core 注册表（GetSupportedProcessKeys），防"目录有 UI 无引擎"。
using System;
using System.Collections.Generic;
using System.Linq;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Station.Services;

namespace Grayson.Vision.WpfUI.Service
{
public class EngineOption
{
    /// <summary>引擎注册键（写 StationConfigModel.ProcessKey；UI 藏匿只作值）</summary>
    public string Key { get; set; }

    /// <summary>展示图标</summary>
    public string Icon { get; set; }

    /// <summary>方案友好名（UI 主显示，替代裸键）</summary>
    public string Name { get; set; }

    /// <summary>一句话摘要（副标题）</summary>
    public string Summary { get; set; }

    /// <summary>适用机型/任务说明（提示语）</summary>
    public string ApplicableTo { get; set; }

    /// <summary>是否需要挂载扩展示教面板（按键查 StationMonitorExtensionRegistry）</summary>
    public bool HasTeachPanel { get; set; }

    /// <summary>下拉展示文本：名称 —— 摘要</summary>
    public string DisplayText => $"{Icon} {Name}　{Summary}";
}

/// <summary>档案需求 → 执行方案建议（向导/工作台"一键采纳"数据源）</summary>
public class EngineSuggestion
{
    public string EngineKey { get; set; }
    public string Reason { get; set; }
    /// <summary>建议是否为强绑定（false=仅提示方向，允许用户换）</summary>
    public bool Confident { get; set; } = true;
}

public static class StationProcessCatalog
{
    // ==================== 引擎静态元数据（友好名/摘要/适用；键与 Core 注册表动态对账） ====================
    private static readonly EngineOption[] StaticCatalog =
    {
        new EngineOption
        {
            Key = "VisionPickPlace",
            Icon = "🤖",
            Name = "通用旋转取放引擎",
            Summary = "视觉定位 → 吸嘴吸取 → U 轴归正 → 摆盘放料（全参数配置化）",
            ApplicableTo = "SCARA/6轴机器人 + 吸嘴（单/双/多，同心或偏心），眼在手/固定相机均可；新机零代码建档首选",
            HasTeachPanel = true
        },
        new EngineOption
        {
            Key = "StandaloneVision",
            Icon = "🧠",
            Name = "独立视觉任务引擎（纯软件）",
            Summary = "本地图像源 → 深度学习/测量视觉链 → 判据 → CSV（无相机/运控/PLC 依赖）",
            ApplicableTo = "深度学习推理、外观测量任务模板的工位载体；绑定配方(ReadImageFile→DlInference…) + 定时器触发源，监视页【启动】即离线连续检测",
            HasTeachPanel = false
        },
        new EngineOption
        {
            Key = "MahjongPick",
            Icon = "🀄",
            Name = "双滑台卡片取放（专用）",
            Summary = "ZMC 双滑台龙门卡片抓放：真空回环/三色灯/双工台特色时序",
            ApplicableTo = "MahjongPick 双滑台（无旋转轴，保留现场行为）—— 其余同类机建议用通用引擎",
            HasTeachPanel = true
        },
        new EngineOption
        {
            Key = "MahjongDualNozzle",
            Icon = "🀄",
            Name = "双吸嘴麻将取放（专用）",
            Summary = "Epson SCARA 双吸嘴/单吸嘴麻将定位取放（固定角归正 + 视觉归正已内置）",
            ApplicableTo = "MahjongDualNozzle 原机（既有工位切新引擎前保留）；同类新机建议 VisionPickPlace",
            HasTeachPanel = true
        }
    };

    /// <summary>与 Core 工厂注册表动态对账后的全部方案（键缺失的元数据不显示，防目录领先引擎）</summary>
    public static EngineOption[] GetOptions()
    {
        var supported = GetSupportedKeys();
        return StaticCatalog
            .Where(o => supported.Contains(o.Key, System.StringComparer.OrdinalIgnoreCase))
            .Select(o => new EngineOption
            {
                Key = o.Key,
                Icon = o.Icon,
                Name = o.Name,
                Summary = o.Summary,
                ApplicableTo = o.ApplicableTo,
                HasTeachPanel = StationMonitorExtensionRegistry.IsRegistered(o.Key)
            })
            .ToArray();
    }

    /// <summary>按键查目录元数据（未知键返回 null）</summary>
    public static EngineOption Find(string engineKey)
    {
        if (string.IsNullOrWhiteSpace(engineKey)) return null;
        foreach (var o in StaticCatalog)
        {
            if (string.Equals(o.Key, engineKey, System.StringComparison.OrdinalIgnoreCase)) return o;
        }
        return null;
    }

    /// <summary>是否已注册该引擎的扩展参数/示教面板（无副作用）</summary>
    public static bool HasPanelFor(string engineKey)
    {
        return StationMonitorExtensionRegistry.IsRegistered(engineKey);
    }

    private static IEnumerable<string> GetSupportedKeys()
    {
        try
        {
            var host = App.StationHostRuntime as IStationHostRuntime
                       ?? Grayson.Vision.Core.Station.StationHostRuntime.GlobalInstance;
            if (host != null) return host.GetSupportedProcessKeys() ?? new string[0];
        }
        catch { /* 运行时不就绪时退化为静态目录键 */ }
        return StaticCatalog.Select(o => o.Key).ToArray();
    }

    // ==================== 档案需求 → 执行方案建议（v1 规则表，P 段抽 JSON 事实表） ====================

    /// <summary>
    /// 由需求档案推导"建议执行方案"。
    /// 规则（v1 务实版）：仅"定位抓取"类任务需要运动节拍引擎；检测类任务（尺寸/外观/OCR/有无）默认纯视觉链，
    /// 由配方承载即可（无取放周期）。执行机构形态决定引擎族：机器人(SCARA/6轴)→通用旋转取放；运动板卡直驱→专用取放。
    /// </summary>
    public static EngineSuggestion SuggestFor(StationProfileRequirement req)
    {
        if (req == null) return null;

        string task = req.TaskType ?? string.Empty;
        string actuator = req.ActuatorKind ?? string.Empty;

        if (task.Contains("定位抓取") || task.Contains("取放"))
        {
            if (actuator.Contains("机器人"))
            {
                bool needAngle = req.AngleNeed != null
                                 && (req.AngleNeed.Contains("角度") && !req.AngleNeed.Contains("无角度"));
                return new EngineSuggestion
                {
                    EngineKey = "VisionPickPlace",
                    Reason = needAngle
                        ? "需求档案=定位抓取 + 机器人 + 带角度 —— 建议通用旋转取放引擎（U 轴归正/偏心补偿全配置）"
                        : "需求档案=定位抓取 + 机器人 —— 建议通用旋转取放引擎（同心/偏心、单/双吸嘴均可，配置化）",
                    Confident = true
                };
            }
            if (actuator.Contains("板卡") || actuator.Contains("直驱"))
            {
                return new EngineSuggestion
                {
                    EngineKey = "MahjongPick",
                    Reason = "需求档案=定位抓取 + 运动板卡直驱 —— 参考 MahjongPick 专用机（真空回环/工台切换），新龙门取放机可基于通用引擎配置",
                    Confident = false
                };
            }
            // 未填执行机构：给方向但低置信
            return new EngineSuggestion
            {
                EngineKey = "VisionPickPlace",
                Reason = "需求档案=定位抓取（执行机构未填）—— 先按通用旋转取放引擎推荐；如为无旋转龙门请改选专用方案",
                Confident = false
            };
        }

        // 检测类任务：无取放节拍 → 不挂执行方案（纯视觉链由配方承载）
        return null;
    }
}
}
