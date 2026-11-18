using System.Collections.Concurrent;
using DispensingMachine.Communication.Protocol;

namespace DispensingMachine.Communication.Drivers;

/// <summary>
/// 仿真 PLC：用内存数组模拟寄存器区，接口和真驱动完全一致。
/// 只负责把 IPlcDriver 的读写翻成内存访问，不负责轮询节拍、不负责四步握手流程、不负责把寄存器翻译成业务状态 ——
/// 那些分别归 PlcPollingService、HandshakeManager 和 PlcSnapshot。仿真件替它们多做一步，
/// 就会把真机上的时序问题掩盖掉，测试全绿也就失去意义。
///
/// 它存在的意义不是"偷懒"，而是：
///   1. 脱机也能跑通整条链路（没设备时也能开发、演示）
///   2. 能注入故障（断线、延迟、异常响应），把边界条件测干净
/// 真机上把这一行换成 new ModbusTcpDriver() 即可，上层代码零改动。
///
/// 依赖方向单向：这里只实现 IPlcDriver、只读 PlcRegisterMap 的地址常量，不引用 Devices / Core / App；
/// 反过来 PlcPollingService 与 HandshakeManager 只认 IPlcDriver，App 层只在装配时 new 一次。
/// 唯一的逆向例外是界面上的仿真面板：它把 IPlcDriver 向下转型回 SimulatedPlcDriver 调 PresetRegister
/// 去伪造 PLC 侧的状态字 —— 这条路径只在脱机演示时成立，接真 PLC 时不会被执行。
///
/// 为什么用 ConcurrentDictionary 而不是"数组 + 一把 lock"：
///   心跳 Timer、握手应答、读写线程是三条并发路径，数组方案要在每个访问点手动加锁，漏一处就是脏读；
///   ConcurrentDictionary 让单次读或写自身安全，代价是丢掉"整块寄存器是一张快照"的语义 ——
///   而仿真件本来就不需要快照一致性，那件事是 PlcSnapshot 的职责。
///
/// 为什么握手的应答用发后不管（_ = RespondHandshakeAsync(...)）而不是在写入方法里 await：
///   真 PLC 的扫描周期是在写入返回之后才跑的，await 会把第①步写 CMD 和第②步等 ACK 黏成一步，
///   HandshakeManager 的超时分支在脱机环境下就再也测不出来了。
/// </summary>
public sealed class SimulatedPlcDriver : IPlcDriver, IDisposable
{
    private readonly ConcurrentDictionary<ushort, ushort> _holding = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _heartbeatTimer;

    /// <summary>模拟通讯延迟，用来观察轮询是否稳定。</summary>
    public int SimulatedLatencyMs { get; set; } = 5;

    /// <summary>
    /// 模拟 PLC 处理握手命令的耗时（ms）。
    /// 真机上是 PLC 扫描周期 + 执行机构动作时间，这里用一个短延时代替。
    /// </summary>
    public int SimulatedHandshakeDelayMs { get; set; } = 30;

    /// <summary>故障注入开关：模拟网线拔掉。</summary>
    public bool SimulateDisconnect { get; set; }

    /// <summary>握手应答任务的取消源，Dispose 时统一取消。</summary>
    private readonly CancellationTokenSource _ackCts = new();

    // 这三个属性服务于日志和界面上的"看起来连上了没"，不保证链路真的健康：
    // 轮询线程靠 IsConnected 决定读不读，所以它在 ConnectAsync 返回之前就已经被置成 true。
    public bool IsConnected { get; private set; }
    public string Endpoint { get; private set; } = "(未连接)";
    public byte StationId { get; private set; } = 1;

    /// <summary>
    /// 模拟 PLC（可编程逻辑控制器）驱动的构造函数。
    /// 刻意在构造阶段就启动心跳定时器（而非等到 ConnectAsync 连接时才创建），
    /// 配合 IsConnected 门控，避免连接建立瞬间出现“心跳尚未初始化”的空窗期。
    /// </summary>
    public SimulatedPlcDriver()
    {
        // 创建 System.Threading.Timer（多线程异步计时器），它会在后台 ThreadPool（线程池）中定时触发：
        // 参数 1：回调委托 _ => { ... }，使用 Discard（弃元参数 _）表示忽略传入的 state 状态对象
        // 参数 2：state 状态对象，不需要传递额外上下文，传 null
        // 参数 3：dueTime = 500（首次触发延迟 500ms）
        // 参数 4：period = 500（之后每隔 500ms 周期性触发一次，对齐工业心跳节拍）
        _heartbeatTimer = new Timer(_ =>
        {
            // 1. 连接门控：如果当前模拟驱动处于“未连接”状态，直接返回，心跳暂停跳动
            //    （真实 PLC 断线时，上位机也看不到新心跳；连上后下一拍自动恢复跳动）
            if (!IsConnected) return;

            // 2. 从线程安全字典 _holding（模拟 PLC 保持寄存器内存区）中安全读取当前心跳值：
            //    若该地址尚未初始化过，GetOrAdd 会自动写入默认值 (ushort)0 并返回 0 .GetOrAdd第一个参数ushort是寄存器地址，第二个参数ushort是寄存器当前数值
            ushort current = _holding.GetOrAdd(PlcRegisterMap.Heartbeat, (ushort)0); // 这个是键值对，第一个参数是键，第二个参数是值，current是通过键取值

            // 3. 心跳加 1 并写回字典：
            //    - C# 中 ushort + 1 会触发整数提升变成 int，所以必须强转为 (ushort)
            //    - 加上 unchecked 允许 65535 + 1 自然溢出回绕为 0，精准还原真实 16 位 PLC 寄存器的行为；
            // (ushort)65536 → 不加 unchecked 会抛 OverflowException，(ushort)65536 → 不加 unchecked 会抛 OverflowException，
            // 加 unchecked   → 静默截断成 0 ; 设备连续跑 9 小时左右，心跳数字会从 65535 跳回 0
            _holding[PlcRegisterMap.Heartbeat] = unchecked((ushort)(current + 1));  // 写入内存寄存器字典，等待轮询来取   刚才的值 + 1，覆盖原值

        }, null, 500, 500);
    }

    /// <summary>
    /// 启动流程里第一个不能失败的调用，所以这里选择抛异常而不是返回 false ——
    /// 调用方要能区分"参数填错（异常，立刻停下来改）"和"设备暂时不在（可以重连重试）"。
    /// </summary>
    /// <param name="ip">只用来拼 Endpoint 和报错文案，不做任何解析；填错 IP 在仿真里照样"连接成功"。</param>
    /// <param name="port">同上，只进 Endpoint 字符串，不校验范围。</param>
    /// <param name="stationId">原样回填到 StationId；真机上它是 Modbus 从站号（1~247），仿真件不做校验。</param>
    /// <param name="ct">取消时 Task.Delay 直接抛 OperationCanceledException，走不到故障注入分支。</param>
    /// <returns>永远返回 true。想验证连接失败分支，请打开 SimulateDisconnect 让它抛 IOException。</returns>
    public async Task<bool> ConnectAsync(string ip, int port, byte stationId = 1, CancellationToken ct = default)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);   // 模拟 TCP 三次握手耗时
        if (SimulateDisconnect)
            throw new IOException($"无法连接到 {ip}:{port}（故障注入）");

        StationId = stationId;              // 从站id
        Endpoint = $"{ip}:{port}";
        IsConnected = true;                 // ← 这个标志决定轮询读不读
        return true;
    }

    /// <summary>
    /// 现场里"主动停机"和"线缆被拔"是两件事，这里只做前者：把连接标志清掉，让下一次读写抛 IOException。
    /// 不负责清 CMD/ACK 残留 —— 那是重连之后 HandshakeManager 第一步 ResetBothAsync 的活。
    /// </summary>
    /// <returns>同步完成，调用方不必 await；保留 Task 返回只是为了和 IPlcDriver 签名一致。</returns>
    public Task DisconnectAsync()
    {
        IsConnected = false;
        Endpoint = "(未连接)";
        return Task.CompletedTask;
    }

    /// <summary>
    /// 握手轮询专走这条读路径：HandshakeManager 每隔 PollIntervalMs 读一次单寄存器，
    /// 所以每次都必须过 SimulateIoAsync，让延迟和断链检查真实参与到握手超时的计时里。
    /// </summary>
    /// <param name="count">不做上限校验，传 0 得到空数组；真机 Modbus 单次最多 125 个，仿真件不拦越界，靠上层守住寄存器表的约定。</param>
    /// <returns>返回定长数组，没写过的地址补 0，不会返回 null；断链时抛 IOException 而不是返回空值。</returns>
    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        await SimulateIoAsync(ct).ConfigureAwait(false);
        return ReadBlock(_holding, startAddress, count);
    }

    /// <summary>
    /// 轮询引擎唯一的批量读入口：PlcSnapshot.FromRegisters 按下标把返回值翻译成业务状态，
    /// 所以这里必须"从 StatusStart 起、连续 StatusCount 个"原样返回，不能跳地址、不能压缩空洞。
    /// </summary>
    /// <returns>断链时抛 IOException，由轮询循环兜住转成 PollingError 事件；不会返回 null 或短数组。</returns>
    public async Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
    {
        // 输入寄存器在仿真里和保持寄存器共用一块内存，便于外部随意布置测试数据
        await SimulateIoAsync(ct).ConfigureAwait(false);
        return ReadBlock(_holding, startAddress, count);
    }

    /// <summary>
    /// 握手第①步（置 CMD）和第③步（撤 CMD）都落在这一条写入上，所以它除了写值还得顺手扮演 PLC 的应答侧 ——
    /// 少了这一半，HandshakeManager 的等待逻辑在脱机环境里永远等不到 ACK，握手用例只能靠猜。
    /// </summary>
    /// <param name="address">只有等于 HandshakeCmd 才触发应答；写参数区（100~102）不会有多余副作用。</param>
    /// <param name="value">原样回写到 ACK，包括 0 —— 第③步撤命令正是靠"写 0 之后 ACK 也跟成 0"才收尾的。</param>
    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        await SimulateIoAsync(ct).ConfigureAwait(false);            // ← 模拟网络延迟 + 拿锁
        _holding[address] = value;                                  // ← 写进内存字典（相当于 PLC 的寄存器区）

        // 模拟 PLC 梯形图的握手应答：
        // 真实 PLC 扫描到 CMD 变化后执行动作，再把 ACK 置成同样的值。
        // 这里用"延时 + 回写 ACK"代替那段梯形图，让上位机看到完整的四步往返。
        // 触发应答必须排在上面的赋值之后：应答任务靠读回 CMD 判断这条应答是否过期，
        // 顺序反过来它永远读到旧值，过期判断就形同虚设
        if (address == PlcRegisterMap.HandshakeCmd)                  // ★ 关键：写的是握手命令寄存器，就顺手扮演 PLC 的应答侧
            _ = RespondHandshakeAsync(value);
    }

    /// <summary>
    /// 扮演 PLC 的应答侧：延时之后把 ACK 跟成 CMD 的值。
    /// 写之前先确认 CMD 没有又被改过，避免上一轮的应答污染新一轮握手。
    /// </summary>
    private async Task RespondHandshakeAsync(ushort cmdValue)
    {
        try
        {
            await Task.Delay(SimulatedHandshakeDelayMs, _ackCts.Token).ConfigureAwait(false);

            // 延时期间被断开：直接放弃应答，让上层走超时分支 ——
            // 这里如果"好心"补写一个 ACK，断链场景就变成假成功，比不写危险得多
            if (!IsConnected || SimulateDisconnect) return;

            // 期间 CMD 已经变了，说明这条应答过期，直接丢弃
            if (_holding.GetOrAdd(PlcRegisterMap.HandshakeCmd, (ushort)0) != cmdValue)
                return;

            _holding[PlcRegisterMap.HandshakeAck] = cmdValue;
        }
        catch (OperationCanceledException)
        {
            // 驱动释放，正常退出
        }
    }

    /// <summary>
    /// 参数下发靠这条捷径：一次把 TargetX / TargetY / Speed 三个写下去，省掉三次单写叠起来的通讯延迟。
    /// 注意仿真里它只是个循环赋值，不是真机的原子块写 —— 上层别把"多寄存器写"当原子操作用。
    /// </summary>
    /// <param name="values">不校验长度和边界；长度超过 65535-startAddress 时地址会从 ushort 回绕，覆盖到低地址的寄存器。</param>
    public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default)
    {
        await SimulateIoAsync(ct).ConfigureAwait(false);
        for (int i = 0; i < values.Length; i++)
            _holding[(ushort)(startAddress + i)] = values[i];
    }

    /// <summary>供测试代码直接布置寄存器（绕过通讯层）。</summary>
    public void PresetRegister(ushort address, ushort value) => _holding[address] = value;

    /// <summary>
    /// 界面上的仿真面板和测试断言要用：瞄一眼当前值就够，不该为它付一次通讯延迟。
    /// 它不检查连接状态，所以断链之后照样读得到值 —— 拿它断言"断链后数据是否还在"时要想清楚这一点。
    /// </summary>
    /// <returns>没写过的地址返回 0，不会返回 null。</returns>
    public ushort PeekRegister(ushort address) => _holding.GetOrAdd(address, (ushort)0);

    /// <summary>
    /// 所有读写共用的"进入通讯"闸门：把延迟、断链检查、串行化三件事收在一处，
    /// 这样仿真件和 ModbusTcpDriver 在"什么时候会抛 IOException"上保持同构，上层才不用按驱动类型写分支。
    /// </summary>
    /// <param name="ct">取消时抛 OperationCanceledException；等锁期间也会因它退出，此时还没走到断链检查。</param>
    private async Task SimulateIoAsync(CancellationToken ct)
    {
        // 整个"延迟 + 检查"都扣在锁里：真 PLC 是一条连接一问一答，并发读写只会互相踩包；
        // 串行化之后，上层要是漏了串行控制，脱机阶段就能暴露出来
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Task.Delay 传 0 是合法的，传负数会抛 ArgumentOutOfRangeException；
            // 测试里常把它设成 0 来关掉延迟，所以这个判断不能省
            if (SimulatedLatencyMs > 0)
                await Task.Delay(SimulatedLatencyMs, ct).ConfigureAwait(false);     // ① 先耗掉 5ms

            // 顺序不能反：先耗掉延迟再判连接，才对得上真机拔网线时"先等 TCP 超时才报断"的时序；
            // 反过来的话断链在仿真里瞬间返回，上层根本没机会跑到慢响应下的那条竞态路径
            if (!IsConnected || SimulateDisconnect)
                throw new IOException("仿真 PLC：连接已断开");                          // ② 再判连接
        }
        finally
        {
            _lock.Release();            // 释放锁
        }
    }

    /// <summary>
    /// 一次读块必须一口气取到连续 count 个地址：中间若有心跳或应答线程在写，
    /// 边读边判空会让同一帧里混进两个时刻的值，上层看到的快照就是拼出来的。全部取完再返回才谈得上"一帧"。
    /// </summary>
    /// <returns>定长数组，没写过的地址补 0；长度完全由 count 决定，调用方要自己保证它和寄存器表的段长一致。</returns>
    private static ushort[] ReadBlock(ConcurrentDictionary<ushort, ushort> memory, ushort start, ushort count)
    {
        ushort[] result = new ushort[count];
        for (int i = 0; i < count; i++)
            result[i] = memory.GetOrAdd((ushort)(start + i), (ushort)0);
        return result;
    }

    /// <summary>
    /// 关窗或切驱动时必须走到这里，否则心跳 Timer 和还挂在 Task.Delay 上的应答任务会继续活着，
    /// 表现为"程序关了寄存器还在涨"—— 现场排查这种幽灵线程极其费时间。
    /// </summary>
    public void Dispose()
    {
        // 顺序不能换：先 Cancel 让应答任务的 Task.Delay 抛 OCE 退出，再 Dispose 掉取消源。
        // 反过来可能往已释放的 CTS 上注册回调，抛 ObjectDisposedException，而且是在后台线程上 —— 直接崩进程
        _ackCts.Cancel();
        _ackCts.Dispose();
        _heartbeatTimer.Dispose();
        _lock.Dispose();
    }
}
