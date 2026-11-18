namespace DispensingMachine.Core.Alarms;

/// <summary>报警级别。决定界面用什么颜色、是否需要立即停机。</summary>
/// <remarks>
/// 只回答"要不要停、停到什么程度"，不回答"是哪个部件坏了"—— 部件身份由 AlarmInfo.Source 背着，
/// 塞进枚举的话每加一台设备就得加一个值。
/// 依赖方：AlarmCodes.DefaultLevel 按码段给出兜底级别；界面与报警灯只读这个值，反过来不成立，
/// 这里不知道 UI 用什么颜色，颜色是 UI 自己的事。
/// 三个值按严重程度升序排是刻意的，为什么这么做而不是随手排：界面排序和"取当前最高级别"都直接比大小，
/// 谁重排一次，就是一处不会报错的静默事故。
/// </remarks>
public enum AlarmLevel
{
    Warning,   // 警告：可继续运行，但需关注
    Error,     // 错误：当前动作中止
    Fatal      // 致命：整机停机，必须人工干预
}

/// <summary>
/// 报警码字典。
///
/// 分段的用意：运维看到第一位数字就知道该叫谁。
///   1xxx 电气/伺服    2xxx 气动    3xxx 安全
///   4xxx 通讯         5xxx 工艺    6xxx 模式与操作
///
/// 只做"码值 ↔ 文案 ↔ 兜底级别"的对照表，不判断什么时候该报哪个码 ——
/// 判断散落在 WorkstationController 的各个工步里，那才是唯一知道现场条件的地方；
/// 也不负责去重、确认、落库，那些属于报警历史与日志层。
///
/// 依赖方向：Core 被 Devices / Communication / App 引用，这里不反向 using 其中任何一层，
/// 所以新增一个码只需要动这一个文件，不会牵动上面任何一个调用点。
///
/// 用 const int 而不是 enum 是刻意的：码值要原样写进日志、报给 MES、跟 PLC 报警字对齐，
/// 数字本身就是对现场和对接方的契约；换成 enum 后有人在中间插一个值，历史日志里的码就全线错位。
/// </summary>
public static class AlarmCodes
{
    // —— 1xxx 轴与伺服 ——
    public const int AxisMoveTimeout = 1001;
    public const int AxisServoAlarm = 1002;
    public const int AxisPositiveLimit = 1003;
    public const int AxisNegativeLimit = 1004;
    public const int AxisNotHomed = 1005;

    // —— 2xxx 气动 ——
    public const int CylinderExtendTimeout = 2001;
    public const int CylinderRetractTimeout = 2002;
    public const int AirPressureLow = 2003;
    public const int CylinderSensorConflict = 2004;

    // —— 3xxx 安全 ——
    public const int EmergencyStopPressed = 3001;
    public const int SafetyDoorOpened = 3002;

    // —— 4xxx 通讯 ——
    public const int PlcCommTimeout = 4001;
    public const int ScannerTimeout = 4002;

    // —— 5xxx 工艺 ——
    public const int ProductMissing = 5001;
    public const int BarcodeEmpty = 5002;
    public const int QualityDefective = 5003;
    public const int RecipeNotLoaded = 5004;

    // —— 6xxx 模式与操作 ——
    public const int ModeConflict = 6001;
    // 6002~6004 空着不复用：码值一旦发出去就进了历史日志和对接方手里，
    // 补一个旧号看着整齐，实际会让几个月前的日志指向另一种故障。
    public const int AutoStartConditionUnmet = 6005;

    /// <summary>
    /// 报警码给现场看时只有一串数字等于没信息，所以文案统一在这一处翻译：
    /// 界面列表、日志、诊断结论都来调它，保证同一个码在任何地方措辞一致，
    /// 而不是各界面各写一份字符串、出了事故再花时间核对是不是同一件事。
    /// </summary>
    /// <param name="code">只接受上面登记过的码。PLC 侧新加、上位机还没升级的码会落进 default 分支，不抛异常。</param>
    /// <returns>永远不返回 null，调用方不必判空。但拿到"未知报警"不是可忽略的兜底文案，
    /// 它意味着上位机字典落后于 PLC，要按"字典缺失"去查 PLC 侧码表，不能当成真实报警名往上报。</returns>
    public static string Describe(int code) => code switch
    {
        AxisMoveTimeout => "轴运动超时",
        AxisServoAlarm => "伺服报警",
        AxisPositiveLimit => "触发正限位",
        AxisNegativeLimit => "触发负限位",
        AxisNotHomed => "轴未回零",
        CylinderExtendTimeout => "气缸伸出超时",
        CylinderRetractTimeout => "气缸缩回超时",
        AirPressureLow => "气压不足",
        CylinderSensorConflict => "气缸双传感器冲突",
        EmergencyStopPressed => "急停按钮按下",
        SafetyDoorOpened => "安全门打开",
        PlcCommTimeout => "PLC 通讯超时",
        ScannerTimeout => "扫码枪无应答",
        ProductMissing => "工位缺料",
        BarcodeEmpty => "条码为空或污损",
        QualityDefective => "产品质量不良",
        RecipeNotLoaded => "未加载配方",
        ModeConflict => "模式冲突",
        AutoStartConditionUnmet => "自动启动条件不满足",
        _ => "未知报警"
    };

    /// <summary>
    /// 每个报警点自己写 level 容易越写越随意，从 PLC 上来的码更是只有数字，
    /// 所以需要一张"码段 → 兜底级别"的表：级别要调整时只改这里，
    /// 而不是去翻几十个 RaiseAlarm 调用点、还得担心漏了哪个。
    /// </summary>
    /// <param name="code">未登记的码也会走到这里，按码段兜底，不抛异常、不需要调用方先校验。</param>
    /// <returns>永远不返回 null。但落到 _ 分支只说明"不属于 3xxx/4xxx"，不代表这条报警真能继续运行 ——
    /// 涉及人身安全的新码必须补进映射，不能依赖兜底。</returns>
    public static AlarmLevel DefaultLevel(int code) => code switch
    {
        // 3xxx 与 4xxx 都判 Fatal，却没有合并成 >= 3000 and < 5000：
        // 安全段和通讯段往后会各自调整（比如通讯短时抖动可能降成 Warning），
        // 合并了就是改一处、误伤另一处。
        // 通讯超时之所以算 Fatal 而不是 Error：上位机一旦读不到 PLC，就失去对设备当前动作的判断权，
        // 这时候继续跑自动等于闭眼开车，停机比猜测安全。
        >= 3000 and < 4000 => AlarmLevel.Fatal,
        >= 4000 and < 5000 => AlarmLevel.Fatal,
        _ => AlarmLevel.Error
    };
}

/// <summary>一条报警记录。</summary>
/// <remarks>
/// 只承载"一条报警从触发到恢复"的可绑定状态，不负责判定与去重 ——
/// 谁该报警、同一个码连报十次要不要合并成一条，是 WorkstationController 和日志层的事。
/// 依赖 DispensingMachine.ObservableObject 提供变更通知，被界面的 AlarmHistory 直接绑定；
/// 它不引用 Devices / Communication / App 里的任何类型。
/// 之所以把 Duration 做成"算出来的属性"而不是每条报警挂一个定时器刷新，是因为：
/// 十条报警就是十个定时器，还得处理 UI 线程调度；算出来的属性只在界面取值的瞬间算一次，几乎不要钱，
/// 而且定时器一旦忘了停，报警消掉了还在后台刷。
/// </remarks>
public sealed class AlarmInfo : ObservableObject
{
    // 恢复时必须连着写 IsActive = false 与 ClearTime = DateTime.Now 两行（WorkstationController 里是成对出现的）：
    // 只写前者，Duration 会继续实时增长，界面看着像这条报警还没消。
    private bool _isActive = true;
    // null 有语义：表示这条报警还活着，别当成"没有时间"随手补个 DateTime.Now，那样 Duration 会瞬间归零。
    private DateTime? _clearTime;

    // 下面五个属性一律 init 而不是 set：报警进了 AlarmHistory 就是要落日志的历史记录，
    // 允许事后改码值，等于让界面显示 A、日志里记的是 B，追责任时谁都不认。
    public int Code { get; init; }
    public string Message { get; init; } = string.Empty;
    // 默认字符串必须和 WorkstationController.RaiseAlarm 的 source 默认值保持同一个"整机"：
    // 两边不一致，界面按来源分组时会凭空多出一栏"整机"和一栏空白。
    public string Source { get; init; } = "整机";
    // 默认 Error 而不是 Warning：漏报的代价远大于多停一次，宁可让操作工多确认一下。
    public AlarmLevel Level { get; init; } = AlarmLevel.Error;
    // 用本地时间不用 UtcNow：这个时间要跟操作工的手表、PLC 的钟对得上，
    // 现场没人做时区换算，存 UTC 只会让对表时算出几小时的假间隔。
    public DateTime TriggerTime { get; init; } = DateTime.Now;

    // IsActive 和 ClearTime 必须一起写：它们各自只负责一件事（还要不要算、算到哪一刻），
    // 单改一个不会报错，只是界面上那条报警的时间从此不动或者永远在涨。
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public DateTime? ClearTime
    {
        get => _clearTime;
        set
        {
            // 这一句不能省：Duration 是算出来的，改 ClearTime 它自己不会通知界面，
            // 少了它，界面上的"持续 3.2s"会永远停在恢复前的那个数。
            if (SetProperty(ref _clearTime, value))
                OnPropertyChanged(nameof(Duration));
        }
    }

    /// <summary>持续时长。未恢复时实时算，恢复后固定 —— 一个属性两种用途。</summary>
    /// <remarks>
    /// 取的是本机时钟，而 TriggerTime 可能来自 PLC 上报：两边没对时时这里不做钳位，算成负数或几小时也照原样返回。
    /// 为什么钳到 0 反而不对：钳位会把"没对时"这个真问题藏成一条看起来正常的 0s 记录，
    /// 只有让现场看到荒谬的数字，才会有人去查时钟。
    /// </remarks>
    public TimeSpan Duration => (ClearTime ?? DateTime.Now) - TriggerTime;

    // 日志落盘和界面列表刻意共用同一个字符串：现场对号时看到的就是同一串字符。
    // 各写各的格式，出了事故先得花时间判断"日志里这条和界面上那条是不是同一件事"。
    // ERR- 前缀是运维在日志里直接搜的锚点，改格式等于废掉他们手上的检索习惯。
    public string Display => $"[{TriggerTime:HH:mm:ss}] ERR-{Code} {Message}";

    // "持续 / 历时" 是颜色之外的第二重区分手段：黑白截图、色弱的人、隔着两米看屏幕都还能分辨。
    // F1 只留一位小数：秒级精度对现场足够，多给几位只会让列宽随着数字来回跳。
    public string DurationText => IsActive
        ? $"持续 {Duration.TotalSeconds:F1}s"
        : $"历时 {Duration.TotalSeconds:F1}s";
}
