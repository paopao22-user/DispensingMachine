using DispensingMachine.Devices.Motion;
using DispensingMachine.Devices.Pneumatic;

namespace DispensingMachine.Core.Alarms;

/// <summary>诊断结果：不只告诉你"错了"，还告诉你"为什么错"和"该怎么办"。</summary>
/// <remarks>
/// 只负责承载"已经算完的结论"，自身不引用任何设备类型，
/// 因此可以脱离硬件和界面单独构造、单独断言；它也不含报警码和时间戳 ——
/// 那两样归 AlarmInfo 管，同一件事在两个类型里各存一份，日后必然一边改了另一边没改。
///
/// 为什么拆成三段文本 + 一个布尔，而不是提前拼成一段给人看的字符串：
///   症状、根因、建议在界面上是不同的字号和折叠层级，将来还要按 IsHardwareFault
///   统计"硬件故障和工艺参数问题各占多少"；一旦提前拼成一整段，
///   这些用途都只能回头解析文本，而文案里本身就带换行和编号，解析必然出错。
/// </remarks>
public sealed record DiagnosisResult(
    string FaultSymptom,
    string RootCause,
    string MaintenanceAdvice,
    bool IsHardwareFault);

/// <summary>
/// 专家诊断服务。
/// 只把"报警码 + 设备当前状态"翻译成一句能照着做的排查方向；
/// 它不决定要不要停机、不碰任何输出、也不参与控制时序 —— 诊断永远是只读的旁观者。
///
/// 现场新人看到"1 号报警"往往不知道从哪下手。
/// 把老师傅的判断逻辑固化成规则树，报警发生时直接给出排查方向 ——
/// 这是上位机软件真正能产生价值的地方之一。
///
/// 依赖方向单向：只读 Devices 层的 AxisModule / CylinderModule 状态，不反向被它们引用；
/// 当前唯一调用方是 WorkstationController.RaiseAlarm，结论经 LastDiagnosis 交给界面。
/// 换机型时只改这一个文件，控制层和界面都不用动。
///
/// 为什么规则写死在 switch 里，而不是做成 XML 或数据库配置：
///   规则一共十几条、一年改不了一次，写死在编译期就能用 AlarmCodes 常量做引用检查，
///   常量一改名立刻编译报错；配置化则要额外扛热加载和字段校验，
///   还要接受"配置写错导致报警时诊断静默失效"这种现场最难查的问题。
///
/// 为什么 Diagnose 既不抛异常也不阻塞：
///   它是在 RaiseAlarm 里被调用的，那时设备已经出事；
///   诊断自己再抛异常会把原始报警吞掉，现场看到的就变成一条诊断异常，真正的根因反而丢了。
/// </summary>
public sealed class DiagnosticService
{
    /// <summary>按报警码 + 当前设备状态推断根因。</summary>
    /// <remarks>
    /// 现场要的是"报警那一刻就能念给操作员听的话"，而不是让他先翻手册，
    /// 所以判断必须现场化：同一个码配上不同的轴状态，结论就该不一样。
    /// 报警码只决定从哪棵规则树进入，真正的分支交给实时状态 ——
    /// 现场经常出现码已经报了、但真实状态还停在更早一步的情况，码比状态更不可信。
    /// 全程只读模型属性，不读寄存器也不发指令，因此在报警回调线程里直接调用是安全的。
    /// </remarks>
    /// <param name="alarmCode">传 AlarmCodes 里的常量，不要传界面上的 "ERR-1001" 文本。没匹配到规则的码不抛异常，会落到兜底结论。</param>
    /// <param name="axis">可以为 null：报警来源拿不到轴模块时（例如 PLC 通讯中断）必须允许省略，此时只能给降级结论。不要在其他线程持续改轴状态的同时调用 —— 内部会连读多个字段，各字段可能来自不同时刻。</param>
    /// <param name="cylinder">可以为 null，语义同 axis；只有气缸类报警码才用得到。</param>
    /// <returns>
    /// 永远返回非 null，调用方不需要判空。命中最后那条兜底结论代表"规则没覆盖"，
    /// 界面应按"信息不足、请保存完整日志"呈现，别让操作员照着去拆机。
    /// </returns>
    public DiagnosisResult Diagnose(int alarmCode, AxisModule? axis = null, CylinderModule? cylinder = null)
    {
        return alarmCode switch
        {
            AlarmCodes.AxisMoveTimeout => DiagnoseAxisTimeout(axis),
            // 1002 复用同一棵树：伺服报警在现场的表现就是"轴不动"，两者要查的物理环节高度重叠
            AlarmCodes.AxisServoAlarm => DiagnoseAxisTimeout(axis),

            AlarmCodes.AxisPositiveLimit => new DiagnosisResult(
                "X 轴触发正限位",
                "目标位置超出机械行程，或原点偏移量设置错误",
                "1. 检查配方中的位置参数是否超出设备行程\n2. 重新执行回零，确认原点位置\n3. 检查硬限位开关是否卡死",
                IsHardwareFault: true),

            AlarmCodes.AxisNegativeLimit => new DiagnosisResult(
                "X 轴触发负限位",
                "反向运动超出机械行程",
                "1. 检查回零方向设置\n2. 手动点动退离限位后再回零",
                IsHardwareFault: true),

            AlarmCodes.CylinderExtendTimeout => DiagnoseCylinder(cylinder, extending: true),
            AlarmCodes.CylinderRetractTimeout => DiagnoseCylinder(cylinder, extending: false),

            AlarmCodes.CylinderSensorConflict => new DiagnosisResult(
                "气缸两个磁簧开关同时闭合",
                "磁簧开关短路，或气缸内部串气导致活塞停在中间位置",
                "1. 用万用表测量两个磁簧开关的通断\n2. 检查气管是否接反\n3. 更换损坏的磁簧开关",
                IsHardwareFault: true),

            AlarmCodes.PlcCommTimeout => new DiagnosisResult(
                "PLC 通讯超时",
                "网线断开、IP 配置错误，或 PLC 处于 STOP 状态",
                "1. ping 一下 PLC 的 IP\n2. 检查网线水晶头\n3. 确认 PLC 处于 RUN 状态",
                IsHardwareFault: true),

            AlarmCodes.ProductMissing => new DiagnosisResult(
                "工位长时间没有工件",
                "上游供料中断，或工件到位传感器失效",
                "1. 检查上游传送带是否运行\n2. 手动放一个工件测试传感器\n3. 检查传感器供电和接线",
                IsHardwareFault: true),

            AlarmCodes.ScannerTimeout => new DiagnosisResult(
                "扫码枪无应答",
                "串口/网口断开，或扫码枪未上电",
                "1. 检查扫码枪电源指示灯\n2. 确认通讯参数（波特率/IP）与软件一致\n3. 用串口助手单独发一次触发命令",
                IsHardwareFault: true),

            AlarmCodes.AxisNotHomed => new DiagnosisResult(
                "轴未回零就尝试运动",
                "上电后没有执行回零流程，或回零过程中途被打断",
                "1. 切换到手动模式执行回零\n2. 检查原点开关是否有效",
                IsHardwareFault: false),

            _ => new DiagnosisResult(
                AlarmCodes.Describe(alarmCode),
                "暂无匹配的诊断规则",
                "请记录报警时的完整日志，联系设备厂商",
                IsHardwareFault: false)
        };
    }

    /// <summary>
    /// 轴类报警共用的一棵规则树。光看报警码分不清是驱动器报警、限位、没使能还是机械卡阻，
    /// 必须结合轴当前的几个标志位才能收敛到一个结论，否则同一个现象每次报出的原因都可能不同。
    /// </summary>
    /// <param name="axis">可为 null（调用方拿不到轴模块）；非 null 时只读它的 Model，不触发运动、使能或复位。</param>
    /// <returns>永远返回非 null。里面的 IsHardwareFault 只回答"要不要叫人去现场拧螺丝"，不回答要不要停机 —— 停不停由报警级别决定。</returns>
    private static DiagnosisResult DiagnoseAxisTimeout(AxisModule? axis)
    {
        if (axis is null)
            return new DiagnosisResult("轴运动超时", "缺少轴状态信息，无法进一步判断", "请检查轴模块是否正常初始化", false);

        AxisModel m = axis.Model;

        // 下面几条判断顺序不能调换：驱动器报警时往往同时点亮限位、丢使能、报未回零，
        // 先判最"物理"的那一条，才不会被后面更显眼的标志带偏方向
        if (m.Alarm)
            return new DiagnosisResult(
                $"{m.Name} 轴伺服报警",
                "伺服驱动器过载、过流或编码器异常",
                "1. 查看驱动器面板显示的错误码\n2. 检查电机动力线和编码器线\n3. 复位后低速试运行",
                IsHardwareFault: true);

        if (m.PositiveLimit || m.NegativeLimit)
            return new DiagnosisResult(
                $"{m.Name} 轴触发限位",
                "运动方向错误或目标位置超出行程",
                "1. 手动退离限位\n2. 核对配方位置参数",
                IsHardwareFault: true);

        if (!m.ServoReady)
            return new DiagnosisResult(
                $"{m.Name} 轴未使能",
                "伺服未上电或使能信号丢失",
                "1. 检查驱动器供电\n2. 检查急停回路是否闭合",
                IsHardwareFault: true);

        if (!m.HomeDone)
            return new DiagnosisResult(
                $"{m.Name} 轴未回零",
                "上电后没有完成回零流程",
                "执行回零，确认原点开关有效",
                IsHardwareFault: false);

        double error = Math.Abs(m.TargetPosition - m.CurrentPosition);
        // 0.1mm 是位置环的到位容差：差值小于它就算"已经到位、只是控制器还没撤 Busy"，
        // 这种情形不该建议现场去查机械卡阻，否则十个报警里有八个会指错方向
        if (m.Busy && error > 0.1)
            return new DiagnosisResult(
                $"{m.Name} 轴卡在中途，距目标还差 {error:F2}mm",
                "机械卡阻、负载过大，或伺服增益不足",
                "1. 断电后手动推动滑块，确认是否卡死\n2. 检查导轨润滑\n3. 适当降低加速度",
                IsHardwareFault: true);

        return new DiagnosisResult(
            $"{m.Name} 轴未在预期时间内到位",
            "加速度或减速度设置过小，导致运动时间超出超时门限",
            "1. 适当调大超时时间\n2. 或提高加速度参数",
            IsHardwareFault: false);
    }

    /// <summary>
    /// 气缸伸/缩超时共用一条规则树。两个方向的物理排查方向完全不同（伸出查气源和电磁阀，
    /// 缩回查排气和弹簧），但"先排除传感器冲突、再按方向给建议"的骨架是一样的；
    /// 拆成两份只会让同一句建议日后一边改了、另一边忘了改。
    /// </summary>
    /// <param name="cylinder">可为 null；为 null 时只能给"请检查气缸模块"这类降级结论。</param>
    /// <param name="extending">true 对应 2001 伸出超时，false 对应 2002 缩回超时。调用方最容易在这里把两个码对调，一错就让现场查反方向。</param>
    /// <returns>永远返回非 null；HasAlarm 为 true 时优先返回传感器冲突结论，因为此时气缸停在哪一侧都无从判断。</returns>
    private static DiagnosisResult DiagnoseCylinder(CylinderModule? cylinder, bool extending)
    {
        string action = extending ? "伸出" : "缩回";

        if (cylinder is null)
            return new DiagnosisResult($"气缸{action}超时", "缺少气缸状态信息", "请检查气缸模块", false);

        // 传感器冲突必须先判：两个磁簧同时闭合时活塞可能停在中间，
        // "伸出超时"和"缩回超时"其实是同一个故障的两种表现，再按方向给建议只会误导
        if (cylinder.HasAlarm)
            return new DiagnosisResult(
                $"气缸{action}超时且双传感器冲突",
                "两个磁簧开关同时闭合，无法判断真实位置",
                "检查磁簧开关是否短路",
                IsHardwareFault: true);

        return new DiagnosisResult(
            $"气缸{action}超时（磁簧开关未闭合）",
            extending
                ? "气压不足、电磁阀未动作，或磁簧开关位置偏移"
                : "排气不畅、弹簧复位力不足，或磁簧开关失效",
            extending
                ? "1. 检查气源压力是否 ≥ 0.4MPa\n2. 手动按电磁阀测试按钮确认能动作\n3. 调整磁簧开关位置"
                : "1. 检查消声器是否堵塞\n2. 手动推回确认无机械卡阻\n3. 更换磁簧开关",
            IsHardwareFault: true);
    }
}
