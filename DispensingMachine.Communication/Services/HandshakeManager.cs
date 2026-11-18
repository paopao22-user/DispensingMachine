using System.Diagnostics;
using DispensingMachine.Communication.Drivers;

namespace DispensingMachine.Communication.Services;

/// <summary>握手结果，用于日志和故障分析。</summary>
public sealed record HandshakeResult(
    bool Success,
    TimeSpan TotalElapsed,
    TimeSpan AckElapsed,
    TimeSpan ClearElapsed,
    string Message);

/// <summary>
/// 四步闭环握手。
///
/// 寄存器状态走一圈：
///   (0,0) --①上位机写 CMD=1--> (1,0)
///         --②PLC 执行完置 ACK=1--> (1,1)
///         --③上位机撤 CMD=0--> (0,1)
///         --④PLC 复位 ACK=0--> (0,0)
///
/// ★ 第三步不能省：PLC 靠 CMD 的 0→1 上升沿识别新命令。
///   如果写完不撤，CMD 一直是 1，下次再写 1 就没有边沿，PLC 不会响应。
///
/// ★ 超时兜底必须把 CMD 和 ACK 都清掉：
///   只清 CMD 会残留 (0,1) 这种非法状态，下次握手的第②步会直接假成功。
/// </summary>
public sealed class HandshakeManager
{
    private readonly IPlcDriver _driver;

    public HandshakeManager(IPlcDriver driver) => _driver = driver;

    /// <summary>握手全过程（四步合计）的超时上限，防止每步各等一次导致总时长翻倍。</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>轮询应答寄存器的间隔。</summary>
    public int PollIntervalMs { get; set; } = 10;

    public async Task<HandshakeResult> ExecuteAsync(
        ushort cmdAddress,
        ushort cmdValue,
        ushort ackAddress,
        ushort expectedAck,
        CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();     //开始计时

        // 防御：进来先把两个寄存器拉回 (0,0)，把上一轮可能残留的脏状态清掉
        await ResetBothAsync(cmdAddress, ackAddress, ct).ConfigureAwait(false);

        // ===== 第①步：置命令位 =====
        await _driver.WriteSingleRegisterAsync(cmdAddress, cmdValue, ct).ConfigureAwait(false);

        // ===== 第②步：等 PLC 应答（上升沿由 PLC 自己产生）=====
        var ackSw = Stopwatch.StartNew();      // 开始计时 
        if (!await WaitRegisterAsync(ackAddress, expectedAck, ackSw, ct).ConfigureAwait(false))
        {
            // 超时：必须把 CMD 和 ACK 一起清，否则残留 (0,1) 会让下一轮假成功
            await SafeResetAsync(cmdAddress, ackAddress).ConfigureAwait(false);
            throw new TimeoutException(
                $"握手超时：等待 ACK({ackAddress})= {expectedAck} 超过 {TotalTimeout.TotalMilliseconds:F0}ms，" +
                $"已强制回滚 CMD({cmdAddress}) 与 ACK({ackAddress})");
        }
        var ackElapsed = ackSw.Elapsed;     // 响应消耗时间

        // ===== 第③步：撤回命令位（制造下一次的上升沿）=====
        await _driver.WriteSingleRegisterAsync(cmdAddress, 0, ct).ConfigureAwait(false);

        // ===== 第④步：等 PLC 复位应答位 =====
        var clearSw = Stopwatch.StartNew();
        if (!await WaitRegisterAsync(ackAddress, 0, clearSw, ct).ConfigureAwait(false))
        {
            await SafeResetAsync(cmdAddress, ackAddress).ConfigureAwait(false);
            throw new TimeoutException(
                $"握手收尾超时：等待 ACK({ackAddress}) 复位到 0 超过 {TotalTimeout.TotalMilliseconds:F0}ms");
        }
        var clearElapsed = clearSw.Elapsed;     // 撤销消耗时间

        totalSw.Stop();                     // 停止计时
        return new HandshakeResult(
            Success: true,
            TotalElapsed: totalSw.Elapsed,
            AckElapsed: ackElapsed,
            ClearElapsed: clearElapsed,
            Message: $"握手完成：应答 {ackElapsed.TotalMilliseconds:F0}ms，收尾 {clearElapsed.TotalMilliseconds:F0}ms");
    }

    /// <summary>等待某个寄存器变成期望值。用的是"总超时"，不是每步单独计时。</summary>
    private async Task<bool> WaitRegisterAsync(ushort address, ushort expected, Stopwatch sw, CancellationToken ct)
    {
        while (sw.Elapsed < TotalTimeout)               // 没超时   ← 数据 B（时间）当循环条件
        {
            ct.ThrowIfCancellationRequested();          // ← 数据 C（取消）当出口

            ushort[] values = await _driver.ReadHoldingRegistersAsync(address, 1, ct).ConfigureAwait(false);    // 驱动器读取保持寄存器
            if (values[0] == expected)          // 第一个值为期待值时，返回true    // ← 数据 A 判定成功
                return true;

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);     //轮询
        }
        return false;                           // ← 数据 B 用完（超时）
    }

    private async Task ResetBothAsync(ushort cmdAddress, ushort ackAddress, CancellationToken ct)
    {
        await _driver.WriteSingleRegisterAsync(cmdAddress, 0, ct).ConfigureAwait(false);    // 重置寄存器单边的值
        await _driver.WriteSingleRegisterAsync(ackAddress, 0, ct).ConfigureAwait(false);
    }

    /// <summary>回滚用，故意不传取消令牌 —— 出错路径上哪怕外部已取消也要尽力把寄存器归零。</summary>
    private async Task SafeResetAsync(ushort cmdAddress, ushort ackAddress)
    {
        try { await _driver.WriteSingleRegisterAsync(cmdAddress, 0, CancellationToken.None).ConfigureAwait(false); }
        catch { /* 回滚失败只能记日志，不能掩盖原始异常 */ }

        try { await _driver.WriteSingleRegisterAsync(ackAddress, 0, CancellationToken.None).ConfigureAwait(false); }
        catch { }
    }
}
