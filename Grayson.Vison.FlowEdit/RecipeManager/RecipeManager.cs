using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Grayson.Vision.Contracts.Business.Models;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Grayson.Vison.FlowEdit.Services
{
    /// <summary>
    /// 负责配方序列化、文件持久化与工具箱模板扫描服务
    /// </summary>
    public class RecipeManager
    {
        private readonly string _recipesFolderPath;

        public RecipeManager()
        {
            _recipesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");
            if (!Directory.Exists(_recipesFolderPath))
            {
                Directory.CreateDirectory(_recipesFolderPath);
            }
        }

        /// <summary>
        /// 扫描 /Recipes/ 目录，将 .json 导出为复合积木注入工具箱
        /// </summary>
        public void LoadCompositeRecipeTemplates(ObservableCollection<UnitMeta> toolBox, Action<string> logAction)
        {
            try
            {
                var existingComposites = toolBox.Where(x => x.Category == NodeCategory.CompositeEx && x.NodeId.StartsWith("RECIPE_")).ToList();
                foreach (var item in existingComposites) toolBox.Remove(item);

                var files = Directory.GetFiles(_recipesFolderPath, "*.json");
                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    toolBox.Add(new UnitMeta
                    {
                        NodeId = $"RECIPE_{fileName}",
                        DisplayName = $"📦 {fileName}",
                        CategoryName = UnitMeta.GetEnumDescription(NodeCategory.CompositeEx),
                        Category = NodeCategory.CompositeEx,
                        Type = NodeType.CompositeFlow,
                        Description = file
                    });
                }

                logAction?.Invoke($"📂 已加载 {files.Length} 个复合流程模板到工具箱。");
            }
            catch (Exception ex)
            {
                logAction?.Invoke($"⚠️ 扫描 Recipes 目录失败: {ex.Message}");
            }
        }

        public void SavePipelineAsRecipe(FlowProcessModel currentProcess, string recipeName, ObservableCollection<UnitMeta> toolBox, Action<string> logAction)
        {
            if (string.IsNullOrWhiteSpace(recipeName) || currentProcess == null) return;

            string filePath = Path.Combine(_recipesFolderPath, $"{recipeName}.json");
            var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto, Formatting = Formatting.Indented };

            string json = JsonConvert.SerializeObject(currentProcess, settings);
            File.WriteAllText(filePath, json);

            logAction?.Invoke($"💾 当前流程已保存为模板: {filePath}");
            LoadCompositeRecipeTemplates(toolBox, logAction);
        }

        public void ExportRecipe(FlowProcessModel rootProcess, Action<string> logAction)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "Recipe File (*.json)|*.json", FileName = "VisionStationRecipe.json" };
            if (sfd.ShowDialog() == true)
            {
                string json = JsonConvert.SerializeObject(rootProcess, Formatting.Indented, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                File.WriteAllText(sfd.FileName, json);
                logAction?.Invoke($"📤 配方成功导出至: {sfd.FileName}");
            }
        }

        public FlowProcessModel ImportRecipe(Action<string> logAction)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Recipe File (*.json)|*.json" };
            if (ofd.ShowDialog() == true)
            {
                string json = File.ReadAllText(ofd.FileName);
                var process = JsonConvert.DeserializeObject<FlowProcessModel>(json, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                if (process != null)
                {
                    logAction?.Invoke($"📥 成功导入配方文件: {ofd.FileName}");
                    return process;
                }
            }
            return null;
        }
    }
}