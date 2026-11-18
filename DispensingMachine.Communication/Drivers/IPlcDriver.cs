namespace DispensingMachine.Communication.Drivers;

/// <summary>
/// PLC 驱动抽象。
/// 上层（轮询引擎、握手管理器）只依赖这个接口，不关心底层是 Modbus 还是 S7。
/// 换 PLC 品牌时，只需要新增一个实现，业务代码一行不用改。
/// </summary>
public interface IPlcDriver
{
    bool IsConnected { get; }
    string Endpoint { get; }
    byte StationId { get; }

    Task<bool> ConnectAsync(string ip, int port, byte stationId = 1, CancellationToken ct = default);
    Task DisconnectAsync();

    Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default);
    Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default);
    Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default);
    Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default);
}
