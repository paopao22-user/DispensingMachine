using System.Windows;
using DispensingMachine.App.Views;
using DispensingMachine.Communication.Drivers;
using DispensingMachine.Communication.Services;
using DispensingMachine.Core.Configuration;
using DispensingMachine.Core.Recipes;
using DispensingMachine.Core.Scheduling;
using DispensingMachine.Devices.Motion;
using DispensingMachine.Devices.Peripherals;
using DispensingMachine.Devices.Plc;
using DispensingMachine.Devices.Pneumatic;
using Prism.DryIoc;
using Prism.Ioc;

namespace DispensingMachine.App;

/// <summary>
/// 应用入口（Prism 8）。
///
/// Prism 帮我们做了三件事：
///   1. CreateShell —— 决定启动时显示哪个窗口
///   2. RegisterTypes —— 集中登记依赖注入关系（相当于手写一个容器）
///   3. ViewModelLocator —— 按命名约定自动把 View 和 ViewModel 绑起来，
///      所以 MainWindow.xaml 里只需要写 AutoWireViewModel="True"，不用在代码后置里 new
/// </summary>
// 继承 PrismApplication 而不是 Application：从基类拿到 Container、CreateShell、RegisterTypes 三件套，
// 所以这里既不用自己 new DryIoc 容器，也不用在 Main 里手工启动窗口。
public partial class App : PrismApplication
{
    /// <summary>决定启动时显示哪个窗口；必须走容器解析，窗口将来有构造依赖时才能一并注入。</summary>
    protected override Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // —— 配置与配方（全局唯一）——
        containerRegistry.RegisterSingleton<MachineConfig>();
        containerRegistry.RegisterSingleton<RecipeManager>();

        // —— 设备（全局唯一：一台设备只能有一个实例，否则界面看到的和实际控制的不是同一个）——  四个设备的构造函数都需要一个name参数，因此传入lambud表达式
        containerRegistry.RegisterSingleton<SensorModule>(() => new SensorModule("进料传感器"));
        containerRegistry.RegisterSingleton<CylinderModule>(() => new CylinderModule("夹紧气缸"));
        containerRegistry.RegisterSingleton<AxisModule>(() => new AxisModule("X 轴"));
        containerRegistry.RegisterSingleton<ScannerModule>(() => new ScannerModule("扫码枪"));

        // —— 通讯层（驱动抽象 + 轮询 + 握手）——
        // 驱动在这里"装配"：换一行就换一个 PLC 品牌，上层的轮询、握手、工步全部不用改。
        // 这就是依赖倒置的实际收益 —— 上层只认 IPlcDriver，不认具体实现。
        MachineConfig config = new();                            // ① 现在就造 → 对象真实存在
        IPlcDriver plcDriver = config.UseSimulatedPlc           // ② 有对象了，可以读属性
            ? new SimulatedPlcDriver()
            : new ModbusTcpDriver();                            // ③ 顺手把驱动也造出来

        containerRegistry.RegisterInstance(config);             // ④ 交货：把这个现成对象登记给容器
        containerRegistry.RegisterInstance(plcDriver);              // ⑤ 交货
        containerRegistry.RegisterSingleton<PlcPollingService>();   // 注册单例，全局唯一. `RegisterSingleton` 把对象的创建**推迟到解析阶段**，但我们需要在**注册阶段**就用这个对象做判断
        containerRegistry.RegisterSingleton<HandshakeManager>();

        // 命令通道：把四步握手包装成"请求点胶"的业务语言，Core 层只认它，不认寄存器地址
        containerRegistry.RegisterSingleton<PlcCommandChannel>();

        // —— 调度核心 ——
        containerRegistry.RegisterSingleton<WorkstationController>();
    }
}
