using Grayson.Vision.Contracts.Calibration.Models;
using Grayson.Vision.Contracts.Calibration.Services;
using Grayson.Vision.Nodes.Common;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Grayson.Vision.Nodes.All.CalibrationLocation.CalibrationApply
{
    public class CalibrationApplyParam : ParamBase
    {
        private string _calibrationProfileName = "Default_HandEye";
        /// <summary>
        /// 绑定的标定方案名称/标识
        /// </summary>
        public string CalibrationProfileName
        {
            get => _calibrationProfileName;
            set => Set(ref _calibrationProfileName, value);
        }

        private string _cameraSlotKey = "";
        /// <summary>
        /// ★相机槽键（2026-09-12，Cam_A / Cam_C …）：视觉链节点按"一个相机一个方案"的口径
        /// 定位到该槽已发布的九点矩阵 H。非空时，RefreshMatrixFiles 会优先把该槽对应的
        /// .tup 自动选中（匹配文件名里的 Cam_ 槽标识 / 工位级发布目录）。
        /// 留空 = 不按槽过滤，沿用"手选矩阵文件路径"的旧行为（向后兼容）。
        /// 注意：本节点只消费 H（输出命令位域 H(u)），e/t 补偿不在此叠加——
        /// 那是流程编排层/生产引擎的职责（它们才知道拍照机位与作业 U 角）。
        /// </summary>
        public string CameraSlotKey
        {
            get => _cameraSlotKey;
            set
            {
                if (Set(ref _cameraSlotKey, value))
                {
                    // 换槽即重扫并按槽自动选中（若本槽有已发布矩阵）
                    RefreshMatrixFiles();
                }
            }
        }

        private string _homMatFilePath = "";
        /// <summary>
        /// 标定矩阵文件路径 (.tup / .mat)
        /// </summary>
        public string HomMatFilePath
        {
            get => _homMatFilePath;
            set
            {
                if (Set(ref _homMatFilePath, value))
                {
                    // 手动/浏览选中的路径可能不在自动扫描结果里，就地补进候选列表，
                    // 保证属性面板下拉框始终能显示当前选中的路径。
                    EnsureInAvailableList(value);
                }
            }
        }

        private bool _isPixelToWorld = true;
        /// <summary>
        /// 转换方向：true 为 像素 -> 物理 (mm)；false 为 物理 -> 像素
        /// </summary>
        public bool IsPixelToWorld
        {
            get => _isPixelToWorld;
            set => Set(ref _isPixelToWorld, value);
        }

        /// <summary>
        /// 候选标定矩阵文件列表（全路径），供属性面板下拉选择。
        /// 由 RefreshMatrixFiles() 自动扫描标定文件存储目录生成；
        /// 浏览/手动指定的路径也会实时补入（EnsureInAvailableList）。
        /// </summary>
        public ObservableCollection<string> AvailableMatrixFiles { get; } = new ObservableCollection<string>();

        /// <summary>
        /// 重新扫描标定文件的存储目录，刷新候选列表。来源：
        ///   1) Config\Calibrations\*.json —— 标定方案配置里记录的矩阵路径（标定管理界面产物）
        ///   2) Recipes\**\Calib\*.tup —— 标定矩阵发布目录（工位级 / 配方级两个作用域；
        ///      ★2026-09-15 已废弃的"设备级"作用域 Recipes\Devices 被显式排除）
        ///   3) 当前已选路径（兜底，保证下拉框不丢当前值）
        /// 扫描结果按文件名排序，重复项按全路径去重（Windows 路径大小写不敏感）。
        /// ⚠ 防坑：ComboBox 的 SelectedValue 双向绑定在候选列表"清空→重建"的瞬间会把
        /// 当前选中值写回 null，导致 HomMatFilePath 被清空——因此重建后必须恢复原值；
        /// 若原值对应文件已被删除，则保留一条占位项（路径仍显示，运行时由 Executor 报错提示）。
        /// </summary>
        public void RefreshMatrixFiles()
        {
            string keep = _homMatFilePath;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1. 标定方案配置（CalibrationService 的注册表）里记录的矩阵路径
            try
            {
                string configDir = Path.Combine(baseDir, "Config", "Calibrations");
                if (Directory.Exists(configDir))
                {
                    foreach (var jsonFile in Directory.GetFiles(configDir, "*.json"))
                    {
                        try
                        {
                            var profile = JsonConvert.DeserializeObject<CalibrationProfile>(File.ReadAllText(jsonFile));
                            AddIfExists(files, profile?.HomMatFilePath);
                        }
                        catch
                        {
                            // 单个配置损坏不影响其余方案
                        }
                    }
                }
            }
            catch { }

            // 2. Recipes 发布目录下所有 .tup（工位级 / 配方级两个作用域）
            //    ★2026-09-15 单轨存储：显式排除已废弃的"设备级"作用域（Recipes\Devices）——
            //      历史残留文件不应再出现在候选里（否则有人会手选到即将被清理的路径）。
            try
            {
                string recipesDir = Path.Combine(baseDir, "Recipes");
                if (Directory.Exists(recipesDir))
                {
                    foreach (var tup in Directory.GetFiles(recipesDir, "*.tup", SearchOption.AllDirectories))
                    {
                        if (CalibrationMatrixStore.IsLegacyDeviceScopePath(tup))
                        {
                            continue;
                        }
                        files.Add(tup);
                    }
                }
            }
            catch { }

            // 3. 当前已选路径兜底（文件仍存在时）
            AddIfExists(files, keep);

            AvailableMatrixFiles.Clear();
            foreach (var f in files
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                AvailableMatrixFiles.Add(f);
            }

            // 4. 恢复当前值（见方法注释的防坑说明）
            if (!string.IsNullOrEmpty(keep))
            {
                bool inList = AvailableMatrixFiles.Any(f => string.Equals(f, keep, StringComparison.OrdinalIgnoreCase));
                if (!inList)
                {
                    AvailableMatrixFiles.Add(keep); // 占位项：文件已删除时仍显示原路径
                }
                if (string.IsNullOrEmpty(_homMatFilePath))
                {
                    HomMatFilePath = keep;
                }
            }

            // 5. ★按相机槽自动选中（2026-09-12）：槽键非空且当前未选中时，
            //    匹配文件名里带该槽标识（Cam_A/Cam_C…）的九点矩阵 H（排除旋转/偏心产物），
            //    让"一个相机一个方案"在选文件时落到该槽的 H，不必手翻。
            TryAutoSelectBySlot();
        }

        /// <summary>按相机槽键自动选中已发布的 H 矩阵（找不到则保持现状，不报错）。</summary>
        private void TryAutoSelectBySlot()
        {
            if (string.IsNullOrWhiteSpace(_cameraSlotKey)) return;
            if (!string.IsNullOrEmpty(_homMatFilePath)) return; // 已有选中值，尊重用户手选

            string slot = _cameraSlotKey.Trim();
            string matched = AvailableMatrixFiles.FirstOrDefault(f =>
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.IndexOf(slot, StringComparison.OrdinalIgnoreCase) < 0) return false;
                // 排除旋转/偏心/像素当量产物，只认九点矩阵 H
                return name.IndexOf("九点", StringComparison.Ordinal) >= 0
                       || name.IndexOf("HandEye", StringComparison.OrdinalIgnoreCase) >= 0;
            });
            if (matched != null)
            {
                HomMatFilePath = matched;
            }
        }

        /// <summary>把存在的文件路径加入候选集合（忽略 null/空白/不存在）</summary>
        private static void AddIfExists(HashSet<string> set, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    set.Add(path);
                }
            }
            catch { }
        }

        /// <summary>把当前选中的路径补进候选列表（存在且未在列表时）</summary>
        private void EnsureInAvailableList(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (AvailableMatrixFiles.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;
            try
            {
                if (File.Exists(path))
                {
                    AvailableMatrixFiles.Add(path);
                }
            }
            catch { }
        }
    }
}
