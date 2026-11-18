namespace DispensingMachine.Devices.Pneumatic;

/// <summary>
/// 气缸状态。
///
/// 气缸只有两个磁簧开关（伸出到位、缩回到位），没有位置反馈，
/// 所以"动没动到位"只能靠这两个开关判断 —— 这也是它最容易卡住的地方。
/// </summary>
public sealed class CylinderModel : ObservableObject
{
    private bool _extendCommand;
    private bool _retractCommand;
    private bool _extendedSensor;
    private bool _retractedSensor = true;

    public CylinderModel(string name) => Name = name;

    public string Name { get; }

    /// <summary>伸出电磁阀输出。</summary>
    public bool ExtendCommand
    {
        get => _extendCommand;
        set => SetProperty(ref _extendCommand, value);
    }

    /// <summary>缩回电磁阀输出。</summary>
    public bool RetractCommand
    {
        get => _retractCommand;
        set => SetProperty(ref _retractCommand, value);
    }

    /// <summary>伸出到位磁簧开关。</summary>
    public bool ExtendedSensor
    {
        get => _extendedSensor;
        set => SetProperty(ref _extendedSensor, value);
    }

    /// <summary>缩回到位磁簧开关（默认在缩回位）。</summary>
    public bool RetractedSensor
    {
        get => _retractedSensor;
        set => SetProperty(ref _retractedSensor, value);
    }

    /// <summary>故障注入：打开后气缸永远到不了位，用来验证超时看门狗。</summary>
    public bool SimulateFailure { get; set; }
}
