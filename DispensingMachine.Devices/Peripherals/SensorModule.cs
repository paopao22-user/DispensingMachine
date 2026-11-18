using DispensingMachine.Devices.Abstractions;

namespace DispensingMachine.Devices.Peripherals;

/// <summary>
/// 通用开关量传感器（这里用来检测工件是否到位）。
/// 不负责判断"该有料时没料"，不负责产生报警码，也不碰 DI 地址与采集周期 —— 它只是把最新一次采样结果摆在这里。
/// 传感器是最简单的设备：只有一个布尔量，没有动作。所以它的 IsReady 恒为 true。
///
/// 依赖方向是单向的：
///   向下只依赖 ObservableObject（变更通知）、IDeviceModule（设备契约）和 AsyncTimeoutHelper（超时看门狗），
///   不认识 Core 与 App 里的任何类型；向上由 App 用 Prism 注册成单例，被 WorkstationController 的 Step 0 等料时 await，
///   被 MainWindowViewModel 绑定显示、写 IsOn、再镜像到 PLC 的 ProductDetected 位 —— 动它的成员前先看那两处怎么用。
///
/// 为什么等信号靠轮询电平，而不是订阅 PropertyChanged 等一次跳变：
///   工件可能在工步开始之前就已经到位（上一件还没被取走，或开机时料就停在工位上），
///   只等"由 false 变 true"那一下就会永远错过；电平判断是幂等的，早到了和刚到了都能立刻返回。
///   代价是最多空转一个采样周期，且要求 IsOn 由别的线程持续刷新。
///
/// 为什么 IsOn 直接开成可写属性，而不是只读属性 + 内部 SetSignal：
///   仿真送料需要把工件"放"到工位上，真机上同一个写入点换成 DI 刷新，两条路径才共用一份工步代码；
///   代价是任何调用方都能伪造传感器信号，所以约定：写只出现在送料仿真与 DI 刷新两处，工步逻辑只读。
///
/// 为什么 HasAlarm 恒为 false、ResetAlarm 恒返回 true，而不是给传感器也挂一个报警位：
///   传感器只回答"此刻有没有料"，它并不知道"该有料时没料"—— 那是上层按超时判出来的（Step 0 超时 → ProductMissing）；
///   两层各存一份报警，现场就会一遍停机刷出两条报警码，还说不清该信哪条。
/// </summary>
public sealed class SensorModule : ObservableObject, IDeviceModule
{
    private bool _isOn;

    /// <summary>
    /// 名字在这里落地成只读的 Name。传感器没有任何可配参数：采集周期、DI 地址、信号极性都归 Communication 层，
    /// 塞进构造函数只会和那边形成两份真相。
    /// </summary>
    /// <param name="name">用于日志与报警来源标注；同一台机器上不要起两个同名传感器，否则报警里分不清是哪一只没亮。</param>
    public SensorModule(string name) => Name = name;

    public string Name { get; }

    /// <summary>
    /// 最新一次采样到的开关量：真机上由 DI 刷新，仿真里由送料逻辑写。
    /// 它只是一份状态快照，不是同步信号 —— 写入方和读取方之间没有任何内存屏障，
    /// 拿它来判断"某件事已经做完"迟早会踩到顺序问题；要等条件成立请一律走 WaitSignalAsync。
    /// </summary>
    public bool IsOn
    {
        get => _isOn;
        set => SetProperty(ref _isOn, value);
    }

    /// <summary>传感器没有"动作"，永远就绪。</summary>
    public bool IsReady => true;

    /// <summary>没有独立报警位，恒为 false：缺料是上层等超时等出来的，理由见类头最后一段。</summary>
    public bool HasAlarm => false;

    /// <summary>
    /// 恒返回 true，而不是抛"不支持"：统一的报警清除流程会对每台设备都调一遍，
    /// 传感器这里必须能被无脑跳过，否则上层得为它单独写一个类型判断分支。
    /// </summary>
    public bool ResetAlarm() => true;

    /// <summary>
    /// 把"等一个开关量变成目标状态"收敛成一次带超时的 await，让工步里能像等气缸一样等料。
    /// 读的是本地缓存的 IsOn，不是现场真值：IsOn 那边一旦没人刷新，这里只会一路等到超时，
    /// 所以调用前先确认采集（或仿真送料）这条链路是活的，别拿它当"设备断了"的诊断手段。
    /// </summary>
    /// <param name="targetState">要等的电平。等工件被取走就传 false —— 传 false 时若当前本来就没有信号，
    /// 会立刻返回 true，所以它不能用来确认"工件刚刚离开"这个动作发生过。</param>
    /// <param name="timeout">从进入本方法开始计时，不是从信号第一次变化开始；给短了会把正常上料节拍误判成缺料。</param>
    /// <param name="ct">一般传整条工步流程的令牌。取消是抛 OperationCanceledException，不会返回 false，
    /// 调用方不必为取消单独写"缺料"分支。</param>
    /// <returns>返回 false 只代表超时且条件始终没成立，调用方应当停机报警（Step 0 就是这么用的）；
    /// 不要在这里自己循环重试，一直等不到料说明上游机构已经坏了，继续等只会把异常拖成停机。</returns>
    public Task<bool> WaitSignalAsync(bool targetState, TimeSpan timeout, CancellationToken ct = default)
        => AsyncTimeoutHelper.WaitUntilAsync(
            condition: () => IsOn == targetState,
            timeout: timeout,
            // 30ms 是采样间隔：工件随输送带走得快，间隔必须明显小于信号的最短持续时间，
            // 否则"到位后马上被带走"那一小段会被两次采样整段漏掉，上料等料就永远等不到
            intervalMs: 30,
            token: ct);
}
