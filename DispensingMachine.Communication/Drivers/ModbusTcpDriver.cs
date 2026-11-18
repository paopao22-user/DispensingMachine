using System.Buffers.Binary;
using System.Net.Sockets;

namespace DispensingMachine.Communication.Drivers;

/// <summary>
/// Modbus TCP 驱动（真实 Socket 实现）。
///
/// 通讯层里最底的一环：把"读/写寄存器"翻译成 Modbus TCP 字节流，再按 MBAP 的长度字段把响应拆回数值。
/// 不负责寄存器地址的业务含义、轮询节拍、握手时序和断线重连 —— 那些在 PlcPollingService / HandshakeManager 里。
///
/// 下面只依赖 System.Net.Sockets 和同目录的 IPlcDriver、ModbusException；
/// 上层（PlcPollingService、HandshakeManager、PlcCommandChannel）通过 IPlcDriver 拿到它，
/// 谁也不被它反过来引用 —— 依赖方向单向下沉，换 PLC 品牌只动这一层，业务代码一行不改。
///
/// 请求帧（12 字节，以读保持寄存器 0x03 为例）：
///   [0-1] 事务号   [2-3] 协议号=0   [4-5] 长度=6   [6] 站号
///   [7] 功能码 0x03   [8-9] 起始地址   [10-11] 寄存器数量
///
/// 响应帧长度不固定，必须靠 MBAP 里的"长度"字段判断：
///   正常响应 = 7 + 3 + 2*count 字节
///   异常响应 = 7 + 2 字节（只有功能码和异常码）
/// 所以【不能硬等固定长度】，否则遇到异常响应会永远等不到足够字节。
///
/// 为什么要用 _lock 把请求压成"一条连接上同时只有一个请求在飞"，而不是开连接池并发：
///   一条 TCP 连接上的应答只能靠事务号区分配对，并发写会让应答交错；只要丢一帧，
///   后面所有应答就整体错位。宁可让上层排队等到超时，也不在这里假装并发。
///
/// 为什么超时用 CancellationTokenSource.CancelAfter，而不是靠 TcpClient 的 ReceiveTimeout/SendTimeout：
///   那两个属性只对同步 Read/Write 生效，走 WriteAsync/ReadAsync 时完全不管用；
///   异步接口只认 CancellationToken，所以超时必须自己兜。
/// </summary>
public sealed class ModbusTcpDriver : IPlcDriver, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private int _transactionId;

    /// <summary>单次通讯超时（连接、读、写共用）。</summary>
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// 别把它当心跳用：TcpClient.Connected 只是"上一次 I/O 的结果"，网线拔掉后它仍可能报 true，
    /// 判断链路是否还活着要看 PLC 自己刷的心跳寄存器（PlcRegisterMap.Heartbeat）。
    /// </summary>
    public bool IsConnected => _tcpClient?.Connected == true && _stream != null;
    public string Endpoint { get; private set; } = "(未连接)";
    public byte StationId { get; private set; } = 1;

    /// <summary>
    /// 建立到 PLC 的长连接。驱动的前提是"一个 PLC 一条长连接"，之后所有读写都复用同一个 _stream，
    /// 没有"每次请求临时连一下"的兜底路径，所以上层必须先连成功再发请求。
    /// </summary>
    /// <param name="ip">PLC 的 IP。写主机名虽然也能解析，但现场 DNS 不可靠，解析失败要等到超时才报错。</param>
    /// <param name="port">Modbus TCP 端口，通常 502；经过网关映射的设备要按实际映射值填。</param>
    /// <param name="stationId">从站号。直连单个 PLC 一般填 1，走网关时它才是真正的设备站号；连上之后固定，中途要换必须重连。</param>
    /// <param name="ct">只取消"建立连接"这一步，连上之后它不再约束后续读写。</param>
    /// <returns>只有成功才会走到 return true，失败一律抛异常（SocketException / OperationCanceledException / IOException），
    /// 判返回值等于白判 —— 调用方应当 catch，而不是 if (!ok) 当业务分支。</returns>
    public async Task<bool> ConnectAsync(string ip, int port, byte stationId = 1, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);        // 上锁，防止两个同时连接  // 异步锁：让出线程，且可取消
        try
        {
            // 重连前先把旧连接拆干净，避免句柄泄漏
            DisconnectInternal();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); //创建CTS，内部和外部都可以取消
            timeoutCts.CancelAfter(TimeoutMs);

            _tcpClient = new TcpClient
            {
                NoDelay = true,          // 关闭 Nagle 算法，工业场景要求小包即发   关掉之后：每次 WriteAsync 都立即发出去。
                ReceiveTimeout = TimeoutMs,
                SendTimeout = TimeoutMs
            };

            // 上面两个 Timeout 对异步读写不生效，真正给这次连接兜底的是 timeoutCts 的 CancelAfter
            await _tcpClient.ConnectAsync(ip, port, timeoutCts.Token).ConfigureAwait(false);    // 传入超时 token
            _stream = _tcpClient.GetStream();           //  建立之后所有读写都走它

            StationId = stationId;
            Endpoint = $"{ip}:{port}";
            return true;
        }
        catch
        {
            DisconnectInternal();
            throw;
        }
        finally
        {
            _lock.Release();        // 释放锁
        }
    }

    /// <summary>
    /// 显式断开。运行中要"断开再连"（手动重连、切换仿真/真机）只能走它，
    /// 因为 Dispose 会把 _lock 一起释放，之后这个驱动实例就废了。
    /// 不抛异常、可重复调用，所以异常路径上也可以直接关。
    /// 故意不取 _lock：卡死的连接如果还要排队等锁就永远断不掉，代价是在途请求会以 ObjectDisposedException 结束。
    /// </summary>
    public Task DisconnectAsync()
    {
        DisconnectInternal();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 拆连接的唯一出口：连接失败分支、DisconnectAsync、Dispose 全走这里，
    /// 就是为了不出现"某条路径漏拆、句柄越积越多"。必须可重复调用。
    /// </summary>
    private void DisconnectInternal()
    {
        try { _stream?.Dispose(); } catch { /* 关闭阶段的异常忽略 */ }
        try { _tcpClient?.Close(); } catch { }
        _stream = null;
        _tcpClient = null;
        Endpoint = "(未连接)";
    }

    /// <summary>
    /// 读保持寄存器（0x03）。参数区、配方、握手位都在这一区，业务数据的"读"基本都走它。
    /// 和 0x04 分成两个入口，是因为地址空间和写权限不同（输入寄存器只读、由 PLC 侧刷新）；
    /// 帧结构其实一模一样，所以真正的读逻辑共用下面那份实现。
    /// </summary>
    /// <param name="startAddress">起始寄存器地址，必须和 PLC 地址表的口径一致 —— 差 1 就会整块读偏，
    /// 读到隔壁参数而且不报错，这种错比断线难查得多。</param>
    /// <param name="count">要读的寄存器个数，不是字节数（一个寄存器占 2 字节）。协议上限 125，
    /// 这里没有显式拦，超了靠 PLC 回异常码 0x03 兜底。</param>
    /// <returns>长度恒为 count 的数组，永不为 null。失败抛 ModbusException（PLC 拒绝，带异常码）
    /// 或 IOException（超时、串包、断线）—— 这两类要分开处理：前者说明地址/参数配错了，重试没用；后者才值得重试。</returns>
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => ReadRegistersInternalAsync(0x03, startAddress, count, ct);

    /// <summary>
    /// 读输入寄存器（0x04）。PLC 把实时状态（机台状态、坐标、DIO 位域、心跳）刷在这一区，
    /// 轮询引擎每一拍读的就是它 —— 所以这里必须便宜：一次批量读，别拆成多次单读。
    /// </summary>
    public Task<ushort[]> ReadInputRegistersAsync(ushort startAddress, ushort count, CancellationToken ct = default)
        => ReadRegistersInternalAsync(0x04, startAddress, count, ct);

    /// <summary>
    /// 写单个保持寄存器（0x06）。点动、握手命令这类"一地址一值"的操作走它，
    /// 不要图省事用批量写传一个长度 1 的数组 —— 单写帧更短，PLC 的解析路径也最短。
    /// </summary>
    /// <param name="address">目标寄存器地址。</param>
    /// <param name="value">原样写下去的 16 位值，缩放由调用方先算好（例如速度百分比要先换算成寄存器整数）。</param>
    /// <returns>Task 完成代表 PLC 已回显确认。PLC 拒写、串包、超时都以异常抛出，没有"返回 false"的分支可判。</returns>
    public async Task WriteSingleRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);            // ← 抢占连接（一条 TCP 同时只能一个请求在飞）
        try
        {
            EnsureConnected();                                       // ← 没连上直接抛，不让后面崩在诡异的地方

            byte[] frame = new byte[12];                            // ← 0x06 帧固定 12 字节
            ushort transId = NextTransactionId();                   // ← 事务号：请求与响应的配对凭证

            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transId);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);          // 协议号
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);          // 后续字节数
            // 这个 6 是固定的：站号 1 + 功能码 1 + 地址 2 + 值 2；MBAP 自己的前 6 字节不计入长度字段
            frame[6] = StationId;
            frame[7] = 0x06;                                                    // 写单个寄存器
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), address);    // 寄存器地址
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), value);     // 写入的值

            await SendAndReceiveAckAsync(frame, transId, ct).ConfigureAwait(false);         // 发出去 + 收应答
        }
        finally
        {
            _lock.Release();        // 释放锁
        }
    }

    /// <summary>
    /// 写多个连续保持寄存器（0x10）。X/Y 坐标 + 速度这种"必须一起生效"的参数只能用它，
    /// 因为拆成几次单写时，PLC 可能正好在两帧之间扫到半新半旧的组合，动作直接跑偏。
    /// </summary>
    /// <param name="values">不能为 null、不能为空，最多 123 个（协议硬限制，来历见下面的行内注释）。
    /// 调用方还要保证这些寄存器在 PLC 地址表里是连续的，跨区写入会被 PLC 回异常码 0x02。</param>
    /// <returns>Task 完成代表 PLC 已回显确认；任何失败都抛异常，不会静默丢掉。</returns>
    public async Task WriteMultipleRegistersAsync(ushort startAddress, ushort[] values, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        // 123 不是随手写的数：0x10 的 PDU 最大 253 字节，减去功能码+地址+数量+字节数共 6 字节，剩下的再除以 2
        if (values.Length is 0 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), "单次写寄存器数量必须在 1~123 之间");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            byte[] frame = new byte[13 + values.Length * 2];
            ushort transId = NextTransactionId();

            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transId);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
            // 长度字段从站号开始数：站号 1 + 功能码 1 + 地址 2 + 数量 2 + 字节数 1 + 数据 2N
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(7 + values.Length * 2));
            frame[6] = StationId;
            frame[7] = 0x10;                                                    // 写多个寄存器
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), (ushort)values.Length);
            // 字节数字段是 0x10 独有的，0x06 帧里没有这个字节 —— 这就是两种帧长差 1 的原因
            frame[12] = (byte)(values.Length * 2);

            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13 + i * 2), values[i]);

            await SendAndReceiveAckAsync(frame, transId, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 内部异步读取 Modbus 保持/输入寄存器的核心方法（线程安全）。
    /// 完成：抢锁 -> 组帧 -> 发送 -> 分两段精准收包 -> 协议校验 -> 大端序解包。
    /// </summary>
    /// <param name="functionCode">功能码：0x03（读保持寄存器）或 0x04（读输入寄存器）</param>
    /// <param name="startAddress">PLC 寄存器起始地址</param>
    /// <param name="count">要连续读取的寄存器数量（例如 14 个）</param>
    /// <param name="ct">外部传入的取消令牌（如轮询停止信号）</param>
    private async Task<ushort[]> ReadRegistersInternalAsync(
        byte functionCode, ushort startAddress, ushort count, CancellationToken ct)
    {
        // ==================== 段 0：并发互斥与连接检查 ====================
        // 1. 异步抢占信号量锁：确保同一时刻只有一个请求在操作底层的 TCP _stream，防止读写交错导致串包
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 2. 拿锁后再次确认当前 TCP 连接是否有效（未连接则直接抛异常，进入 finally 释放锁）
            EnsureConnected();

            // ==================== 段 1：组 12 字节 Modbus TCP 请求帧 ====================
            // 固定结构：7 字节 MBAP（Modbus 应用协议头） + 5 字节 PDU（协议数据单元） = 12 字节
            byte[] frame = new byte[12];
            ushort transId = NextTransactionId(); // 生成本次请求唯一的事务号，用于核对回包

            // 使用 Big-Endian（大端序，高位字节在前）写入多字节字段
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transId);      // [0..1] 事务号（Transaction ID）
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);            // [2..3] 协议号：Modbus 固定为 0
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);            // [4..5] 后续长度：站号(1) + 功能码(1) + 起始地址(2) + 数量(2) = 6
            frame[6] = StationId;                                                 // [6]    从站号（Unit ID）
            frame[7] = functionCode;                                              // [7]    功能码（0x03 或 0x04）
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(8), startAddress); // [8..9] 起始寄存器地址
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(10), count);       // [10..11] 读取寄存器个数

            // ==================== 段 2：构建“整次请求”联动超时令牌 ====================
            // 将外部停止令牌 ct 与本次超时令牌绑定：外部喊停 OR 超过 TimeoutMs，任一触发立即中断网络 IO
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);  // 外部：退出程序  内部： 超时，外部、内部任何一个都能触发
            timeoutCts.CancelAfter(TimeoutMs);

            // ==================== 段 3：发送请求帧 ====================
            await _stream!.WriteAsync(frame, timeoutCts.Token).ConfigureAwait(false);

            // ==================== 段 4：第一段读取 —— 固定 7 字节 MBAP 头 ====================
            // 必须先读 7 字节头，才能知道后面 PDU 还有多少字节，避免 TCP 半包/粘包导致错位
            byte[] mbap = new byte[7];      // 先构建空的mbap头
            await ReadExactAsync(mbap, timeoutCts.Token).ConfigureAwait(false);

            ushort respTransId = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(0)); // 响应事务号
            ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(2));  // 响应协议号
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(4));      // 后续字节数（含 1 字节站号）
            byte unitId = mbap[6];                                                     // 响应站号

            // ==================== 段 5：校验 MBAP 头合法性 ====================
            // 核对事务号是否等于 transId、协议号是否为 0、站号是否一致、长度是否合理，防止串包与脏数据
            ValidateHeader(respTransId, transId, protocolId, unitId, length);

            // ==================== 段 6：第二段读取 —— 按长度精准读取 PDU ====================
            int pduLength = length - 1; // MBAP 中的 length 包含 1 字节的 unitId，故减去 1 得到纯 PDU 长度
            byte[] pdu = new byte[pduLength];           // 构建空的pdu
            await ReadExactAsync(pdu, timeoutCts.Token).ConfigureAwait(false);

            // ==================== 段 7：协议异常与数据完整性校验 ====================
            // 7.1 先判异常响应：Modbus 规定异常时将功能码最高位置 1（如 0x03 变为 0x83），异常码在 pdu[1]
            //     必须先于“功能码相等校验”执行，否则 0x83 != 0x03 会掩盖真正的 PLC 异常码！
            if ((pdu[0] & 0x80) != 0)
                throw new ModbusException(pdu[0], pdu[1]);

            // 7.2 再判功能码是否与请求一致
            if (pdu[0] != functionCode)
                throw new IOException($"响应功能码不匹配：期望 0x{functionCode:X2}，实际 0x{pdu[0]:X2}");

            // 7.3 校验字节数与缓冲区边界，防止 PLC 回复畸形帧导致后续解析越界
            int byteCount = pdu[1];             // PLC 声称的数据区字节数（例如 14 个寄存器 -> 28 字节）
            if (byteCount != count * 2)         // 每个寄存器固定占 2 字节
                throw new IOException($"响应数据长度不匹配：期望 {count * 2} 字节，实际 {byteCount} 字节");
            if (pdu.Length < 2 + byteCount)     // PDU 总长至少为：功能码(1) + 字节数(1) + 数据区(byteCount)
                throw new IOException($"响应帧不完整：PDU 长度 {pdu.Length}，至少需要 {2 + byteCount}");

            // ==================== 段 8：大端序解包为 ushort[] ====================
            ushort[] result = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                // 每 2 个字节合并成一个 16 位无符号整数（高字节在前）
                result[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(2 + i * 2, 2));
            }

            return result;
        }
        finally
        {
            // ==================== 段 9：强制释放锁 ====================
            // 无论正常返回、超时还是抛异常，必须释放信号量，否则后续所有轮询和写入全部死锁
            _lock.Release();
        }
    }

    /// <summary>写操作的应答：0x06 回显原请求，0x10 回显前 4 字节。同样按长度字段读。</summary>
    /// <remarks>只验事务号和异常位，不逐字节比对回显内容：事务号已经能证明"这是本次请求的应答"，
    /// 再比地址/数量属于重复校验；而 0x10 的回显本来就比请求短，比错字段反而会造出一个假失败。</remarks>
    /// <param name="frame">必须已经填好长度字段和事务号，这里只管发和收。</param>
    /// <param name="transId">必须和 frame 里写入的那个值一致，否则 ValidateHeader 直接判为串包。</param>
    private async Task SendAndReceiveAckAsync(byte[] frame, ushort transId, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeoutMs);           // ← 3 秒兜底

        await _stream!.WriteAsync(frame, timeoutCts.Token).ConfigureAwait(false);   // ← 12 字节进网络

        byte[] mbap = new byte[7];
        await ReadExactAsync(mbap, timeoutCts.Token).ConfigureAwait(false);     // ← 先读 7 字节头

        ushort respTransId = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(0));
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(2));
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(4));
        byte unitId = mbap[6];

        ValidateHeader(respTransId, transId, protocolId, unitId, length);       // ← 校验事务号，防串包

        // 长度字段含站号，所以 PDU 长度是 length-1；这个换算错 1 字节，就会把下一帧的头啃掉
        byte[] pdu = new byte[length - 1];                                      // ← 按长度字段读，不硬编码
        await ReadExactAsync(pdu, timeoutCts.Token).ConfigureAwait(false);      // 读取pdu

        // 写操作的异常响应只有功能码 + 异常码两字节，不判就会把"PLC 拒写"当成写成功
        if ((pdu[0] & 0x80) != 0)                                               // ← 异常响应：功能码最高位为 1
            throw new ModbusException(pdu[0], pdu[1]);
    }

    /// <summary>统一校验 MBAP 头，防止串包和脏数据。</summary>
    /// <param name="length">MBAP 长度字段，含站号。调用方必须在下一次"按 length 分配 PDU 数组"之前用它，
    /// 否则非法长度会变成负的数组长度或一次巨大分配，抛出来的异常和真正的原因完全无关。</param>
    private static void ValidateHeader(
        ushort respTransId, ushort expectedTransId, ushort protocolId, byte unitId, ushort length)
    {
        if (protocolId != 0)
            throw new IOException($"MBAP 协议号非法：{protocolId}，应为 0");
        if (respTransId != expectedTransId)
            throw new IOException($"事务号不匹配：期望 {expectedTransId}，实际 {respTransId}（可能是串包）");
        // 2 是"站号之外至少还剩功能码 + 1 字节数据"的下限；260 按 Modbus TCP 最大 ADU 估算，这里只做粗过滤
        if (length < 2 || length > 260)
            throw new IOException($"MBAP 长度字段非法：{length}");
        _ = unitId; // 站号在单从站场景下仅做记录，多从站网关场景才会比对
    }

    /// <summary>
    /// 读满指定字节数。
    /// 网络流是"流"，一次 ReadAsync 不保证返回你要的全部字节，必须循环累加。
    /// </summary>
    /// <param name="buffer">长度必须精确等于"这一帧还剩多少字节要读"：多要 1 字节就会一直等到超时，
    /// 少要则留下残包，把下一帧的 MBAP 读错位，之后每一帧都跟着乱。</param>
    /// <param name="ct">必须带超时（上游统一用 TimeoutMs 的 CancelAfter）。给 CancellationToken.None 的话，
    /// PLC 一拔网线这里就永久挂住，整个轮询线程跟着卡死。</param>
    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream!.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            // ReadAsync 返回 0 表示对端已经关掉发送方向，不是"暂时没数据"，必须当断线处理
            if (read == 0)
                throw new IOException("PLC 关闭了连接（读到 0 字节）");
            offset += read;
        }
    }

    /// <summary>
    /// 事务号是请求与响应的配对凭证：ValidateHeader 全靠它判断"这一帧到底是不是我要的那一帧"。
    /// unchecked 回绕是协议允许的，绕回后仍留 65535 个在途余量，不要改成溢出检查。
    /// </summary>
    private ushort NextTransactionId() => unchecked((ushort)Interlocked.Increment(ref _transactionId));

    /// <summary>
    /// 未连接时立刻抛"编程错误"，而不是让 _stream! 在下面自己抛 NullReferenceException ——
    /// 后者的堆栈停在 ReadExactAsync 里，排查时会被误导成网络问题。
    /// </summary>
    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("PLC 未连接，请先调用 ConnectAsync");
    }

    /// <summary>
    /// 释放连接和信号量。驱动是容器里的单例，正常退出时由容器调用；
    /// 调过之后这个实例就作废了，需要"断开后还能重连"请走 DisconnectAsync。
    /// </summary>
    public void Dispose()
    {
        DisconnectInternal();
        // 顺序不能反：先拆连接再释放信号量，否则还在等锁的请求会在已释放的 SemaphoreSlim 上抛 ObjectDisposedException
        _lock.Dispose();
    }
}
