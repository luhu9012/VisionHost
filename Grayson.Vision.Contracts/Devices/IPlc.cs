using Grayson.Vision.Contracts.Core;

namespace Grayson.Vision.Contracts.Devices
{
    /// <summary>PLC通用点位读写接口，适配Modbus、S7等协议</summary>
    public interface IPlc : IDevice
    {
        Result ReadBit(string addr, out bool val);
        Result WriteBit(string addr, bool val);

        Result ReadInt(string addr, out int val);
        Result WriteInt(string addr, int val);

        Result ReadFloat(string addr, out float val);
        Result WriteFloat(string addr, float val);
    }
}