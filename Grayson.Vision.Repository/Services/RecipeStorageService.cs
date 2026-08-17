using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grayson.Vision.Contracts.Recipe.Models;
using Grayson.Vision.Contracts.Recipe.Services;
using Newtonsoft.Json;

namespace Grayson.Vision.Repository.Services
{
    /// <summary>
    /// 基于本地 JSON 文件的配方存储服务实现。
    /// 保留磁盘路径兼容性（Recipes 目录），上层 ViewModel 不再直接操作文件。
    /// </summary>
    public class RecipeStorageService : IRecipeStorageService
    {
        private readonly string _recipesFolderPath;

        public RecipeStorageService(string recipesFolderPath = null)
        {
            _recipesFolderPath = recipesFolderPath
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

            if (!Directory.Exists(_recipesFolderPath))
                Directory.CreateDirectory(_recipesFolderPath);
        }

        public List<RecipeModel> GetAllRecipes()
        {
            var list = new List<RecipeModel>();
            if (!Directory.Exists(_recipesFolderPath)) return list;

            var files = Directory.GetFiles(_recipesFolderPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var recipe = JsonConvert.DeserializeObject<RecipeModel>(json);
                    if (recipe != null) list.Add(recipe);
                }
                catch { /* 兼容旧文件或损坏文件，静默跳过 */ }
            }

            return list;
        }

        public RecipeModel LoadRecipe(string recipeIdOrPath)
        {
            if (string.IsNullOrWhiteSpace(recipeIdOrPath)) return null;

            if (File.Exists(recipeIdOrPath))
            {
                try
                {
                    string json = File.ReadAllText(recipeIdOrPath);
                    return JsonConvert.DeserializeObject<RecipeModel>(json);
                }
                catch { return null; }
            }

            return GetAllRecipes().FirstOrDefault(r =>
                r.RecipeId == recipeIdOrPath || r.RecipeCode == recipeIdOrPath);
        }

        public bool SaveRecipe(RecipeModel recipe)
        {
            if (recipe == null) return false;

            recipe.LastModifiedTime = DateTime.Now;

            string fileName = $"{recipe.RecipeCode ?? recipe.RecipeId}.json";
            string filePath = Path.Combine(_recipesFolderPath, fileName);

            try
            {
                string json = JsonConvert.SerializeObject(recipe, Formatting.Indented);
                File.WriteAllText(filePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool DeleteRecipe(string recipeId)
        {
            if (string.IsNullOrWhiteSpace(recipeId)) return false;

            var recipe = GetAllRecipes().FirstOrDefault(r =>
                r.RecipeId == recipeId || r.RecipeCode == recipeId);
            if (recipe == null) return false;

            string fileName = $"{recipe.RecipeCode ?? recipe.RecipeId}.json";
            string filePath = Path.Combine(_recipesFolderPath, fileName);

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    return true;
                }
            }
            catch { }

            return false;
        }
        public List<string> CleanupGarbageRecipes()
        {
            var removedFiles = new List<string>();
            if (!Directory.Exists(_recipesFolderPath)) return removedFiles;

            var files = Directory.GetFiles(_recipesFolderPath, "*.json");

            foreach (var file in files)
            {
                bool isGarbage = false;

                try
                {
                    string json = File.ReadAllText(file);

                    // 1. 文件内容为空或全是空格
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        isGarbage = true;
                    }
                    else
                    {
                        var recipe = JsonConvert.DeserializeObject<RecipeModel>(json);

                        // 2. 核心字段缺失判定（如配方 ID、编号或名称同时为空，视为损坏脏数据）
                        if (recipe == null ||
                           (string.IsNullOrWhiteSpace(recipe.RecipeId) && string.IsNullOrWhiteSpace(recipe.RecipeCode)))
                        {
                            isGarbage = true;
                        }

                        // 3. 文件名与内容中的 RecipeCode / RecipeId 不匹配（如改名后残留的旧文件）
                        //    例如：文件名为 RCP-Old.json，但内部 RecipeCode 已被改成 RCP-New
                        else
                        {
                            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(file);
                            bool nameMatchesId = string.Equals(fileNameWithoutExt, recipe.RecipeId, StringComparison.OrdinalIgnoreCase);
                            bool nameMatchesCode = string.Equals(fileNameWithoutExt, recipe.RecipeCode, StringComparison.OrdinalIgnoreCase);

                            if (!nameMatchesId && !nameMatchesCode)
                            {
                                // 检查磁盘上是否已经存在合法的 RCP-New.json 文件，如果存在，说明当前文件是遗留的旧副本脏数据
                                string correctFilePath = Path.Combine(_recipesFolderPath, $"{recipe.RecipeCode ?? recipe.RecipeId}.json");
                                if (File.Exists(correctFilePath))
                                {
                                    isGarbage = true; // 判定为重命名后遗留的孤儿文件
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // 4. 无法正常反序列化的损坏 JSON 文件
                    isGarbage = true;
                }
                catch (Exception)
                {
                    // 其他文件读取异常，暂不破坏性删除，跳过
                    continue;
                }

                // 执行脏数据物理删除
                if (isGarbage)
                {
                    try
                    {
                        File.Delete(file);
                        removedFiles.Add(file);
                    }
                    catch
                    {
                        // 文件被其他进程占用，忽略
                    }
                }
            }

            return removedFiles;
        }
    }
}
