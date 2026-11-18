using DispensingMachine.Devices.Abstractions;

namespace DispensingMachine.Devices.Motion;

/// <summary>
/// 伺服轴模块（门面模式）。
///
/// 对外只暴露"业务语言"：使能、回零、走到某个位置、停。
/// 内部封装了：准入检查 → 软限位检查 → 运动执行 → 到位判定 → 状态维护。
/// 上层工步代码只需要 await 一个方法，不用关心这些细节。
///
/// 不负责什么：
///   不碰 Modbus、不认寄存器地址 —— 那是 Communication 层的事；
///   不决定"什么时候该走到哪个位置" —— 那是 Core 层工步的职责；
///   不判断整机能不能启动 —— 这里只管一根轴自己的使能、回零、限位和到位。
///
/// 依赖方向是单向下行：
///   往下只依赖同项目的 AxisModel（状态载体）、ObservableObject（变更通知）、IDeviceModule（设备契约），
///   不认识 Core、App 里的任何类型。反过来被 WorkstationController（工步）、DiagnosticService（诊断）
///   和 MainWindowViewModel（经 Prism 容器注入）依赖 —— 改这里的签名要先看那三处怎么调的。
///
/// 为什么状态全放在 AxisModel、模块本身只留一个 _motionCts：
///   位置每拍都在变，把"数据 + 通知"和"准入 + 时序"揉在一起，轴一多就抽不出 IDeviceModule；
///   分开之后界面绑的是 Model，工步调的是 Module，谁都不用知道对方的细节。
///
/// 为什么"超软限位"返回 false，而"被取消"却抛异常 —— 两种失败长得像，性质完全不同：
///   返回 false 表示这条指令本来就不该发，调用方看返回值继续往下走；
///   取消表示有人叫停了机器，必须一路抛上去让工步机收尾，写成返回值极容易被某个 if 静默吞掉。
/// </summary>
public sealed class AxisModule : ObservableObject, IDeviceModule
{
    /// <summary>到位容差（mm）。真机上由编码器分辨率和机械精度决定。</summary>
    public double Tolerance { get; set; } = 0.1;

    /// <summary>仿真的插补周期，20ms 对应真实运动卡 0.5~1ms 的放大版。</summary>
    public int SimulationTickMs { get; set; } = 20;

    // 由 Stop() 取消、与调用方传入的 ct 链接：操作员按停止和上位机停机，都必须能打断同一段运动
    private CancellationTokenSource? _motionCts;

    /// <summary>
    /// 只接受一个名字，Model 在构造里自己 new，不接受外部注入。
    /// 允许从外面塞一个 Model 进来，就等于允许两根轴共享同一份位置状态，
    /// 那种"X 轴动了、Y 轴坐标跟着变"的现场问题最难查。
    /// </summary>
    public AxisModule(string name)
    {
        Model = new AxisModel(name);
        Model.PropertyChanged += (_, _) =>      // 属性改变时SetProperty，由于绑定直接修改界面值
        {
            // 模型内部任何变化都要向上冒泡，否则界面绑 IsReady 不会刷新
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(HasAlarm));
            OnPropertyChanged(nameof(CurrentPosition));
            OnPropertyChanged(nameof(StateText));
        };
    }

    /// <summary>
    /// 把整个 Model 暴露出去，而不是逐个转发 ServoReady / HomeDone / PositiveLimit。
    /// 工步和诊断要读的细分状态有十来个，一个个转发只会把门面堆成一长串样板属性；
    /// 代价是调用方能绕过门面直接改 Model.CurrentPosition，所以约定只读、只当快照用。
    /// </summary>
    public AxisModel Model { get; }

    public string Name => Model.Name;

    /// <summary>
    /// 界面绑定的实时坐标（mm），运动途中每一拍都在变。
    /// 只能当读数看：拿它和 target 比会误判 —— 轴上可能已经停在这个位置，却是被 Stop 打断停下的，
    /// 也可能是指令还在收尾（Busy 未清）。到没到，只有由运动结束那一刻算出来的 Model.InPosition 说了算。
    /// </summary>
    public double CurrentPosition => Model.CurrentPosition;

    /// <summary>
    /// 把 AxisState 翻成界面能直接绑的中文。
    /// 为什么不写成 XAML 的 DataTrigger 或 IValueConverter：七种状态散进 XAML 之后，
    /// 改一句措辞要翻好几个文件，而且日志和报警来源标注就没法复用同一份说法了 —— 只留这一个翻译点。
    /// </summary>
    public string StateText => Model.State switch
    {
        AxisState.Disabled => "未使能",
        AxisState.Enabled => "已使能",
        AxisState.Homing => "回零中",
        AxisState.Ready => "就绪",
        AxisState.Moving => "运动中",
        AxisState.Jogging => "点动",
        AxisState.Alarm => "报警",
        _ => "未知"
    };

    /// <summary>就绪 = 已使能 + 已回零 + 无报警 + 不忙。四个条件缺一不可。</summary>
    public bool IsReady => Model.ServoReady && Model.HomeDone && !Model.Alarm && !Model.Busy;

    public bool HasAlarm => Model.Alarm;

    /// <summary>
    /// 清掉报警位，让 IsReady 有机会重新为 true —— 报警不清，"就绪"永远回不来，产线只能靠重启软件恢复。
    /// </summary>
    /// <returns>
    /// 这里恒为 true：仿真没有驱动器回读，清逻辑位就等于复位成功。
    /// 真机上必须回读驱动器的报警码，确认真的复位掉才能返回 true，否则调用方会拿这个 true 当"故障已排除"继续开机。
    /// </returns>
    public bool ResetAlarm()
    {
        if (!Model.Alarm) return true;

        Model.Alarm = false;
        // 清报警不等于回过零：没回零的轴只能退回 Enabled，否则界面显示"就绪"，一下指令就撞
        Model.State = Model.HomeDone ? AxisState.Ready : AxisState.Enabled;
        return true;
    }

    /// <summary>伺服使能。</summary>
    /// <param name="ct">上位机停机时用它掐断这段等待；已经在报警时不会进入等待，传什么都直接返回。</param>
    /// <returns>
    /// 返回 false 只有一个原因：驱动器在报警。调用方先 ResetAlarm 再来，不要原地循环重试 ——
    /// 报警没清时重试多少次都是 false，只会刷满日志、把开机流程卡死在这里。
    /// </returns>
    public async Task<bool> ServoOnAsync(CancellationToken ct = default)
    {
        if (Model.Alarm)            // 如果报警直接返回
            return false;

        // 真机上是等驱动器 READY 信号；仿真里给一点延时让日志看得出时序
        await Task.Delay(150, ct);

        Model.ServoReady = true;            // 驱动器使能回读
        Model.State = AxisState.Enabled;    // 轴的阶段
        return true;
    }

    /// <summary>回零：慢速碰机械原点，然后清零。</summary>
    /// <returns>
    /// false 表示没使能或正在报警，一条指令都没下发；
    /// 中途被取消则抛 OperationCanceledException 而不是返回 false，调用方要按"回零没做完"处理，绝不能当成功继续走工步。
    /// </returns>
    public async Task<bool> HomeAsync(CancellationToken ct = default)
    {
        if (!Model.ServoReady || Model.Alarm)       // 如果伺服没准备好或者存在报警，直接返回false
            return false;

        Model.State = AxisState.Homing;
        Model.Busy = true;
        Model.InPosition = false;

        try
        {
            // 真机上是找原点开关；仿真里从当前位置匀速回 0
            // 0.3 是回零降速比（碰原点时冲击小），10 是 mm/s 下限 —— 少了这个下限，Velocity 被配方改成 0 之后这根轴就再也回不了零
            double speed = Math.Max(Model.Velocity * 0.3, 10);
            await MoveToInternalAsync(0, speed, ct);  //返回的Task，如果取消了也跟着Task返回到这里，抛出取消异常

            Model.CurrentPosition = 0;
            Model.TargetPosition = 0;
            Model.HomeDone = true;
            Model.InPosition = true;
            Model.State = AxisState.Ready;
            return true;
        }
        catch (OperationCanceledException)
        {
            // 半途中断也必须离开 Homing，但 HomeDone 仍是 false —— 状态叫 Ready 不代表能走，IsReady 会继续把这根轴挡在外面
            Model.State = AxisState.Ready;
            throw;
        }
        finally
        {
            // Busy 必须在 finally 里清：写进成功分支的话，中途一抛异常这根轴就永远显示"忙"，
            // 之后任何运动指令都会被 CheckReady 拒掉，只能重启软件
            Model.Busy = false;
        }
    }

    /// <summary>绝对定位。返回时保证要么到位、要么已经报警/取消。</summary>
    /// <param name="target">目标位置（mm），与 Model.MinPosition / MaxPosition 同一坐标系，是回零后的机械坐标而非增量。</param>
    /// <param name="ct">外部停机信号；这里会再链接一个内部 CTS，Stop() 取消的是内部那个。</param>
    /// <returns>
    /// false 有三种来源，调用方必须分开处理：没使能/没回零/正忙（一条指令都没发出）、
    /// 超软限位（已置 PositiveLimit / NegativeLimit）、走完了但落在 Tolerance 之外。
    /// 取消既不返回 false 也不返回 true，而是抛异常 —— 让"有人叫停"一路传上去，别在某一层被当成普通失败吞掉。
    /// </returns>
    public async Task<bool> MoveAbsoluteAsync(double target, CancellationToken ct = default)
    {
        if (!CheckReady()) return false;        // 检查是否准备好了
        if (!CheckSoftLimit(target)) return false;      // 检查软限位
        // 第三步：置状态
        _motionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);   // CreateLinkedTokenSource 外部、内部取消都能取消token
        // 先写目标再置忙：反过来会有一拍"状态是运动中、目标还是上一个点"，界面上的目标值会跳一下
        Model.TargetPosition = target;
        Model.Busy = true;
        Model.InPosition = false;
        Model.State = AxisState.Moving;

        try
        {
            await MoveToInternalAsync(target, Model.Velocity, _motionCts.Token);    // ← 只负责推进

            // 到位判据：光"走完了"不算，还要落在容差内
            Model.InPosition = Math.Abs(Model.CurrentPosition - target) <= Tolerance;       // ← 到位判据
            Model.State = AxisState.Ready;
            return Model.InPosition;
        }
        catch (OperationCanceledException)
        {
            // 被停止/急停打断：就地停住，绝不置到位
            Model.State = AxisState.Ready;
            throw;
        }
        finally
        {
            Model.Busy = false;             // 不忙了
            _motionCts?.Dispose();          // 释放内部资源
            _motionCts = null;
        }
    }

    /// <summary>
    /// 相对移动。工步是按"从上一个点再往前 10mm"来描述动作的；
    /// 让几十处工步各自读一次位置再相加，等于把累加基准散到各处，迟早有一处写成 TargetPosition + delta。
    /// 注意基准取的是调用这一刻的 CurrentPosition：上一条运动必须先等到它返回，否则基准落在半路，误差会一段段累积。
    /// </summary>
    /// <param name="delta">增量（mm），可为负；不要传 NaN —— 这里不做校验，NaN 会一路污染到 Model 上。</param>
    /// <returns>语义与 MoveAbsoluteAsync 完全相同，false 同样有"没就绪 / 超限位 / 不到位"三种来源。</returns>
    public async Task<bool> MoveRelativeAsync(double delta, CancellationToken ct = default)
        => await MoveAbsoluteAsync(Model.CurrentPosition + delta, ct);

    /// <summary>停止当前运动。</summary>
    /// <remarks>只发取消信号就返回，不等轴真的停稳；要确认停下、并拿到收尾后的状态，得等对应的 MoveAbsoluteAsync 返回。</remarks>
    public void Stop()
    {
        try { _motionCts?.Cancel(); }
        catch (ObjectDisposedException) { /* 已经结束的运动，忽略 */ }
    }

    /// <summary>直接设定位置，供插补器按拍推进使用。</summary>
    /// <remarks>
    /// 这条路故意绕开 CheckReady 和 CheckSoftLimit：
    /// 插补一拍就要写几十次，每次都走一遍准入检查既没意义，又会把 Busy / State 这些状态位搅乱。
    /// 代价是软限位改由调用方（插补器）自己守 —— 传进来的值必须已经裁剪到 MinPosition~MaxPosition 之内。
    /// </remarks>
    public void SetCommandPosition(double position) => Model.CurrentPosition = position;

    /// <summary>
    /// 下发运动指令前的准入检查。判断条件和 IsReady 目前一模一样，故意重复一份而不去调 IsReady：
    /// 准入将来收紧时（真机可能要加伺服 READY 位、安全门信号），不该顺带改掉界面上"就绪"的含义。
    /// </summary>
    /// <returns>false 说明这次指令一条都没发出去，调用方按"条件不满足"报报警码，不要重试。</returns>
    private bool CheckReady()
    {
        if (Model.Alarm) return false;        // ① 无报警
        if (!Model.ServoReady) return false;  // ② 伺服已使能
        if (!Model.HomeDone) return false;    // ③ 已回零
        if (Model.Busy) return false;         // ④ 不忙
        return true;
    }

    /// <summary>
    /// 下发前先卡软限位。为什么不放到运动循环里判：进了循环才发现超限，位置已经被推出去一截，还得反向退回来；
    /// 软限位是"这条指令该不该发"的问题，不是"发出去以后怎么救"的问题。
    /// </summary>
    /// <param name="target">目标位置（mm），须与 Model.MinPosition / MaxPosition 同一坐标系（回零后的机械坐标）。</param>
    /// <returns>
    /// false 时 PositiveLimit / NegativeLimit 已经替调用方置好了，直接读这两个位报报警码即可；
    /// 不要再自己比一次 target 和上下限 —— 两处判据迟早会因为只改了其中一处而对不上，限位报警的来源就说不清了。
    /// </returns>
    private bool CheckSoftLimit(double target)
    {
        if (target < Model.MinPosition || target > Model.MaxPosition)   // 检查位置是否超过软限位
        {
            Model.NegativeLimit = target < Model.MinPosition;       // 负限位 = true,报警
            Model.PositiveLimit = target > Model.MaxPosition;       // 正限位 = true,报警
            return false;
        }
        Model.NegativeLimit = false;
        Model.PositiveLimit = false;
        return true;
    }

    /// <summary>
    /// 仿真的运动核心：按固定周期把当前位置往目标推。
    /// 真机上这里是一行 SDK 调用（比如 LTDMC.dmc_pmove），仿真里要自己算。
    /// </summary>
    /// <param name="speed">mm/s，必须为正；方向由 target 与当前位置的差决定，传负数永远走不到目标。</param>
    private async Task MoveToInternalAsync(double target, double speed, CancellationToken ct)
    {
        // speed 是 mm/s、SimulationTickMs 插补周期 是 ms，除以 1000.0 才能换成"每一拍走多少 mm"；
        // 这是仿真里唯一把时间折算成距离的地方，改 SimulationTickMs 等于顺手改了加减速曲线
        double stepPerTick = speed * SimulationTickMs / 1000.0;     // 每拍步长 = 速度 × 插补周期 ÷ 1000

        while (true)
        {
            // 必须先查取消、再推进位置：反过来的话 Stop() 之后这一拍仍然会多走一步，
            // 那一步在真机上就是撞工件
            ct.ThrowIfCancellationRequested();               // ① 先查取消

            double delta = target - Model.CurrentPosition;      // ② 算还差多远
            if (Math.Abs(delta) <= stepPerTick)                 // ③ 剩得不够一步 → 直接贴到目标，退出
            {
                // 最后不足一步时直接贴到目标值：按步长硬推的话位置永远差一个尾数，
                // 一段段相对运动接下去，误差会累积到肉眼可见
                Model.CurrentPosition = target;
                return;
            }
            // ④ 走一步，睡一拍
            Model.CurrentPosition += Math.Sign(delta) * stepPerTick;    // 属性改变时触发PropertyChanged事件，属性改变时修改界面值
            await Task.Delay(SimulationTickMs, ct);     // 睡眠20ms
        }
    }
}
