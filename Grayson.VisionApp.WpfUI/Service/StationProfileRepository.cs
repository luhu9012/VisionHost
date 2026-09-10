//===================================================================================
// Copyright (c) 2026 Grayson.Vision. All rights reserved.
// 文件名: StationProfileRepository.cs
// 说 明: StationProfile 草稿/档案 JSON 仓储（仿 TemplateManager 落盘惯例）。
//        · 落盘目录：{BaseDirectory}\Config\StationProfiles\*.json
//          （Draft 草稿以 ProfileId.json 命名；Active 以 StationId.json 命名，便于按工位检索）
//        · 只负责 StationProfile（需求元数据 + 派生建议）的存取；
//          真实工位的运行时配置（StationConfigModel）仍走 StationConfigService(LiteDB)。
//===================================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grayson.Vision.Contracts.Station.Models;
using Grayson.Vision.Contracts.Infrastructure.Logging;
using Newtonsoft.Json;

namespace Grayson.Vision.WpfUI.Service
{
    public class StationProfileRepository
    {
        private readonly string _directory;

        public StationProfileRepository()
        {
            _directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "StationProfiles");
            if (!Directory.Exists(_directory))
            {
                Directory.CreateDirectory(_directory);
            }
        }

        /// <summary>列出全部草稿（Draft，按更新时间倒序）——向导"载入草稿"列表</summary>
        public List<StationProfile> ListDrafts()
        {
            return ListAll().Where(p => p.Status == StationProfileStatus.Draft).ToList();
        }

        /// <summary>列出全部档案（Active，按更新时间倒序）</summary>
        public List<StationProfile> ListAll()
        {
            var list = new List<StationProfile>();
            try
            {
                foreach (var file in Directory.GetFiles(_directory, "*.json"))
                {
                    try
                    {
                        var p = JsonConvert.DeserializeObject<StationProfile>(File.ReadAllText(file));
                        if (p != null) list.Add(p);
                    }
                    catch (Exception ex)
                    {
                        LogBus.Warn(nameof(StationProfileRepository), $"StationProfile 解析失败，跳过: {Path.GetFileName(file)} - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(StationProfileRepository), "读取 StationProfile 目录失败", ex);
            }
            return list.OrderByDescending(p => p.UpdatedTime).ToList();
        }

        /// <summary>按 ProfileId 取草稿（用于向导"载入草稿"）</summary>
        public StationProfile GetDraft(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId)) return null;
            foreach (var p in ListAll())
            {
                if (string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>按 StationId 取档案（工作台/监视显示该工位的方案元数据时调用）</summary>
        public StationProfile GetByStationId(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId)) return null;
            foreach (var p in ListAll())
            {
                if (string.Equals(p.StationId, stationId, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        /// <summary>保存（自动按状态命名文件；Active 迁移时如旧 Draft 文件存在则删除）</summary>
        public bool Save(StationProfile profile)
        {
            if (profile == null) return false;
            try
            {
                profile.UpdatedTime = DateTime.Now;
                string file = Path.Combine(_directory, profile.ProfileFileName);
                // 状态从 Draft 提升为 Active：旧草稿文件（ProfileId.json）若不同名则清除
                if (profile.Status == StationProfileStatus.Active)
                {
                    string draftFile = Path.Combine(_directory, profile.ProfileId + ".json");
                    if (!string.Equals(draftFile, file, StringComparison.OrdinalIgnoreCase) && File.Exists(draftFile))
                    {
                        File.Delete(draftFile);
                    }
                }
                File.WriteAllText(file, JsonConvert.SerializeObject(profile, Formatting.Indented));
                LogBus.Info(nameof(StationProfileRepository),
                    $"StationProfile 已保存: {profile.ProfileFileName} 状态={profile.Status} 工位={profile.StationCode ?? "(草稿)"} 任务={profile.Requirement?.TaskType ?? "未填"}");
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(StationProfileRepository), "保存 StationProfile 失败", ex);
                return false;
            }
        }

        /// <summary>删除（草稿删除 / 工位删除时清理档案）</summary>
        public bool Delete(string profileId, string stationId = null)
        {
            try
            {
                var targets = new List<string>();
                if (!string.IsNullOrWhiteSpace(profileId)) targets.Add(Path.Combine(_directory, profileId + ".json"));
                if (!string.IsNullOrWhiteSpace(stationId)) targets.Add(Path.Combine(_directory, stationId + ".json"));
                foreach (var f in targets)
                {
                    if (File.Exists(f))
                    {
                        File.Delete(f);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                LogBus.Error(nameof(StationProfileRepository), "删除 StationProfile 失败", ex);
                return false;
            }
        }
    }
}
