using Grayson.Vision.Nodes.Common;
using System;
using System.IO;
using System.Windows.Input;

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
            set => Set(ref _isBatchFolder, value);
        }

        private string _folderPath = @"C:\VisionImages\Batch\";
        public string FolderPath
        {
            get => _folderPath;
            set => Set(ref _folderPath, value);
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