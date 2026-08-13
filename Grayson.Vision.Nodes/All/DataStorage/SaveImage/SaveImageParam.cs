using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DataStorage.SaveImage
{
    public class SaveImageParam : ParamBase
    {
        private string _basePath = "D:\\VisionImages";
        /// <summary>
        /// 存盘根目录
        /// </summary>
        public string BasePath
        {
            get => _basePath;
            set => Set(ref _basePath, value);
        }

        private string _imageFormat = "jpg";
        /// <summary>
        /// 图像格式: jpg / png / bmp
        /// </summary>
        public string ImageFormat
        {
            get => _imageFormat;
            set => Set(ref _imageFormat, value);
        }

        private bool _saveOkImage = true;
        /// <summary>
        /// 是否保存 OK 图片
        /// </summary>
        public bool SaveOkImage
        {
            get => _saveOkImage;
            set => Set(ref _saveOkImage, value);
        }

        private bool _saveNgImage = true;
        /// <summary>
        /// 是否保存 NG 图片
        /// </summary>
        public bool SaveNgImage
        {
            get => _saveNgImage;
            set => Set(ref _saveNgImage, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(BasePath) && string.IsNullOrWhiteSpace(BasePath))
                    return "图像保存根目录不能为空";
                return null;
            }
        }
        #endregion
    }
}