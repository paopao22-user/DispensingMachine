using System.Diagnostics;

namespace DispensingMachine.Devices.Abstractions;

/// <summary>
/// 动作超时看门狗。
///
/// 全项目所有"等某个条件成立"的地方，最终都落到这一个方法上：
///   等气缸到位、等轴到位、等扫码枪出结果、等传感器有信号。
///
/// 为什么不用 Task.Delay(超时时间) 然后检查一次？
///   因为那样"超时"和"完成"的判定精度都只有一次采样，
///   中间万一条件刚好成立又失效，就会漏判。
///   轮询式检查既能及时发现完成，也能精确控制超时。
/// </summary>
public static class AsyncTimeoutHelper
{
    /// <summary>同步条件版本（读本地状态，比如仿真设备的传感器）。</summary>
    public static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        int intervalMs = 50,
        CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();                  // 开始计时

        while (!token.IsCancellationRequested)          // 查询token是否被取消
        {
            if (condition()) return true;               // 条件满足,返回true
            if (sw.Elapsed > timeout) return false;     // 超时，返回false

            await Task.Delay(intervalMs, token);        // 轮询
        }

        return false;
    }

    /// <summary>异步条件版本（条件本身要读 PLC，每次判断都是一次网络往返）。</summary>
    public static async Task<bool> WaitUntilAsync(
        Func<Task<bool>> asyncCondition,
        TimeSpan timeout,
        int intervalMs = 50,
        CancellationToken token = default)
    {
        var sw = Stopwatch.StartNew();                      // 开始计时

        while (!token.IsCancellationRequested)              // 查询token是否被取消
        {
            if (await asyncCondition()) return true;        // 条件满足 返回true
            if (sw.Elapsed > timeout) return false;         // 超时，返回false

            await Task.Delay(intervalMs, token);            // 轮询
        }

        return false;
    }
}
