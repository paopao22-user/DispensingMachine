namespace DispensingMachine.Core.Recipes;

/// <summary>
/// 配方（一个产品型号对应一份工艺参数）。
///
/// 为什么要有配方？因为换产品时不能靠改代码或改界面输入框，
/// 必须"载入一份文件 → 整组参数一起生效"，否则现场改一半忘了另一半，就会撞机。
///
/// 这一层只负责把一组工艺参数装进一个可绑定、可序列化的壳子里；
/// 不负责判断参数能不能用 —— 越界、位置顺序、速度上限的拦截全在 RecipeManager.Validate。
///
/// 依赖方向是单向的：只依赖 Devices 层的 ObservableObject 拿属性变更通知，不引用任何 WPF 类型，
/// 所以以后换界面框架（WPF/Avalonia/控制台）这份配方不用动；
/// 往上被 RecipeManager 收进集合、挑出当前配方，被 WorkstationController 在工步里读着执行，
/// 也被 MainWindowViewModel 直接绑定到界面。
///
/// 为什么每个参数写成属性 + SetProperty，而不是 public 字段：
/// 配方编辑要双向绑定到输入控件，控件改值必须立刻回到模型、模型改值也要刷新控件；
/// 用字段就没有 PropertyChanged，界面会一直停在上一份配方的旧值上。
/// </summary>
public sealed class RecipeModel : ObservableObject
{
    // 下面这些初值是"新建一份空配方"时的保守占位值，不是推荐工艺值：
    // 0 ≤ 50 ≤ 120 刚好满足 Validate 的单调要求（等待位 ≤ 扫码位 ≤ 加工位），
    // 顺手 new 出来就能过校验，不会一上手就是一份非法配方。
    // 注意 CylinderTimeoutMs 的 3000 和 WorkstationController 里 `?? 3000` 的兜底值必须一致，改一处就得改两处。
    private string _recipeName = "默认配方";
    private string _productCode = "P-0001";
    private double _waitPosition = 0;
    private double _scanPosition = 50;
    private double _workPosition = 120;
    private double _moveVelocity = 200;
    private int _clampDelayMs = 200;                // ms，夹紧后保压
    private int _dispenseTimeMs = 300;              // ms，点胶工艺时间
    private int _cylinderTimeoutMs = 3000;          // ms，气缸超时
    private int _scanTimeoutMs = 2000;              // ms，扫码超时

    /// <summary>
    /// 配方的业务主键：RecipeManager 的查找与选中全靠这个名字做 FirstOrDefault。
    /// 名字一改，ApplyEditingCopy 会找不到目标而整份拒绝写回，
    /// 所以它实际是只读标识，不是能随便改的显示名 —— 真要改名等于新建一份配方。
    /// </summary>
    public string RecipeName
    {
        get => _recipeName;
        set => SetProperty(ref _recipeName, value);
    }

    /// <summary>
    /// 产品型号，只用于追溯和日志显示。
    /// 它不参与 Validate 的任何一条判定，也不能拿来定位配方，允许为空。
    /// </summary>
    public string ProductCode
    {
        get => _productCode;
        set => SetProperty(ref _productCode, value);
    }

    /// <summary>
    /// 审计字段：谁在什么时候动了这份配方。
    /// 别把 UpdatedTime 当成"这份配方何时被载入"——从 JSON 反序列化时它会被文件里的旧值覆盖；
    /// 另外 UpdatedBy 目前没有任何写回路径给它赋值，永远停在构造时的 "operator"，
    /// 现场真要追溯改配方的人，得先补上写回处的赋值。
    /// </summary>
    public DateTime UpdatedTime { get; set; } = DateTime.Now;
    public string UpdatedBy { get; set; } = "operator";

    // —— 位置参数（mm）——
    // 这三个不是三个互不相干的参数：Validate 强制 等待位 ≤ 扫码位 ≤ 加工位，
    // 单独改其中一个就可能让整份配方在载入时被整体拒绝，所以界面上要按顺序校验，不能逐个放行。
    public double WaitPosition
    {
        get => _waitPosition;
        set => SetProperty(ref _waitPosition, value);
    }

    public double ScanPosition
    {
        get => _scanPosition;
        set => SetProperty(ref _scanPosition, value);
    }

    public double WorkPosition
    {
        get => _workPosition;
        set => SetProperty(ref _workPosition, value);
    }

    // —— 运动参数 ——
    /// <summary>
    /// 轴运动速度，单位 mm/s，必须大于 0 且不超过 MachineConfig.MaxAllowableVelocity。
    /// WorkstationController 每到一个移动工步才把它刷进轴模型，
    /// 所以改这个值只影响下一次移动，不会中途改掉正在走的这一段速度。
    /// </summary>
    public double MoveVelocity
    {
        get => _moveVelocity;
        set => SetProperty(ref _moveVelocity, value);
    }

    // —— 工艺参数 ——
    /// <summary>
    /// 夹紧后的保压时间，单位 ms；填 0 表示不等待。
    /// 保压是为等气压稳定再动轴，省掉它工件容易被带偏，现场遇到"位置老是差一点"先查这一项。
    /// </summary>
    public int ClampDelayMs
    {
        get => _clampDelayMs;
        set => SetProperty(ref _clampDelayMs, value);
    }

    /// <summary>
    /// 点胶时间，单位 ms。
    /// 真机上出胶量由 PLC 的定时器决定，上位机只是等这段时间；
    /// 仿真模式下没有 PLC 定时器，就靠这个值模拟物理过程，所以它不能填 0，否则仿真出胶等于瞬时完成。
    /// </summary>
    public int DispenseTimeMs
    {
        get => _dispenseTimeMs;
        set => SetProperty(ref _dispenseTimeMs, value);
    }

    // —— 超时门限 ——
    /// <summary>
    /// 气缸动作超时门限，单位 ms，伸出和缩回共用这一个值。
    /// 超时只报警停机、不做重试 —— 磁簧开关在门限内没闭合，多半是气压不足或机械卡死，
    /// 自动重试会把问题变成撞机；手动夹紧/松开在没载入配方时也拿这个值兜底。
    /// </summary>
    public int CylinderTimeoutMs
    {
        get => _cylinderTimeoutMs;
        set => SetProperty(ref _cylinderTimeoutMs, value);
    }

    /// <summary>
    /// 扫码超时门限，单位 ms，从下发扫码命令开始计时。
    /// 填得太短会在枪头刚对准时就误报，填得太长则停机排查变慢，别为了"少报警"一味放大。
    /// </summary>
    public int ScanTimeoutMs
    {
        get => _scanTimeoutMs;
        set => SetProperty(ref _scanTimeoutMs, value);
    }

    /// <summary>
    /// 深拷贝。
    /// 用途：界面上编辑的是副本，点"应用"才替换正在运行的配方 ——
    /// 操作员改到一半取消，运行中的配方一点不受影响（编辑/运行分离）。
    ///
    /// 为什么用 MemberwiseClone 而不是手写逐字段复制：
    /// 现有成员全是值类型和不可变 string，浅拷贝与深拷贝在此等价，手写十几个字段只会漏；
    /// 但将来一旦加入引用类型成员（点位列表、子对象），这里必须改成显式复制，
    /// 否则"副本"与运行中的配方共用同一个对象，编辑隔离就失效了。
    ///
    /// 调用方注意：副本的 RecipeName 必须原样保留，RecipeManager 靠它找回原配方。
    /// </summary>
    public RecipeModel Clone() => (RecipeModel)MemberwiseClone();
}
