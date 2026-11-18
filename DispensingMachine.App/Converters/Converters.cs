using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DispensingMachine.Core.Alarms;
using DispensingMachine.Core.Configuration;

namespace DispensingMachine.App.Converters;

/// <summary>
/// 把毫米位置换算成画布上的像素坐标。
/// ConverterParameter 传轨道总长度（px），所以换界面尺寸不用改代码。
/// 只回答"这个毫米值该落在轨道的第几个像素上"，不回答这个位置在工艺上合不合法 ——
/// 软限位由 AxisModule 拿 MinPosition/MaxPosition 判定，这里再判一次就是两套真相。
/// </summary>
/// <remarks>
/// 依赖方向：只用到 WPF 的 IValueConverter，向上被 MainWindow.xaml 里轨道滑块那一条绑定使用；
/// 轴模型并不知道有个画布在画它，所以换界面宽度只改 XAML，换行程只改这里的属性。
///
/// 为什么行程做成属性、轨道长度却走 ConverterParameter：
///   行程是设备参数（要和 AxisModel 的 500mm 对齐），写成属性才能和轴配置放在一起看；
///   轨道长度是布局参数，只有 XAML 知道画布多宽，塞进 C# 就得每换一次界面回来改一次代码。
///   代价是这两件事必须改两处，所以参数漏传时不要指望它自己对齐。
/// </remarks>
public sealed class MmToCanvasLeftConverter : IValueConverter
{
    /// <summary>
    /// 轨道行程（mm），决定"走完 500mm 正好到头"。必须和 AxisModel.MaxPosition 一致，
    /// 对不上时不会报错，只表现为滑块永远走不满或者提前贴边。
    /// 只能在绑定第一次求值前设好：这里没有变更通知，运行期改它界面不会自己刷新。
    /// </summary>
    public double MaxMillimeters { get; set; } = 500;

    /// <summary>
    /// 绑定的比例换算入口。之所以不在 ViewModel 里先算成像素再暴露一个属性：
    /// ViewModel 一旦知道画布是 250px 宽，就再也搬不进没有界面的测试里了。
    /// </summary>
    /// <param name="value">轴位置，单位 mm，源属性是 double。
    /// 必须是装箱的 double：源属性将来换成 float/int 不会报错，会静默返回 0，滑块一直贴最左边。</param>
    /// <param name="parameter">轨道长度（px），XAML 里写的是 "250"；不传则退回历史默认值，
    /// 那个默认值和当前画布宽度并不一致。</param>
    /// <returns>永远是非负像素、不会是 null，调用方不用判空；越界的位置被钳在轨道两端，
    /// 宁可贴边显示，也不让滑块画到画布外面去。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double mm) return 0d;

        // 420 是早期 420px 画布的遗留默认值，和现在的 250 对不上：漏传参数时滑块会画到轨道外，
        // 这个错看得见，别把它当成可用的兜底值
        double trackWidth = parameter is string s && double.TryParse(s, out double w) ? w : 420d;
        // 钳的是比例而不是像素：回零漂移会让位置变成 -0.3 或者超出 500，按比例钳住才是和画布宽度无关的写法
        double ratio = Math.Clamp(mm / MaxMillimeters, 0, 1);
        return ratio * trackWidth;
    }

    /// <summary>
    /// 轨道滑块是只读展示，拖动它不该反写轴位置，所以这里直接抛，而不是返回原值装作支持双向。
    /// 真被调到说明 XAML 里有人把绑定改成了 TwoWay —— 让它当场炸出来，比悄悄写坏轴位置安全。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔值 → 画刷。参数格式："真值颜色|假值颜色"。</summary>
/// <remarks>
/// 只做"这个布尔量该显示成哪种颜色"的映射，不判断这个布尔量本身对不对 ——
/// 到位信号到底是 true 还是 false 意味着什么，得去 Devices 层看传感器语义。
///
/// 依赖方向：依赖 WPF 的画刷类型和被绑定的 bool 属性，被 XAML 的 Fill / Foreground 直接使用；
/// 反过来配色由 XAML 用字符串传进来，所以换主题不用重新编译 C#。
///
/// 为什么颜色走 ConverterParameter，而不是给每种用途写一个转换器子类：
///   轴到位指示、气缸的缩回/伸出传感器用的是同一套映射，只差颜色；一个参数把这类需求收在一处，
///   代价是颜色写错编译器管不了，只会在运行期表现为"颜色不对"。
/// </remarks>
public sealed class BoolToBrushConverter : IValueConverter
{
    // 整个进程共用一个：BrushConverter 本身无状态，每次 Convert 都 new 一个等于每刷一次绑定就多一次分配
    private static readonly BrushConverter BrushConverter = new();

    /// <summary>
    /// 给各种指示灯、传感器圆点取色用。写成转换器而不是在 XAML 里给每个控件配一对 DataTrigger：
    /// 同一套绿/灰在界面上出现在好几处，改主题时逐条改必然漏一处。
    /// </summary>
    /// <param name="value">必须是装箱的 bool：null、1、"True" 都会落到假值分支，
    /// 既不抛异常也不会变灰 —— 排查"灯不亮"时先确认绑定源真的是 bool。</param>
    /// <param name="parameter">"真值颜色|假值颜色"，色值写 #RRGGBB 或已知颜色名；
    /// 只写一种颜色时两个状态同色，漏掉竖线的症状是"这盏灯永远不变色"。</param>
    /// <returns>只在拿不到画笔对象时退回 Brushes.Gray，不会返回 null：绑定拿到 null 会让画笔
    /// 退回默认黑色，看着像"状态正常"；灰色至少还是个显眼的"不知道"。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // 只认装箱的 true：bool? 为 null 时走假值分支，也就是"没读到等于未到位"的保守取法
        bool flag = value is true;
        // 兜底配色是调试期用的默认值；两个状态刻意给了不同颜色，漏传参数时至少看得出状态在变
        string[] colors = (parameter as string ?? "#4CAF50|#55555F").Split('|');
        // 少一个颜色时两个状态都用第一个色：宁可显示"没变化"，也不要抛异常把整个界面卡在启动那一帧
        string hex = flag ? colors[0] : (colors.Length > 1 ? colors[1] : colors[0]);
        // ?? Gray 几乎不会命中：ConvertFromString 遇到非法色值是抛异常而不是返回 null，
        // 这个兜底只在返回值不是 Brush 时生效，别指望它接住拼错的颜色名
        return BrushConverter.ConvertFromString(hex) as Brush ?? Brushes.Gray;
    }

    /// <summary>
    /// 颜色没有逆映射：同一个 #4CAF50 既可能表示到位也可能表示运行，反推不出布尔，
    /// 所以这里抛异常而不是猜一个值回写。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>整机状态 → 颜色。运行绿、暂停黄、报警红、就绪蓝。</summary>
/// <remarks>
/// 只把 MachineState 翻成一个颜色，不参与"状态该不该变"的判断 ——
/// 什么条件允许进 Running 由 WorkstationController 的状态机把关，界面只负责显示。
///
/// 依赖方向：依赖 Core 的 MachineState 与 WPF 的画刷类型，被顶栏状态条那一处绑定使用；
/// Core 不知道自己会被画成绿还是红，所以换配色只动这一个文件和 XAML。
///
/// 为什么 Ready 与 Completed 共用一个蓝：
///   现场看灯的人只关心"这一件走完了、下一件能不能接着做"，这两个状态在这一点上答案相同；
///   多一种蓝只是多一条要培训的规则，真正必须一眼分开的是绿（在跑）和红（别碰）。
/// </remarks>
public sealed class StateToBrushConverter : IValueConverter
{
    /// <summary>
    /// 顶栏"当前状态"那块色块的背景走这一处取色。让转换器返回画笔、而不是让 ViewModel 暴露 Brush 属性：
    /// ViewModel 一旦 new SolidColorBrush，WPF 类型就渗进了本该能脱开界面测试的核心逻辑。
    /// </summary>
    /// <param name="value">必须是装箱的 MachineState；传 null 或别的类型不会报错，会落到灰色兜底。</param>
    /// <returns>永远返回实心画笔、不会是 null；没覆盖到的状态（Idle / Init / Homing / Stopping）
    /// 显示灰色 —— 看到灰灯先回来查映射表，而不是怀疑 PLC 掉线。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            MachineState.Running => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            MachineState.Ready or MachineState.Completed => new SolidColorBrush(Color.FromRgb(0x42, 0xA5, 0xF5)),
            MachineState.Paused => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
            MachineState.Alarm or MachineState.EmergencyStop => new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
            // 兜底灰而不是抛异常：以后枚举加值时界面先灰着跑，总比整条状态栏在启动那一帧崩掉好
            _ => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x99))
        };
    }

    /// <summary>
    /// 状态只能由 WorkstationController 推进，改颜色改不了状态，所以不做回写；
    /// 抛出去是为了让误配成 TwoWay 的绑定在开发期立刻暴露。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>报警级别 → 颜色。</summary>
/// <remarks>
/// 只把 AlarmLevel 翻成颜色，不决定这条报警该不该弹窗、要不要停机 ——
/// 停机与联锁判断在 WorkstationController 的报警处理里，界面照着上色就够。
///
/// 依赖方向：依赖 Core 的 AlarmLevel，被当前报警的级别标签、报警历史左侧的级别色条两处绑定共用；
/// "码值 → 级别"的推断在 AlarmCodes.DefaultLevel，这里只管级别到颜色的最后一步。
///
/// 为什么 Warning 给黄、不跟着一起给红：
///   警告是可以带着继续跑的，现场必须一眼分出"能继续"和"必须停"；
///   把警告也染红，操作员很快就对所有红色麻木，真故障反而被忽略。
/// </remarks>
public sealed class AlarmLevelToBrushConverter : IValueConverter
{
    /// <summary>
    /// 当前报警的级别标签、报警历史左侧的级别色条共用这一处配色：各写一套红黄，改色时必然漏一处。
    /// </summary>
    /// <param name="value">必须是装箱的 AlarmLevel；级别本身由 AlarmCodes 算好，这里不做码值推断。</param>
    /// <returns>永远返回实心画笔；未知级别落到亮红而不是灰 —— 灰会让一条没人映射过的
    /// 报警看起来像"没有报警"，宁可错得刺眼。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            AlarmLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x00)),
            AlarmLevel.Fatal => new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F)),
            // 兜底是亮红不是灰：以后 AlarmLevel 加值不会有编译错误，而灰色会让新级别在界面上"消失"
            _ => new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50))
        };
    }

    /// <summary>
    /// 级别由报警码算出、颜色只是它的投影，没有"改颜色改级别"这条路，所以拒绝回写。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed，非 null → Visible。</summary>
/// <remarks>
/// 只回答"这块面板该不该占位"，不回答里面这个值是什么 —— 判断依据只有 null，
/// 空字符串、0、空集合都算有值，照样会显示出来。
///
/// 依赖方向：依赖 WPF 的 Visibility 和绑定的可空属性（CurrentAlarm、LastDiagnosis），
/// 由 XAML 直接使用；持有这些对象的一方只管把属性置 null，不需要知道有块面板要跟着收缩。
///
/// 为什么直接判 null，而不是去绑 Controller.HasAlarm：
///   HasAlarm 本身就是 CurrentAlarm is not null 的复述，绑它等于把同一个事实在两处表达一遍；
///   这里直接判 null，报警一清面板立刻跟着收，不必再依赖另一个属性的通知发得对不对。
/// </remarks>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// 在 XAML 里静态设成 True 就能翻面（例如改成"没有内容时才显示提示"）。
    /// 运行期改它不会刷新界面：转换器不实现变更通知，绑定也不会重新求值。
    /// 另外转换器是按资源键共享的实例，这个 Invert 会作用于所有用它的绑定 ——
    /// 需要两个方向就声明两个资源，别指望在某一条绑定上临时改。
    /// </summary>
    public bool Invert { get; set; }

    /// <summary>
    /// 给"有内容才显示"的面板用。绑可空引用类型时不必让 ViewModel 额外给一个布尔，
    /// 少一个属性就少一处忘记同步的机会。
    /// </summary>
    /// <param name="value">为 null 才收起；空字符串、0、空集合都按"有值"处理 ——
    /// 这是和常见 IsNullOrEmpty 版本不一样的地方，用前先确认语义对得上。</param>
    /// <returns>只返回 Visible 或 Collapsed，绝不返回 Hidden —— Hidden 仍然占着布局位置，
    /// 报警面板收起来时那片空白会把下面的按钮挤掉。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is not null;
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 可见性没有逆映射（用户点不出一个 null 来），所以拒绝回写。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>布尔 → 可见性。Invert=true 时反过来。</summary>
/// <remarks>
/// 只把 bool 换成 Visible / Collapsed，不负责"这个控件该隐藏还是该禁用" ——
/// 会让操作员困惑的按钮应该 IsEnabled=false 留在原位，那种场景去绑 IsEnabled，别拿这个凑。
///
/// 依赖方向：依赖 WPF 的 Visibility，被 XAML 的 Visibility 绑定使用；
/// ViewModel 侧只需要 bool，不需要知道控件在不在。
///
/// 为什么和 NullToVisibility 分成两个类，而不是合成一个吃 object 的转换器：
///   "没数据"和"条件不成立"在现场是两种不同的问题，混在一起以后排查"面板怎么不见了"
///   还得先问清是哪一种；分开写，看一眼 XAML 就知道判断依据是什么。
/// </remarks>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// XAML 里写 Invert="True" 表示"条件不成立时才显示"。
    /// 同样只能在装载时设：没有变更通知，运行期改不刷新；而且资源按实例共享，
    /// 一个资源键只能代表一个方向。
    /// </summary>
    public bool Invert { get; set; }

    /// <summary>
    /// 给"条件成立才出现"的控件用。没直接用 WPF 自带的 BooleanToVisibilityConverter，
    /// 是因为它不支持取反，而这里要正反两种条件共用同一套写法。
    /// </summary>
    /// <param name="value">必须是装箱的 bool：null 当成 false，于是"还没读到"和
    /// "条件不成立"在这里得到同一个显示结果，别拿它区分"未知"和"否"。</param>
    /// <returns>只返回 Visible / Collapsed，不返回 Hidden：隐藏的控件仍然占位，
    /// 整行会留下一块按不动的空白。</returns>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 用户没法靠"看见或看不见"回写一个布尔值，回写路径本来就不存在，所以直接抛。
    /// </summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
