using System;
using System.Collections.Generic;
using System.IO;
using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Infrastructure.Logging;

namespace Grayson.Vision.Contracts.Calibration.Services
{
    /// <summary>
    /// 标定矩阵（.tup）的唯一存储真源 —— 单轨存储。
    ///
    /// ★★ 2026-09-15 定案（消除双写）：标定矩阵的持久归宿**只有工位级**一处：
    ///     Recipes\Workstations\{工位码|Default}\Calib\{方案名}_HandEye.tup
    ///   历史遗留的"设备级"目录 Recipes\Devices\{设备ID}\Calib\ 已废弃：
    ///     · 不再写入（向导 EnsureMatrixPersisted / 管理页发布链全部改走工位级）
    ///     · 不再读取（消费端兜底候选不再拼设备目录）
    ///     · 存量数据由 MigrateAndPurgeDeviceScope 迁到工位级后整目录归档清除
    ///   矩阵不是相机/轴卡等**设备实例**的属性，而是"这台机械手 + 这台相机在该工位安装位姿"的
    ///   属性 ⇒ 与设备绑定会随设备换装/多工位共用同一相机而静默失配（同一台相机被两个工位领用时，
    ///   设备目录只能存一份，两个工位的标定互相覆盖）。
    ///
    /// 作用域语义（保留两档，不是双写）：
    ///   0 工位级  Recipes\Workstations\{工位码}\Calib  —— **持久归宿**（标定产物就住这里）
    ///   1 配方级  Recipes\{配方Code}\Calib             —— **发布副本**（按需分发到消费它的配方）
    /// </summary>
    public static class CalibrationMatrixStore
    {
        /// <summary>已废弃的设备级作用域目录名（只用于识别/清理，禁止再写入）</summary>
        public const string LegacyDeviceScopeFolder = "Devices";

        /// <summary>工位级作用域目录名</summary>
        public const string StationScopeFolder = "Workstations";

        /// <summary>标定目录名</summary>
        public const string CalibFolder = "Calib";

        /// <summary>无绑定工位时的兜底作用域键</summary>
        public const string DefaultScopeKey = "Default";

        /// <summary>矩阵产物文件名后缀（{Sanitize(方案名)} + 此后缀）</summary>
        public const string MatrixFileSuffix = "_HandEye.tup";

        /// <summary>Recipes 根目录</summary>
        public static string RecipesRoot
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes"); }
        }

        /// <summary>工位级标定目录：Recipes\Workstations\{工位码|Default}\Calib</summary>
        public static string GetStationCalibDir(string stationCode)
        {
            string station = string.IsNullOrWhiteSpace(stationCode) ? DefaultScopeKey : stationCode.Trim();
            return Path.Combine(RecipesRoot, StationScopeFolder, station, CalibFolder);
        }

        /// <summary>配方级标定目录：Recipes\{配方Code|Default}\Calib（发布副本，非持久归宿）</summary>
        public static string GetRecipeCalibDir(string recipeCode)
        {
            string key = string.IsNullOrWhiteSpace(recipeCode) ? DefaultScopeKey : recipeCode.Trim();
            return Path.Combine(RecipesRoot, key, CalibFolder);
        }

        /// <summary>已废弃的设备级标定目录：Recipes\Devices\{设备ID}\Calib（只读识别用）</summary>
        public static string GetLegacyDeviceCalibDir(string deviceId)
        {
            string device = string.IsNullOrWhiteSpace(deviceId) ? DefaultScopeKey : deviceId.Trim();
            return Path.Combine(RecipesRoot, LegacyDeviceScopeFolder, device, CalibFolder);
        }

        /// <summary>废弃设备级作用域根目录：Recipes\Devices</summary>
        public static string LegacyDeviceScopeRoot
        {
            get { return Path.Combine(RecipesRoot, LegacyDeviceScopeFolder); }
        }

        /// <summary>矩阵产物标准文件名：{Sanitize(方案名)}_HandEye.tup</summary>
        public static string GetMatrixFileName(string profileName)
        {
            return SanitizeFileName(profileName) + MatrixFileSuffix;
        }

        /// <summary>文件名清洗：替换 Windows 非法文件名字符（防标定方案名含冒号/斜杠等导致落盘失败）</summary>
        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Calibration";
            }
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }
            return new string(chars);
        }

        /// <summary>该路径是否落在已废弃的"设备级"作用域内（Recipes\Devices\...）</summary>
        public static bool IsLegacyDeviceScopePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                string full = Path.GetFullPath(path);
                string legacyRoot = Path.GetFullPath(LegacyDeviceScopeRoot);
                if (!legacyRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                {
                    legacyRoot += Path.DirectorySeparatorChar;
                }
                return full.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 解析档案对应的矩阵文件路径（按序取第一个存在的文件）：
        ///   1) profile.HomMatFilePath —— 档案记录的当前路径（★若它落在废弃的设备级作用域，跳过：
        ///      该目录随时会被清理，用它只会读到"即将消失的文件"）
        ///   2) 设备级路径的同名文件在**工位级**目录的对应位置（历史档案迁移后的落点）
        ///   3) 工位级目录\{Sanitize(方案名)}_HandEye.tup（标准产物名）
        /// 全都不存在 ⇒ 返回 null（调用方须明示"矩阵缺失"，不许静默降级）。
        /// </summary>
        public static string ResolveMatrixPath(CalibrationProfile profile)
        {
            if (profile == null)
            {
                return null;
            }
            foreach (var candidate in EnumerateMatrixCandidates(profile))
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // 单个候选异常不影响其余候选
                }
            }
            return null;
        }

        /// <summary>按优先级枚举档案可能的矩阵路径候选（不去重，不做存在性判断）</summary>
        public static IEnumerable<string> EnumerateMatrixCandidates(CalibrationProfile profile)
        {
            if (profile == null)
            {
                yield break;
            }

            string recorded = profile.HomMatFilePath;
            if (!string.IsNullOrWhiteSpace(recorded) && !IsLegacyDeviceScopePath(recorded))
            {
                yield return recorded;
            }

            string stationDir = GetStationCalibDir(profile.BoundStationCode);

            // 档案记录的是设备级路径 ⇒ 换到工位级目录找同名文件（迁移后的落点）
            if (!string.IsNullOrWhiteSpace(recorded))
            {
                // ⚠ C# 不允许在【含 catch 的 try 块体】里 yield return（CS1626）。
                //   故先算出文件名、再在 try 之外 yield —— 语义不变（GetFileName 失败即视为无同名文件）。
                string sameName = null;
                try
                {
                    sameName = Path.GetFileName(recorded);
                }
                catch
                {
                }
                if (!string.IsNullOrWhiteSpace(sameName))
                {
                    yield return Path.Combine(stationDir, sameName);
                }
            }

            yield return Path.Combine(stationDir, GetMatrixFileName(profile.Name));
        }

        /// <summary>
        /// 把矩阵从临时/任意来源落盘到**工位级**唯一归宿（幂等：源即目标时直接返回）。
        /// 失败返回 false 并给出 message（调用方须保留原路径并告警，不许静默当作成功）。
        /// </summary>
        public static bool TryPersistToStationScope(
            string sourceFile,
            string profileName,
            string stationCode,
            out string targetFile,
            out string message)
        {
            targetFile = null;
            message = null;

            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                message = "源矩阵路径为空";
                return false;
            }

            try
            {
                string targetDir = GetStationCalibDir(stationCode);
                Directory.CreateDirectory(targetDir);
                targetFile = Path.Combine(targetDir, GetMatrixFileName(profileName));

                if (string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
                {
                    return true; // 已在目标位
                }

                if (!File.Exists(sourceFile))
                {
                    message = "源矩阵文件不存在: " + sourceFile;
                    return false;
                }

                File.Copy(sourceFile, targetFile, true);
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 一次性迁移 + 清理：把 Recipes\Devices 下所有 .tup 迁到对应工位级目录，
        /// 随后把整棵 Recipes\Devices 移入 Archive\_purged_device_calib_&lt;时间戳&gt; 归档（不直接删除）。
        /// 幂等：Recipes\Devices 不存在时直接返回 changed=false。
        ///
        /// ★ 迁移目标是"档案可解析到的工位"：按文件名在 Config\Calibrations 里反查档案的
        ///   BoundStationCode；反查不到则落到 Default 作用域（宁可留数据在 Default 也不丢）。
        /// </summary>
        public static bool MigrateAndPurgeDeviceScope(out int migratedCount, out int failedCount, out string archivePath)
        {
            migratedCount = 0;
            failedCount = 0;
            archivePath = null;

            string legacyRoot = LegacyDeviceScopeRoot;
            if (!Directory.Exists(legacyRoot))
            {
                return false;
            }

            var stationByFileName = BuildStationLookup();

            string[] tups = new string[0];
            try
            {
                tups = Directory.GetFiles(legacyRoot, "*.tup", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                LogBus.Warn("CalibrationMatrixStore", "扫描设备级标定目录失败: " + ex.Message);
            }

            foreach (var tup in tups)
            {
                try
                {
                    string fileName = Path.GetFileName(tup);
                    string stationCode;
                    if (!stationByFileName.TryGetValue(fileName, out stationCode))
                    {
                        stationCode = DefaultScopeKey;
                    }

                    string targetDir = GetStationCalibDir(stationCode);
                    Directory.CreateDirectory(targetDir);
                    string target = Path.Combine(targetDir, fileName);

                    if (File.Exists(target))
                    {
                        migratedCount++; // 工位级已有同名产物（工位级本来就是主写路径）⇒ 无需覆盖
                        continue;
                    }

                    File.Copy(tup, target, false);
                    migratedCount++;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    LogBus.Warn("CalibrationMatrixStore", "设备级矩阵迁移失败 " + tup + " : " + ex.Message);
                }
            }

            try
            {
                string archiveDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Archive");
                Directory.CreateDirectory(archiveDir);
                archivePath = Path.Combine(archiveDir,
                    "_purged_device_calib_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.Move(legacyRoot, archivePath);
            }
            catch (Exception ex)
            {
                archivePath = null;
                LogBus.Warn("CalibrationMatrixStore", "设备级标定目录归档失败（数据未删除）: " + ex.Message);
            }

            LogBus.Info("CalibrationMatrixStore",
                "设备级标定作用域已废弃：迁移 " + migratedCount + " 个矩阵，失败 " + failedCount
                + " 个，目录归档至 " + (archivePath ?? "(归档失败，目录保留)"));

            return true;
        }

        /// <summary>
        /// 建立 "矩阵文件名 → 该产物所属工位码" 的反查表。
        /// 依据：档案名 → 产物名 {Sanitize(档案名)}_HandEye.tup，档案里的 BoundStationCode 才是真源。
        /// 同名档案（复合工位上下相机 e 档曾同名）取先到者，不做猜测性覆盖。
        /// 注：本类位于 Contracts 层（无项目引用），故直接读 Config\Calibrations\*.json 而不经 Repository。
        /// </summary>
        private static Dictionary<string, string> BuildStationLookup()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string configDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Calibrations");
                if (!Directory.Exists(configDir))
                {
                    return map;
                }
                foreach (var jsonFile in Directory.GetFiles(configDir, "*.json"))
                {
                    try
                    {
                        var profile = Newtonsoft.Json.JsonConvert.DeserializeObject<CalibrationProfile>(
                            File.ReadAllText(jsonFile));
                        if (profile == null || string.IsNullOrWhiteSpace(profile.Name))
                        {
                            continue;
                        }
                        string fileName = GetMatrixFileName(profile.Name);
                        if (map.ContainsKey(fileName))
                        {
                            continue;
                        }
                        map[fileName] = string.IsNullOrWhiteSpace(profile.BoundStationCode)
                            ? DefaultScopeKey
                            : profile.BoundStationCode.Trim();
                    }
                    catch
                    {
                        // 单个档案损坏不影响其余档案
                    }
                }
            }
            catch (Exception ex)
            {
                LogBus.Warn("CalibrationMatrixStore", "读取标定档案建立工位反查表失败（迁移将落到 Default）: " + ex.Message);
            }
            return map;
        }
    }
}
