using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DispensingMachine.Communication.Drivers;
using DispensingMachine.Communication.Protocol;
using DispensingMachine.Communication.Services;
using DispensingMachine.Core.Alarms;
using DispensingMachine.Core.Configuration;
using DispensingMachine.Core.Recipes;
using DispensingMachine.Core.Scheduling;
using DispensingMachine.Devices.Motion;
using DispensingMachine.Devices.Peripherals;
using DispensingMachine.Devices.Pneumatic;
using Prism.Commands;
using Prism.Mvvm;

namespace DispensingMachine.App.ViewModels;

/// <summary>
/// 主界面 ViewModel。
///
/// 它的职责只有三件，一行界面代码都不应该有：
///   1. 把设备状态暴露给界面（绑定源）
///   2. 把界面操作翻译成控制器命令（Command）
///   3. 维护界面自己的状态（提示文字、日志、选中项）
///
/// 注意这里【没有】任何 MessageBox、没有控件引用、没有 Dispatcher 相关逻辑
/// （只有在仿真产品进料时用了一下定时器，那是仿真驱动，不是界面逻辑）。
/// 这就是 MVVM 的意义：换一套界面，这个文件可以原样复用。
///
/// 边界再划一次：工步推进、八重起跑线自检、报警裁定都归 WorkstationController，
/// 寄存器地址、四步握手、轮询节拍都归 Communication 层 —— 这一层只做搬运和翻译，不产生任何工艺决策。
///
/// 依赖方向单向（App → ViewModel → Core / Communication / Devices）：
///   向下依赖 WorkstationController、RecipeManager、MachineConfig、IPlcDriver、PlcPollingService
///   和四个设备模块，全部由 App.RegisterTypes 注入，这里不 new 任何设备、也不拼连接参数；
///   向上被 MainWindow.xaml 的绑定消费，并由 MainWindow.xaml.cs 的 Loaded 回调触发 InitializeAsync。
///   Core 与 Communication 都不知道 MainWindowViewModel 存在，工步逻辑因此能脱离界面跑。
///
/// 为什么仿真进料、状态回写这两个定时器放在 ViewModel，而不是塞进 WorkstationController：
///   这两件事只在脱机演示时成立 —— 真机上状态字由 PLC 产生、工件由上游机构送。
///   放进控制器会让演示代码和现场代码纠缠在一起，将来删仿真就得动主流程；
///   放在这里，删掉这两个定时器即可，控制器和通讯层一行都不用改。
/// </summary>
public sealed class MainWindowViewModel : BindableBase
{
    private readonly WorkstationController _controller;     // 
    private readonly RecipeManager _recipeManager;
    private readonly MachineConfig _config;
    private readonly IPlcDriver _plcDriver;
    private readonly PlcPollingService _polling;
    private readonly DispatcherTimer _productFeedTimer;
    private readonly DispatcherTimer _plcSyncTimer;

    // 进料延时计数：连续数够几拍才认为"上游把料送到了"。不是给界面看的，
    // 只用来把仿真送料做成有时序的过程，而不是瞬间把传感器点亮
    private int _productFeedTicks;

    /// <summary>
    /// 九个依赖全部从容器注入，为的是让"界面绑定的设备"和"控制器命令的设备"是同一个实例：
    /// 这里若图省事自己 new AxisModule、new SensorModule，界面动的是这根轴、工步读的是另一根轴，
    /// 现场症状就是"轴明明动了，流程却报没到位"，几乎无从反查。
    ///
    /// 两个定时器在构造函数里就开始走，不等 InitializeAsync：
    /// 让仿真进料先跑起来，操作员点"启动"时料已经在工位上；
    /// 否则按了启动还得干等一两秒料才送到，现场第一反应是"按钮没反应"，接着就是连点或按停止。
    /// </summary>
    /// <param name="controller">必须和 XAML 绑定、全部命令共用同一个实例；重新 new 一次会丢掉工步、报警历史和生产计数。</param>
    /// <param name="config">构造函数里立刻要读行程上限和通讯地址，所以传进来之前配置必须已经填好，不能指望事后补。</param>
    /// <param name="polling">只在这里订阅一次；重复订阅会让同一条快照刷两遍界面，心跳数字会跳着走。</param>
    public MainWindowViewModel(
        WorkstationController controller,
        RecipeManager recipeManager,
        MachineConfig config,
        IPlcDriver plcDriver,
        PlcPollingService polling,
        AxisModule axis,
        CylinderModule cylinder,
        ScannerModule scanner,
        SensorModule sensor)
    {
        _controller = controller;       // 
        _recipeManager = recipeManager;
        _config = config;
        _plcDriver = plcDriver;
        _polling = polling;

        Axis = axis;
        Cylinder = cylinder;
        Scanner = scanner;
        Sensor = sensor;

        _recipeManager.LoadDefaults();
        Axis.Model.MinPosition = _config.XMinPosition;
        Axis.Model.MaxPosition = _config.XMaxPosition;
        // 200 是没加载到配方时的兜底速度（mm/s）：别改成 0 —— 轴会原地不动，却一直报"运动超时"，
        // 而这种故障最难反查，根因其实在参数上
        Axis.Model.Velocity = _recipeManager.CurrentRecipe?.MoveVelocity ?? 200;
        SelectedRecipeName = _recipeManager.CurrentRecipe?.RecipeName ?? string.Empty;

        // 用仿真驱动器把默认位置摆在等待位，避免一开机就是 0 号位看着像没初始化
        Axis.Model.CurrentPosition = _recipeManager.CurrentRecipe?.WaitPosition ?? 0;

        StartCommand = new DelegateCommand(ExecuteStart);
        PauseCommand = new DelegateCommand(ExecutePause);
        ResumeCommand = new DelegateCommand(ExecuteResume);
        StopCommand = new DelegateCommand(ExecuteStop);
        ResetCommand = new DelegateCommand(ExecuteReset);
        EmergencyStopCommand = new DelegateCommand(ExecuteEmergencyStop);
        NextStepCommand = new DelegateCommand(() => _controller.NextStep());
        HomeCommand = new DelegateCommand(async () => await RunGuardedAsync(() => _controller.ManualHomeAsync()));
        ClampCommand = new DelegateCommand(async () => await RunGuardedAsync(() => _controller.ManualClampAsync()));
        UnclampCommand = new DelegateCommand(async () => await RunGuardedAsync(() => _controller.ManualUnclampAsync()));
        // 50 是没加载到配方时的兜底扫码位（mm）：只保证手动"移动到扫码位"有个确定目标，不参与自动流程 ——
        // 自动流程的目标一律取自配方，取不到配方它宁可直接报警，也不替现场瞎猜一个位置
        MoveToScanCommand = new DelegateCommand(async () => await RunGuardedAsync(
            () => _controller.ManualMoveAsync(_recipeManager.CurrentRecipe?.ScanPosition ?? 50)));
        LoadRecipeCommand = new DelegateCommand(ExecuteLoadRecipe);
        ApplyRecipeCommand = new DelegateCommand(ExecuteApplyRecipe);
        ConnectPlcCommand = new DelegateCommand(async () => await ConnectPlcAsync());

        _controller.PropertyChanged += (_, e) =>  // ② 事件驱动：工步/状态一变就立刻跑一次   e 是 PropertyChangedEventArgs
        {
            if (e.PropertyName == nameof(WorkstationController.Step) ||     // 判断"变的是不是 Step 或 State"
                e.PropertyName == nameof(WorkstationController.State))
            {
                AutoFeedProduct();                                       // 是 → 跑一次送料检查
            }
            else if (e.PropertyName == nameof(WorkstationController.LastHandshakeText))     // 判断变的是不是 握手信息
            {
                // 每次点胶握手的耗时直接进日志，现场能看出通讯质量有没有变差
                Log($"PLC 握手：{_controller.LastHandshakeText}");         // 变的是握手信息 → 写日志
            }
        };

        // 仿真进料：每 500ms 检查一次，工位空闲且没料就"送来"一个工件                       // ① 周期驱动：每 500ms 一拍  定时器
        _productFeedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };  // 对象初始化器的用法
        _productFeedTimer.Tick += (_, _) => AutoFeedProduct();  // 订阅事件，s 和 e 两个都用 _ 表示"我不需要这两个参数"，因为AutoFeedProduct没有参数因此这里需要弃元,订阅AutoFeedProduct 自动送料
        _productFeedTimer.Start();       // 不调这句，Tick 永远不会触发 —— 定时器只是被创建了，没启动

        // 把设备真实状态同步到仿真 PLC 的寄存器区，
        // 这样轮询引擎读回来的就是"看起来像真的"数据，整条通讯链路才算闭环。
        _plcSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _plcSyncTimer.Tick += (_, _) => SyncStateToPlcRegisters();
        _plcSyncTimer.Start();

        _polling.SnapshotUpdated += OnSnapshotUpdated;
        _polling.PollingError += OnPollingError;
    }

    // ==================== 绑定源 ====================

    // Controller / Axis / Cylinder 这些是直接把设备对象交给 XAML 绑 Model.*，没有逐个字段包一层 VM 属性：
    // 字段几十个，包一层只是多出几十行纯转发，改个名字还得改两处；
    // 代价是 XAML 里会出现业务名字（Controller.Step 之类），换一套界面时这部分绑定得跟着重写。
    // 真正需要主动通知界面的量（状态文字、PLC 状态、提示语）才在下面单独用私有字段 + SetProperty。

    public WorkstationController Controller => _controller;
    public AxisModule Axis { get; }
    public CylinderModule Cylinder { get; }
    public ScannerModule Scanner { get; }
    public SensorModule Sensor { get; }

    public ObservableCollection<RecipeModel> Recipes => _recipeManager.Recipes;
    public RecipeModel? CurrentRecipe => _recipeManager.CurrentRecipe;
    public ObservableCollection<string> Logs { get; } = new();
    public ObservableCollection<AlarmInfo> AlarmHistory => _controller.AlarmHistory;

    private string _statusMessage = "设备就绪，等待启动";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    private string _selectedRecipeName = string.Empty;
    public string SelectedRecipeName
    {
        get => _selectedRecipeName;
        set => SetProperty(ref _selectedRecipeName, value);
    }

    private string _plcStatusText = "PLC 未连接";
    public string PlcStatusText
    {
        get => _plcStatusText;
        set => SetProperty(ref _plcStatusText, value);
    }

    private string _plcSnapshotText = "—";
    public string PlcSnapshotText
    {
        get => _plcSnapshotText;
        set => SetProperty(ref _plcSnapshotText, value);
    }

    private string _plcHeartbeatText = "0";
    public string PlcHeartbeatText
    {
        get => _plcHeartbeatText;
        set => SetProperty(ref _plcHeartbeatText, value);
    }

    private string _plcLatencyText = "—";
    public string PlcLatencyText
    {
        get => _plcLatencyText;
        set => SetProperty(ref _plcLatencyText, value);
    }

    /// <summary>界面上显示当前工步进度条用（0~8）。</summary>
    // 工步号是 0/10/…/80 的刻度（留空档是为了以后能在半步位置插工步而不必重编号），
    // 所以这里要除以 10；把 Step 原样丢给进度条会立刻爆表。
    // 另外它是个没有变更通知的计算属性，真要绑进 XAML，得在 Step 变化时自己 RaisePropertyChanged(nameof(StepProgress))。
    public int StepProgress => _controller.Step / 10;

    // ==================== 命令 ====================

    // 这些命令一律不带 CanExecute，按钮永远可点，能不能执行由控制器裁定（启动自检、运行中禁止切配方等）。
    // 不拿 IsRunning 去禁用按钮，是因为禁用只是视觉上的阻挡：现场还有外部按钮盒、快捷键和 PLC 侧触发，
    // 真正的准入必须落在控制器里；界面只负责把拒绝原因原样显示给操作员。

    public DelegateCommand StartCommand { get; }
    public DelegateCommand PauseCommand { get; }
    public DelegateCommand ResumeCommand { get; }
    public DelegateCommand StopCommand { get; }
    public DelegateCommand ResetCommand { get; }
    public DelegateCommand EmergencyStopCommand { get; }
    public DelegateCommand NextStepCommand { get; }
    public DelegateCommand HomeCommand { get; }
    public DelegateCommand ClampCommand { get; }
    public DelegateCommand UnclampCommand { get; }
    public DelegateCommand MoveToScanCommand { get; }
    public DelegateCommand LoadRecipeCommand { get; }
    public DelegateCommand ApplyRecipeCommand { get; }
    public DelegateCommand ConnectPlcCommand { get; }

    // ==================== 初始化 ====================

    /// <summary>窗口加载后调用：连仿真 PLC 并启动轮询。</summary>
    /// <remarks>
    /// 之所以挂在 Loaded 上、而不是放进构造函数：窗口先画出来，连接万一失败，
    /// 界面上至少有一句"连接失败"可看，而不是启动即白屏。
    /// 内部已经吞掉异常（失败只落日志和状态文字），所以 MainWindow 里 await 它不会把窗口拖崩。
    /// </remarks>
    public async Task InitializeAsync()
    {
        await ConnectPlcAsync();
        Log("系统启动完成，通讯层已进入常态轮询");
    }

    // ==================== 命令实现 ====================

    /// <summary>
    /// 把"启动"交给控制器裁定，而不是在这里逐条判 Mode / 报警 / 配方：
    /// 自检规则只许有一份，否则界面和外部按钮盒会给出不一样的拒绝理由，操作员就无法信任任何一边。
    /// </summary>
    /// <remarks>
    /// 拒绝时 message 是逐条换行的清单，界面必须保留原始换行；这里只往日志补一句"启动被拒绝"，
    /// 不能把清单压成一行 —— 压了就等于告诉操作员"启动失败"，却不告诉他是哪一条没过。
    /// </remarks>
    private void ExecuteStart()
    {
        if (_controller.StartAuto(out string message))
        {
            Log(message);       // 记日记
            StatusMessage = message;
        }
        else
        {
            // 启动被拒时把"哪几条没过"完整展示出来 —— 这就是八重起跑线的现场效果
            Log("启动被拒绝");
            StatusMessage = message;
        }
    }

    /// <summary>
    /// 必须紧跟一句"当前工步结束后生效"的反馈：Pause 只是置标志，按下按钮机构不会立刻停，
    /// 少了这行提示，操作员会以为没按上，反复点，最后去按急停。
    /// </summary>
    private void ExecutePause()
    {
        _controller.Pause();
        Log("请求暂停（在当前工步结束后生效）");
    }

    /// <summary>
    /// 恢复按钮在"其实没暂停"时也要点得动：操作员对设备状态的判断经常慢半拍，
    /// 这时拒绝恢复只会让人以为程序卡死；控制器那边连点无副作用，所以这里不做任何前提校验。
    /// </summary>
    private void ExecuteResume()
    {
        _controller.Resume();
        Log("恢复运行");
    }

    /// <summary>
    /// 停止和急停是两个按钮、两条路径：停止让流程在工步边界退出、设备整体仍可继续用，
    /// 所以这里不能顺手清报警、也不能把状态改写成急停 —— 那是急停按钮的职责。
    /// </summary>
    private void ExecuteStop()
    {
        _controller.Stop();
        Log("请求停止");
    }

    /// <summary>
    /// 复位只做控制器允许的那一部分（清报警、回就绪态）：急停未物理释放、或设备仍在运行，控制器都会拒绝，
    /// 而拒绝原因必须在界面上显示出来，否则操作员会一直点复位，并认定程序卡死了。
    /// </summary>
    private void ExecuteReset()
    {
        bool ok = _controller.Reset(out string message);
        StatusMessage = message;
        Log(message);
    }

    /// <summary>
    /// 急停不加二次确认、不做防抖、不查状态：任何一次弹窗确认都可能让机构多走一段。
    /// "断轴动力 → 停流程 → 报致命报警"这个顺序由控制器保证，这里绝不能自己先 Stop 或先改状态字。
    /// </summary>
    private void ExecuteEmergencyStop()
    {
        _controller.TriggerEmergencyStop();
        Log("急停触发！");
    }

    /// <summary>
    /// 切换配方要把设备此刻的真实状态一起交给 RecipeManager，让它自己判"运行中禁止切换"：
    /// 界面这边没有第二份状态可以用来拦，也拦不住其他入口发来的切换请求。
    /// </summary>
    /// <remarks>
    /// 选中项为空时直接返回、不写提示 —— 列表还没点过，不该算成一次失败操作。
    /// 切换成功后要把速度刷进轴模型：配方里的 MoveVelocity 要等下一次运动才被读走，
    /// 不在此时同步，界面显示的"当前速度"会和实际跑的速度差一档。
    /// </remarks>
    private void ExecuteLoadRecipe()
    {
        if (string.IsNullOrWhiteSpace(SelectedRecipeName)) return;  // ① 空值直接返回（列表还没点过，不算失败）

        bool ok = _recipeManager.SelectRecipe(                      // 切换配方
            SelectedRecipeName, _controller.State, _config, out string message);

        StatusMessage = message;                                    // ← 界面顶部状态栏显示
        Log(message);                                               // ← 日志面板加一行

        if (ok && _recipeManager.CurrentRecipe is not null)         // 切换配方成功并且当前配方不为空
        {
            Axis.Model.Velocity = _recipeManager.CurrentRecipe.MoveVelocity;    // ← 立刻刷速度
            RaisePropertyChanged(nameof(CurrentRecipe));                        //  ← 通知界面换对象了
        }
    }

    /// <summary>
    /// 应用编辑副本要走 ApplyEditingCopy，而不是直接改 CurrentRecipe 上的字段：
    /// 校验不过时原配方一个字段都不能动，否则等于让半套参数上了产线。
    /// </summary>
    /// <remarks>没有当前配方就直接返回：此时界面上根本没有可编辑的对象，连提示都无从谈起。</remarks>
    private void ExecuteApplyRecipe()
    {
        RecipeModel? current = _recipeManager.CurrentRecipe;
        if (current is null) return;

        bool ok = _recipeManager.ApplyEditingCopy(current, _config, out string message);
        StatusMessage = message;
        Log(message);
    }

    /// <summary>
    /// 连接失败只写状态文字和日志、不往外抛：窗口已经在运行，抛出去就只剩一个崩溃对话框，
    /// 现场连"是不是网线没插"都看不出来，也没法再点一次"连接"。
    /// </summary>
    /// <remarks>
    /// 顺序不能反：先连驱动、再启动轮询。轮询第一拍就去读寄存器，
    /// 驱动还没连上时那一拍读到的是 IOException，界面刚打开就先自己报一轮"通讯异常"。
    /// </remarks>
    private async Task ConnectPlcAsync()
    {
        try
        {
            await _plcDriver.ConnectAsync(_config.PlcIpAddress, _config.PlcPort, _config.PlcStationId);// ① 连驱动
            await _polling.StartAsync();    // ② 启动轮询
            PlcStatusText = $"已连接 {_config.PlcIpAddress}:{_config.PlcPort}";    // ③ 改界面文字
            Log($"PLC 通讯已建立：{_config.PlcIpAddress}:{_config.PlcPort}");       // 记日志
        }
        catch (Exception ex)
        {
            PlcStatusText = "连接失败";
            Log($"PLC 连接失败：{ex.Message}");
        }
    }

    // ==================== 通讯回调 ====================

    /// <summary>
    /// 轮询快照更新事件的处理回调。
    /// 注意：由于 PlcPollingService 内部使用了 ConfigureAwait(false)，此回调会在后台线程池线程中被触发，
    /// 因此这里是 ViewModel 中少数必须显式借助 Dispatcher（UI 线程调度器）切回主线程的地方之一。
    /// </summary>
    /// <param name="sender">事件发送者（此处固定为 null，刻意不暴露 PlcPollingService 实例，防止越权操作）</param>
    /// <param name="snap">本拍采集并翻译好的不可变 PLC 状态快照</param>
    private void OnSnapshotUpdated(object? sender, PlcSnapshot snap)
    {
        // 1. 使用 Null-conditional Operator（空条件运算符 ?.）访问 Application.Current：
        //    在程序正在关闭（Application.Current 已销毁为 null）或单元测试环境下，安全跳过，避免抛出空引用异常。
        // 2. 调用 Dispatcher.BeginInvoke（异步投递），而不是 Invoke（同步死等）：
        //    - BeginInvoke 把下面这段 UI 更新任务打包放进 UI 线程的 Message Queue（消息队列）后，后台轮询线程立刻返回干下一轮；
        //    - 若用 Invoke，轮询线程必须停下来死等界面画完，一旦界面卡顿（如拖动窗口），10Hz 的采样节拍立刻被拖垮，甚至可能引发死锁。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // 以下 3 行代码已经在 UI 主线程中安全执行，会通过 INotifyPropertyChanged 通知界面控件刷新：

            // 3. 更新心跳计数文本（确认 PLC 通信与程序运行是否活跃）
            PlcHeartbeatText = snap.Heartbeat.ToString();

            // 4. 拼接状态摘要文本：
            //    - :F2 表示保留 2 位小数格式化输出（如 12.50mm）
            //    - 使用三元运算符将 bool 状态转为直观的中文操作员提示（ON/OFF、伸出/缩回）
            PlcSnapshotText = $"状态 {snap.MachineState} | X {snap.XPosition:F2}mm | " +
                              $"伺服 {(snap.ServoReady ? "ON" : "OFF")} | " +
                              $"气缸 {(snap.CylinderExtended ? "伸出" : "缩回")}";

            // 5. 更新界面最后刷新时刻（精确到毫秒 HH:mm:ss.fff），方便调试观察刷新频率是否稳定在 100ms 左右
            PlcLatencyText = $"{DateTime.Now:HH:mm:ss.fff}";
        });
    }

    /// <summary>
    /// 偶发读失败不能把界面整个切成"未连接"：轮询下一拍自己就恢复了，
    /// 所以这里只改状态文字，不动 PlcLatencyText —— 让"最后一次成功读到快照的时刻"继续留在界面上当线索。
    /// </summary>
    private void OnPollingError(object? sender, Exception ex)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            PlcStatusText = $"通讯异常：{ex.Message}";
        });
    }

    /// <summary>
    /// 【仅仿真模式使用】将软件内部各硬件模型与控制器的当前状态，
    /// 反向编码打包后写入 SimulatedPlcDriver（模拟 PLC 驱动）的保持寄存器中。
    /// 这样后续的 PlcPollingService 就能像面对真机一样，通过读寄存器来驱动整个界面。
    /// </summary>
    private void SyncStateToPlcRegisters()
    {
        // 1. 类型与连接双重守卫（Pattern Matching，模式匹配）：
        //    - 若当前驱动不是 SimulatedPlcDriver（说明接的是真实硬件 ModbusTcpDriver），直接返回，真机绝不执行此方法；
        //    - 若虽然是模拟驱动但尚未连接（!IsConnected），同样跳过，避免未连接时往寄存器写数据。
        if (_plcDriver is not SimulatedPlcDriver simulated || !simulated.IsConnected) return;

        // 2. 位域打包（Bit-packing）：创建一个 16 位全 0 的容器，将 6 个分散的 bool 开关量逐一压入对应 Bit 位    收集 6 路开关量
        ushort dio = 0;
        dio = PlcDioBits.Set(dio, PlcDioBits.ServoReady, Axis.Model.ServoReady);                        // 写入伺服就绪位
        dio = PlcDioBits.Set(dio, PlcDioBits.CylinderExtended, Cylinder.Model.ExtendedSensor);          // 写入气缸伸出到位位
        dio = PlcDioBits.Set(dio, PlcDioBits.CylinderRetracted, Cylinder.Model.RetractedSensor);        // 写入气缸缩回到位位
        dio = PlcDioBits.Set(dio, PlcDioBits.ProductDetected, Sensor.IsOn);                             // 写入产品光电检测位
        dio = PlcDioBits.Set(dio, PlcDioBits.InPosition, Axis.Model.InPosition);                        // 写入轴运动到位位
        dio = PlcDioBits.Set(dio, PlcDioBits.EmergencyStop, _controller.State == MachineState.EmergencyStop); // 写入急停状态位

        // 3. 写入机台状态枚举值：直接强转为 ushort（前提：C# 枚举数值必须与 PLC 点表定义严丝合缝）
        simulated.PresetRegister(PlcRegisterMap.MachineState, (ushort)_controller.State);

        // 4. 写入 X 轴坐标（物理毫米 -> 0.01mm 整数 -> 有符号 short -> 无符号 ushort 补码）
        //    注意：中间的 (short) 强转绝不能省，否则负数 double 直接转 ushort 会丢失补码语义
        simulated.PresetRegister(PlcRegisterMap.XPosition, unchecked((ushort)(short)(Axis.Model.CurrentPosition * 100)));

        // 5. 写入 Y 轴坐标：即使当前机台是单轴设备没有 Y 轴，也必须显式写入 0 占位，确保协议表每格都有确定初始值
        simulated.PresetRegister(PlcRegisterMap.YPosition, 0);

        // 6. 将打包好的 16 位开关量整体写入 DIO 寄存器
        simulated.PresetRegister(PlcRegisterMap.DioBits, dio);

        // 7. 写入当前报警代码：利用空条件运算符 ?. 与空合并运算符 ??，无报警时安全写入 0
        simulated.PresetRegister(PlcRegisterMap.AlarmCode, (ushort)(_controller.CurrentAlarm?.Code ?? 0));

        // 8. 写入扫码结果状态（按协议压缩为状态码：0=无条码 / 1=已读到条码；状态 2=读码失败 暂未在此处赋值）
        simulated.PresetRegister(PlcRegisterMap.BarcodeResult,
            (ushort)(string.IsNullOrEmpty(_controller.CurrentBarcode) ? 0 : 1));
    }

    // ==================== 仿真进料 ====================

    private int _lastProducedCount;

    /// <summary>
    /// 模拟上游送料。
    ///
    /// 判定依据只有两条，都不依赖"当前是不是 Ready"：
    ///   ① 产能计数增加 → 这一件做完了，工件被取走
    ///   ② 工步停在 Step 0 且没有料 → 说明工位空了，过一会儿送一个来
    ///
    /// 之前写成 "State == Ready 才送料"，结果启动后 State 变 Running 就走了 else 分支，
    /// 把刚送来的料当成"已完成取走"清掉了 —— 典型的仿真逻辑把真实流程吃掉。
    /// </summary>
    private void AutoFeedProduct()
    {
        // ① 有一件下线 → 工件被取走，工位清空
        if (_controller.ProducedCount != _lastProducedCount)                // 1.有一件下线 → 工件被取走，工位清空
        {
            _lastProducedCount = _controller.ProducedCount;   // 记住新的计数
            Sensor.IsOn = false;                              // 工位空了
            _productFeedTicks = 0;                            // 重新开始数拍
            return;
        }

        if (Sensor.IsOn)                    // 传感器感应到物体                 // 2.已经有料，别重复送
        {
            _productFeedTicks = 0;          // 有料就清零，说明不需要送    上次数到 2（差一拍就送料）→ 料被外部送来了 → 返回，如果不清零相当于跳过等待3拍共1.5s
            return;
        }

        // ② 只有工步停在 0（等料）时才送料，其他工步说明正在加工，不该再有料进来
        if (_controller.Step != 0) return;                                  // 3.不在等料，不许送

        _productFeedTicks++;                                                // 4.数满 3 拍才送,因为送料是500ms触发一次，因此三次为1.5s
        // 3 拍 × 500ms = 1.5s 的上料延时：仿真里这段间隔不能省，否则"工位刚空"和"料已送到"会挤在同一拍上，
        // 界面看不出任何节拍，也不好判断流程是不是真的在等料
        if (_productFeedTicks >= 3)
        {
            Sensor.IsOn = true;
            Log("上游送入工件，到位传感器触发");
        }
    }

    // ==================== 辅助 ====================

    /// <summary>
    /// 手动命令的共同收尾：控制器那几个手动作要么回一句文案、要么抛异常，
    /// 不统一收在这里，每个命令都得抄一遍 try/catch，而且迟早有人漏掉"取消"那一支。
    /// </summary>
    /// <param name="action">必须返回可直接显示的文案；抛 OperationCanceledException 表示被停止或急停打断，
    /// 那是正常出口而不是故障，所以单独接住、不当异常报给操作员。</param>
    private async Task RunGuardedAsync(Func<Task<string>> action) // 传进来一个没有参数、调用后会异步返回一句文案的函数
    {
        try
        {
            StatusMessage = await action();     // 执行动作，把“一句人话”记进 StatusMessage
            Log(StatusMessage);         // 成功了写入日志
        }
        catch (OperationCanceledException)      // 单独接住“取消”,取消不是错误
        {
            StatusMessage = "操作已取消";
        }
        catch (Exception ex)                    // 兜住所有其它异常
        {
            StatusMessage = $"操作异常：{ex.Message}";
            Log(StatusMessage);
        }
    }

    /// <summary>
    /// 日志只留最近 200 条：它是给现场看的运行记录，不是审计日志 —— 无上限增长会让跑了一整天的界面越拖越慢。
    /// 需要追责的报警不进这里，那是 AlarmHistory 的活。
    /// </summary>
    /// <param name="message">不用自己带时间戳，这里统一加；别传多行文本 —— 日志面板是自动换行的，
    /// 一条清单会撑高好几行，现场往下滚的时候反而更容易看漏关键那条。</param>
    public void Log(string message)
    {
        Logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        if (Logs.Count > 200)
            Logs.RemoveAt(Logs.Count - 1);
    }
}
