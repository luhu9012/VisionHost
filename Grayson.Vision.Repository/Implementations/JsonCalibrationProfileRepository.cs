// Implementations/JsonCalibrationProfileRepository.cs
//===================================================================================
// 2026-09-15：标定档案【单一存储】实现（JSON），替代原先的 LiteDbCalibrationProfileRepository。
//
// 为什么统一到 JSON：
//   同一个目录 Config\Calibrations 早已被两处既有代码当作真源读取——
//     · CalibrationService.GetAllProfiles()             ← 校验台产物聚合的唯一数据源
//     · CalibrationApplyParam.RefreshMatrixFiles()      ← 节点属性面板的矩阵候选
//   而写入方（标定中心 / 标定向导）此前走 LiteDB。两套存储 ⇒ 库里明明有
//   「吸嘴1_旋转中心 e」（Cam_A/ST_002，HasRotationCenter=true），校验台却报
//   "e偏心=未标 旋转中心O=未标"——因为 Config\Calibrations 是空目录，聚合退化为单档案。
//   统一后：写什么就读到什么，校验台/节点/向导/中心四处同源。
//
// 文件布局：Config\Calibrations\<方案名>__<Id前8位>.json
//   文件名保留方案名便于人工定位；后缀 Id 保证改名后仍能定位并清理旧文件。
//   文件内容 = CalibrationProfile 模型本身（与上述两个既有读取方的反序列化类型一致）。
//===================================================================================
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Repository.Core;
using Grayson.Vision.Repository.Entities;
using Grayson.Vision.Repository.Interfaces;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Text;

namespace Grayson.Vision.Repository.Implementations
{
    /// <summary>
    /// 标定档案（CalibrationProfile）的 JSON 单一存储。
    /// 写入原子化（先写 .tmp 再落定），单文件损坏只跳过该文件，不影响其余方案。
    /// </summary>
    public class JsonCalibrationProfileRepository : ICalibrationProfileRepository
    {
        /// <summary>存储根目录。默认与 CalibrationService._configDirectory 完全一致：
        /// <c>&lt;AppBase&gt;\Config\Calibrations</c>——两处必须同源，改这里等于改全部读法。</summary>
        public static string RootDirectory { get; set; } =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Calibrations");

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
        };

        /// <summary>最近一次失败/异常的原因（接口没有错误通道，调用方可读它打日志）。</summary>
        public string LastError { get; private set; }

        // ==================== 读 ====================

        public IEnumerable<CalibrationProfilePo> GetAll()
        {
            var list = new List<CalibrationProfilePo>();
            LastError = null;
            try
            {
                if (!Directory.Exists(RootDirectory)) return list;

                var byId = new Dictionary<string, CalibrationProfilePo>(StringComparer.OrdinalIgnoreCase);
                var broken = new List<string>();
                foreach (var file in Directory.GetFiles(RootDirectory, "*.json"))
                {
                    try
                    {
                        var model = JsonConvert.DeserializeObject<CalibrationProfile>(
                            File.ReadAllText(file, Encoding.UTF8));
                        if (model == null) { broken.Add(Path.GetFileName(file) + "(空)"); continue; }

                        var po = Wrap(model);
                        if (string.IsNullOrWhiteSpace(po.Id)) po.Id = IdSuffixOf(Path.GetFileNameWithoutExtension(file));
                        if (string.IsNullOrWhiteSpace(po.Model.Id)) po.Model.Id = po.Id;

                        // 同 Id 残留（改名/手工拷贝）→ 取 UpdatedAt 最新那份
                        if (byId.TryGetValue(po.Id, out var prev) && prev.Model.UpdatedAt >= po.Model.UpdatedAt)
                            continue;

                        byId[po.Id] = po;
                    }
                    catch (Exception ex)
                    {
                        broken.Add(Path.GetFileName(file) + "(" + ex.Message + ")");
                    }
                }

                list.AddRange(byId.Values.OrderBy(p => p.ProfileName, StringComparer.OrdinalIgnoreCase));

                // 损坏文件不静默：留给调用方在日志里显式暴露
                if (broken.Count > 0)
                    LastError = "有 " + broken.Count + " 个标定档案文件读取失败并已跳过: " + string.Join("; ", broken);
            }
            catch (Exception ex)
            {
                LastError = "扫描标定档案目录失败(" + RootDirectory + "): " + ex.Message;
            }
            return list;
        }

        public CalibrationProfilePo GetById(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            return GetAll().FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Model?.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<CalibrationProfilePo> Find(Expression<Func<CalibrationProfilePo, bool>> predicate)
        {
            if (predicate == null) return new List<CalibrationProfilePo>();
            // JSON 后端：谓词在内存里求值（LINQ to Objects），语义与 LiteDB 的简单谓词一致。
            return GetAll().AsQueryable().Where(predicate).ToList();
        }

        public CalibrationProfilePo GetByName(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return null;
            return GetAll().FirstOrDefault(p =>
                string.Equals(p.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
        }

        public CalibrationProfilePo GetByStationCode(string stationCode)
        {
            if (string.IsNullOrWhiteSpace(stationCode)) return null;
            return GetAll()
                .Where(p => string.Equals(p.BoundStationCode, stationCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Model?.UpdatedAt ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        // ==================== 写 ====================

        public bool Insert(CalibrationProfilePo entity) => Write(entity, isNew: true);

        public bool Update(CalibrationProfilePo entity) => Write(entity, isNew: false);

        public bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            try
            {
                if (!Directory.Exists(RootDirectory)) return false;
                int n = 0;
                foreach (var file in FilesOfId(id))
                {
                    File.Delete(file);
                    n++;
                }
                LastError = null;
                return n > 0;
            }
            catch (Exception ex)
            {
                LastError = "删除标定档案失败: " + ex.Message;
                return false;
            }
        }

        private bool Write(CalibrationProfilePo po, bool isNew)
        {
            try
            {
                if (po == null || po.Model == null)
                {
                    LastError = "标定档案为空（Model 缺失）——已拒绝写入，避免产生不可消费的空档案";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(po.Id))
                    po.Id = string.IsNullOrWhiteSpace(po.Model.Id) ? Guid.NewGuid().ToString("N") : po.Model.Id;
                if (string.IsNullOrWhiteSpace(po.Model.Id)) po.Model.Id = po.Id;
                if (string.IsNullOrWhiteSpace(po.Model.Name))
                {
                    LastError = "方案名为空——已拒绝写入（文件名必须可读，否则无法人工定位）";
                    return false;
                }

                // 同步 PO 冗余字段（读回来时由 Wrap 重建，这里保持自洽）
                po.ProfileName = po.Model.Name;
                po.CalibrationQuantity = po.Model.Quantity;
                po.BoundStationCode = po.Model.BoundStationCode;
                po.BoundDeviceId = po.Model.BoundDeviceId;
                po.IsCalibrated = po.Model.IsCalibrated;
                po.UpdatedTime = po.Model.UpdatedAt;
                if (isNew || po.CreatedTime == default(DateTime)) po.CreatedTime = po.Model.UpdatedAt;

                Directory.CreateDirectory(RootDirectory);

                string target = PathFor(po.Model.Name, po.Model.Id);
                string tmp = target + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(po.Model, JsonSettings), new UTF8Encoding(false));
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);

                // 改名后清掉同 Id 的旧文件名（否则会留一份读起来"同 Id 取最新"的野档案）
                foreach (var stale in FilesOfId(po.Model.Id).Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(stale); } catch { /* 清不掉不影响新档案 */ }
                }

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "写入标定档案失败: " + ex.Message;
                return false;
            }
        }

        // ==================== 路径与文件名 ====================

        private static string PathFor(string name, string id) =>
            Path.Combine(RootDirectory, Sanitize(name) + "__" + IdSuffixOf(id) + ".json");

        /// <summary>文件名后缀：Id 前 8 位（够定位，又不会把文件名撑长）。</summary>
        private static string IdSuffixOf(string id) =>
            string.IsNullOrWhiteSpace(id) ? "" : (id.Length >= 8 ? id.Substring(0, 8) : id);

        /// <summary>按 Id 后缀找出该档案的所有文件（覆盖改名残留）。</summary>
        private static IEnumerable<string> FilesOfId(string id)
        {
            string suffix = IdSuffixOf(id);
            if (string.IsNullOrWhiteSpace(suffix) || !Directory.Exists(RootDirectory))
                return Enumerable.Empty<string>();
            return Directory.GetFiles(RootDirectory, "*.json")
                .Where(f =>
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    int i = stem.LastIndexOf("__", StringComparison.Ordinal);
                    return i >= 0 && string.Equals(stem.Substring(i + 2), suffix, StringComparison.OrdinalIgnoreCase);
                });
        }

        /// <summary>文件名安全化：非法字符替换为 '_'，并截断避免超长路径。</summary>
        private static string Sanitize(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Calibration";
            var invalid = Path.GetInvalidFileNameChars();
            string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            s = s.Trim().TrimEnd('.');
            if (s.Length > 80) s = s.Substring(0, 80);
            return string.IsNullOrWhiteSpace(s) ? "Calibration" : s;
        }

        private static CalibrationProfilePo Wrap(CalibrationProfile m) => new CalibrationProfilePo
        {
            Id = m.Id,
            ProfileName = m.Name,
            CalibrationQuantity = m.Quantity,
            BoundStationCode = m.BoundStationCode,
            BoundDeviceId = m.BoundDeviceId,
            IsCalibrated = m.IsCalibrated,
            Model = m,
            CreatedTime = m.UpdatedAt,
            UpdatedTime = m.UpdatedAt,
        };
    }
}
