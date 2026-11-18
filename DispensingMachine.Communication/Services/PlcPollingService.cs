using DispensingMachine.Communication.Drivers;
using DispensingMachine.Communication.Protocol;

namespace DispensingMachine.Communication.Services;

/// <summary>
/// 常态化轮询引擎：每 100ms 批量读一次 PLC，把寄存器翻译成强类型快照。
/// 不负责写指令下发、断线重连和 PLC 协议细节，也不碰任何界面元素。
///
/// 依赖方向是单向的：
///   向下只用 IPlcDriver 抽象和 PlcRegisterMap 的地址窗口，真机 ModbusTcpDriver 与
///   仿真 SimulatedPlcDriver 都能原样替换；向上被 App 层 ViewModel 订阅
///   SnapshotUpdated / PollingError，Communication 这一层并不知道界面存在。
///
/// 为什么用 PeriodicTimer 而不是 Task.Delay：
///   Task.Delay 是"上一拍结束后再等 100ms"，单拍耗时会累积漂移；
///   PeriodicTimer 走的是绝对时间轴，单拍抖动不会累积，落后太多会自动跳过。
///
/// 为什么要把"等待节拍"单独放在第二个 try 里：
///   如果读寄存器抛异常，而等待又写在同一个 try 里，异常会直接跳到 catch，
///   等待被跳过 —— 于是循环立刻进入下一拍，又抛异常，变成死循环刷屏。
///
/// 为什么快照用"整帧替换 + volatile"而不是加读写锁：
///   轮询 10Hz、UI 读得更勤，锁争用比数据本身还贵；
///   快照不可变，换引用本身就是一次原子写，读端永远拿到完整的一帧。
/// </summary>
public sealed class PlcPollingService : IDisposable
{
    private readonly IPlcDriver _driver;
    private CancellationTokenSource? _cts;
    private Task? _pollingTask;
    private bool _disposed;

    /// <summary>
    /// 轮询周期。调小之前先算通讯预算：每拍都要走完一次完整的 Modbus 往返，
    /// 周期压到往返耗时以下，轮询会一直追着下一拍跑、CPU 全耗在等待上。
    /// 只能在 StartAsync 之前改 —— PeriodicTimer 是在循环内部创建的，运行中改不会生效。
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>当前快照。用 volatile 保证引用替换对其他线程立即可见。</summary>
    private volatile PlcSnapshot _current = PlcSnapshot.Empty;
    /// <summary>
    /// 给不走事件的取数场景用，例如发写指令前先确认一眼当前状态。
    /// 启动前和断链期间都会返回 PlcSnapshot.Empty，永远不会是 null，调用方不必判空；
    /// 但 Empty 的 Timestamp 是 DateTime.MinValue，可用它区分"从没读到过"和"读到过"。
    /// </summary>
    public PlcSnapshot Current => _current;

    /// <summary>
    /// 只回答"轮询循环还在不在"，不回答"通讯好不好"：读寄存器失败时循环并不退出，这里仍是 true。
    /// 想知道链路是否真的活着，去看 Current.Timestamp 有没有在往前走。
    /// </summary>
    public bool IsRunning => _pollingTask is { IsCompleted: false };

    /// <summary>每完成一拍就触发一次（在后台线程上触发，订阅方自行处理线程问题）。</summary>
    public event EventHandler<PlcSnapshot>? SnapshotUpdated;

    /// <summary>单拍读失败时触发。注意：偶发失败不停止轮询，让下一拍自己恢复。</summary>
    public event EventHandler<Exception>? PollingError;

    /// <summary>
    /// 只接管驱动实例，不负责建立连接：什么时候连、连不上怎么重试由上层决定，
    /// 引擎只保证"连上之后才读数据"。
    /// </summary>
    /// <param name="driver">整个生命周期共用同一个驱动实例，不要每拍 new 一个。
    /// 传 null 在构造处不会立刻报错，而是在第一拍里以 NullReferenceException 暴露，所以必须非空。</param>
    public PlcPollingService(IPlcDriver driver) => _driver = driver;

    /// <summary>
    /// 把轮询交给线程池后立刻返回，让 UI 线程不用为启动动作挂起等待。
    /// 重复调用是安全的：已有循环在跑时直接返回，否则两个循环会抢同一个驱动、把响应搅乱。
    /// </summary>
    /// <param name="externalToken">外部取消令牌（典型来源是窗口关闭时那个）；它一旦取消，
    /// 循环静默退出、IsRunning 变 false，不会把异常抛回调用方。</param>
    /// <returns>返回的 Task 只代表"启动动作已完成"，不是轮询的生命周期；
    /// 对着它 await 等不到循环结束，要真正停下来必须再调 StopAsync()。</returns>
    public Task StartAsync(CancellationToken externalToken = default)
    {
        if (IsRunning) return Task.CompletedTask;       // ① 防重复启动

        // 内部取消 + 外部取消，谁都能停
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);      // ② 建"内外双控"的取消源 
        _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);    // ③ 把循环丢进线程池
        return Task.CompletedTask;      // ④ 立刻返回（不等循环） // ← 同步返回，不是 async 方法
    }

    /// <summary>
    /// 停止必须等循环真的退出：Cancel 只是"请求"，循环可能正卡在一次 Modbus 往返里，
    /// 提前 Dispose 掉 CTS，它醒过来时就会撞上已释放的对象。
    /// 重复调用安全：没启动过或已经停过，直接返回。
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null || _pollingTask is null) return;

        try
        {
            _cts.Cancel();
            await _pollingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _pollingTask = null;
        }
    }

/// <summary>
/// PLC（可编程逻辑控制器）后台异步轮询主循环。
/// 负责按固定节拍批量读取寄存器、生成快照并安全广播，同时确保任何非取消异常都不会中断轮询。
/// </summary>
/// <param name="ct">CancellationToken（取消令牌），用于外部优雅停止轮询任务</param>
private async Task PollingLoopAsync(CancellationToken ct)
{
    // 1. 创建 PeriodicTimer（周期性定时器），默认周期 PollingInterval 为 100ms
    //    使用 using var 确保退出循环（正常取消或异常退出）时自动释放底层定时器资源
    using var timer = new PeriodicTimer(PollingInterval);       // 100 ms 一次
    // 2. 只要外部没有发出取消请求，就持续进行轮询
    while (!ct.IsCancellationRequested)
    {
        // ==================== 第一段：干活（数据采集与分发） ====================
        // 核心设计：业务/通信异常绝不能破坏外层循环，也不能跳过后面的“等节拍”步骤
        try
        {
            if (_driver.IsConnected) // 路径 A：底层驱动处于已连接状态
            {
                // 3. 异步批量读取输入寄存器（一次网络往返拉取 14 个连续寄存器，降低通信耗时）
                //    ConfigureAwait(false) 表示恢复执行时不切回原 SynchronizationContext（同步上下文），留在线程池运行
                ushort[] registers = await _driver              // // 第 1 行：让驱动去 PLC 读 14 个寄存器
                    .ReadInputRegistersAsync(PlcRegisterMap.StatusStart, PlcRegisterMap.StatusCount, ct)
                    .ConfigureAwait(false);
                // 4. 将原始 ushort[] 寄存器数组按地址映射表解析为强类型的不可变快照对象，并更新当前引用
                _current = PlcSnapshot.FromRegisters(registers);  // 第 2 行：registers 返回了一个长度为 14 的 ushort[] 数组,FromRegisters把上面这行“天书一样的数字”，翻译成人类和 UI 界面能看懂的字段
                // 5. 安全广播快照更新事件：逐个隔离订阅者异常，防止 UI 层或业务层报错打挂当前轮询线程
                RaiseSafely(SnapshotUpdated, _current);                 // 第 3 行：把新鲜出炉的 _current 广播出去
            }
            else // 路径 B：底层驱动处于未连接/断线状态
            {
                // 6. 断线时不静默吞掉，明确构造 IOException（输入输出异常）上报给上层
                //    让界面和报警系统能及时感知“通信离线”，同时继续保持循环等待重连或恢复
                RaiseSafely(PollingError, new IOException("PLC 连接已断开，轮询暂停"));
            }
        }
        // 7. 使用 Exception Filter（异常过滤器）：仅当“确实是外部 ct 请求取消”时，才认为是正常停机并退出循环
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            break; // 收到停机指令，安静退出循环
        }
        // 8. 捕获所有其他运行时异常（包括通信超时、读寄存器失败、数据解析异常等）
        //    注意：此处只上报不 break，保证下一拍继续尝试通信
        catch (Exception ex)
        {
            RaiseSafely(PollingError, ex);
        }
        
        // ==================== 第二段：等节拍（周期控制） ====================
        // 核心设计：独立 try 块包裹等待逻辑，确保无论第一段成功、断线还是抛异常，都必须等够节拍再进下一轮
        try
        {
            // 9. 异步等待下一个 100ms 节拍到达；若返回 false 表示 timer 已被释放，直接跳出循环
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))    // 等待100ms之后返回true,取反为false,回到while进行下一次循环
                break;
        }
        catch (OperationCanceledException)
        {
            // 10. 在等待节拍期间收到取消信号，同样安静退出循环
            break;
        }
    }
}

    /// <summary>
    /// 逐个调用订阅者，单个订阅者抛异常不影响其他订阅者，也不影响轮询循环。
    /// </summary>
    /// <typeparam name="T">事件参数类型（例如 PlcSnapshot 快照数据）</typeparam>
    /// <param name="handler">可空的多播事件委托实例，挂载了外部所有订阅者的回调方法</param>
    /// <param name="args">本次轮询采集并封装好的数据对象</param>
    private static void RaiseSafely<T>(EventHandler<T>? handler, T args)
    {
        // 1. 防御性判空：使用 'is null' 模式匹配检查事件是否无人订阅
        //    若没有任何订阅者，委托对象为 null，直接短路返回，避免后续空引用异常
        if (handler is null) return;
        // 2. 拆解多播委托链：
        //    - handler.GetInvocationList() 将底层单向链表结构的委托拆分为独立的 Delegate[]（委托数组）
        //    - .Cast<EventHandler<T>>() 使用 LINQ（语言集成查询）将基类 Delegate 强转为具体的泛型事件委托
        //    - foreach 逐个取出单个订阅者委托 single，准备进行独立隔离调用
        foreach (EventHandler<T> single in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            // 3. sender（事件发送者）故意传入 null：
            //    刻意隐藏 PlcPollingService 实例本身，防止订阅方通过 (PlcPollingService)sender 强转后越权调用服务方法
            //    强制订阅方只从 args 参数中消费数据，实现通信层与业务层的单向解耦
            try
            {
                // 4. 在当前轮询线程中，同步执行这一个订阅者的回调方法
                single(null, args);
            }
            catch
            {
                // 5. 异常隔离兜底：
                //    某个订阅者内部报错（如空引用、UI 跨线程异常等）在此处被直接拦截并消化
                //    绝不允许异常冒泡到外层的 foreach 循环，更不能打断外层的 PLC（可编程逻辑控制器）轮询主循环
            }
        }
    }

    /// <summary>
    /// 只发取消、不等待循环退出：Dispose 通常落在关窗或 DI 容器销毁的路径上，
    /// 在那里阻塞等一次 Modbus 往返超时，界面就卡住了。想确保真的停了，请先 await StopAsync()。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        // 这里不等 _pollingTask：循环收到取消会自己收尾，等待却会阻塞调用线程
        _cts?.Dispose();
    }
}
