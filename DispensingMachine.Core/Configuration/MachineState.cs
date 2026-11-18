namespace DispensingMachine.Core.Configuration;

/// <summary>
/// 整机状态。按"正常 / 人为打断 / 故障"三组来记，比死记十个值容易得多。
/// </summary>
public enum MachineState
{
    // —— 正常生产循环 ——
    Idle,           // 待机：上电初始态
    Init,           // 初始化：伺服使能
    Homing,         // 回零
    Ready,          // 就绪：可以启动自动
    Running,        // 运行中
    Completed,      // 单件完成

    // —— 人为受控打断（现场保留，可恢复）——
    Paused,         // 暂停
    Stopping,       // 停止过渡：等机构停稳

    // —— 故障与安全防御 ——
    Alarm,          // 报警
    EmergencyStop   // 急停
}

/// <summary>
/// 运行模式。和 MachineState 是【正交】的两个维度：
/// 同一时刻既有状态也有模式，任意组合都可能出现（比如 Manual + Ready）。
/// </summary>
public enum MachineMode
{
    Manual,   // 手动：只能点动单个机构
    Auto,     // 自动：跑完整工步
    Debug     // 调试：单步执行，每步停一下
}
