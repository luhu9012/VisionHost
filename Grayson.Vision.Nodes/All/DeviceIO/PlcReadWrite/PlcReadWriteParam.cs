using Grayson.Vision.Contracts.Devices.Enums;
using Grayson.Vision.Nodes.Common;

namespace Grayson.Vision.Nodes.All.DeviceIO.PlcReadWrite
{
    public class PlcReadWriteParam : ParamBase
    {
        private string _plcAlias = "MainPLC";

        /// <summary>
        /// 关联逻辑 PLC 设备
        /// </summary>
        [LogicalDeviceBinding(deviceType: DeviceCategory.PLC, deviceName: "Main PLC", requiredSpec: "Siemens/Modbus PLC")]
        public string PlcAlias
        {
            get => _plcAlias;
            set => Set(ref _plcAlias, value);
        }

        private bool _isWriteMode = false;
        /// <summary>
        /// 操作模式：False = 读取, True = 写入
        /// </summary>
        public bool IsWriteMode
        {
            get => _isWriteMode;
            set => Set(ref _isWriteMode, value);
        }

        private string _address = "DB1.DBD0";
        /// <summary>
        /// PLC 寄存器地址（如 DB1.DBD0, M0.0, 40001 等）
        /// </summary>
        public string Address
        {
            get => _address;
            set => Set(ref _address, value);
        }

        private string _dataType = "Float";
        /// <summary>
        /// 数据类型：Bool, Int16, Int32, Float, Double, String
        /// </summary>
        public string DataType
        {
            get => _dataType;
            set => Set(ref _dataType, value);
        }

        private string _writeValue = "0.0";
        /// <summary>
        /// 写入模式下的默认设定值
        /// </summary>
        public string WriteValue
        {
            get => _writeValue;
            set => Set(ref _writeValue, value);
        }

        #region 参数校验逻辑
        public override string this[string columnName]
        {
            get
            {
                if (columnName == nameof(PlcAlias) && string.IsNullOrWhiteSpace(PlcAlias))
                    return "PLC 设备别名不能为空";
                if (columnName == nameof(Address) && string.IsNullOrWhiteSpace(Address))
                    return "PLC 寄存器地址不能为空";
                return null;
            }
        }
        #endregion
    }
}