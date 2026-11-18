using System.Diagnostics;
using DispensingMachine.Devices.Abstractions;

namespace DispensingMachine.Devices.Pneumatic;

/// <summary>
/// 气缸模块。
/// 负责一个双作用气缸从发令到确认到位的完整时序：置输出 → 等磁簧闭合 → 超时撤输出。
/// 不负责什么：不碰线圈地址和输出极性（Communication 层的事）、不决定什么时候该夹紧（Core 层工步的事）、
/// 也不决定超时给多长 —— 那个值由配方 CylinderTimeoutMs 传进来。
///
/// 一次完整动作是三件事：
///   ① 置电磁阀输出      —— 发出指令
///   ② 等磁簧开关到位    —— 读反馈（这段物理时间无法消除，只能等）
///   ③ 超时就报错        —— 看门狗兜底
///
/// 注意"发令"和"等结果"必须分开：电磁阀通电到磁簧闭合中间有几百毫秒的物理延时。
/// 把所有等待都交给 AsyncTimeoutHelper，好处是超时逻辑只有一份，不会各处不一致。
///
/// 依赖方向是单向下行：
///   往下只依赖同项目的 CylinderModel（状态载体）、AsyncTimeoutHelper（超时看门狗）、
///   ObservableObject（变更通知）、IDeviceModule（设备契约），不认识 Core、App 里的任何类型；
///   反过来被 WorkstationController（Step 10 夹紧 / Step 70 松开）、DiagnosticService（2001/2002 报警诊断）、
///   MainWindowViewModel（界面绑定）和 App 的 Prism 注册依赖 —— 改这里的签名要先看那几处怎么调的。
///
/// 为什么仿真反馈是 ExtendAsync / RetractAsync 内部算出来的，而不是另起一个"传感器线程"去刷新：
///   一旦有独立线程，就必然出现"指令已发、反馈却被提前置位"的时序问题，而这段代码要验证的是工步顺序和超时分支，
///   不是去复刻一个真实的 DI 扫描周期。代价是 ExtendedSensor / RetractedSensor 只允许这个模块写，
///   界面和诊断一律按只读快照处理，否则等于人工伪造磁簧信号，超时和冲突报警都会失效。
/// </summary>
public sealed class CylinderModule : ObservableObject, IDeviceModule
{
    /// <summary>仿真动作耗时（ms）。真机由气压和负载决定，通常 200~500ms。</summary>
    public int SimulatedTravelMs { get; set; } = 220;

    /// <summary>
    /// 只接受一个名字，Model 在构造里自己 new，不接受外部注入。
    /// 允许从外面塞一个 CylinderModel 进来，就等于允许两个工位共享同一份磁簧状态 ——
    /// 那种"夹紧气缸动了、挡料气缸也跟着显示伸出"的问题在现场最难查。
    /// </summary>
    /// <param name="name">用于日志和报警来源标注；同一工位里不要起两个同名气缸，否则报警分不清是哪一只没到位。</param>
    public CylinderModule(string name)
    {
        Model = new CylinderModel(name);
        Model.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(HasAlarm));
            OnPropertyChanged(nameof(StateText));
        };
    }

    /// <summary>
    /// 把整个 Model 暴露出去，而不是逐个转发 ExtendCommand / ExtendedSensor。
    /// 界面绑的是 Model，工步调的是 ExtendAsync / RetractAsync，两边都不用知道对方的细节；
    /// 代价是调用方能绕过动作时序直接改传感器位，所以约定：外部只读，
    /// 写只能走 ExtendAsync / RetractAsync / ResetAlarm 这三条路，否则磁簧位和真实输出会互相说谎。
    /// </summary>
    public CylinderModel Model { get; }

    /// <summary>名字只存在 Model 上，这里转发而不是再存一份：改设备名只改构造参数，不会出现两处不一致。</summary>
    public string Name => Model.Name;

    /// <summary>就绪 = 在缩回位 + 不在伸出位 + 无报警。</summary>
    /// <remarks>
    /// 要求"缩回位"才叫就绪，是因为夹紧气缸伸出时不能进料；停在中间（两个磁簧都没亮）同样不算就绪 ——
    /// 中间位说明活塞卡住或气源掉了，放行下一工步就是撞机。
    /// 反过来说，别拿 IsReady 当"缩回动作做完了"的判据：缩回成不成，看 RetractAsync 的返回值。
    /// </remarks>
    public bool IsReady => Model.RetractedSensor && !Model.ExtendedSensor && !HasAlarm;

    /// <summary>
    /// 两个磁簧同时亮 = 物理上不可能的状态（短路或开关卡死）。
    /// 这种信号组合必须当故障处理，不能当成"正常"。
    /// </summary>
    public bool HasAlarm => Model.ExtendedSensor && Model.RetractedSensor;

    /// <summary>
    /// 界面、日志、报警共用的状态说法，四个分支的判断顺序在这里固定死。
    /// 为什么不写成 XAML 的 DataTrigger 或 IValueConverter：同一句话要同时出现在界面和报警文案里，
    /// 散进 XAML 之后改一句措辞要翻好几个文件，日志和界面还会各说一套。
    /// 顺序有讲究：HasAlarm 必须第一个判 —— 两个磁簧同时闭合时若先判 ExtendedSensor，
    /// 传感器短路这种真故障会被显示成"已伸出"的正常状态，这是现场最容易被骗过去的一类问题。
    /// </summary>
    public string StateText => HasAlarm ? "传感器冲突"
        : Model.ExtendedSensor ? "已伸出"
        : Model.RetractedSensor ? "已缩回"
        : "动作中";

    /// <summary>
    /// 清掉传感器冲突位，让 IsReady 有机会重新为 true —— 报警不清，"就绪"永远回不来，产线只能靠重启软件恢复。
    /// </summary>
    /// <returns>
    /// 这里恒为 true：仿真没有别的地方可以回读，把两个磁簧位改成"缩回到位"就等于人工复位。
    /// 真机上不能照这么写 —— 直接改传感器位会把真实故障掩盖掉（磁簧真的短路时，这一下照样显示正常，下一次伸出才发现）；
    /// 正确做法是回读 DI，确认两个磁簧恢复成一开一合，再返回 true。
    /// </returns>
    public bool ResetAlarm()
    {
        Model.ExtendedSensor = false;
        Model.RetractedSensor = true;
        return true;
    }

    /// <summary>
    /// 把"置输出 → 等磁簧闭合 → 超时撤输出"合成一个 await，工步只关心成功还是超时。
    /// 为什么要它而不是让工步自己写输出再自己 Task.Delay：撤输出这件事必须保证在成功分支之外也会执行到 ——
    /// 电磁阀长期通电会烧线圈、气缸一直憋压，超时和被取消两条路都得先把 ExtendCommand 拉低。
    /// </summary>
    /// <param name="timeout">
    /// 等磁簧闭合的上限，单位随 TimeSpan，取值来自配方 CylinderTimeoutMs（默认 3000ms）。
    /// 这是硬超时而不是"等一会儿"：比真机行程时间（气压正常时 200~500ms）还小，正常动作也会被判超时；
    /// 给到十几秒，气缸真卡住了也迟迟不报警，工件在上面那个工步白白多等十几秒。
    /// </param>
    /// <param name="ct">
    /// 停机 / 急停信号。被取消时不返回 false 而是抛 OperationCanceledException，别当成普通超时处理。
    /// 另外这里会 await Task.Delay，不要握着锁调它。
    /// </param>
    /// <returns>
    /// false 只表示"timeout 内磁簧没闭合"，此时输出已经撤掉，活塞可能卡在伸出途中。
    /// 调用方必须按"夹紧没完成"报 CylinderExtendTimeout 并停在当前工步，
    /// 不能当成功继续走 —— 没夹紧就去动轴，工件会被带偏甚至甩飞。
    /// </returns>
    public async Task<bool> ExtendAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        // 下面两行有一拍"两个线圈同时为 true"：仿真里这一拍没有任何人读，不构成问题；
        // 真机上的双线圈电磁阀必须先撤对侧再通本侧，两条线圈同时得电会把阀芯顶在中间位、气缸干脆不动
        Model.ExtendCommand = true;             // 写 PLC 线圈   // ① 发令
        Model.RetractCommand = false;

        var sw = Stopwatch.StartNew();      // 开始计时

        try
        {
            // 仿真的"传感器"就是时间到了 —— 真机上这里换成读 DI 点
            // 30ms 是采样节拍，到位判定误差就是这个量级；真机读 DI 时不能低于 PLC 的扫描周期，否则同一个信号会被重复读到。
            // 条件里那只 sw 是上面 ExtendAsync 自己 new 的（判行程走了多久），AsyncTimeoutHelper 内部另有一只秒表判超时 —— 两只各管各的，调超时不要动这只
            bool ok = await AsyncTimeoutHelper.WaitUntilAsync(                      // 动作超时看门狗   // ② 等结果
                condition: () => !Model.SimulateFailure && sw.ElapsedMilliseconds >= SimulatedTravelMs,
                timeout: timeout,
                intervalMs: 30,
                token: ct);

            if (!ok)                                // 超时分支：撤输出 + 返回 false
            {
                // 超时必须撤掉输出，否则电磁阀长期通电、气缸憋压
                Model.ExtendCommand = false;        // ③-a 超时：撤输出
                return false;
            }

            // 先置新到位位、再清旧位：反过来的话中间会有一拍两个磁簧都不亮，
            // 界面闪一下"动作中"，那一拍的 IsReady 也是 false，正好卡在工步判断的缝里
            Model.ExtendedSensor = true;            // ③-b 成功：先置新位
            Model.RetractedSensor = false;          //      再清旧位
            return true;
        }
        catch (OperationCanceledException)              // 撤输出 + 原样抛
        {
            // 取消不是超时：输出照样要撤（停机后线圈还带电会烧），但异常必须原样抛上去，
            // 换成返回 false 会让"有人叫停"在工步里退化成一次普通超时，停机原因就丢了
            Model.ExtendCommand = false;            // ③-c 取消：撤输出
            throw;                                  //      原样抛
        }
    }

    /// <summary>
    /// 松开夹紧，等缩回到位磁簧闭合。
    /// 为什么不写成 ExtendAsync(false) 一个方法带方向参数：两个方向的报警码（2001/2002）和诊断建议
    /// （伸出侧查气源压力和阀芯，缩回侧查消声器堵塞和复位弹簧）完全不同，
    /// 合成一个 bool 参数之后，调用点只看得到 true/false，方向信息在诊断那一层就找不回来了。
    /// </summary>
    /// <param name="timeout">等缩回到位磁簧闭合的上限（配方 CylinderTimeoutMs），含义与 ExtendAsync 一致。</param>
    /// <param name="ct">停机信号，取消时同样抛异常，且输出已撤。</param>
    /// <returns>
    /// false 表示 timeout 内没缩回到位，此时活塞可能仍停在伸出侧、挡着来料通道；
    /// 调用方报 CylinderRetractTimeout 后要停在当前工步，别往下走 —— 后续取料/放料会直接撞在气缸上。
    /// </returns>
    public async Task<bool> RetractAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        Model.RetractCommand = true;
        Model.ExtendCommand = false;

        var sw = Stopwatch.StartNew();

        try
        {
            // 采样节拍刻意和伸出侧一样取 30ms：两个方向的到位精度若不一致，
            // "伸出偏慢、缩回偏快"这种对比结论就是假的，现场会顺着它查错方向
            bool ok = await AsyncTimeoutHelper.WaitUntilAsync(
                condition: () => !Model.SimulateFailure && sw.ElapsedMilliseconds >= SimulatedTravelMs,
                timeout: timeout,
                intervalMs: 30,
                token: ct);

            if (!ok)
            {
                Model.RetractCommand = false;
                return false;
            }

            // 与伸出侧对称：先清"已伸出"再置"已缩回"，中间不会出现两个磁簧同时亮的假冲突
            Model.ExtendedSensor = false;
            Model.RetractedSensor = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            // 同伸出侧：撤输出后必须原样 throw —— 停机时活塞停在半路这一事实，只有异常能传到工步机
            Model.RetractCommand = false;
            throw;
        }
    }
}
