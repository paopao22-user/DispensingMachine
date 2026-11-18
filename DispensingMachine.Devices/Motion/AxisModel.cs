namespace DispensingMachine.Devices.Motion;

/// <summary>
/// 单轴运行时状态（唯一真相来源）。
///
/// 这一层只保管一根轴的事实——坐标、工艺参数、各种标志位，谁问都给同一份答案。
/// 不负责什么：
///   不检查能不能动、不判断到没到、不发指令、不清报警 —— 准入与时序全在 AxisModule 里算完再写进来；
///   也不认识整机状态 MachineState：一根轴"就绪"和产线"能开"是两回事，别在这里推导。
///
/// 记住三组变量的分工：
///   CurrentPosition  —— 现在在哪（每拍都在变）
///   TargetPosition   —— 要去哪（下命令时写一次）
///   InPosition       —— 到了没（由控制器算出，UI 只读）
///
/// 依赖方向是单向的：这里只继承 ObservableObject 拿变更通知，不认识 AxisModule、不认识 PLC 协议、
/// 更不认识任何界面类型；反过来被 AxisModule（唯一写入方）、DiagnosticService（只读诊断）
/// 和界面绑定同时依赖 —— 所以改属性名之前先搜这三处，XAML 里的绑定路径写错在编译期不报错，
/// 只会安静地显示不出来。
///
/// 为什么这里全是"可变属性 + SetProperty"的纯数据，而不把 IsReady / HasAlarm 这类判断一起搬进来：
///   判断一旦住进 Model，Model 就得认识"使能、回零、报警"这套业务口径，
///   而这正是 AxisModule 门面要独占的东西；两处都能算，迟早出现两份略有差异的"就绪"定义，
///   现场表现就是界面亮着"就绪"、一按启动却不动。
///
/// 为什么每次赋值都要绕一层 SetProperty 而不是直接写字段：
///   位置每拍都在变，通知发多了界面会持续重绘；SetProperty 先比相等再通知，
///   停机静止时这些属性一个事件都不产生 —— 界面不闪、日志不刷。
/// </summary>
public sealed class AxisModel : ObservableObject
{
    private double _currentPosition;
    private double _targetPosition;
    // 100 / 500 是仿真台的空载默认值（mm/s、mm/s²），只为界面一打开就有数可显示；
    // 真机由配方 MoveVelocity 和配置覆盖，别把这组数字当工艺参数抄进 SOP。
    private double _velocity = 100;
    private double _acceleration = 500;
    // 初值必须是未使能：默认给 Ready 的话，开机第一拍界面就是绿的、工步也敢放行运动，
    // 而此刻驱动器还没上电，指令发出去不是撞限位就是直接报警
    private AxisState _state = AxisState.Disabled;
    private bool _servoReady;
    private bool _homeDone;
    private bool _busy;
    private bool _inPosition;
    private bool _alarm;
    private bool _positiveLimit;
    private bool _negativeLimit;

    /// <summary>
    /// 名字只在这里赋一次，之后只读：它是日志、报警、界面列头唯一认轴的依据，
    /// 允许运行中改名，事故回放时就再也对不上"现场那根轴"了。
    /// </summary>
    /// <param name="name">只用于显示与日志，不参与任何寻址；不能为空，也不要重名 —— 两根同名轴在同一张报警列表里无法区分。</param>
    public AxisModel(string name) => Name = name;

    /// <summary>对外标识用，不参与寻址；重名会让报警和日志指向两根不同的轴。</summary>
    public string Name { get; }

    /// <summary>实际位置（mm）。</summary>
    /// <remarks>
    /// 只有运动循环在写它，其余层一律只读：界面若反向赋个值进去，下一拍就被真值覆盖，
    /// 现场看到的是"刚输入的数字自己跳回去了"。判断到没到别拿它减 TargetPosition，看 InPosition。
    /// </remarks>
    public double CurrentPosition
    {
        get => _currentPosition;
        set => SetProperty(ref _currentPosition, value);
    }

    /// <summary>目标位置（mm）。</summary>
    /// <remarks>
    /// 它记的是"最近一次下发过的目的地"，不代表那条指令还活着：Stop() 或报警打断后它不会被清回实际位置，
    /// 所以不能用 TargetPosition != CurrentPosition 判断"还在运动"，那个问题去问 Busy。
    /// </remarks>
    public double TargetPosition
    {
        get => _targetPosition;
        set => SetProperty(ref _targetPosition, value);
    }

    /// <summary>最大速度（mm/s）。</summary>
    /// <remarks>
    /// 运动循环每拍都读它；给 0 不会报错，只会让仿真每拍推进 0mm —— 轴原地不动而 Busy 一直是 true，
    /// 上层看到的是"运动超时"，根因却在参数上，这种故障最难反查。
    /// </remarks>
    public double Velocity
    {
        get => _velocity;
        set => SetProperty(ref _velocity, value);
    }

    /// <summary>加速度（mm/s²）。</summary>
    /// <remarks>
    /// 目前只有界面在改它：仿真运动核心是按匀速逐拍推进的，不做加减速积分，所以调小看不出区别。
    /// 接真机时必须把它连同 Velocity 一起下发给驱动器，否则就是匀速起停，撞机的冲击全落在机械上。
    /// </remarks>
    public double Acceleration
    {
        get => _acceleration;
        set => SetProperty(ref _acceleration, value);
    }

    /// <summary>
    /// 这根轴自己的阶段（见 AxisState），和整机状态 MachineState 是两个层级，谁也推导不出谁。
    /// 各流程节点由 AxisModule 写入，界面拿它出文案；拿它做准入判断会漏掉报警、限位这些正交标志。
    /// </summary>
    public AxisState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    /// <summary>驱动器使能回读，是所有运动的第一个门槛；报警时它可能仍然是 true，所以不能只凭它放行指令。</summary>
    public bool ServoReady
    {
        get => _servoReady;
        set => SetProperty(ref _servoReady, value);
    }

    /// <summary>
    /// 唯一能证明"零点可信"的标志：位置正好是 0 不算回过零，中途被打断的那次回零在这里仍是 false。
    /// 重新上电或复位后必须重新回零，不要因为 CurrentPosition 显示 0 就顺手把它置 true。
    /// </summary>
    public bool HomeDone
    {
        get => _homeDone;
        set => SetProperty(ref _homeDone, value);
    }

    /// <summary>控制器正在执行运动指令。</summary>
    public bool Busy
    {
        get => _busy;
        set => SetProperty(ref _busy, value);
    }

    /// <summary>已到达目标位置（由控制器在运动结束时算出，不是自己设的）。</summary>
    public bool InPosition
    {
        get => _inPosition;
        set => SetProperty(ref _inPosition, value);
    }

    /// <summary>
    /// 报警是锁存的：置起来之后，运动结束、重新使能都不会让它自己消失，只有 ResetAlarm 能清。
    /// 界面据此亮红色，工步据此拒绝一切下发 —— 别在运动成功的分支里顺手把它置 false。
    /// </summary>
    public bool Alarm
    {
        get => _alarm;
        set => SetProperty(ref _alarm, value);
    }

    /// <summary>
    /// 软限位越界标记，不是硬件限位开关的回读：它由 AxisModule 在每次绝对定位前用 Min/MaxPosition 重算，
    /// 被拒的那条指令压根没发出去。所以它亮着说明"有人想走出行程"，而不是"已经撞上去了"。
    /// </summary>
    public bool PositiveLimit
    {
        get => _positiveLimit;
        set => SetProperty(ref _positiveLimit, value);
    }

    /// <summary>与 PositiveLimit 成对：两个都由 CheckSoftLimit 每次重算，外部手动置位没有意义，下一拍就被覆盖。</summary>
    public bool NegativeLimit
    {
        get => _negativeLimit;
        set => SetProperty(ref _negativeLimit, value);
    }

    /// <summary>
    /// 软限位下界（mm），和 CurrentPosition 同一坐标系，不是相对回零点的偏移。
    /// 0 只是仿真台的默认行程，真机由 MainWindowViewModel 从 MachineConfig 覆盖写入；
    /// 这里刻意不给变更通知 —— 行程是装配期一次写死的东西，运行中改了界面也不该悄悄变。
    /// </summary>
    public double MinPosition { get; set; } = 0;
    /// <summary>
    /// 软限位上界（mm）。上下界写反（Min &gt; Max）不会报错，而是让 CheckSoftLimit 拒绝所有目标，
    /// 现场症状是"每个点位都报超行程"，先回来对这两个数。
    /// </summary>
    public double MaxPosition { get; set; } = 500;
}
