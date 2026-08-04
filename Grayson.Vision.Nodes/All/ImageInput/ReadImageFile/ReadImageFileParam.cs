using Grayson.Vision.Contracts.Infrastructure.Mvvm; 
using Grayson.Vision.Nodes.Common;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Newtonsoft.Json;

namespace Grayson.Vision.Nodes.All.ImageInput.ReadImageFile
{
    public class ReadImageFileParam : ParamBase
    {
        private string _filePath = @"C:\VisionImages\test.png";
        public string FilePath
        {
            get => _filePath;
            set => Set(ref _filePath, value);
        }

        private bool _isBatchFolder = false;
        public bool IsBatchFolder
        {
            get => _isBatchFolder;
            set
            {
                if (Set(ref _isBatchFolder, value))
                {
                    if (_isBatchFolder) LoadFolderFiles();
                }
            }
        }

        private string _folderPath = @"C:\VisionImages\Batch\";
        public string FolderPath
        {
            get => _folderPath;
            set => Set(ref _folderPath, value);
        }

        private bool _loopFolder = true;
        public bool LoopFolder
        {
            get => _loopFolder;
            set => Set(ref _loopFolder, value);
        }

        public ObservableCollection<string> FileItems { get; } = new ObservableCollection<string>();

        private string _selectedFilePath;
        public string SelectedFilePath
        {
            get => _selectedFilePath;
            set => Set(ref _selectedFilePath, value);
        }

        private int _currentImageIndex = 0;
        public int CurrentImageIndex
        {
            get => _currentImageIndex;
            set => Set(ref _currentImageIndex, value);
        }

        // 🌟 复用项目统一的 ICommand 接口定义
        [JsonIgnore]
        public ICommand BrowseFileCommand { get; }
        [JsonIgnore]
        public ICommand BrowseFolderCommand { get; }

        public ReadImageFileParam()
        {
            // 🌟 直接调用复用的 RelayCommand 实现
            BrowseFileCommand = new RelayCommand(ExecuteBrowseFile);
            BrowseFolderCommand = new RelayCommand(ExecuteBrowseFolder);
        }

        /// <summary>
        /// 单文件选择弹框逻辑
        /// </summary>
        private void ExecuteBrowseFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "请选择视觉检测图片",
                Filter = "图像文件 (*.png;*.jpg;*.bmp;*.tif;*.jpeg)|*.png;*.jpg;*.bmp;*.tif;*.jpeg|所有文件 (*.*)|*.*"
            };

            if (!string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(FilePath);
            }

            if (dialog.ShowDialog() == true)
            {
                FilePath = dialog.FileName;
            }
        }

        /// <summary>
        /// 批处理文件夹选择弹框逻辑
        /// </summary>
        private void ExecuteBrowseFolder()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "请选择批处理图片文件夹";

                if (!string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath))
                {
                    dialog.SelectedPath = FolderPath;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    FolderPath = dialog.SelectedPath;
                    LoadFolderFiles();
                }
            }
        }

        public void LoadFolderFiles()
        {
            FileItems.Clear();
            if (!IsBatchFolder || string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
            {
                SelectedFilePath = null;
                CurrentImageIndex = 0;
                return;
            }

            var supportedExts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff" };
            var files = Directory.GetFiles(FolderPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => supportedExts.Contains(Path.GetExtension(f).ToLower()))
                .OrderBy(f => f)
                .ToList();

            foreach (var f in files)
            {
                FileItems.Add(f);
            }

            if (FileItems.Count > 0)
            {
                if (CurrentImageIndex < 0 || CurrentImageIndex >= FileItems.Count)
                {
                    CurrentImageIndex = 0;
                }
                SelectedFilePath = FileItems[CurrentImageIndex];
            }
            else
            {
                SelectedFilePath = null;
                CurrentImageIndex = 0;
            }
        }

        public override string this[string columnName]
        {
            get
            {
                if (!IsBatchFolder && columnName == nameof(FilePath) && string.IsNullOrWhiteSpace(FilePath))
                    return "文件路径不能为空";
                if (IsBatchFolder && columnName == nameof(FolderPath) && string.IsNullOrWhiteSpace(FolderPath))
                    return "文件夹路径不能为空";
                return null;
            }
        }
    }
}