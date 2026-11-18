using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DispensingMachine.Core.Configuration;

namespace DispensingMachine.Core.Recipes;

/// <summary>
/// 配方管理器：负责配方的增删查、切换权限校验、参数防呆、JSON 持久化。
/// 它不驱动任何机构、不判断一个工步有没有做完、也不决定什么时候该换配方 ——
/// 那些是 WorkstationController 的事，这里只回答"这组参数能不能用、能不能换上去"。
///
/// 三个职责刻意放在一起，因为它们是同一件事的三面：
///   存得下（持久化）→ 改得对（校验）→ 切得安全（权限）
///
/// 依赖方向是单向的：只认识 RecipeModel 和 MachineConfig（后者只读它的硬件上限，不写回），
/// 被 WorkstationController 与 MainWindowViewModel 读 CurrentRecipe。
/// 反过来这里不认识界面，也不认识 PLC —— 这也是为什么校验要求调用方把 MachineState 传进来，
/// 而不是自己去问设备"现在能不能停"。
///
/// 为什么编辑要拆成 GetEditingCopy / ApplyEditingCopy 两步，而不是让界面直接绑 Recipes 里的对象：
///   操作员改到一半的配方字段已经是脏的，直接绑在出料中的配方上，下一件产品就会拿着半套参数去跑。
/// </summary>
public sealed class RecipeManager
{
    // 现场习惯是拿记事本直接改 json，所以缩进排版、中文不转义；
    // AllowReadingFromString 是兼容手填时把数字写成 "150" 这种带引号的形式，否则一处手误就整份读不出来
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// 界面配方列表的数据源。只允许在 UI 线程增删 —— ObservableCollection 不是线程安全的，
    /// 后台线程动它会让绑定层直接抛异常。
    /// </summary>
    public ObservableCollection<RecipeModel> Recipes { get; } = new();

    /// <summary>当前生效的配方。工步执行时读的就是它。</summary>
    public RecipeModel? CurrentRecipe { get; private set; }

    /// <summary>装载内置默认配方，保证程序启动就有东西可用。</summary>
    /// <remarks>
    /// 之所以写死在代码里而不是随程序带一份 json：第一次上机的机器磁盘上什么配置都没有，
    /// 此时列表为空的话界面和工步都拿不到参数，操作员连"先随便试一件"都做不到。
    /// </remarks>
    public void LoadDefaults()
    {
        Recipes.Clear();
        Recipes.Add(new RecipeModel { RecipeName = "标准件-A", ProductCode = "P-A001" });
        // 第二份刻意偏离默认值（40/100/150/450 全不同），启动后一眼就能看出参数是从配方读的、不是写死在代码里
        Recipes.Add(new RecipeModel
        {
            RecipeName = "薄板-B",
            ProductCode = "P-B002",
            ScanPosition = 40,
            WorkPosition = 100,
            MoveVelocity = 150,
            DispenseTimeMs = 450
        });
        CurrentRecipe = Recipes[0];
    }

    /// <summary>
    /// 参数防呆：把配方值和设备硬件能力比对，任何一条越界都拒绝加载。
    /// 这一步是"防撞机"的最后一道闸，绝不能省。
    /// </summary>
    /// <param name="recipe">待校验的参数组，可以直接传界面上的编辑副本，不需要先写回集合。</param>
    /// <param name="config">必须传设备当前真实的硬件上限；拿一份放宽过的测试配置来校验，等于把这层保护关掉。</param>
    /// <param name="errors">只要有错就保证非 null 且至少一条，可直接拼给操作员看，调用方无需再补上下文。</param>
    /// <returns>返回 false 表示存在越界项，调用方必须放弃加载或写回，不能"提示一下继续跑"。</returns>
    public bool Validate(RecipeModel recipe, MachineConfig config, out List<string> errors)
    {
        errors = new List<string>();

        if (string.IsNullOrWhiteSpace(recipe.RecipeName))
            errors.Add("配方名称不能为空");

        if (recipe.WaitPosition < config.XMinPosition || recipe.WaitPosition > config.XMaxPosition)
            errors.Add($"等待位 {recipe.WaitPosition}mm 超出 X 轴行程 [{config.XMinPosition}, {config.XMaxPosition}]");

        if (recipe.ScanPosition < config.XMinPosition || recipe.ScanPosition > config.XMaxPosition)
            errors.Add($"扫码位 {recipe.ScanPosition}mm 超出 X 轴行程");

        if (recipe.WorkPosition < config.XMinPosition || recipe.WorkPosition > config.XMaxPosition)
            errors.Add($"加工位 {recipe.WorkPosition}mm 超出 X 轴行程");

        if (recipe.MoveVelocity <= 0 || recipe.MoveVelocity > config.MaxAllowableVelocity)
            errors.Add($"运动速度 {recipe.MoveVelocity}mm/s 超出设备能力上限 {config.MaxAllowableVelocity}mm/s");

        // 工艺顺序必须单调：等待位 ≤ 扫码位 ≤ 加工位，否则会来回折返
        // 条件故意写成反向再取非：正向写法碰上 NaN 会一路判 false 直接漏过，反向能把它当错误拦下来
        if (!(recipe.WaitPosition <= recipe.ScanPosition && recipe.ScanPosition <= recipe.WorkPosition))
            errors.Add("位置顺序错误：必须满足 等待位 ≤ 扫码位 ≤ 加工位");

        if (recipe.CylinderTimeoutMs <= 0)
            errors.Add("气缸超时时间必须大于 0");

        if (recipe.ScanTimeoutMs <= 0)
            errors.Add("扫码超时时间必须大于 0");

        return errors.Count == 0;
    }

    /// <summary>切换配方。运行中禁止切换，避免跑到一半参数被换掉。</summary>
    /// <param name="recipeName">按名称精确匹配、区分大小写；调用方要先把界面选中项的首尾空格 Trim 掉。</param>
    /// <param name="state">必须是设备此刻的真实状态，传界面上缓存的旧状态，运行中也能切过去。</param>
    /// <param name="config">硬件上限，切换前再校验一次 —— 上次存着能用的配方，换机型后可能已经越界。</param>
    /// <param name="message">成败都给一句能直接显示给操作员的中文；失败不止一条原因时是换行拼接的清单。</param>
    /// <returns>返回 false 时 CurrentRecipe 保持原值不动，调用方不需要回滚，但必须把 message 显示出来。</returns>
    public bool SelectRecipe(string recipeName, MachineState state, MachineConfig config, out string message)
    {
        if (state is MachineState.Running or MachineState.Paused or MachineState.Stopping) // ① 状态闸门，机器在跑的时候和暂停的时候不能切换配方
        {
            message = $"当前状态 {state} 不允许切换配方，请先停止设备";
            return false;
        }

        RecipeModel? target = Recipes.FirstOrDefault(r => r.RecipeName == recipeName);  // ② 查找配方
        if (target is null)
        {
            message = $"未找到配方：{recipeName}";
            return false;
        }

        if (!Validate(target, config, out List<string> errors))         // ③ 参数闸门
        {
            message = "配方校验失败：\n  " + string.Join("\n  ", errors);
            return false;
        }

        CurrentRecipe = target;                                         // ④ 生效（只有走到这里才改状态）
        message = $"已切换配方：{target.RecipeName}（{target.ProductCode}）";
        return true;
    }

    /// <summary>
    /// 取一份可以随便改的副本给界面绑，免得输入框每敲一个字就改到正在出料的配方。
    /// </summary>
    /// <param name="recipeName">按名称精确匹配；注意配方名同时是后面 ApplyEditingCopy 的定位键。</param>
    /// <returns>找不到时返回 null，调用方应直接放弃进入编辑态，不要拿个空对象兜底继续填。</returns>
    public RecipeModel? GetEditingCopy(string recipeName)
        => Recipes.FirstOrDefault(r => r.RecipeName == recipeName)?.Clone();

    /// <summary>把编辑副本应用回原配方（校验通过才写回）。</summary>
    /// <param name="editing">必须来自 GetEditingCopy，且 RecipeName 不能改 —— 它是找回原配方的唯一依据。</param>
    /// <param name="config">写回前用当前硬件上限再校验一次，越界就整份丢弃，不做部分写入。</param>
    /// <param name="message">失败时给出具体哪一条越界；成功时返回已更新的配方名。</param>
    /// <returns>返回 false 时原配方一个字段都没动过，界面应保留编辑内容让操作员改，不要清空输入框。</returns>
    public bool ApplyEditingCopy(RecipeModel editing, MachineConfig config, out string message)
    {
        if (!Validate(editing, config, out List<string> errors))
        {
            message = "配方校验失败：\n  " + string.Join("\n  ", errors);
            return false;
        }

        // 靠名称找回原对象：改过名的副本在这里必然找不到，所以界面上的配方名应当设成只读
        RecipeModel? target = Recipes.FirstOrDefault(r => r.RecipeName == editing.RecipeName);
        if (target is null)
        {
            message = $"未找到配方：{editing.RecipeName}";
            return false;
        }

        // 逐个字段就地覆盖，而不是 target = editing：工步和界面都持有 target 这个引用，
        // 换引用等于让它们继续对着旧对象跑；RecipeName/ProductCode 不覆盖也是故意的，前者是定位键，后者是产品标识
        target.WaitPosition = editing.WaitPosition;
        target.ScanPosition = editing.ScanPosition;
        target.WorkPosition = editing.WorkPosition;
        target.MoveVelocity = editing.MoveVelocity;
        target.ClampDelayMs = editing.ClampDelayMs;
        target.DispenseTimeMs = editing.DispenseTimeMs;
        target.CylinderTimeoutMs = editing.CylinderTimeoutMs;
        target.ScanTimeoutMs = editing.ScanTimeoutMs;
        target.UpdatedTime = DateTime.Now;

        message = $"配方已更新：{target.RecipeName}";
        return true;
    }

    /// <summary>
    /// 把整份配方表落盘。现场调完参数必须能立刻存住，否则断一次电就得全部重填。
    /// 这里不吞异常：没权限、磁盘满都会往外抛，调用方必须 catch 后提示操作员，不能静默当成功。
    /// </summary>
    /// <param name="path">完整文件路径，父目录不存在会自动创建。</param>
    /// <param name="ct">取消令牌：取消只让本次写提前结束，不保证磁盘上留下的是完整 json，调用方要按"可能写坏"处理。</param>
    public async Task SaveToFileAsync(string path, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 先 ToList 取快照再序列化：直接序列化 ObservableCollection，界面线程同时在增删就会抛"集合已修改"
        string json = JsonSerializer.Serialize(Recipes.ToList(), JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    /// 把上次存的配方表读回来。读不到不能让程序起不来，所以缺文件一律按"暂时没有配方"处理。
    /// </summary>
    /// <param name="path">必须和 SaveToFileAsync 用同一个路径，路径不一致时读到的永远是空。</param>
    /// <param name="ct">取消令牌，读取过程中提前退出。</param>
    /// <returns>返回 0 表示文件不存在或内容为空，此时内存里原有的配方会被完整保留，调用方不要据此清空界面。</returns>
    public async Task<int> LoadFromFileAsync(string path, CancellationToken ct = default)
    {
        // 两个提前返回都必须排在 Clear 之前，顺序不能挪：否则一个空文件就能把内置默认配方清光，界面变成空白
        if (!File.Exists(path)) return 0;

        string json = await File.ReadAllTextAsync(path, ct);
        List<RecipeModel>? list = JsonSerializer.Deserialize<List<RecipeModel>>(json, JsonOptions);
        if (list is null || list.Count == 0) return 0;

        Recipes.Clear();
        foreach (RecipeModel r in list)
            Recipes.Add(r);

        // 用 ??= 而不是 =：已经有一份生效配方时，重新载入文件并不意味着现场同意换配方
        CurrentRecipe ??= Recipes[0];
        return Recipes.Count;
    }
}
