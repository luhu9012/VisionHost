using Grayson.Vision.Contracts.Calibration.Models;
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
        ///   2) Recipes\**\Calib\*.tup —— 标定矩阵发布目录（设备级/工位级/配方级三个作用域）
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

            // 2. Recipes 发布目录下所有 .tup（设备级 / 工位级 / 配方级作用域）
            try
            {
                string recipesDir = Path.Combine(baseDir, "Recipes");
                if (Directory.Exists(recipesDir))
                {
                    foreach (var tup in Directory.GetFiles(recipesDir, "*.tup", SearchOption.AllDirectories))
                    {
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
