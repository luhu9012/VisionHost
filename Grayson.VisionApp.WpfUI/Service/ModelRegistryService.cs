// Grayson.Vision.WpfUI/Service/ModelRegistryService.cs
// 模型仓库存储服务（2026-09-09）：Config\ModelRegistry\models.json 单文件清单。
// 模型资产=元数据（框架/输入规格/类别/指标/状态），文件本体在 Config\Models\{AssetCode}\ 下，
// 实际推理由 Plugins.Inference.OnnxRuntime（DlInference 节点）消费（stage9-3 端到端）。
using Grayson.Vision.Contracts.TaskLibrary.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Grayson.Vision.WpfUI.Service
{
    public class ModelRegistryService
    {
        private readonly string _baseDir;
        public string ManifestDir { get; }
        public string ManifestPath => Path.Combine(ManifestDir, "models.json");
        /// <summary>模型文件根目录（Config\Models）</summary>
        public string ModelRootDir => Path.Combine(_baseDir, "Config", "Models");

        public ModelRegistryService(string baseDir = null)
        {
            baseDir = string.IsNullOrWhiteSpace(baseDir)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDir;
            _baseDir = baseDir;
            ManifestDir = Path.Combine(baseDir, "Config", "ModelRegistry");
        }

        private List<ModelAssetInfo> ReadManifest()
        {
            Directory.CreateDirectory(ManifestDir);
            if (!File.Exists(ManifestPath)) return new List<ModelAssetInfo>();
            try
            {
                var list = JsonConvert.DeserializeObject<List<ModelAssetInfo>>(File.ReadAllText(ManifestPath));
                return list ?? new List<ModelAssetInfo>();
            }
            catch
            {
                return new List<ModelAssetInfo>();
            }
        }

        private void WriteManifest(List<ModelAssetInfo> list)
        {
            Directory.CreateDirectory(ManifestDir);
            File.WriteAllText(ManifestPath, JsonConvert.SerializeObject(list, Formatting.Indented));
        }

        public List<ModelAssetInfo> LoadAll()
        {
            return ReadManifest().OrderBy(m => m.AssetCode, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public ModelAssetInfo Load(string assetCode)
        {
            return string.IsNullOrWhiteSpace(assetCode) ? null : ReadManifest().FirstOrDefault(m => m.AssetCode == assetCode);
        }

        public bool Exists(string assetCode)
        {
            return !string.IsNullOrWhiteSpace(assetCode) && ReadManifest().Any(m => m.AssetCode == assetCode);
        }

        public bool Save(ModelAssetInfo asset)
        {
            if (asset == null || string.IsNullOrWhiteSpace(asset.AssetCode)) return false;
            var list = ReadManifest();
            list.RemoveAll(m => m.AssetCode == asset.AssetCode);
            asset.UpdatedTime = DateTime.Now;
            list.Add(asset);
            WriteManifest(list);
            return true;
        }

        public bool Delete(string assetCode)
        {
            if (string.IsNullOrWhiteSpace(assetCode)) return false;
            var list = ReadManifest();
            var removed = list.RemoveAll(m => m.AssetCode == assetCode) > 0;
            if (removed) WriteManifest(list);
            return removed;
        }

        /// <summary>按模型种类生成下一个可用代码：MDL-{DET|SEG|CLS|ANO}-###。</summary>
        public string GenerateCode(DlModelKind kind)
        {
            string prefix;
            switch (kind)
            {
                case DlModelKind.Detection: prefix = "DET"; break;
                case DlModelKind.Segmentation: prefix = "SEG"; break;
                case DlModelKind.Classification: prefix = "CLS"; break;
                default: prefix = "ANO"; break;
            }
            var all = LoadAll();
            int max = 0;
            foreach (var m in all)
            {
                if (m.AssetCode == null || !m.AssetCode.StartsWith("MDL-" + prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
                var tail = m.AssetCode.Substring(("MDL-" + prefix + "-").Length);
                if (int.TryParse(tail, out int n) && n > max) max = n;
            }
            return string.Format("MDL-{0}-{1:D3}", prefix, max + 1);
        }
    }
}
