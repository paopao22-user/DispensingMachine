namespace DispensingMachine.Communication.Protocol;

/// <summary>
/// PLC（可编程逻辑控制器）状态快照（Immutable Object，不可变对象）。
/// 每次轮询生成全新实例并原子替换引用，确保 UI（用户界面）与控制器读取时无须加锁且绝不出现“半新半旧”的数据撕裂。
/// </summary>
// 使用 C# 9+ 的 sealed record（密封不可变记录类型）与 Positional Record（位置记录语法）：
// 1. sealed 禁止继承，防止子类破坏不可变性；
// 2. record 默认生成只读 init 属性（外部创建后无法修改），并自动实现基于值的相等比较（Equals/GetHashCode）
public sealed record PlcSnapshot(
    short MachineState,       // 机台当前状态码（如 0=待机、1=运行、2=报警）
    double XPosition,         // X 轴当前物理位置（已换算为 mm）
    double YPosition,         // Y 轴当前物理位置（已换算为 mm）
    bool ServoReady,          // 伺服电机是否就绪
    bool CylinderExtended,    // 点胶气缸是否伸出到位
    bool CylinderRetracted,   // 点胶气缸是否缩回到位
    bool ProductDetected,     // 光电传感器是否检测到产品
    bool InPosition,          // 运动轴是否到达目标位置
    bool EmergencyStop,       // 急停按钮是否被拍下
    short AlarmCode,          // 当前报警代码（0 表示无报警）
    int Heartbeat,            // PLC 心跳计数（用于判断 PLC 程序是否卡死）
    DateTime Timestamp)       // 本帧快照生成时的本地时间戳
{
    /// <summary>
    /// 哨兵空对象：在系统刚启动、尚未从 PLC 读回第一帧数据时作为默认初始值，避免上层频繁判断 null。
    /// 注意：CylinderRetracted 默认设为 true，表示初始安全状态假设气缸处于缩回位。
    /// </summary>
    public static PlcSnapshot Empty { get; } = new(
        0, 0, 0, false, false, true, false, false, false, 0, 0, DateTime.MinValue);

    /// <summary>
    /// 静态工厂方法：将 PLC 批量读回的 14 个原始 ushort 寄存器数组，解析并封装成强类型快照。翻译成人类和 UI 界面能看懂的字段，即业务实体
    /// 位置寄存器的物理精度约定为 0.01mm（即 PLC 存整数 1234 代表 12.34mm）。
    /// </summary>
    public static PlcSnapshot FromRegisters(ushort[] registers)
    {
        // 1. 防御性长度校验：确保传入数组至少包含约定的状态寄存器数量（14 个），不足直接抛异常，绝不强行越界读取
        if (registers.Length < PlcRegisterMap.StatusCount)
            throw new ArgumentException($"状态寄存器至少需要 {PlcRegisterMap.StatusCount} 个", nameof(registers));

        // 2. 先提取打包了多个布尔开关量的 DIO（数字量输入输出）寄存器
        //    使用“绝对地址 - 起始地址”换算成数组下标（例如：103 - 100 = 3）
        ushort dio = registers[PlcRegisterMap.DioBits - PlcRegisterMap.StatusStart];

        // 3. 构造并返回一个全新的不可变 PlcSnapshot 实例
        return new PlcSnapshot(
            // 使用 unchecked((short)...) 将无符号 ushort 的二进制位原封不动解释为有符号 short（还原负数坐标或负状态码）
            MachineState: unchecked((short)registers[PlcRegisterMap.MachineState - PlcRegisterMap.StatusStart]),

            // 将定点数（0.01mm 单位）除以浮点数 100.0，转换为双精度物理毫米值（例如 -500 -> -5.00mm）
            XPosition: unchecked((short)registers[PlcRegisterMap.XPosition - PlcRegisterMap.StatusStart]) / 100.0,
            YPosition: unchecked((short)registers[PlcRegisterMap.YPosition - PlcRegisterMap.StatusStart]) / 100.0,

            // 位域拆位：从一个 16 位的 dio 整数中，按预设的 Bit（位索引）逐个提取出独立的 bool 状态
            ServoReady: PlcDioBits.Get(dio, PlcDioBits.ServoReady),
            CylinderExtended: PlcDioBits.Get(dio, PlcDioBits.CylinderExtended),
            CylinderRetracted: PlcDioBits.Get(dio, PlcDioBits.CylinderRetracted),
            ProductDetected: PlcDioBits.Get(dio, PlcDioBits.ProductDetected),
            InPosition: PlcDioBits.Get(dio, PlcDioBits.InPosition),     
            EmergencyStop: PlcDioBits.Get(dio, PlcDioBits.EmergencyStop),

            AlarmCode: unchecked((short)registers[PlcRegisterMap.AlarmCode - PlcRegisterMap.StatusStart]),

            // 心跳通常为单调递增计数，直接隐式从 ushort 转为 int
            Heartbeat: registers[PlcRegisterMap.Heartbeat - PlcRegisterMap.StatusStart],

            // 打上当前系统时间，用于上层判断数据新鲜度
            Timestamp: DateTime.Now);
    }
}
