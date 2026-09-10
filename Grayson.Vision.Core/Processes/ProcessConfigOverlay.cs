//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: ProcessConfigOverlay.cs
// 说 明: 业务过程参数「字段级覆盖补丁」工具。
//
// 背景（2026-09-01 定稿）：
//   旧语义：ProcessConfigJson 是「全量快照」——示教写回时把整个 Config 对象序列化
//   进 LiteDB，导致 MahjongPickConfig.cs 等代码默认值被持久化快照冻结（改 PickZ
//   等代码默认值零效果）。
//
//   新语义：ProcessConfigJson 是「字段级补丁」——只存被显式修改过的字段
//   （如示教写回的 NozzleXOffset / TeachMode）。读取 = 代码默认值 + 补丁覆盖；
//   写入 = 在现有补丁上合并本次改动字段，且与代码默认同值的字段自动剪枝。
//   由此代码默认值重新成为唯一权威基线：改 .cs 即生效（未被补丁覆盖的字段）。
//
//   兼容性：旧版全量快照 JSON 仍可正常加载（等同所有字段都被覆盖，行为不变）；
//   由业务侧（如示教面板）在加载时检测并迁移为纯补丁（只保留其关心的字段）。
//===================================================================================

using Grayson.Vision.Contracts.Infrastructure.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grayson.Vision.Core.Processes
{
    /// <summary>业务过程参数「字段级覆盖补丁」读写工具（对任意 Config 类通用）。</summary>
    public static class ProcessConfigOverlay
    {
        /// <summary>
        /// 加载有效配置 = 代码默认值（new T() 的属性初始化器）+ 补丁覆盖。
        /// 空/非法补丁回退纯代码默认值（保证工位能启动并给出可诊断日志）。
        /// </summary>
        public static T LoadEffective<T>(string overlayJson) where T : class, new()
        {
            var cfg = new T();
            if (string.IsNullOrWhiteSpace(overlayJson)) return cfg;
            try
            {
                JsonConvert.PopulateObject(overlayJson, cfg);
            }
            catch (Exception ex)
            {
                LogBus.Warn("ProcessConfigOverlay",
                    $"过程参数补丁 JSON 非法，忽略补丁、使用代码默认值: {ex.Message}");
            }
            return cfg;
        }

        /// <summary>把补丁 JSON 解析为 JObject；空/非法返回空 JObject（不抛异常）。</summary>
        public static JObject Parse(string overlayJson)
        {
            if (string.IsNullOrWhiteSpace(overlayJson)) return new JObject();
            try
            {
                return JObject.Parse(overlayJson) ?? new JObject();
            }
            catch (Exception ex)
            {
                LogBus.Warn("ProcessConfigOverlay",
                    $"过程参数补丁 JSON 解析失败，按空补丁处理: {ex.Message}");
                return new JObject();
            }
        }

        /// <summary>
        /// 把字段级修改合并进现有补丁并返回新补丁 JSON。
        ///
        /// 传入 codeDefaults（代码默认值实例）时启用「同值剪枝」：
        /// 本次写入值与代码默认一致的字段会从补丁中移除（回归代码基线），
        /// 保证把示教值同步回 .cs 后补丁自动瘦身、代码默认重新接管。
        /// 补丁为空时返回 null（即 ProcessConfigJson 置空 = 纯代码默认）。
        /// </summary>
        public static string SetFields(string overlayJson, IDictionary<string, object> fields, object codeDefaults = null)
        {
            var patch = Parse(overlayJson);
            if (fields == null || fields.Count == 0) return patch.Count == 0 ? null : patch.ToString(Formatting.Indented);

            JObject defaults = null;
            if (codeDefaults != null)
            {
                try { defaults = JObject.FromObject(codeDefaults); }
                catch (Exception ex)
                {
                    LogBus.Warn("ProcessConfigOverlay", $"代码默认值序列化失败，跳过同值剪枝: {ex.Message}");
                }
            }

            foreach (var kv in fields)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                var token = JToken.FromObject(kv.Value);

                JToken def;
                if (defaults != null && defaults.TryGetValue(kv.Key, out def) && JToken.DeepEquals(token, def))
                {
                    patch.Remove(kv.Key); // 与代码默认一致 → 无需覆盖
                    continue;
                }
                patch[kv.Key] = token;
            }

            return patch.Count == 0 ? null : patch.ToString(Formatting.Indented);
        }

        /// <summary>补丁中现存的所有字段名（无补丁返回空列表）。</summary>
        public static List<string> GetKeys(string overlayJson)
        {
            return Parse(overlayJson).Properties().Select(p => p.Name).ToList();
        }

        /// <summary>
        /// 逐字段描述补丁与代码默认值的差异（诊断/UI 展示用）。
        /// 返回形如 "PickZ = 130（代码默认 70）" 的行；与默认同值标「与代码默认一致」；
        /// 代码中已不存在的字段标「代码中已不存在（可清除）」。
        /// </summary>
        public static List<string> DescribeDifferences<TDefaults>(string overlayJson) where TDefaults : class, new()
        {
            var result = new List<string>();
            var patch = Parse(overlayJson);
            if (patch.Count == 0) return result;

            JObject defaults;
            try { defaults = JObject.FromObject(new TDefaults()); }
            catch (Exception ex)
            {
                LogBus.Warn("ProcessConfigOverlay", $"代码默认值序列化失败: {ex.Message}");
                return patch.Properties().Select(p => $"{p.Name} = {p.Value}").ToList();
            }

            foreach (var prop in patch.Properties())
            {
                JToken def;
                if (!defaults.TryGetValue(prop.Name, out def))
                {
                    result.Add($"{prop.Name} = {prop.Value}（代码中已不存在，可清除）");
                }
                else if (JToken.DeepEquals(prop.Value, def))
                {
                    result.Add($"{prop.Name} = {prop.Value}（与代码默认一致）");
                }
                else
                {
                    result.Add($"{prop.Name} = {prop.Value}（代码默认 {def}）");
                }
            }
            return result;
        }
    }
}
