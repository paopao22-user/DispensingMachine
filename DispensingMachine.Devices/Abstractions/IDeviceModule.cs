namespace DispensingMachine.Devices.Abstractions;

/// <summary>
/// 所有设备的统一契约。
///
/// 轴、气缸、扫码枪、视觉 —— 上层不需要知道它们各自是什么，
/// 只要问三件事：你叫什么、你准备好了没、你有没有报警。
/// 新增一种设备（比如真空吸嘴），实现这三个属性就能接进流水线。
/// </summary>
public interface IDeviceModule
{
    /// <summary>设备名称，用于日志和报警来源标注。</summary>
    string Name { get; }

    /// <summary>是否处于可执行就绪状态（使能、回零、无报警、不忙）。</summary>
    bool IsReady { get; }

    /// <summary>是否存在未清除的报警。</summary>
    bool HasAlarm { get; }

    /// <summary>清除报警。返回是否清除成功。</summary>
    bool ResetAlarm();
}
