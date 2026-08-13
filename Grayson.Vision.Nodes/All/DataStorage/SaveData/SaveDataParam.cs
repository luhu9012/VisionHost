using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DataStorage.SaveData
{
    public class SaveDataParam : ParamBase
    {
        private string _filePath = "D:\\VisionData\\MeasurementLog.csv";
        /// <summary>
        /// CSV/数据文件路径
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set => Set(ref _filePath, value);
        }

        private string _csvHeader = "Time,Barcode,Result,Value1,Value2";
        /// <summary>
        /// CSV 表头定义
        /// </summary>
        public string CsvHeader
        {
            get => _csvHeader;
            set => Set(ref _csvHeader, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(FilePath) && string.IsNullOrWhiteSpace(FilePath))
                    return "写盘文件路径不能为空";
                return null;
            }
        }
        #endregion
    }
}