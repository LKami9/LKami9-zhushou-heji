using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace OmniToolbox.TreeHouseOnline;

/// <summary>
/// 国服频道私聊守卫（Omni 在线模块）
///
/// 一句话：Omni 国服频道的私聊正文命中关键词，就自动 /logout。
///
/// 整条流程只有四步，代码也按这四步分段：
///   1) 定位客户端：在线模块拿不到 Omni 的频道接口，只能用反射。
///      先按「嵌套类型 ChannelWhisper」认出客户端类型（混淆会改类名，但改不掉这个嵌套名），
///      再从三路对象根出发做宽度优先搜索拿到活动实例：
///        · Dalamud 服务里的本插件实例：PluginManager 是全局服务，插件作用域拿不到，
///          必须走 Service&lt;PluginManager&gt;.GetNullable()（Plugin → OmniHost → ... → 频道客户端）
///        · 宿主挂在 ModuleBase 上的内部回调（通常是模块管理器）
///        · 都找不到时，扫一遍 Omni 程序集的静态字段兜底
///      整个定位过程放后台线程跑（第一版在主线程扫，直接卡出 1.7 秒的 HITCH），主线程只收结果。
///   2) 订阅事件：客户端上有 public event Action&lt;ChannelWhisper&gt; WhisperReceived。
///      泛型处理器 + Delegate.CreateDelegate 做精确签名匹配，不用 Expression / Reflection.Emit
///      （在线模块的引用集不保证有那两个程序集）。
///   3) 判定：事件回调可能在后台线程，所以只入队；判定和登出都在 Framework.Update（主线程）做。
///   4) 登出：ICommandManager.ProcessCommand("/logout")。
///      不用 OmenTools 的 ChatManager.Instance() —— 服务定位器在在线模块里可能拿不到实例（静默无效）。
/// </summary>
public sealed class LKami9ChannelWhisperGuard : ModuleBase
{
    private const string LogTag = "[频道守卫]";

    private static readonly string[] ModeNames = ["包含", "完全相等", "通配符 (* ?)"];

    // 宿主按「以 Config 结尾的成员」绑定并自动保存这个对象，模块里没有 config.Save()
    private LKami9ChannelWhisperGuardConfig config = new();

    // 私聊先入队，主线程再消费
    private readonly ConcurrentQueue<Whisper> incoming = new();

    // 反射拿到的客户端与事件
    private object? client;
    private EventInfo? clientEvent;
    private Delegate? clientHandler;
    private PropertyInfo? propText;
    private PropertyInfo? propSenderAlias;
    private PropertyInfo? propSenderName;
    private PropertyInfo? propSenderWorld;
    private PropertyInfo? propSelfAlias;

    // 计时与状态
    private DateTime nextScanUtc = DateTime.MinValue;
    private DateTime logoutAtUtc = DateTime.MinValue;
    private DateTime lastTriggerUtc = DateTime.MinValue;
    private bool logoutQueued;
    private bool updateHooked;
    private bool chatHooked;

    // 扫描在后台线程跑，主线程只消费结果，绝不卡帧
    private volatile bool scanRunning;
    private volatile bool scanResultReady;
    private int scanAttempts;
    private object? scanResultClient;
    private string scanResultStatus = string.Empty;
    private string scanResultChain = "-";
    private string scanResultRoots = "-";

    // 插件实例探测的状态（静态，跨扫描复用）
    private static object? cachedPluginInstance;
    private static volatile bool serviceProbeStuck;

    // 给设置页看的状态
    private string status = "等待扫描";
    private string lastHit = "无";
    private string chain = "-";
    private string roots = "-";

    public override ModuleInfo Info { get; } = new()
    {
        Title = "国服频道私聊守卫",
        Description = "监听 Omni 国服频道私聊，正文命中指定内容后自动登出游戏，也可以只提醒。",
        Category = ModuleCategory.Automation,
        Author = "LKami9",
        SupportUrls = [],
    };

    public override bool HasSettings => true;

    // #region 1. 生命周期

    protected override void OnEnable()
    {
        HookUpdate();
        HookGameChat();
        nextScanUtc = DateTime.MinValue;
        status = "已启用，正在定位国服频道";
        Log("已启用");
    }

    protected override void OnDisable()
    {
        Detach();
        UnhookGameChat();
        UnhookUpdate();

        while (incoming.TryDequeue(out _))
        {
        }

        logoutQueued = false;
        scanResultReady = false;
        scanResultClient = null;
        scanAttempts = 0;
        status = "已停用";
        Log("已停用");
    }

    protected override void OnDispose() => Detach();

    private void HookUpdate()
    {
        if (updateHooked || DalamudServices.Framework is null)
        {
            return;
        }

        DalamudServices.Framework.Update += OnUpdate;
        updateHooked = true;
    }

    private void UnhookUpdate()
    {
        if (!updateHooked)
        {
            return;
        }

        try
        {
            DalamudServices.Framework.Update -= OnUpdate;
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        updateHooked = false;
    }

    private void HookGameChat()
    {
        if (chatHooked)
        {
            return;
        }

        try
        {
            DalamudServices.ChatGUI.ChatMessage += OnGameChat;
            chatHooked = true;
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private void UnhookGameChat()
    {
        if (!chatHooked)
        {
            return;
        }

        try
        {
            DalamudServices.ChatGUI.ChatMessage -= OnGameChat;
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        chatHooked = false;
    }

    // #region 2. 主循环

    private void OnUpdate(IFramework framework)
    {
        try
        {
            ApplyScanResult();

            if (client is null && !scanRunning && DateTime.UtcNow >= nextScanUtc)
            {
                StartScan();
            }

            Drain();

            if (logoutQueued && DateTime.UtcNow >= logoutAtUtc)
            {
                DoLogout();
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    /// <summary>把后台线程的扫描结果搬回主线程：订阅事件、更新 UI 文案。</summary>
    private void ApplyScanResult()
    {
        if (!scanResultReady)
        {
            return;
        }

        scanResultReady = false;
        status = scanResultStatus;
        chain = scanResultChain;
        roots = scanResultRoots;

        var found = scanResultClient;
        scanResultClient = null;
        if (found is not null && client is null)
        {
            Subscribe(found, found.GetType());
        }
    }

    /// <summary>对象图遍历很重（第一版在主线程扫了 1.7 秒），必须放到后台线程。</summary>
    private void StartScan()
    {
        scanRunning = true;
        scanAttempts++;

        // 5s → 10s → 20s → 30s 封顶，命中后自然不再扫
        var wait = Math.Min(30, 5 * (int)Math.Pow(2, Math.Min(3, scanAttempts - 1)));
        nextScanUtc = DateTime.UtcNow.AddSeconds(wait);

        // 静态字段那一轮最贵：第一次全跑，之后每三次带一次
        var includeStatic = scanAttempts == 1 || scanAttempts % 3 == 0;

        Task.Run(() =>
        {
            try
            {
                ScanOnce(includeStatic);
            }
            catch (Exception ex)
            {
                Publish(null, "扫描异常：" + ex.GetType().Name, "-", ex.Message);
                Log(ex);
            }
            finally
            {
                scanRunning = false;
            }
        });
    }

    /// <summary>只读反射，不碰 UI、不碰 Dalamud 服务，可在任意线程跑。</summary>
    private void ScanOnce(bool includeStatic)
    {
        // Omni 每次重载都会在同一个 AppDomain 里留下新的 Assembly 实例，
        // 同名类型可能有多个 Type 对象；只认一个就会永远匹配不上活动实例。
        var clientTypes = FindClientTypes();
        if (clientTypes.Count == 0)
        {
            Publish(null, "未找到国服频道类型：Omni 版本过旧，或频道功能未加载", "-", "类型搜索无结果");
            return;
        }

        var typeNote = clientTypes.Count > 1 ? "类型×" + clientTypes.Count : clientTypes[0].Name;

        var instance = FindClientInstance(clientTypes, includeStatic, out var note, out var description);
        if (instance is null)
        {
            Publish(null, "已认出类型（" + typeNote + "），等待客户端创建", description, note);
            return;
        }

        Publish(instance, "已定位客户端，正在订阅", description, note);
    }

    private void Publish(object? instance, string newStatus, string newChain, string newRoots)
    {
        scanResultClient = instance;
        scanResultStatus = newStatus;
        scanResultChain = newChain;
        scanResultRoots = newRoots;
        scanResultReady = true;
    }

    // #region 3. 反射定位

    private static IEnumerable<Assembly> OmniAssemblies()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string name;
            try
            {
                name = assembly.GetName().Name ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (name.StartsWith("OmniToolbox", StringComparison.Ordinal) ||
                name.StartsWith("OmenTools", StringComparison.Ordinal))
            {
                yield return assembly;
            }
        }
    }

    /// <summary>GetTypes 会因为个别类型缺依赖整体失败，这里逐个筛。</summary>
    private static Type[] SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var list = new List<Type>();
            foreach (var type in ex.Types)
            {
                if (type is not null)
                {
                    list.Add(type);
                }
            }

            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 客户端类型 = 含嵌套类型 ChannelWhisper 的那个类型。
    /// 返回全部同名候选：Omni 重载后旧 Assembly 仍在 AppDomain 里，Type 对象会重复。
    /// </summary>
    private static List<Type> FindClientTypes()
    {
        var found = new List<Type>();

        foreach (var assembly in OmniAssemblies())
        {
            foreach (var type in SafeTypes(assembly))
            {
                try
                {
                    Type? candidate = null;
                    if (type.Name == "ChannelWhisper" && type.DeclaringType is not null)
                    {
                        candidate = type.DeclaringType;
                    }
                    else if (type.GetNestedType("ChannelWhisper", BindingFlags.Public | BindingFlags.NonPublic) is not null)
                    {
                        candidate = type;
                    }

                    if (candidate is not null && !found.Contains(candidate))
                    {
                        found.Add(candidate);
                    }
                }
                catch
                {
                    // 类型不可解析时跳过
                }
            }
        }

        return found;
    }

    /// <summary>
    /// 三路根：插件实例（Service&lt;PluginManager&gt;）→ 宿主回调 → 静态字段。
    /// note 记录每路的结果，直接显示在设置页诊断里，省得靠猜。
    /// </summary>
    private object? FindClientInstance(List<Type> targets, bool includeStatic, out string note, out string description)
    {
        description = "-";
        var notes = new List<string>();

        var plugin = GetPluginInstanceViaService(out var why);
        notes.Add("插件实例=" + (plugin is null ? "无(" + why + ")" : "有"));

        if (plugin is not null)
        {
            var walker = new ObjectWalker(targets);
            var found = walker.Run(new[] { plugin });
            var walkerA = walker;
            notes.Add("图A=" + walker.Visited + "(" + walkerA.KeyTypes() + ")");

            if (found is not null)
            {
                description = walker.Describe(found);
                note = Join(notes);
                return found;
            }
        }

        var hostRoots = new List<object>();
        foreach (var root in HostRoots())
        {
            hostRoots.Add(root);
        }

        notes.Add("宿主根=" + hostRoots.Count);
        if (hostRoots.Count > 0)
        {
            var walker = new ObjectWalker(targets);
            var found = walker.Run(hostRoots);
            notes.Add("图B=" + walker.Visited);

            if (found is not null)
            {
                description = walker.Describe(found);
                note = Join(notes);
                return found;
            }
        }

        if (!includeStatic)
        {
            note = Join(notes);
            return null;
        }

        var staticRoots = new List<object>();
        foreach (var root in StaticRoots())
        {
            staticRoots.Add(root);
        }

        notes.Add("静态根=" + staticRoots.Count);
        var staticWalker = new ObjectWalker(targets);
        var staticFound = staticWalker.Run(staticRoots);
        notes.Add("图C=" + staticWalker.Visited);

        if (staticFound is not null)
        {
            description = staticWalker.Describe(staticFound) + "（静态字段）";
        }

        note = Join(notes);
        return staticFound;
    }

    private static string Join(List<string> parts) => string.Join("；", parts);

    /// <summary>
    /// 服务探测：Dalamud 的 Service&lt;T&gt; 静态构造在非常规宿主里可能卡住，
    /// 所以放到独立线程里最多等 2 秒；一旦超时就永久跳过，不重复泄漏线程。
    /// </summary>
    private static object? GetPluginInstanceViaService(out string why)
    {
        if (cachedPluginInstance is not null)
        {
            why = "已缓存";
            return cachedPluginInstance;
        }

        if (serviceProbeStuck)
        {
            why = "服务探测超时，已跳过";
            return null;
        }

        var done = new bool[1];
        var holder = new object?[1];
        var whyHolder = new string[1];

        var worker = new Thread(() =>
        {
            try
            {
                holder[0] = ProbePluginInstance(out var inner);
                whyHolder[0] = inner;
            }
            catch (Exception ex)
            {
                whyHolder[0] = ex.GetType().Name;
            }
            finally
            {
                done[0] = true;
            }
        })
        {
            IsBackground = true,
            Name = "LKami9ChannelWhisperGuard/probe",
        };

        worker.Start();

        if (!worker.Join(2000))
        {
            serviceProbeStuck = true;
            why = "服务探测超时";
            return null;
        }

        why = whyHolder[0] ?? string.Empty;
        cachedPluginInstance = holder[0];
        return holder[0];
    }

    private static object? ProbePluginInstance(out string why)
    {
        why = string.Empty;

        try
        {
            var dalamud = typeof(IDalamudPluginInterface).Assembly;
            var managerType = dalamud.GetType("Dalamud.Plugin.Internal.PluginManager", false);
            if (managerType is null)
            {
                why = "无 PluginManager 类型";
                return null;
            }

            object? manager = null;

            var serviceOpen = dalamud.GetType("Dalamud.Service`1", false);
            if (serviceOpen is not null)
            {
                try
                {
                    var serviceClosed = serviceOpen.MakeGenericType(managerType);

                    // 只用 GetNullable：Get() 在服务未构造时会一直等 TCS，后台线程会被挂住
                    var getter = serviceClosed.GetMethod("GetNullable", BindingFlags.Public | BindingFlags.Static);
                    if (getter is not null)
                    {
                        var parameters = getter.GetParameters();
                        var args = parameters.Length == 0
                                       ? Array.Empty<object>()
                                       : new object[] { Enum.ToObject(parameters[0].ParameterType, 2) };
                        manager = getter.Invoke(null, args);
                    }
                }
                catch (Exception ex)
                {
                    why = "Service.GetNullable:" + ex.GetType().Name;
                }
            }

            // 次选：插件作用域（能拿到就直接用）
            manager ??= DalamudServices.PluginInterface?.GetService(managerType);

            if (manager is null)
            {
                why = why.Length == 0 ? "管理器为空" : why;
                return null;
            }

            var listField = managerType.GetField(
                "installedPluginsList",
                BindingFlags.Instance | BindingFlags.NonPublic);
            listField ??= managerType.GetField("installedPlugins", BindingFlags.Instance | BindingFlags.NonPublic);

            if (listField?.GetValue(manager) is not IEnumerable plugins)
            {
                why = "拿不到已装插件列表";
                return null;
            }

            object? byAssembly = null;

            foreach (var plugin in plugins)
            {
                if (plugin is null)
                {
                    continue;
                }

                var pluginType = plugin.GetType();
                object? instance;
                try
                {
                    instance = pluginType
                              .GetField("instance", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?.GetValue(plugin);
                }
                catch
                {
                    continue;
                }

                if (instance is null)
                {
                    continue;
                }

                if (!IsOmniType(instance.GetType()))
                {
                    continue;
                }

                string? internalName = null;
                try
                {
                    internalName = pluginType
                                  .GetProperty("InternalName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                                  ?.GetValue(plugin) as string;
                }
                catch
                {
                    // 属性读不到时退回按程序集判断
                }

                if (string.Equals(internalName, "OmniToolbox", StringComparison.OrdinalIgnoreCase))
                {
                    return instance;
                }

                byAssembly ??= instance;
            }

            if (byAssembly is not null)
            {
                return byAssembly;
            }

            why = "未找到 OmniToolbox 插件实例";
            return null;
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name;
            Log(ex);
            return null;
        }
    }

    /// <summary>宿主挂在 ModuleBase 上的内部回调，其 Target 通常就是模块管理器。</summary>
    private IEnumerable<object> HostRoots()
    {
        foreach (var field in typeof(ModuleBase).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            object? value;
            try
            {
                value = field.GetValue(this);
            }
            catch
            {
                continue;
            }

            if (value is Delegate handler)
            {
                if (handler.Target is not null)
                {
                    yield return handler.Target;
                }

                continue;
            }

            if (value is not null)
            {
                yield return value;
            }
        }
    }

    /// <summary>Omni 程序集的静态字段值（兜底用，涉及面稍大，只在第一轮失败时跑）。</summary>
    private static IEnumerable<object> StaticRoots()
    {
        foreach (var assembly in OmniAssemblies())
        {
            foreach (var type in SafeTypes(assembly))
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch
                {
                    continue;
                }

                foreach (var field in fields)
                {
                    object? value;
                    try
                    {
                        if (field.FieldType.IsValueType || field.FieldType == typeof(string))
                        {
                            continue;
                        }

                        value = field.GetValue(null);
                    }
                    catch
                    {
                        continue;
                    }

                    if (value is not null)
                    {
                        yield return value;
                    }
                }
            }
        }
    }

    private void Subscribe(object instance, Type clientType)
    {
        Detach();
        client = instance;

        try
        {
            var evt = clientType.GetEvent("WhisperReceived", BindingFlags.Instance | BindingFlags.Public);
            if (evt?.EventHandlerType is null)
            {
                status = "未找到私聊事件：Omni 版本可能已变更";
                return;
            }

            var arguments = evt.EventHandlerType.GetGenericArguments();
            if (arguments.Length != 1)
            {
                status = "私聊事件签名异常，已停止监听";
                return;
            }

            var payloadType = arguments[0];
            propText = payloadType.GetProperty("Text");
            propSenderAlias = payloadType.GetProperty("SenderAliasID");
            propSenderName = payloadType.GetProperty("SenderDisplayName");
            propSenderWorld = payloadType.GetProperty("SenderHomeWorldName");
            propSelfAlias = clientType.GetProperty("SelfAliasID");

            var method = typeof(LKami9ChannelWhisperGuard).GetMethod(
                nameof(OnWhisperPayload),
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method is null)
            {
                status = "内部处理器缺失，已停止监听";
                return;
            }

            clientHandler = Delegate.CreateDelegate(evt.EventHandlerType, this, method.MakeGenericMethod(payloadType));
            evt.AddEventHandler(instance, clientHandler);
            clientEvent = evt;
            status = "已连接，正在监听私聊";
            Log($"已连接 {clientType.Name}，订阅 {evt.Name}");
        }
        catch (Exception ex)
        {
            status = "订阅私聊事件失败：" + ex.GetType().Name;
            Log(ex);
        }
    }

    private void Detach()
    {
        try
        {
            if (clientEvent is not null && client is not null && clientHandler is not null)
            {
                clientEvent.RemoveEventHandler(client, clientHandler);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        clientEvent = null;
        clientHandler = null;
        client = null;
        propText = null;
        propSenderAlias = null;
        propSenderName = null;
        propSenderWorld = null;
        propSelfAlias = null;
    }

    /// <summary>事件回调：可能来自后台线程，只取值入队，其它什么都不做。</summary>
    private void OnWhisperPayload<T>(T payload)
    {
        try
        {
            var message = (object?)payload;
            if (message is null)
            {
                return;
            }

            var senderAlias = ToInt(propSenderAlias?.GetValue(message));
            var selfAlias = client is null ? 0 : ToInt(propSelfAlias?.GetValue(client));
            if (selfAlias != 0 && senderAlias == selfAlias)
            {
                return;
            }

            incoming.Enqueue(new Whisper
            {
                SenderName = propSenderName?.GetValue(message) as string ?? string.Empty,
                SenderWorld = propSenderWorld?.GetValue(message) as string ?? string.Empty,
                SenderAlias = senderAlias,
                Text = propText?.GetValue(message) as string ?? string.Empty,
            });
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    /// <summary>备用通道：游戏自己的 /tell。</summary>
    private void OnGameChat(IHandleableChatMessage message)
    {
        try
        {
            if (!config.MatchGameTell || message.LogKind != XivChatType.TellIncoming)
            {
                return;
            }

            incoming.Enqueue(new Whisper
            {
                SenderName = message.Sender.TextValue,
                SenderAlias = 0,
                Text = message.Message.TextValue,
                GameTell = true,
            });
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    // #region 4. 判定与登出

    private void Drain()
    {
        while (incoming.TryDequeue(out var whisper))
        {
            lastHit = $"{DateTime.Now:HH:mm:ss} {whisper.Describe()}：{Shorten(whisper.Text, 60)}";
            Log($"私聊 {whisper.Describe()}：{Shorten(whisper.Text, 120)}");

            if (!SenderAllowed(whisper) || !TextMatched(whisper.Text))
            {
                continue;
            }

            Trigger(whisper);
        }
    }

    private void Trigger(Whisper whisper)
    {
        if (!config.EnableLogout)
        {
            status = "命中关键词（仅提醒）：" + Shorten(whisper.Text, 40);
            Announce("[频道守卫] 命中关键词（仅提醒）：" + Shorten(whisper.Text, 40));
            Log("仅提醒模式，不登出");
            return;
        }

        var now = DateTime.UtcNow;
        if (config.CooldownSeconds > 0 && (now - lastTriggerUtc).TotalSeconds < config.CooldownSeconds)
        {
            Log("冷却中，忽略本次命中");
            return;
        }

        lastTriggerUtc = now;
        var delay = Math.Clamp(config.LogoutDelayMs, 0, 30000);
        logoutQueued = true;
        logoutAtUtc = now.AddMilliseconds(delay);
        status = delay <= 0 ? "命中关键词，正在登出" : $"命中关键词，{delay} ms 后登出";

        if (config.AnnounceInChat)
        {
            Announce(delay <= 0
                         ? "[频道守卫] 命中关键词，正在登出游戏"
                         : $"[频道守卫] 命中关键词，{delay} ms 后登出游戏");
        }

        Log($"命中关键词，{delay} ms 后登出");
    }

    /// <summary>
    /// 登出。
    /// 关键：/logout 是**游戏**命令，Dalamud 的 ProcessCommand 只查它自己的命令表（插件命令 + Dalamud 自带命令），
    /// 查不到就直接 false —— 这就是 1.0.2 里“已发送 /logout”却没反应的原因。
    /// 所以主路走 OmenTools 的 ChatManager.SendCommand（反射调用，不引入编译期依赖）。
    /// </summary>
    private void DoLogout()
    {
        logoutQueued = false;

        var viaDalamud = false;
        try
        {
            viaDalamud = DalamudServices.CommandManager?.ProcessCommand("/logout") == true;
        }
        catch (Exception ex)
        {
            Log(ex);
        }

        if (viaDalamud)
        {
            status = "已通过 Dalamud 命令表登出";
            Log("登出：CommandManager 返回 true");
            return;
        }

        if (TrySendCommandViaOmenTools("/logout", out var why))
        {
            status = "已通过 OmenTools 通道发出 /logout";
            Log("登出：OmenTools.ChatManager.SendCommand 已执行");
            return;
        }

        status = "两条登出通道都失败：" + why;
        Log("登出失败：" + why);
    }

    /// <summary>反射调用 OmenTools.OmenService.ChatManager 的 SendCommand / ExecuteMacro。</summary>
    private static bool TrySendCommandViaOmenTools(string command, out string why)
    {
        why = string.Empty;

        try
        {
            Type? manager = null;
            foreach (var assembly in OmniAssemblies())
            {
                manager = assembly.GetType("OmenTools.OmenService.ChatManager", false);
                if (manager is not null)
                {
                    break;
                }
            }

            if (manager is null)
            {
                why = "找不到 OmenTools.ChatManager 类型";
                return false;
            }

            object? instance = null;
            for (var type = manager; type is not null && instance is null; type = type.BaseType)
            {
                var getter = type.GetMethod(
                    "Instance",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                instance = getter?.Invoke(null, null);
            }

            if (instance is null)
            {
                why = "ChatManager 实例为空";
                return false;
            }

            var send = manager.GetMethod(
                "SendCommand",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);

            if (send is not null)
            {
                send.Invoke(instance, new object[] { command });
                return true;
            }

            var macro = manager.GetMethod(
                "ExecuteMacro",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);

            if (macro is not null)
            {
                macro.Invoke(instance, new object[] { command });
                return true;
            }

            why = "ChatManager 上没有 SendCommand / ExecuteMacro(string)";
            return false;
        }
        catch (Exception ex)
        {
            why = ex.GetType().Name + "：" + ex.Message;
            return false;
        }
    }

    private static void Announce(string text)
    {
        try
        {
            DalamudServices.ChatGUI.Print(text, LogTag);
        }
        catch (Exception ex)
        {
            Log(ex);
        }
    }

    private bool SenderAllowed(Whisper whisper)
    {
        var filter = (config.SenderFilter ?? string.Empty).Trim();
        if (filter.Length == 0)
        {
            return true;
        }

        foreach (var part in filter.Split(','))
        {
            var token = part.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (whisper.SenderName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                whisper.SenderWorld.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                (whisper.SenderAlias != 0 && whisper.SenderAlias.ToString() == token))
            {
                return true;
            }
        }

        return false;
    }

    private bool TextMatched(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var mode = Math.Clamp(config.MatchMode, 0, 2);

        foreach (var line in (config.Keywords ?? string.Empty).Split('\n'))
        {
            var keyword = line.Trim();
            if (keyword.Length == 0)
            {
                continue;
            }

            var hit = mode switch
            {
                1 => string.Equals(text.Trim(), keyword, StringComparison.OrdinalIgnoreCase),
                2 => Wildcard(text.Trim(), keyword),
                _ => text.Contains(keyword, StringComparison.OrdinalIgnoreCase),
            };

            if (hit)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>支持 * 和 ? 的通配符匹配（大小写不敏感）。</summary>
    private static bool Wildcard(string text, string pattern)
    {
        int ti = 0, pi = 0, star = -1, mark = 0;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || SameChar(pattern[pi], text[ti])))
            {
                ti++;
                pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                star = pi++;
                mark = ti;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                ti = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }

        return pi == pattern.Length;
    }

    private static bool SameChar(char left, char right) => char.ToLowerInvariant(left) == char.ToLowerInvariant(right);

    // #region 5. 设置页

    public override bool DrawSettings()
    {
        // 设置页打开时补一次钩子（模块可能在其他页被启用）
        HookUpdate();

        var changed = false;

        ImGui.TextWrapped(status);
        ImGui.TextWrapped("最近私聊：" + lastHit);

        if (ImGui.Button("重新扫描"))
        {
            scanAttempts = 0;
            nextScanUtc = DateTime.MinValue;
            status = "已排队重新扫描（后台进行，不会卡帧）";
        }

        if (client is not null)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("已挂载");
        }

        ImGui.Separator();

        ImGui.TextUnformatted("关键词（每行一条，忽略大小写）");
        var keywords = config.Keywords ?? string.Empty;
        if (ImGui.InputTextMultiline("##cn-guard-keywords", ref keywords, 8192, new Vector2(-1f, 96f)))
        {
            config.Keywords = keywords;
            changed = true;
        }

        var mode = Math.Clamp(config.MatchMode, 0, 2);
        if (ImGui.Combo("匹配方式", ref mode, ModeNames, ModeNames.Length))
        {
            config.MatchMode = mode;
            changed = true;
        }

        var sender = config.SenderFilter ?? string.Empty;
        if (ImGui.InputText("发送者过滤", ref sender, 256))
        {
            config.SenderFilter = sender;
            changed = true;
        }

        ImGui.TextWrapped("发送者过滤：显示名 / 服务器 / 化名编号，逗号分隔；留空不限。");

        var logout = config.EnableLogout;
        if (ImGui.Checkbox("命中后自动登出", ref logout))
        {
            config.EnableLogout = logout;
            changed = true;
        }

        var delay = Math.Clamp(config.LogoutDelayMs, 0, 10000);
        if (ImGui.SliderInt("登出延迟（毫秒）", ref delay, 0, 10000))
        {
            config.LogoutDelayMs = delay;
            changed = true;
        }

        var cooldown = Math.Clamp(config.CooldownSeconds, 0, 120);
        if (ImGui.SliderInt("触发冷却（秒）", ref cooldown, 0, 120))
        {
            config.CooldownSeconds = cooldown;
            changed = true;
        }

        var announce = config.AnnounceInChat;
        if (ImGui.Checkbox("动作前在聊天框提示", ref announce))
        {
            config.AnnounceInChat = announce;
            changed = true;
        }

        var tell = config.MatchGameTell;
        if (ImGui.Checkbox("同时检测游戏内 /tell 私聊（备用）", ref tell))
        {
            config.MatchGameTell = tell;
            changed = true;
        }

        if (ImGui.Button("校验关键词规则"))
        {
            var first = FirstKeyword();
            lastHit = first.Length == 0
                           ? "关键词为空，规则不会命中"
                           : TextMatched(first)
                               ? $"自检：样例「{first}」命中"
                               : $"自检：样例「{first}」未命中，检查匹配方式";
        }

        if (ImGui.Button("发送 /logout 测试"))
        {
            DoLogout();
        }

        if (ImGui.CollapsingHeader("诊断"))
        {
            ImGui.TextWrapped("定位链：" + chain);
            ImGui.TextWrapped("三路根：" + roots);
            ImGui.TextWrapped("扫描次数：" + scanAttempts + (scanRunning ? "（进行中）" : string.Empty));
            ImGui.TextWrapped("最近触发：" + (lastTriggerUtc == DateTime.MinValue ? "无" : lastTriggerUtc.ToLocalTime().ToString("HH:mm:ss")));
        }

        return changed;
    }

    public override bool ResetSettings()
    {
        config = new LKami9ChannelWhisperGuardConfig();
        lastHit = "无";
        status = "设置已重置";
        return true;
    }

    // #region 6. 工具

    private string FirstKeyword()
    {
        foreach (var line in (config.Keywords ?? string.Empty).Split('\n'))
        {
            var text = line.Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        short s => s,
        byte b => b,
        _ => 0,
    };

    private static string Shorten(string text, int max) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max] + "…";

    private static bool IsOmniType(Type type)
    {
        try
        {
            var name = type.Assembly.GetName().Name ?? string.Empty;
            return name.StartsWith("OmniToolbox", StringComparison.Ordinal) ||
                   name.StartsWith("OmenTools", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            DalamudServices.PluginLog.Information($"{LogTag} {message}");
        }
        catch
        {
            // 日志服务未就绪
        }
    }

    private static void Log(Exception exception)
    {
        try
        {
            DalamudServices.PluginLog.Warning($"{LogTag} {exception}");
        }
        catch
        {
            // 日志服务未就绪
        }
    }

    private sealed class Whisper
    {
        public string SenderName = string.Empty;

        public string SenderWorld = string.Empty;

        public int SenderAlias;

        public string Text = string.Empty;

        public bool GameTell;

        public string Describe()
        {
            var name = SenderName.Length == 0 ? "未知发送者" : SenderName;
            var world = SenderWorld.Length == 0 ? string.Empty : "@" + SenderWorld;
            var alias = SenderAlias == 0 ? string.Empty : "#" + SenderAlias;
            return (GameTell ? "[tell] " : string.Empty) + name + world + alias;
        }
    }

    private sealed class RefComparer : IEqualityComparer<object>
    {
        public static readonly RefComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// 对象图宽度优先搜索：只进 Omni / OmenTools 的类型和集合，深度 12、节点 8 万封顶。
    /// 记录父节点与字段名，命中后能打印「从哪个字段一路找到客户端」，方便排错。
    /// Visited 会写进诊断，用来判断到底是根不对，还是遍历没走通。
    /// </summary>
    private sealed class ObjectWalker
    {
        private const int MaxDepth = 16;
        private const int MaxNodes = 150000;

        private readonly Type[] targets;
        private readonly Queue<Entry> queue = new();
        private readonly HashSet<object> seen = new(RefComparer.Instance);
        private readonly Dictionary<object, object?> parent = new(RefComparer.Instance);
        private readonly Dictionary<object, string> edge = new(RefComparer.Instance);
        private readonly Dictionary<Type, FieldInfo[]> cache = new();
        private readonly HashSet<string> seenNames = new();

        public ObjectWalker(List<Type> targets) => this.targets = targets.ToArray();

        /// <summary>本轮实际展开的节点数。</summary>
        public int Visited { get; private set; }

        /// <summary>是否属于任一候选客户端类型（同名类型可能来自多个 Assembly 实例）。</summary>
        private bool Matches(object value)
        {
            for (var i = 0; i < targets.Length; i++)
            {
                if (targets[i].IsInstanceOfType(value))
                {
                    return true;
                }
            }

            return false;
        }

        public object? Run(IEnumerable<object> roots)
        {
            foreach (var root in roots)
            {
                Push(root, 0, null, "root");
            }

            while (queue.Count > 0 && Visited < MaxNodes)
            {
                var entry = queue.Dequeue();
                Visited++;

                if (Matches(entry.Node))
                {
                    return entry.Node;
                }

                try
                {
                    seenNames.Add(entry.Node.GetType().Name);
                }
                catch
                {
                    // 拿不到类型名就算了
                }

                if (entry.Depth < MaxDepth)
                {
                    Expand(entry.Node, entry.Depth + 1);
                }
            }

            return null;
        }

        /// <summary>这轮遍历有没有碰到这几个关键类型，用来判断宿主/功能到底起没起。</summary>
        public string KeyTypes()
        {
            var parts = new List<string>();
            parts.Add(seenNames.Contains("OmniHost") ? "OmniHost" : "无OmniHost");
            parts.Add(seenNames.Contains("ModuleControlService") ? "控模块" : "无控模块");
            parts.Add(seenNames.Contains("TreeHouseManager") ? "模块管理器" : "无模块管理器");
            return string.Join("/", parts);
        }

        public string Describe(object found)
        {
            var parts = new List<string>();
            object? current = found;

            for (var guard = 0; guard < 64 && current is not null && parent.TryGetValue(current, out var up); guard++)
            {
                parts.Add(edge.TryGetValue(current, out var name) ? name : "?");
                current = up;
            }

            parts.Reverse();
            return parts.Count == 0 ? "直接命中" : string.Join(" -> ", parts);
        }

        private void Push(object? value, int depth, object? from, string field)
        {
            if (value is null || depth > MaxDepth || value is string)
            {
                return;
            }

            if (value is Delegate handler)
            {
                Push(handler.Target, depth, from, field + ".Target");
                return;
            }

            if (value.GetType().IsValueType)
            {
                return;
            }

            if (Matches(value) || IsTraversable(value.GetType()))
            {
                if (!seen.Add(value))
                {
                    return;
                }

                parent[value] = from;
                edge[value] = field;
                queue.Enqueue(new Entry(value, depth));
            }
        }

        private void Expand(object node, int depth)
        {
            var type = node.GetType();

            if (type.IsArray)
            {
                var array = (Array)node;
                if (array.Length <= 256)
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        Push(array.GetValue(i), depth, node, $"[{i}]");
                    }
                }

                return;
            }

            if (node is IDictionary dictionary)
            {
                if (dictionary.Count <= 128)
                {
                    foreach (var key in dictionary.Keys)
                    {
                        Push(dictionary[key], depth, node, "{" + key + "}");
                    }
                }

                return;
            }

            if (node is IList list)
            {
                if (list.Count <= 256)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        Push(list[i], depth, node, $"[{i}]");
                    }
                }

                return;
            }

            foreach (var field in Fields(type))
            {
                object? child;
                try
                {
                    child = field.GetValue(node);
                }
                catch
                {
                    continue;
                }

                Push(child, depth, node, type.Name + "." + field.Name);
            }
        }

        private FieldInfo[] Fields(Type type)
        {
            if (cache.TryGetValue(type, out var known))
            {
                return known;
            }

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                fields = [];
            }

            cache[type] = fields;
            return fields;
        }

        private static bool IsTraversable(Type type)
        {
            if (IsOmniType(type))
            {
                return true;
            }

            return type.IsArray || typeof(IList).IsAssignableFrom(type) || typeof(IDictionary).IsAssignableFrom(type);
        }

        private readonly struct Entry
        {
            public Entry(object node, int depth)
            {
                Node = node;
                Depth = depth;
            }

            public object Node { get; }

            public int Depth { get; }
        }
    }
}

/// <summary>模块配置：宿主整体序列化，字段用属性，别加 readonly。</summary>
public sealed class LKami9ChannelWhisperGuardConfig
{
    /// <summary>关键词，每行一条。</summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>0 包含 / 1 完全相等 / 2 通配符。</summary>
    public int MatchMode { get; set; }

    /// <summary>发送者过滤，逗号分隔，留空不限。</summary>
    public string SenderFilter { get; set; } = string.Empty;

    /// <summary>false 时只提醒不登出。</summary>
    public bool EnableLogout { get; set; } = true;

    /// <summary>登出延迟（毫秒）。</summary>
    public int LogoutDelayMs { get; set; }

    /// <summary>触发冷却（秒）。</summary>
    public int CooldownSeconds { get; set; } = 10;

    /// <summary>动作前在聊天框提示。</summary>
    public bool AnnounceInChat { get; set; } = true;

    /// <summary>同时检测游戏内 /tell。</summary>
    public bool MatchGameTell { get; set; }
}
