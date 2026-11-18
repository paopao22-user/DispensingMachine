using System.Collections.ObjectModel;
using DispensingMachine.Core.Alarms;
using DispensingMachine.Core.Configuration;
using DispensingMachine.Core.Recipes;
using DispensingMachine.Devices.Motion;
using DispensingMachine.Devices.Peripherals;
using DispensingMachine.Devices.Plc;
using DispensingMachine.Devices.Pneumatic;

namespace DispensingMachine.Core.Scheduling;

/// <summary>
/// 工位控制器 —— 整个项目的"大脑"。
/// 只做"哪一步该谁动"的时序决策，不碰电机脉冲、寄存器地址、扫码枪串口这些设备细节。
///
/// 依赖方向单向：上游是 Devices 层的四个设备模块与 PLC 命令通道，
/// 下游只有 App 的 MainWindowViewModel；控制器不反向引用界面，整套流程因此能在无 UI 的情况下跑完。
///
/// 设计选择：顺序式工步机（while + switch(Step) + await），而不是扫描式状态机。
/// 理由：
///   1. 点胶机是线性工艺，一步做完才做下一步，顺序式天然贴合
///   2. 卡在哪一步，看一眼 Step 就知道，现场排查极快
///   3. await 天然实现"等动作真的做完再往下"，不需要额外的到位轮询
///
/// 八道工序：
///   Step  0  等待工件到位
///   Step 10  气缸夹紧
///   Step 20  X 轴到扫码位
///   Step 30  扫码
///   Step 40  X 轴到点胶位
///   Step 50  点胶
///   Step 60  X 轴回等待位
///   Step 70  气缸松开
///   Step 80  计数并复位
/// </summary>
public sealed class WorkstationController : ObservableObject
{
    private readonly SensorModule _productSensor;
    private readonly CylinderModule _clampCylinder;
    private readonly AxisModule _xAxis;
    private readonly ScannerModule _scanner;
    private readonly PlcCommandChannel _plcCommand;
    private readonly RecipeManager _recipeManager;
    private readonly MachineConfig _config;
    private readonly DiagnosticService _diagnostics = new();

    private CancellationTokenSource? _cycleCts;
    private TaskCompletionSource<bool>? _resumeGate;
    private TaskCompletionSource<bool>? _nextStepGate;
    // volatile：Pause/Stop 可能来自非 UI 线程（外部按钮盒、自动流程），循环那头读到的值必须立刻是新的
    private volatile bool _pauseRequested;      // 暂停请求

    public WorkstationController(
        SensorModule productSensor,
        CylinderModule clampCylinder,
        AxisModule xAxis,
        ScannerModule scanner,
        PlcCommandChannel plcCommand,
        RecipeManager recipeManager,
        MachineConfig config)
    {
        _productSensor = productSensor;
        _clampCylinder = clampCylinder;
        _xAxis = xAxis;
        _scanner = scanner;
        _plcCommand = plcCommand;
        _recipeManager = recipeManager;
        _config = config;
    }

    // ==================== 对外只读状态 ====================

    private MachineState _state = MachineState.Ready;
    public MachineState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }

    public string StateText => State switch
    {
        MachineState.Idle => "待机",
        MachineState.Init => "初始化",
        MachineState.Homing => "回零中",
        MachineState.Ready => "就绪",
        MachineState.Running => "运行中",
        MachineState.Completed => "完成",
        MachineState.Paused => "暂停",
        MachineState.Stopping => "停止中",
        MachineState.Alarm => "报警",
        MachineState.EmergencyStop => "急停",
        _ => "未知"
    };

    public bool IsRunning => State == MachineState.Running;

    private MachineMode _mode = MachineMode.Auto;
    public MachineMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
                OnPropertyChanged(nameof(ModeText));
        }
    }

    public string ModeText => Mode switch
    {
        MachineMode.Manual => "手动",
        MachineMode.Auto => "自动",
        MachineMode.Debug => "调试",
        _ => "未知"
    };

    private int _step;
    public int Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
                OnPropertyChanged(nameof(StepText));
        }
    }

    public string StepText => Step switch
    {
        0 => "等待工件",
        10 => "气缸夹紧",
        20 => "移动到扫码位",
        30 => "扫码",
        40 => "移动到点胶位",
        50 => "点胶作业",
        60 => "返回等待位",
        70 => "气缸松开",
        80 => "计数复位",
        _ => $"未知工步 {Step}"
    };

    private AlarmInfo? _currentAlarm;
    public AlarmInfo? CurrentAlarm
    {
        get => _currentAlarm;
        private set
        {
            if (SetProperty(ref _currentAlarm, value))
                OnPropertyChanged(nameof(HasAlarm));
        }
    }

    public bool HasAlarm => CurrentAlarm is not null;

    public ObservableCollection<AlarmInfo> AlarmHistory { get; } = new();

    private string _currentBarcode = string.Empty;
    public string CurrentBarcode
    {
        get => _currentBarcode;
        private set => SetProperty(ref _currentBarcode, value);
    }

    private int _producedCount;
    public int ProducedCount
    {
        get => _producedCount;
        private set => SetProperty(ref _producedCount, value);
    }

    /// <summary>界面靠它决定"下一步"按钮何时可点：只有调试模式下真的挂住了才为 true。</summary>
    private bool _isWaitingForNextStep;
    public bool IsWaitingForNextStep
    {
        get => _isWaitingForNextStep;
        private set => SetProperty(ref _isWaitingForNextStep, value);
    }

    /// <summary>最近一次诊断结论，界面直接展示给操作员。</summary>
    private DiagnosisResult? _lastDiagnosis;
    public DiagnosisResult? LastDiagnosis
    {
        get => _lastDiagnosis;
        private set => SetProperty(ref _lastDiagnosis, value);
    }

    /// <summary>最近一次 PLC 握手的耗时说明，ViewModel 订阅后写入运行日志。</summary>
    private string _lastHandshakeText = "尚未握手";
    public string LastHandshakeText
    {
        get => _lastHandshakeText;
        private set => SetProperty(ref _lastHandshakeText, value);
    }

    // ==================== 启动前的八重起跑线 ====================

    /// <summary>
    /// 自动启动前的安全自检。八条全部通过才允许启动。
    ///
    /// 为什么要这么多条？因为每一条都对应过现场真实事故：
    ///   没检查模式   → 手动模式下误启动，机构撞人
    ///   没检查报警   → 带故障启动，把小问题放大成大事故
    ///   没检查气缸   → 气缸没退回去就动轴，撞机械
    ///   没检查配方   → 用上一个产品的参数跑新工件，全部报废
    /// </summary>
    /// <returns>返回 false 时 unmetConditions 里是逐条可直接念给操作员听的原因，界面别只回一句"启动失败"。</returns>
    public bool CanStartAuto(out List<string> unmetConditions)
    {
        unmetConditions = new List<string>();       // ← 一开始就 new，保证 out 参数永不为 null.unmetConditions 里是逐条可直接念给操作员听的原因

        if (Mode != MachineMode.Auto)       // 当前是手动/调试模式，只有自动模式才能启动流水线
            unmetConditions.Add($"当前是{ModeText}模式，只有自动模式才能启动流水线");  // 加入到unmetConditions里面，返回失败原因

        if (State is not (MachineState.Ready or MachineState.Completed))    // 当前状态是 xxx，必须处于就绪态
            unmetConditions.Add($"当前状态是{StateText}，必须处于就绪态");

        if (CurrentAlarm is not null)               // 存在未清除的报警：ERR-xxxx
            unmetConditions.Add($"存在未清除的报警：ERR-{CurrentAlarm.Code} {CurrentAlarm.Message}");

        if (!_xAxis.Model.ServoReady)               // X 轴伺服未使能
            unmetConditions.Add($"{_xAxis.Name} 伺服未使能");

        if (!_xAxis.Model.HomeDone)                 // X 轴未回零
            unmetConditions.Add($"{_xAxis.Name} 未回零");

        if (_xAxis.Model.Alarm)                     // 轴存在报警
            unmetConditions.Add($"{_xAxis.Name} 存在伺服报警");

        if (!_clampCylinder.Model.RetractedSensor || _clampCylinder.Model.ExtendedSensor)   // 夹紧气缸不在缩回原位
            unmetConditions.Add($"{_clampCylinder.Name} 不在缩回原位");

        if (_recipeManager.CurrentRecipe is null)       // 未加载任何配方
            unmetConditions.Add("未加载任何配方");

        if (_scanner.HasAlarm)                          // 扫码枪存在报警
            unmetConditions.Add($"{_scanner.Name} 存在报警");

        return unmetConditions.Count == 0;          // 如果条件都满足，返回true,否则返回false
    }

    // ==================== 命令接口 ====================

    /// <summary>
    /// 把界面上的一次"启动"交给后台流程，并在这里统一裁定允许还是拒绝。
    /// 安全检查必须全部堆在入口：等机构动起来再逐工步查，报警就只能事后补救了。
    /// </summary>
    /// <param name="message">成功时是提示文案；失败时是多行逐条原因，界面上要保留换行原样显示。</param>
    /// <returns>返回 false 时不要自动重试、也不要改状态，等操作员修完条件再点一次。</returns>
    public bool StartAuto(out string message)
    {
        if (!CanStartAuto(out List<string> unmet))  // 判断能否自动运行
        {
            message = "启动被拒绝：\n  · " + string.Join("\n  · ", unmet);    // 返回错误信息
            RaiseAlarm(AlarmCodes.AutoStartConditionUnmet, message, "安全自检", AlarmLevel.Warning);    // 报警
            return false;
        }

        _pauseRequested = false;                // 暂停请求为false,未暂停
        _cycleCts = new CancellationTokenSource();  // 造出一个"印章"，同时产出一张"传票"（Token），CTS是一次性的,一旦取消过就不能重置了，每次都要重新new一个

        // 这里不 await：让循环自己跑，调用方（UI 线程）立刻返回。
        // 因为调用发生在 UI 线程，await 的续体会回到 UI 线程，所以全程无跨线程问题。
        _ = RunCycleAsync(_cycleCts.Token);

        message = "自动流程已启动";
        return true;
    }

    /// <summary>
    /// 请求暂停。这里只置标志，真正的停顿发生在下一个工步边界。
    /// 为什么不立刻停：动作已经发给电机和气缸，半路撒手会让机构停在说不清的位置，复位都无从下手。
    /// </summary>
    public void Pause()
    {
        if (State != MachineState.Running) return;
        _pauseRequested = true;         // 暂停请求标志为true
    }

    /// <summary>
    /// 放行暂停中的流程。连点多次没有副作用 —— 闸门已经打开，再设一次结果不会让流程多跑一轮。
    /// </summary>
    public void Resume()
    {
        _pauseRequested = false;
        _resumeGate?.TrySetResult(true);
    }

    /// <summary>
    /// 停止：三处出口一次性全部放行 —— 暂停闸门、单步闸门、取消令牌。
    /// 漏掉任何一个，流程就会在那儿继续挂着，界面永远停在"停止中"。
    /// </summary>
    public void Stop()
    {
        _pauseRequested = false;
        _resumeGate?.TrySetResult(false);      // 出口 1：暂停闸门
        _nextStepGate?.TrySetResult(false);    // 出口 2：调试单步闸门
        _cycleCts?.Cancel();                   // 出口 3：取消令牌
    }

    /// <summary>调试模式下放行下一步。</summary>
    public void NextStep() => _nextStepGate?.TrySetResult(true);

    /// <summary>
    /// 急停：断轴动力 → 停流程 → 报致命报警，这个顺序不能调换。
    /// 报警内部会取消令牌并切状态；若它先跑，轴还会在断电指令送达之前多走一段。
    /// </summary>
    public void TriggerEmergencyStop()
    {
        _xAxis.Stop();                  // ① 先断轴动力
        Stop();                         // ② 再停流程
        RaiseAlarm(AlarmCodes.EmergencyStopPressed, "急停按钮被按下，全部动力切断", "急停回路", AlarmLevel.Fatal);// ③ 最后报警
    }

    /// <summary>复位：清报警，回到就绪态。</summary>
    public bool Reset(out string message)
    {
        // 先判急停：能被软件清掉的急停等于没有急停，必须先确认按钮已物理复位，软件才肯往下走
        if (State == MachineState.EmergencyStop)        // 拒绝 ①：急停不能软件清
        {
            message = "急停状态下需要先物理释放急停按钮";
            return false;
        }

        if (State == MachineState.Running)              // 拒绝 ②：运行中不能复位
        {
            message = "运行中不能复位，请先停止";
            return false;
        }

        _xAxis.ResetAlarm();                            // ① 清设备报警
        _clampCylinder.ResetAlarm();
        _scanner.ResetAlarm();

        if (CurrentAlarm is not null)                   // ② 清当前报警对象
        {
            CurrentAlarm.IsActive = false;              //    标记失效
            CurrentAlarm.ClearTime = DateTime.Now;      //    记下清除时刻
            CurrentAlarm = null;                        //    从"当前"摘掉
        }

        LastDiagnosis = null;           // ③ 清诊断结论
        Step = 0;                       // ④ 工步归零
        State = MachineState.Ready;     // ⑤ 最后才改整机状态
        message = "报警已清除，设备回到就绪状态";
        return true;
    }

    // ==================== 主循环 ====================

    /// <summary>
    /// 自动流程的唯一驱动循环：暂停检查 → 执行一个工步 → 调试模式单步等待，如此往复。
    /// 单独抽成一个方法，StartAuto 才能把它 fire-and-forget 掉，而取消和异常都收在这一个地方。
    /// </summary>
    private async Task RunCycleAsync(CancellationToken token)
    {
        State = MachineState.Running;       // 进门先宣布"我在跑"（界面按钮变灰靠它）   PropertyChanged → 界面顶栏变绿

        try
        {
            while (!token.IsCancellationRequested)      // ← 每圈开头查一次令牌: while 条件：token 没取消 → 进
            {
                // ① 暂停闸门：在工步边界生效，不打断已发出的动作
                if (_pauseRequested)                    // ① 暂停闸门: 放前面是因为优先级别高，可以先暂停
                {
                    State = MachineState.Paused;        // 界面换成"暂停中"
                    bool? resumed = await WaitResumeAsync(token);   // ★ 挂在这里，不占 CPU
                    // resumed 为 null 是令牌被取消、为 false 是操作员按了停止；两种出口一样，都不再往下走
                    if (resumed != true || token.IsCancellationRequested) break;
                    State = MachineState.Running;
                    continue;
                }

                // ② 执行当前工步
                bool keepGoing = await ExecuteStepAsync(token);
                if (!keepGoing) return;

                // ③ 调试模式：每步做完停一下，等操作员点"下一步"
                if (Mode == MachineMode.Debug && await WaitNextStepAsync(token) == false)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // 停止/急停：正常退出路径
        }
        finally
        {
            // 只有还在 Running 才回落就绪；报警和急停状态不能被这里顺手抹掉，否则界面会显示设备"正常"
            if (State == MachineState.Running)
                State = MachineState.Ready;
        }
    }

    /// <summary>
    /// 按当前 Step 执行一小段动作，返回 false 表示本轮到此为止（报警或单件完成）。
    /// 每个 case 只做自己那一步，推进靠外层 while 重新进 switch；
    /// 不让 case 直接调下一个 case，否则一条调用链会把八道工序串死，出事时看不出停在第几步。
    /// </summary>
    private async Task<bool> ExecuteStepAsync(CancellationToken token)
    {
        RecipeModel? recipe = _recipeManager.CurrentRecipe;     // 获取当前配方
        if (recipe is null)
        {
            RaiseAlarm(AlarmCodes.RecipeNotLoaded, "当前没有可用配方，无法执行工步", "配方管理");
            return false;
        }

        switch (Step)
        {
            // ---------------- Step 0：等工件 ----------------
            case 0:
            {
                // 5 秒是上料节拍的硬约定：超时还没工件，说明上游机构本身出了问题，继续等只会把故障拖成停机    // ① 等传感器信号变 true，最多等 5 秒
                bool detected = await _productSensor.WaitSignalAsync(true, TimeSpan.FromSeconds(5), token);

                if (!detected)          // ② 没等到 → 报缺料，结束本轮
                {
                    RaiseAlarm(AlarmCodes.ProductMissing, "5 秒内未检测到工件流入", _productSensor.Name);
                    return false;
                }

                Step = 10;          // ③ 等到了 → 推进到下一步
                return true;
            }

            // ---------------- Step 10：气缸夹紧 ----------------
            case 10:
            {
                bool ok = await _clampCylinder.ExtendAsync(
                    TimeSpan.FromMilliseconds(recipe.CylinderTimeoutMs), token);

                if (!ok)
                {
                    RaiseAlarm(AlarmCodes.CylinderExtendTimeout,
                        $"气缸伸出超时（{recipe.CylinderTimeoutMs}ms 内磁簧开关未闭合）", _clampCylinder.Name);
                    return false;
                }

                // 夹紧后保压一小段，等气压稳定再动轴，否则工件可能被带偏
                if (recipe.ClampDelayMs > 0)
                    await Task.Delay(recipe.ClampDelayMs, token);

                Step = 20;
                return true;
            }

            // ---------------- Step 20 / 40 / 60：X 轴定位 ----------------
            case 20:
                return await MoveXAsync(recipe.ScanPosition, "扫码位", 30, recipe, token);

            case 40:
                return await MoveXAsync(recipe.WorkPosition, "点胶位", 50, recipe, token);

            case 60:
                return await MoveXAsync(recipe.WaitPosition, "等待位", 70, recipe, token);

            // ---------------- Step 30：扫码 ----------------
            case 30:
            {
                string? barcode = await _scanner.ScanAsync(
                    TimeSpan.FromMilliseconds(recipe.ScanTimeoutMs), token);

                if (string.IsNullOrWhiteSpace(barcode))         // 扫码枪未返回有效条码
                {
                    RaiseAlarm(AlarmCodes.ScannerTimeout,
                        $"扫码枪在 {recipe.ScanTimeoutMs}ms 内未返回有效条码", _scanner.Name);
                    return false;
                }

                CurrentBarcode = barcode!;          // 当前的条码
                Step = 40;
                return true;
            }

            // ---------------- Step 50：点胶 ----------------
            case 50:
            {
                // 点胶不能由上位机自己"假装延时"完成，必须把命令真正下发给 PLC：
                //   写 CMD=1 → 等 PLC 回 ACK=1 → 撤 CMD=0 → 等 ACK=0
                // 任何一步超时都会抛异常，这里收敛成 ERR-4001，流程立刻停下。
                bool acked = await _plcCommand.RequestDispenseAsync(token);

                LastHandshakeText = _plcCommand.LastMessage;        // ← 这一行喂给日志

                if (!acked)
                {
                    RaiseAlarm(AlarmCodes.PlcCommTimeout,
                        $"点胶命令握手失败：{_plcCommand.LastMessage}", "PLC 命令通道");
                    return false;
                }

                // 工艺时间：真机上由 PLC 的定时器控制出胶量，上位机只负责等。
                // 仿真里用配方时间模拟这段物理过程。
                await Task.Delay(recipe.DispenseTimeMs, token);

                Step = 60;
                return true;
            }

            // ---------------- Step 70：气缸松开 ----------------
            case 70:
            {
                bool ok = await _clampCylinder.RetractAsync(
                    TimeSpan.FromMilliseconds(recipe.CylinderTimeoutMs), token);

                if (!ok)
                {
                    RaiseAlarm(AlarmCodes.CylinderRetractTimeout,
                        $"气缸缩回超时（{recipe.CylinderTimeoutMs}ms 内磁簧开关未闭合）", _clampCylinder.Name);
                    return false;
                }

                Step = 80;
                return true;
            }

            // ---------------- Step 80：计数复位 ----------------
            case 80:
            {
                ProducedCount++;              // ⑤ 产能 +1
                CurrentBarcode = string.Empty; // ⑤ 清空身份证，为下一件让路
                Step = 0;                      // ② 工步归零
                State = MachineState.Ready;    // ⑤ 状态回就绪
                return false;                  // ← 让 while 也退出
            }

            default:
                // 9999 是"不属于任何已知报警码"的兜底码，现场看到它基本等于工步变量被外部改坏了
                RaiseAlarm(9999, $"未知工步 Step = {Step}", "工步调度");
                return false;
        }
    }

    /// <summary>三个移动工步的共同逻辑：先查就绪，再移动，最后校验到位。</summary>
    /// <param name="target">轴绝对坐标，单位 mm；必须取自配方而不是界面输入，否则换产品时会撞机构。</param>
    /// <param name="nextStep">到位后要写回的工步号；这里写错不会报错，只会让流程跳步或原地打转。</param>
    /// <param name="token">取消时异常向上抛给主循环，不要在这个方法里吞掉。</param>
    /// <returns>false 表示限位/超时报警已经发出，调用方直接结束本轮，绝不能重试。</returns>
    private async Task<bool> MoveXAsync(
        double target, string positionName, int nextStep, RecipeModel recipe, CancellationToken token)
    {
        if (!_xAxis.IsReady)
        {
            RaiseAlarm(AlarmCodes.AxisNotHomed,
                $"{_xAxis.Name} 未处于就绪状态（使能 {_xAxis.Model.ServoReady} / 回零 {_xAxis.Model.HomeDone}）",
                _xAxis.Name);
            return false;
        }

        // 每次移动前把配方速度刷进轴模型，实现"换配方即换速度"
        _xAxis.Model.Velocity = recipe.MoveVelocity;

        bool moved = await _xAxis.MoveAbsoluteAsync(target, token);

        // moved 只说明指令执行完了，不代表真到位：还要读定位完成信号，差一丝也算没到
        if (!moved || !_xAxis.Model.InPosition)
        {
            if (_xAxis.Model.PositiveLimit)
                RaiseAlarm(AlarmCodes.AxisPositiveLimit, $"{_xAxis.Name} 目标 {target}mm 超出正限位", _xAxis.Name);
            else if (_xAxis.Model.NegativeLimit)
                RaiseAlarm(AlarmCodes.AxisNegativeLimit, $"{_xAxis.Name} 目标 {target}mm 超出负限位", _xAxis.Name);
            else
                RaiseAlarm(AlarmCodes.AxisMoveTimeout,
                    $"{_xAxis.Name} 未到达{positionName}（目标 {target}mm，实际 {_xAxis.Model.CurrentPosition:F2}mm）",
                    _xAxis.Name);

            return false;
        }

        Step = nextStep;
        return true;
    }

    // ==================== 暂停 / 单步 闸门 ====================

    /// <summary>
    /// 暂停时把流程挂在这里，直到 Resume / Stop / 取消三者之一发生。
    /// 返回 bool? 而不是 bool：null 是"被取消"、false 是"被停止"，本轮都要结束，分开只为让调用点读起来不含糊。
    /// </summary>
    /// <returns>null 或 false 都必须立刻退出循环，不能当成"继续跑"。</returns>
    private async Task<bool?> WaitResumeAsync(CancellationToken token)
    {
        // 顺序不能反：先建闸门再注册取消。token 若已经取消，Register 会同步回调，
        // 那时 _resumeGate 还是 null，回调里的 TrySetCanceled 会直接空引用。
        // RunContinuationsAsynchronously 也不能省：否则 TrySetResult 会在调用方线程（UI 线程）
        // 上同步把整段续体跑完，操作员点一下"继续"可能连带跑掉后面好几个工步。
        _resumeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);    // ① 建闸门Tcs
        using CancellationTokenRegistration reg = token.Register(() => _resumeGate.TrySetCanceled()); // ② 注册取消：令牌一取消，就把闸门置为"已取消"
        // ③ 挂起等闸门（这是唯一会"睡"的地方）
        try { return await _resumeGate.Task; }                  // 结局①：拿到 bool
        catch (TaskCanceledException) { return null; }          // 结局②：作废
        finally { _resumeGate = null; }
    }

    /// <summary>
    /// 调试模式的单步闸门：每个工步收尾后停下来，等操作员点"下一步"。
    /// 挡在工步之间而不是工步中间，停住时机构总处在明确位置上，调试人员才敢伸手进去。
    /// </summary>
    /// <returns>null 或 false 表示被取消/被停止，调用方应结束循环，而不是继续下一工步。</returns>
    private async Task<bool?> WaitNextStepAsync(CancellationToken token)
    {
        IsWaitingForNextStep = true;
        _nextStepGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration reg = token.Register(() => _nextStepGate.TrySetCanceled());

        try { return await _nextStepGate.Task; }
        catch (TaskCanceledException) { return null; }
        finally
        {
            IsWaitingForNextStep = false;
            _nextStepGate = null;
        }
    }

    // ==================== 报警 ====================

    /// <summary>
    /// 全项目统一的报警入口：工步异常、手动操作、外部调试都从这里进，保证界面上永远只有一条"当前报警"。
    /// 各设备模块的报警不能自己弹窗，必须先汇到这儿，否则界面显示的报警和真正停机的原因会对不上。
    /// </summary>
    /// <param name="code">报警码，取值见 AlarmCodes；9999 只留给"未知工步"这类兜底场景。</param>
    /// <param name="level">默认 Error；只有切断动力的才配 Fatal，因为它会让整条线停下来。</param>
    public void RaiseAlarm(int code, string message, string source = "整机", AlarmLevel level = AlarmLevel.Error)
    {
        var alarm = new AlarmInfo                       // ① 造结构化对象
        {
            Code = code,                    // 1001 / 2001 / 4002...
            Message = message,              // 人话
            Source = source,                // 哪个设备/哪个模块
            Level = level,                  // Warning / Error / Fatal
            TriggerTime = DateTime.Now,     // 什么时候
            IsActive = true                 // 还算不算数
        };

        CurrentAlarm = alarm;                            // ② 设为当前报警
        // 插到 0 是为了让历史列表最新一条排在最上面，界面不用再排一次序
        AlarmHistory.Insert(0, alarm);            // ③ 记入历史（插头） 使用insert的原因是：最新在最上面，一打开就看到。而add最新在最后面

        // 报警的同时把诊断结论算出来，操作员不用再翻手册。 因为状态随时会变化，所以在报警的这一刻就开始诊断
        LastDiagnosis = _diagnostics.Diagnose(code, _xAxis, _clampCylinder);        // ④ 顺手算诊断 

        // 报警必须顺带停机：界面在报"气缸超时"的同时流程还在往下走，是现场最不能接受的假象
        _cycleCts?.Cancel();                             // ⑤ 停车 + 切状态
        State = code == AlarmCodes.EmergencyStopPressed ? MachineState.EmergencyStop : MachineState.Alarm;
    }

    // ==================== 手动操作 ====================

    /// <summary>
    /// 手动回零：先伺服使能再回零，两步都得成功才算数。
    /// 用 && 串起来而不是分两次调用，是为了任何一步失败都不会继续往下走，也不会给界面一个假成功。
    /// </summary>
    public async Task<string> ManualHomeAsync(CancellationToken token = default)
    {
        // 自动运行中不放手让人点轴：这不是权限问题，是人身安全问题
        if (Mode == MachineMode.Auto && State == MachineState.Running)
            return "自动运行中禁止手动操作";

        bool ok = await _xAxis.ServoOnAsync(token) && await _xAxis.HomeAsync(token);    // 伺服准备好了并且已经回零
        return ok ? $"{_xAxis.Name} 回零完成" : $"{_xAxis.Name} 回零失败";
    }

    /// <summary>
    /// 手动把 X 轴开到指定坐标，调机对刀时用。
    /// </summary>
    /// <param name="target">轴绝对坐标，单位 mm；这里不做软限位预判，越界交给轴自己的限位报警。</param>
    /// <returns>成没成功已经编在文案里，调用方拿到的就是可以直接显示的一句话。</returns>
    public async Task<string> ManualMoveAsync(double target, CancellationToken token = default)
    {
        if (Mode == MachineMode.Auto && State == MachineState.Running)      // ① 互锁：自动运行中不放手让人点轴
            return "自动运行中禁止手动操作";

        bool ok = await _xAxis.MoveAbsoluteAsync(target, token);            // ② 真正干活，成败由轴自己判
        return ok ? $"{_xAxis.Name} 移动到 {target:F1}mm 完成" : $"{_xAxis.Name} 移动失败";
    }

    /// <summary>
    /// 手动夹紧，放料取料时临时固定工件用。
    /// </summary>
    /// <returns>超时给的是一句提示文案而不是异常，调用方直接显示即可。</returns>
    public async Task<string> ManualClampAsync(CancellationToken token = default)
    {
        // 3000ms 是没加载配方时的兜底等待：宁可多等一秒，也不要因为一时取不到配方就直接判失败
        int timeout = _recipeManager.CurrentRecipe?.CylinderTimeoutMs ?? 3000;
        bool ok = await _clampCylinder.ExtendAsync(TimeSpan.FromMilliseconds(timeout), token);
        return ok ? "气缸已夹紧" : "气缸夹紧超时";
    }

    /// <summary>
    /// 手动松开气缸，取件或排查卡料时用。
    /// </summary>
    public async Task<string> ManualUnclampAsync(CancellationToken token = default)
    {
        // 和夹紧同一套兜底：拿不到配方就按 3000ms 等，保证手动操作永远有确定的超时
        int timeout = _recipeManager.CurrentRecipe?.CylinderTimeoutMs ?? 3000;
        bool ok = await _clampCylinder.RetractAsync(TimeSpan.FromMilliseconds(timeout), token);
        return ok ? "气缸已松开" : "气缸松开超时";
    }
}
