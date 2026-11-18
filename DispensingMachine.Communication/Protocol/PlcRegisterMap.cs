namespace DispensingMachine.Communication.Protocol;

/// <summary>
/// 寄存器地址表（上位机与 PLC 的"合同"）。
///
/// 工业现场的惯例是：这份表由电气工程师出，双方签字确认，任何一方都不能私自改。
/// 把它集中在一个类里，就是为了让"合同"只有一份，改的时候不会漏。
/// </summary>
public static class PlcRegisterMap
{
    // ============ 输入寄存器区（PLC → 上位机，只读）============
    public const ushort StatusStart = 100;
    public const ushort StatusCount = 14;          // 批量读，一次读 14 个

    public const ushort MachineState = 100;        // 0=待机 1=初始化 2=回零 3=就绪 4=运行 5=暂停 6=报警
    public const ushort XPosition = 101;           // 0.01mm 为单位的有符号整数
    public const ushort YPosition = 102;
    public const ushort DioBits = 103;             // 位域，见 PlcDioBits
    public const ushort AlarmCode = 104;
    public const ushort Heartbeat = 105;           // PLC 每 500ms 自增，用来判断通讯是否还活着
    public const ushort BarcodeResult = 106;       // 0=无 1=读到 2=读码失败

    // ============ 保持寄存器区（上位机 → PLC，可写）============
    public const ushort ParameterStart = 100;
    public const ushort ParameterCount = 3;

    public const ushort TargetX = 100;
    public const ushort TargetY = 101;
    public const ushort Speed = 102;

    // ============ 握手区 ============
    public const ushort HandshakeCmd = 200;        // 上位机写
    public const ushort HandshakeAck = 201;        // PLC 写
}

/// <summary>DIO 位域定义（第 103 号寄存器的每一位代表一个开关量）。</summary>
public static class PlcDioBits
{
    public const int ServoReady = 0;
    public const int CylinderExtended = 1;
    public const int CylinderRetracted = 2;
    public const int ProductDetected = 3;
    public const int InPosition = 4;
    public const int EmergencyStop = 5;

    public static bool Get(ushort word, int bit) => (word & (1 << bit)) != 0;
    public static ushort Set(ushort word, int bit, bool value)
        => value ? (ushort)(word | (1 << bit)) : (ushort)(word & ~(1 << bit));
}
