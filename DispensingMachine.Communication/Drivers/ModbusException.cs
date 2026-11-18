namespace DispensingMachine.Communication.Drivers;

/// <summary>
/// Modbus 异常响应。
/// PLC 收到非法请求时不会照常回数据，而是把功能码最高位置 1，并在下一字节放异常码。
/// 例如：请求 0x03 读寄存器，PLC 返回 0x83 + 0x02（非法数据地址）。
///
/// 负责把「PLC 明确拒绝了这次请求」翻译成一个带异常码、能被单独捕获的类型；
/// 不负责重试、不负责重连、也不负责决定要不要报警停机 —— 那是 ModbusTcpDriver 的调用方的事。
///
/// 依赖是单向的：ModbusTcpDriver 解析 PDU 时构造它，PlcPollingService 和 PlcCommandChannel 在最外层
/// 的 catch (Exception) 里把它连同其它通讯异常一起兜住；这个文件不引用任何项目内类型，只继承 BCL 的 Exception，
/// 所以放在 Communication 层不会把引用方向带反。
///
/// 为什么单独定义一个异常类型，而不是直接抛 IOException 或在消息里加前缀：
///   PLC 拒答（地址非法、值非法）和链路断开在值班员眼里是两种故障，前者查配方和寄存器表，
///   后者查网线和交换机；有了独立类型，上层不必解析异常文本就能决定提示什么，
///   也不会被通用的 catch (Exception) 揉成一句没有信息量的「通讯失败」。
///
/// 为什么 FunctionCode 保存的是 0x83 而不是请求时的 0x03：
///   只有保留 PLC 回来的原始字节，才能和抓包记录逐字节对账；要还原功能码时由调用方自己屏蔽最高位
///   （0x83 与 0x7F 得到 0x03），异常对象里不并存两份容易对不上的功能码。
/// </summary>
public sealed class ModbusException : Exception
{
    /// <summary>PLC 实际回的功能码，最高位已置 1（读 0x03 会变成 0x83）。想拿请求时的功能码要自己屏蔽最高位。</summary>
    public byte FunctionCode { get; }
    /// <summary>PDU 第 2 字节的异常码，不是寄存器地址、也不是错误计数，别拿去和读回来的寄存器值比。</summary>
    public byte ExceptionCode { get; }

    /// <summary>
    /// 驱动层一认出异常 PDU 就靠它把「PLC 拒答」抛上去，而不是自己吞掉返回空数据。
    /// 消息文本在构造时一次拼好，而不是留给日志处再拼：现场的异常最终都要进日志和报警栏，
    /// 拼接点越少，越不容易出现「日志里只剩一个十六进制码、没人看得懂」的工单。
    /// </summary>
    /// <param name="functionCode">传 PLC 回来的原始字节（最高位已置 1，读是 0x83、写多个是 0x90）；传请求时的 0x03 会和抓包记录对不上，也看不出是读还是写出的事。</param>
    /// <param name="exceptionCode">PDU 第 2 字节，规范只定义 0x01~0x06，老设备可能回私有码；这里不校验范围，原样保留，描述降级成「未知异常」。</param>
    public ModbusException(byte functionCode, byte exceptionCode)
        : base($"Modbus 异常响应：功能码 0x{functionCode:X2}，异常码 0x{exceptionCode:X2}（{Describe(exceptionCode)}）")
    {
        FunctionCode = functionCode;
        ExceptionCode = exceptionCode;
    }

    /// <summary>
    /// 把异常码翻成人话：值班员只看报警栏文字，看不到十六进制。
    /// 放在异常类型内部而不是日志层，是因为同一条消息要同时出现在异常 Text、日志和报警栏三处，只维护一份措辞才不会说法不一致。
    /// </summary>
    /// <param name="code">PLC 回来的原始异常码，不保证出现在规范定义过的值里 —— 老设备会回厂商私有码。</param>
    /// <returns>永远返回非 null：查不到定义的码统一返回「未知异常」，调用方拼消息时不必判空。</returns>
    private static string Describe(byte code) => code switch
    {
        0x01 => "非法功能码",
        0x02 => "非法数据地址",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认，处理中",
        // 0x05/0x06 是仅有的两类「隔一拍再发可能就成功」的异常，其余四类重发同一个请求只会再错一次
        0x06 => "从站设备忙",
        // 私有码不能当成漏网情况丢掉：丢掉码就等于让现场只看到「未知异常」，反而没有原始码可查
        _ => "未知异常"
    };
}
