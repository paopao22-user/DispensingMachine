namespace DispensingMachine.Devices.Motion;

/// <summary>单轴状态。这是"设备级"状态，和整机状态（MachineState）是两个层级。</summary>
/// <remarks>
/// 这一层只回答"这根轴此刻处在哪个阶段"，供界面显示与工步判断共用一个口径。
/// 不负责什么：
///   不保管坐标、速度、限位、报警码 —— 那些在 AxisModel 里，这里只有一个阶段值，没有任何测量量；
///   也不替调用方下结论"能不能动"：Ready 只说明回过零，真正的放行条件由 AxisModule 现算。
///
/// 依赖方向是单向的：这个枚举不引用任何类型，是 Devices 层最底的一个符号；
/// 反过来被 AxisModel（唯一持有者）、AxisModule（唯一写入方，并负责翻成 StateText）依赖，
/// 界面只绑 StateText，不直接绑枚举。加成员前先搜这两处 —— StateText 的 switch 漏了分支不会编译报错，
/// 只会安静地显示"未知"。
///
/// 为什么不把 ServoReady / HomeDone / Busy / Alarm 四个 bool 直接交给界面去拼：
///   四个 bool 有 16 种组合，其中大半在现场根本不存在；界面和日志各拼一遍，就会拼出两份"这根轴现在叫什么"，
///   排查时两个人说的不是同一件事。收敛成一个枚举，说法只剩一份。
///
/// 为什么不沿用 MachineState 的成员名和数值：
///   层级不同 —— MachineState.Ready 是"整机可以启动自动"，AxisState.Ready 只是"这根轴回过零"；
///   靠枚举类型区分，编译器能挡住互相赋值，而一旦用 int 或 (ushort) 强转（项目里 MachineState 已经这么写进
///   寄存器了）这层保护就没了，所以 AxisState 若要映射到状态字，必须先显式赋数字，不能再靠声明顺序。
/// </remarks>
public enum AxisState
{
    // 必须留在第一个：成员不显式赋值时按声明顺序取 0，而 AxisModel 里 _state 的初值就是 default(AxisState)；
    // 把 Disabled 挪走，等于让轴在上电第一拍就以"已使能"的身份出现在界面和工步面前，驱动器却还没上电
    Disabled,   // 未使能
    Enabled,    // 已使能，未回零
    Homing,     // 回零中
    // 别把 Ready 当成"可以运动"的许可证：它只表示回过零。回零中途被取消时 AxisModule 照样把状态写回
    // Ready，而 HomeDone 仍是 false —— 放不放行一律问 AxisModule.IsReady / CheckReady，别看这个值
    Ready,      // 已回零，可运动
    Moving,     // 运动中
    // 目前没有任何一条路径写入 Jogging：AxisModule 还没实现点动，点动只能借 MoveRelativeAsync 走，过程记成
    // Moving。所以界面按点动时看到的是"运动中"，别据此判定点动没生效；真要补点动，写入点也在这里一起补
    Jogging,    // 点动
    Alarm       // 报警
}
