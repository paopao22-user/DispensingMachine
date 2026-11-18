namespace DispensingMachine.Core.Configuration;

/// <summary>
/// 设备硬件参数（"三盒模型"里的第一个盒子）。
///
/// 这一盒是设备出厂就定死的物理限制：行程多长、最快能跑多快、PLC 在哪个 IP。
/// 操作员不能改，配方也不能覆盖它 —— 否则填一个超行程的值上去就会撞机。
/// </summary>
public sealed class MachineConfig
{
    public string MachineId { get; set; } = "DISPENSING-01";        // 机器id

    // —— 机械行程（mm）——
    public double XMinPosition { get; set; } = 0;
    public double XMaxPosition { get; set; } = 500;

    // —— 硬件能力上限 ——
    public double MaxAllowableVelocity { get; set; } = 300;

    // —— 通讯参数 ——
    public string PlcIpAddress { get; set; } = "192.168.1.100";
    public int PlcPort { get; set; } = 502;
    public byte PlcStationId { get; set; } = 1;

    /// <summary>
    /// 是否使用仿真 PLC。
    /// true  = 内存仿真，脱机可跑（默认）
    /// false = 真实 Modbus TCP，连现场 PLC 或 MThings 从站
    /// 驱动可插拔的验证点：改这一个标志，上层一行代码都不用动。
    /// </summary>
    public bool UseSimulatedPlc { get; set; } = true;
}
