using System.Diagnostics;
using DispensingMachine.Devices.Abstractions;

namespace DispensingMachine.Devices.Peripherals;

/// <summary>
/// 工业扫码枪模块。
/// 负责把"触发一次扫码"收敛成一个带超时的异步动作，并把条码与报警状态暴露给上层；
/// 不负责串口/TCP 报文收发、不负责判断条码是否属于当前工单，也不碰任何界面元素。
///
/// 扫码是"发一条触发命令 → 等设备回一帧字符串"的典型一问一答模式，
/// 和 Modbus 的请求/应答是同一种交互模型。
///
/// 依赖方向是单向的：
///   向下只用 AsyncTimeoutHelper 的等待机制和 ObservableObject 的属性通知，不引用 Core 与 App 中的任何类型；
///   向上被 WorkstationController 的工位流程按步调用，由 App 层注册成单例、交给 ViewModel 绑定。
///   于是 Devices 这一层永远不需要知道"扫完码之后要去点胶"这件事。
///
/// 为什么超时和"读不出内容"都归一成 null，而不是抛出两种不同的异常：
///   条码污损、反光、标签翘边在现场是常态，不是程序错误；上层一条"扫不出来就报警停机"
///   的分支就够用。若用异常区分，每个调用点都要写 try/catch，真正的编码错误会和现场故障混在一起。
///
/// 为什么仿真也走 AsyncTimeoutHelper 轮询，而不是 Task.Delay 一下直接给结果：
///   只有让仿真走和真机一样的"等待 → 检查 → 出结果"路径，超时分支才能在仿真环境里被真正跑到，
///   否则仿真路径和超时逻辑就是两段永远对不上的代码。
/// </summary>
public sealed class ScannerModule : ObservableObject, IDeviceModule
{
    /// <summary>仿真读码耗时（ms）。</summary>
    public int SimulatedDecodeMs { get; set; } = 150;

    /// <summary>故障注入：模拟条码污损，读不出内容。</summary>
    public bool SimulateBadRead { get; set; }

    /// <summary>故障注入：模拟扫码枪无应答。
    /// 打开后耗时是调用方传入的整个 timeout，而不是 SimulatedDecodeMs ——
    /// 调它的人常把超时值改小来跑得快一点，改的其实是"多久认输"。</summary>
    public bool SimulateTimeout { get; set; }

    private string _lastBarcode = string.Empty;
    private bool _alarm;

    /// <summary>
    /// 扫码枪只靠"名字"参与上层流程：报警记录、日志、界面都靠它区分是哪一把枪。
    /// </summary>
    /// <param name="name">非空且在同一条产线内唯一。传设备位号（如"扫码枪-上料位"），
    /// 不要传会随语言/皮肤变化的界面文案，否则翻旧日志时对不上现场。</param>
    public ScannerModule(string name) => Name = name;

    public string Name { get; }

    /// <summary>
    /// 最近一次成功读到的条码，直接供界面和追溯记录绑定。
    /// 失败时不会清空，仍保留上一次的值 —— 所以流程里绝不能拿它当"本次扫码结果"，
    /// 必须用 ScanAsync 的返回值。set 保持私有，是为了让写入点只剩"读码成功"和 InjectBarcode 两处，
    /// 流程中不会出现"没扫码却有条码"的脏数据。
    /// </summary>
    public string LastBarcode
    {
        get => _lastBarcode;
        private set => SetProperty(ref _lastBarcode, value);
    }

    /// <summary>
    /// 扫码枪自身的故障标志，参与 IDeviceModule 的"有没有报警"问答。
    /// IsReady 是由它派生的，但属性通知只会发 Alarm 这一条：将来把 IsReady 单独绑到界面时，
    /// 要额外订阅 Alarm 才会刷新，别指望它跟着自动变。
    /// </summary>
    public bool Alarm
    {
        get => _alarm;
        private set => SetProperty(ref _alarm, value);
    }

    /// <summary>扫码枪只要没报警就算就绪（它是被动设备，不需要回零）。</summary>
    public bool IsReady => !Alarm;

    public bool HasAlarm => Alarm;

    /// <summary>
    /// 给复位按钮和流程复位走的入口：清掉故障标志，让工位可以继续跑。
    /// 扫码枪没有回零、使能这类物理动作要重做，清标志就等于恢复就绪，
    /// 所以这里不需要像轴那样再等一个到位反馈。
    /// </summary>
    /// <returns>恒为 true —— 它是在满足 IDeviceModule"清报警可能失败"的契约，不是为了报告结果。
    /// 调用方想确认设备是否真的恢复，请去读 IsReady；真机上若要下发命令清报警，这里才可能返回 false。</returns>
    public bool ResetAlarm()
    {
        Alarm = false;
        return true;
    }

    /// <summary>触发一次扫码。成功返回条码字符串，失败或超时返回 null。
    /// 工位流程走到扫码步必须拿到"当前这一只工件"的条码才能继续，而读码要等设备物理出码，
    /// 不能同步阻塞 UI 线程，所以这里必须是一个可 await、可取消的异步动作。
    /// 失败时本方法只置位 Alarm，并不中断流程：要不要停机、报什么报警码，由调用方决定。
    /// </summary>
    /// <param name="timeout">从发令到出条码的总预算，不是单次轮询间隔。
    /// 传零或负值会立刻判超时（先查条件再查时间，所以它连一次设备都不等）；
    /// 取值来自配方 ScanTimeoutMs，真机上一般留到设备标称读码时间的 2~3 倍。</param>
    /// <param name="ct">取消时抛 OperationCanceledException，而不是返回 null ——
    /// 调用方要能区分"操作员按了停止"和"扫不出码"，前者不该报设备故障。</param>
    /// <returns>null 表示超时或读到了空内容（污损、反光），调用方应视为本件不合格，走报警并终止当前流程；
    /// 空字符串不会作为返回值出现 —— 空白结果已被统一折叠成 null。
    /// 返回 null 时 LastBarcode 仍是上一次的旧值，别顺手拿来顶替。</returns>
    public async Task<string?> ScanAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (SimulateTimeout)        // 扫码超时
        {
            // 模拟设备完全无应答：一直等到超时
            // 故意等满整个 timeout 才认输，而不是立刻 return null：真机的"没应答"就是白等一个超时周期，
            // 仿真若把这段耗时省掉，按时间轴复盘的人会误以为超时是瞬间发生的
            await Task.Delay(timeout, ct);
            Alarm = true;
            return null;
        }

        // 这把表只量"仿真读码耗时"；判超时是 WaitUntilAsync 内部另起的一把表。
        // 两个计时器各管一件事，别拿 sw 去判超时，否则超时预算会被读码进度悄悄吃进去
        var sw = Stopwatch.StartNew();          // 开始计时

        string? result = null;

        await AsyncTimeoutHelper.WaitUntilAsync(            // 动作超时看门狗
            condition: () =>
            {
                // 真机上这一行换成"串口/网口缓冲区里有没有一帧完整条码"；
                // 这里的 150ms 只是让仿真出码看起来像设备，不是设备的真实指标
                if (sw.ElapsedMilliseconds < SimulatedDecodeMs) return false;   //  ① 时间没到 → 继续等

                // 读码完成，产生结果
                result = SimulateBadRead ? string.Empty : GenerateBarcode();    // ② 时间到了 → 产生结果，并宣布"成立"
                return true;
            },
            timeout: timeout,       // 超时时间
            // 比默认的 50ms 更密：仿真出码只有 150ms，间隔太大时"到点"会被量化得很粗，
            // SimulatedDecodeMs 调小之后还会连带把超时判定的精度拖歪
            intervalMs: 20,         // 轮询周期是20ms一次
            token: ct);

        // 故意不看 WaitUntilAsync 返回的 bool：等超时和"等到了但内容是空"在这里是同一个出口，
        // 都折叠成 null。调用方只需处理一种失败，不用为两种失败各写一套分支
        if (string.IsNullOrWhiteSpace(result))
        {
            Alarm = true;
            return null;
        }

        LastBarcode = result!;
        // 读码成功顺手清报警：扫码枪的故障（脏污、反光、标签歪）往往是一次性的，不像气缸那样要人工复位。
        // 代价是界面报警会自己熄灭，"报警灯灭了"不能当作已经排查过的证据
        Alarm = false;          // 清报警
        return result;          // 返回结果
    }

    /// <summary>供外部注入条码（比如测试时指定固定值）。
    /// 单独开一个方法而不放开 LastBarcode 的 set：条码写入点只剩"读码成功"和"人工注入"两处，
    /// 流程里不会出现"没扫码却有条码"的脏数据；.Trim() 也放在这一处做，是因为现场手输常带空格和回车。
    /// </summary>
    /// <param name="barcode">传空白串会把 LastBarcode 清成空，但并不置位 Alarm ——
    /// 想模拟"扫不出来"，要用 SimulateBadRead，而不是注入空串。</param>
    public void InjectBarcode(string barcode) => LastBarcode = barcode.Trim();

    /// <summary>
    /// 仿真专用：造一个"看起来像真货"的条码，好让界面显示、追溯记录、条码去重这些下游逻辑
    /// 能被完整地跑一遍，而不是靠一串假字符占位。
    /// 真机条码格式由设备和工艺定（Code128、GS1-128 前缀、校验位各不相同），
    /// 不要把这里的拼接规则复制进解析代码。
    /// </summary>
    private static string GenerateBarcode()
    {
        // 模拟一个 18 位产品条码：日期 + 流水号
        // 注意上面"18 位"是笔误：P + 8 位日期 + 6 位流水实际只有 15 个字符。
        // 解析时别硬编码长度或字段偏移，要按实测到的真实编码规则来
        return $"P{DateTime.Now:yyyyMMdd}{Random.Shared.Next(100000, 999999)}";
    }
}
