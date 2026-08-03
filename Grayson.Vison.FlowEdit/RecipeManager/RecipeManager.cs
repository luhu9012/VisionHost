using Grayson.Vision.Contracts.Business.Enums;
using Grayson.Vision.Contracts.Business.Models;
using Grayson.Vision.Contracts.Logging;
using Grayson.Vision.Contracts.Recipe.DTOs; // 引入 DTO 和 RecipeConverter
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Services;
using Newtonsoft.Json;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Grayson.Vison.FlowEdit.Services
{
    public class RecipeManager
    {
        private readonly string _recipesFolderPath;
        private readonly IFileDialogService _fileDialogService;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore // 忽略空值，精简输出
        };

        public RecipeManager(IFileDialogService fileDialogService = null)
        {
            _fileDialogService = fileDialogService;
            _recipesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

            if (!Directory.Exists(_recipesFolderPath))
            {
                Directory.CreateDirectory(_recipesFolderPath);
            }
        }

        public void LoadCompositeRecipeTemplates(ObservableCollection<UnitMeta> toolBox)
        {
            if (toolBox == null) return;

            try
            {
                var existingComposites = toolBox
                    .Where(x => x.Category == NodeCategory.CompositeGroup && x.NodeId.StartsWith("RECIPE_"))
                    .ToList();

                foreach (var item in existingComposites)
                {
                    toolBox.Remove(item);
                }

                var files = Directory.GetFiles(_recipesFolderPath, "*.json");
                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    toolBox.Add(new UnitMeta
                    {
                        NodeId = $"RECIPE_{fileName}",
                        DisplayName = $"🧩 {fileName}",
                        CategoryName = UnitMeta.GetEnumDescription(NodeCategory.CompositeGroup),
                        Category = NodeCategory.CompositeGroup,
                        Type = NodeType.CompositeFlow,
                        Description = file
                    });
                }

                LogBus.Debug("RecipeManager", $"已成功加载 {files.Length} 个复合流程模板到工具箱。");
            }
            catch (Exception ex)
            {
                LogBus.Error("RecipeManager", $"扫描 Recipes 模板目录失败: {ex.Message}", ex);
            }
        }

        // RecipeManager.cs

        /// <summary>
        /// 【保存子流程/复合模板】：将当前画板流程转换为 ProcessDto 写入 Templates/Recipes 目录，并刷新工具箱
        /// </summary>
        public void SavePipelineAsRecipe(FlowProcessModel currentProcess, string recipeName, ObservableCollection<UnitMeta> toolBox)
        {
            if (string.IsNullOrWhiteSpace(recipeName) || currentProcess == null) return;

            try
            {
                // 1. 组合文件路径（保存在模板目录下）
                string filePath = Path.Combine(_recipesFolderPath, $"{recipeName}.json");

                // 2. 🌟 仅将当前流程 FlowProcessModel 转为 ProcessDto（不是完整 RecipeModel）
                ProcessDto processDto = RecipeConverter.ToProcessDto(currentProcess);
                processDto.ProcessName = recipeName; // 确保模板名称一致

                // 3. 写入 JSON 文件
                WriteJsonToFile(filePath, processDto);

                LogBus.Info("RecipeManager", $"子流程已成功保存为复合模板: {filePath}");

                // 4. 重新扫描并刷新左侧工具箱
                LoadCompositeRecipeTemplates(toolBox);
            }
            catch (Exception ex)
            {
                LogBus.Error("RecipeManager", $"保存复合模板 [{recipeName}] 失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 【保存/导出配方】：存盘完整 RecipeModel (DTO 模式)
        /// </summary>
        public bool ExportRecipe(RecipeModel recipe)
        {
            if (recipe == null)
            {
                LogBus.Warn("RecipeManager", "导出失败：配方对象为空。");
                return false;
            }

            try
            {
                var filePath = _fileDialogService?.ShowSaveFileDialog(
                    "Vision Recipe File (*.json)|*.json",
                    $"{recipe.RecipeName ?? "VisionRecipe"}.json");

                if (!string.IsNullOrEmpty(filePath))
                {
                    // 🌟 转为极简完整 DTO（包含 Recipe 元数据与所有子流程）
                    var dto = RecipeConverter.ToDto(recipe);
                    WriteJsonToFile(filePath, dto);

                    LogBus.Info("RecipeManager", $"配方导出成功: {filePath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("RecipeManager", $"导出配方文件失败: {ex.Message}", ex);
            }

            return false;
        }

        /// <summary>
        /// 【打开/导入配方】：导入并还原为完整 RecipeModel
        /// </summary>
        public RecipeModel ImportRecipe()
        {
            try
            {
                var filePath = _fileDialogService?.ShowOpenFileDialog("Vision Recipe File (*.json)|*.json");
                if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath, Encoding.UTF8);
                    var dto = JsonConvert.DeserializeObject<VisionRecipeDto>(json, JsonSettings);

                    if (dto != null)
                    {
                        // 🌟 还原完整 RecipeModel VM
                        var recipe = RecipeConverter.ToModel(dto);
                        LogBus.Info("RecipeManager", $"配方 [{recipe.RecipeName}] 加载还原成功: {filePath}");
                        return recipe;
                    }

                    LogBus.Error("RecipeManager", $"导入失败：无法解析 [{filePath}] 为有效 DTO 配方模型。");
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("RecipeManager", $"导入配方文件异常: {ex.Message}", ex);
            }

            return null;
        }
        #region 私有辅助方法

        /// <summary>
        /// 抽取公共的序列化写入方法，保证 UTF-8 编码统一
        /// </summary>
        private void WriteJsonToFile(string filePath, object targetDto)
        {
            string json = JsonConvert.SerializeObject(targetDto, JsonSettings);
            File.WriteAllText(filePath, json, Encoding.UTF8);
        }

        #endregion
    }
}