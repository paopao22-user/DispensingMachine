# 点胶机上位机控制系统（day15）

工业精密点胶机的上位机软件。WPF + Prism（MVVM / DI），四层分层，通讯层为真实 Modbus TCP 实现，设备层可脱机仿真运行。

---

## 一、解决方案结构

```
DispensingMachine.sln
├── DispensingMachine.App            界面层（WPF + Prism，MVVM）
├── DispensingMachine.Core           调度核心（工步机 / 报警 / 配方 / 诊断）
├── DispensingMachine.Devices        设备抽象层（轴 / 气缸 / 扫码枪 / 传感器）
└── DispensingMachine.Communication  通讯层（Modbus TCP / 轮询 / 四步握手）
```

依赖方向严格单向，不允许反向引用：

```
App ──▶ Core ──▶ Devices ──▶ Communication
```

---

## 二、工艺主线（八道工序）

```
Step  0  等待工件到位        传感器
Step 10  气缸夹紧            气缸 + 磁簧开关
Step 20  X 轴到扫码位        伺服轴 + 软限位
Step 30  扫码                扫码枪
Step 40  X 轴到点胶位        伺服轴
Step 50  点胶                工艺延时
Step 60  X 轴回等待位        伺服轴
Step 70  气缸松开            气缸
Step 80  产能计数 + 复位
```

---

## 三、六个核心技术点

| # | 技术点 | 位置 |
|---|---|---|
| 1 | 顺序式工步机（`while` + `switch(Step)` + `await`） | `Core/WorkstationController.cs` |
| 2 | 启动前八重安全自检 | `WorkstationController.CanStartAuto` |
| 3 | 四步闭环握手（上升沿触发 + 超时双寄存器回滚） | `Communication/HandshakeManager.cs` |
| 4 | 常态化轮询（`PeriodicTimer` 绝对时间轴 + 不可变快照 + 订阅者异常隔离） | `Communication/PlcPollingService.cs` |
| 5 | Modbus TCP 驱动（MBAP 组帧 / 长度前缀解析 / 异常响应 / 事务号校验） | `Communication/ModbusTcpDriver.cs` |
| 6 | 设备统一契约 `IDeviceModule`（门面模式） | `Devices/IDeviceModule.cs` |

---

## 四、几个关键设计决策

**为什么用顺序式工步机，而不是扫描式状态机**

点胶机是线性工艺，一步做完才做下一步。`await` 天然实现"等动作真的做完再往下"，且卡在哪一步看 `Step` 就知道。扫描式状态机适合多状态并行、需要随时响应外部指令的场合，这里用不上。

**为什么暂停在工步边界生效**

暂停不打断已经发出的动作，避免机构停在半路产生机械应力。急停才立即取消（`CancellationToken`）。

**为什么轮询要把"等待节拍"单独放在第二个 try**

读寄存器抛异常时，如果等待和读取在同一个 `try` 里，异常会跳到 `catch` 跳过等待，循环立刻进入下一拍又抛异常 —— 变成死循环刷屏。等待必须独立，任何异常都不能跳过节拍。

**为什么快照是不可变对象**

寄存器数组会被下一拍覆盖，UI 可能读到半新半旧的数据。每拍 `new` 一个完整快照再一次性替换引用，读取端天然线程安全。

**为什么超时要把 CMD 和 ACK 都清零**

只清 CMD 会残留 `(0,1)` 这种非法状态，下一轮握手的第②步会直接"假成功"。

**为什么配方要深拷贝**

界面编辑的是副本，点"应用"才写回。操作员改到一半取消，运行中的配方不受影响（编辑 / 运行分离）。

---

## 五、运行

```bash
dotnet run --project DispensingMachine.App
```

启动后自动连接仿真 PLC 并进入 100ms 常态轮询。点【启动自动】即可跑完整流程。

**故障注入验证**（改代码里这三个开关，用来演示超时与报警）：

- `CylinderModel.SimulateFailure` → 气缸伸出超时（ERR-2001）
- `ScannerModule.SimulateTimeout` → 扫码枪无应答（ERR-4002）
- `AxisModule.Model.MaxPosition` 调小 → 触发软限位（ERR-1003）

---

## 六、报警码分段

| 段位 | 类别 | 典型码 |
|---|---|---|
| 1xxx | 轴与伺服 | 1001 运动超时 / 1003 正限位 / 1005 未回零 |
| 2xxx | 气动 | 2001 伸出超时 / 2004 双传感器冲突 |
| 3xxx | 安全 | 3001 急停 |
| 4xxx | 通讯 | 4001 PLC 超时 / 4002 扫码无应答 |
| 5xxx | 工艺 | 5001 缺料 / 5003 质量不良 |
| 6xxx | 模式 | 6005 启动条件不满足 |

报警触发时，`DiagnosticService` 同时给出「现象 → 根因 → 维修建议」三段式诊断。
