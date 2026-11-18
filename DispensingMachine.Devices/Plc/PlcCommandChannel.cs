using DispensingMachine.Communication.Protocol;
using DispensingMachine.Communication.Services;

namespace DispensingMachine.Devices.Plc;

/// <summary>
/// PLC 命令通道（设备门面）。
///
/// 为什么要有这一层？
///   Core 层不应该认识 Modbus 寄存器。它只想说"开始点胶"，
///   而不是"往 200 号寄存器写 1，等 201 号变 1，再写回 0，再等 201 变 0"。
///   把四步握手包在这里，Core 的依赖方向依然只有 Core → Devices。
///
/// 一次点胶命令的完整往返：
///   ① 上位机写 CMD(200) = 1
///   ② PLC 执行点胶，完成后置 ACK(201) = 1
///   ③ 上位机撤回 CMD = 0（制造下一次的上升沿）
///   ④ PLC 复位 ACK = 0
///
/// 任何一步超时，HandshakeManager 都会把 CMD 和 ACK 一起清零，
/// 保证下一轮不会因为残留状态而"假成功"。
/// </summary>
public sealed class PlcCommandChannel
{
    /// <summary>点胶命令码。真机上会扩成枚举：1=点胶 2=清洗 3=换胶。</summary>
    public const ushort CmdDispense = 1;

    private readonly HandshakeManager _handshake;

    public PlcCommandChannel(HandshakeManager handshake) => _handshake = handshake;

    /// <summary>最近一次握手的总耗时，用来观察通讯质量。</summary>
    public TimeSpan LastElapsed { get; private set; }

    /// <summary>最近一次握手的结果说明（成功耗时 / 失败原因）。</summary>
    public string LastMessage { get; private set; } = "尚未执行";

    /// <summary>
    /// 向上位机下游的 PLC 发送“请求点胶”握手指令，并等待 PLC 确认应答。
    /// 设计核心：将“主动取消”与“通信/设备故障”严格区分开——取消往上抛，故障收敛成 false。
    /// </summary>
    /// <param name="ct">CancellationToken（取消令牌），默认值 default 表示调用方也可不传取消信号</param>
    /// <returns>握手成功返回 true；超时、断线或 PLC 返回异常码则返回 false</returns>
    public async Task<bool> RequestDispenseAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. 执行经典的工业“一问一答”寄存器握手（Command-Ack Handshake）：
            //    - 往命令寄存器 (HandshakeCmd) 写入点胶命令字 CmdDispense
            //    - 轮询等待 PLC 在应答寄存器 (HandshakeAck) 中回填相同的 CmdDispense 作为确认
            //    - 期间若被 ct 取消、或超过握手时限、或底层 Modbus 报错，ExecuteAsync 会直接抛出对应异常
            HandshakeResult result = await _handshake.ExecuteAsync(
                PlcRegisterMap.HandshakeCmd, CmdDispense,
                PlcRegisterMap.HandshakeAck, CmdDispense,
                ct);

            // 2. 握手成功：记录本次握手总耗时与结果描述，供日志系统或界面状态栏展示
            LastElapsed = result.TotalElapsed;
            LastMessage = result.Message;

            // 3. 告诉上层工步机：PLC 已收到并确认点胶指令，可以继续往下走下一工步
            return true;
        }
        catch (OperationCanceledException)
        {
            // 4. 专门拦截“取消信号”（例如操作员拍了急停或点了停止按钮）：
            //    记录状态文本后，使用裸 throw 原样向上抛出！
            //    绝不把“人为取消”混同为“握手失败(false)”，否则工步机会误弹设备故障报警框
            LastMessage = "握手被取消";
            throw;
        }
        catch (Exception ex)
        {
            // 5. 兜底拦截所有非取消类的异常（如握手超时、TCP 断线、Modbus 异常码）：
            //    把详细的错误信息存进 LastMessage 留作排查线索，同时向工步机返回 false，
            //    把“该报哪个报警码、是否重试”的业务决策权完全交还给上层工步机
            LastMessage = $"握手失败：{ex.Message}";
            return false;
        }
    }
}
