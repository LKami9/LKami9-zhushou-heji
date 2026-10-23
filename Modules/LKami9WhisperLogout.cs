using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using OmenTools.Interop.Game.AddonEvent;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI.Theme;

namespace LKami9.WhisperLogout;

/// <summary>
/// LKami9频道私聊登出
///
/// 监听 Omni 国服频道的私聊（世界频道服务的 WhisperReceived 事件），
/// 收到内容命中指定关键词的私聊时自动登出游戏：发送 /logout 并自动点掉确认框。
/// 典型用途：人不在电脑前时，让朋友往自己的频道化名私聊一个约定好的词，角色立即下线。
///
/// 实现说明：
/// - 国服频道的私聊端点没有公开文档，这里不自己拼 HTTP 请求，而是反射拿 Omni 本体里的
///   世界频道服务单例，直接订阅它公开的 WhisperReceived 事件（add 方法为 public，可跨程序集订阅）。
/// - 服务类型名经过混淆、会随版本变化，因此按「成员特征」发现：含 WhisperReceived 事件、
///   且事件参数类型带 Text / SenderAliasID / RecipientAliasID 属性的类即世界频道服务。
/// - 登出走 Dalamud 命令通道（/logout），确认框用 OmenTools 的 AddonSelectYesnoEvent.ClickYes()，
///   与 Omni 自动登录模块同一套做法。
/// </summary>
public sealed class LKami9WhisperLogout : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9频道私聊登出",
        Description = "监听 Omni 国服频道私聊，命中指定关键词时自动 /logout 并确认，可远程让角色下线。",
        Category    = ModuleCategory.Automation,
        Author      = "LKami9",
        Commands    =
        [
            new("模拟一次命中（不登出）", "/omni LKami9WhisperLogout test"),
            new("立即执行登出",         "/omni LKami9WhisperLogout logout"),
        ],
    };

    public override bool HasSettings => true;

    // 宿主按“以 Config 结尾的成员”绑定已保存的配置；不能是 readonly，宿主需要给它赋值
    private LKami9WhisperLogoutConfig config = new();

    // ── 世界频道服务（反射发现，随 Omni 版本兼容）──────────────────────────────
    private Type? serviceType;
    private object? service;
    private bool serviceIsFallback;
    private EventInfo? whisperEvent;
    private Delegate? whisperHandler;
    private PropertyInfo? selfAliasIdProperty;
    private PropertyInfo? connectedProperty;
    private string serviceNote = string.Empty;

    // ── 运行时状态 ────────────────────────────────────────────────────────────
    private readonly ConcurrentQueue<WhisperHit> hitQueue = [];
    private bool frameworkSubscribed;
    private bool logoutPending;
    private DateTime logoutDeadlineUtc;
    private DateTime nextProbeUtc;
    private int connectAttempts;
    private DateTime nextFallbackUtc;
    private DateTime nextInstanceSearchUtc;
    private static Type? cachedTransportInterface;
    private string status = "尚未开始监听。";
    private string lastHitSummary = "还没有命中记录。";
    private int matchCount;
    private List<string> keywordCache = [];
    private string keywordCacheSource = "\u0000";
    private List<int> senderIdFilter = [];
    private List<string> senderNameFilter = [];
    private string senderFilterCacheSource = "\u0000";

    private System.Numerics.Vector4 WarnVec => OmniTheme.Tokens.Warning;
    private System.Numerics.Vector4 GoodVec => OmniTheme.Tokens.Success;
    private System.Numerics.Vector4 BadVec  => OmniTheme.Tokens.Error;

    #region 生命周期

    protected override void OnEnable()
    {
        this.status = "正在寻找国服频道服务…";
        this.ProbeService();
        this.SubscribeFramework();
    }

    protected override void OnDisable()
    {
        this.UnsubscribeWhisper();
        this.UnsubscribeFramework();
        this.logoutPending = false;
        this.status = "已停止监听。";
    }

    protected override void OnDispose()
    {
        this.UnsubscribeWhisper();
        this.UnsubscribeFramework();
    }

    #endregion

    #region 服务发现与订阅

    /// <summary>
    /// 按成员特征找世界频道服务：含 WhisperReceived 事件，且事件参数类型带
    /// Text / SenderAliasID / RecipientAliasID 三个属性。混淆会改类名，但这些公开成员名不会改。
    /// </summary>
    private static Type? FindWorldChatServiceType()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null || type.IsInterface || type.IsEnum || type.IsAbstract)
                    continue;

                EventInfo? whisper;
                try
                {
                    whisper = type.GetEvent("WhisperReceived", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                catch
                {
                    continue;
                }

                if (whisper?.EventHandlerType is not { IsGenericType: true } handlerType)
                    continue;
                if (handlerType.GetGenericTypeDefinition() != typeof(Action<>))
                    continue;

                var dto = handlerType.GetGenericArguments()[0];
                if (dto.GetProperty("Text") is null ||
                    dto.GetProperty("SenderAliasID") is null ||
                    dto.GetProperty("RecipientAliasID") is null)
                    continue;

                return type;
            }
        }

        return null;
    }

    /// <summary>
    /// 取服务实例。混淆器把 Omni 的单例访问器方法体换成了 return null，
    /// 那些“单例静态字段”也全是无人写入的孤儿字段，静态路子全断；
    /// 因此依次尝试：① 各类型静态字段里直接持有服务实例；
    /// ② 从 Dalamud 服务事件的订阅者对象出发，在 Omni 程序集内做有界 BFS。
    /// </summary>
    private static object? FindLiveInstance(Type type)
    {
        var viaStatic = FindInStaticFields(type);
        if (viaStatic != null)
            return viaStatic;

        return FindInstanceByGraphWalk(type);
    }

    /// <summary>
    /// 扫描 Omni 自己程序集里的静态字段，找直接持有目标类型实例的。
    /// 只扫 OmniToolbox.* 程序集：服务与传输类型都声明在那里，别的程序集不可能持有，
    /// 而全量扫描 79 个程序集要 1 秒以上，不能放在 5 秒的探测周期里。
    /// </summary>
    private static object? FindInStaticFields(Type targetType)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!IsOmniAssembly(assembly))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null)
                    continue;

                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                catch
                {
                    continue;
                }

                foreach (var field in fields)
                {
                    try
                    {
                        // field.FieldType 访问在依赖缺失时也会抛（如 System.Drawing.Common 没加载），必须包住
                        if (!targetType.IsAssignableFrom(field.FieldType))
                            continue;
                        if (field.GetValue(null) is { } value)
                            return value;
                    }
                    catch
                    {
                        // 读不了的字段跳过
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 运行时对象图遍历找实例。根节点取自 Dalamud 各服务对象上事件/委托的订阅者
    /// （Omni 的 UI、框架更新、插件命令等都会订阅），之后只在 Omni 自己的程序集内
    /// 逐字段扩展，避免钻进游戏对象图。有界（时间 + 数量）保证不会卡住。
    /// </summary>
    private static object? FindInstanceByGraphWalk(Type targetType)
    {
        List<object> roots;
        try
        {
            roots = CollectDelegateRoots();
        }
        catch
        {
            return null;
        }

        if (roots.Count == 0)
            return null;

        var visited = new HashSet<object>(ReferenceComparer.Instance);
        var queue = new Queue<object>(roots.Count);
        foreach (var root in roots)
        {
            if (root == null || !visited.Add(root))
                continue;
            if (targetType.IsInstanceOfType(root))
                return root;
            queue.Enqueue(root);
        }

        var fieldCache = new Dictionary<Type, FieldInfo[]>();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var budget = 30000;

        while (queue.Count > 0 && budget-- > 0 && DateTime.UtcNow < deadline)
        {
            var current = queue.Dequeue();
            var currentType = current.GetType();

            // 只沿 Omni 自己的对象走，控制图规模
            if (!IsOmniAssemblyType(currentType))
                continue;

            if (!fieldCache.TryGetValue(currentType, out var fields))
            {
                fields = GetWalkableFields(currentType);
                fieldCache[currentType] = fields;
            }

            foreach (var field in fields)
            {
                object? value;
                try
                {
                    value = field.GetValue(current);
                }
                catch
                {
                    continue;
                }

                if (value == null)
                    continue;

                if (value is Array array)
                {
                    foreach (var element in array)
                    {
                        if (element == null || element is string)
                            continue;
                        if (targetType.IsInstanceOfType(element))
                            return element;
                        if (visited.Add(element))
                            queue.Enqueue(element);
                    }

                    continue;
                }

                if (targetType.IsInstanceOfType(value))
                    return value;
                if (visited.Add(value))
                    queue.Enqueue(value);
            }
        }

        DalamudServices.PluginLog.Information(
            $"[LKami9WhisperLogout] 对象图遍历未命中 {targetType.Name}：根 {roots.Count} 个，访问 {visited.Count} 个对象，剩余队列 {queue.Count}");
        return null;
    }

    /// <summary>取类型的可遍历实例字段；字段类型解析失败（缺依赖程序集）时跳过该字段。</summary>
    private static FieldInfo[] GetWalkableFields(Type type)
    {
        try
        {
            var all = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var result = new List<FieldInfo>(all.Length);
            foreach (var field in all)
            {
                try
                {
                    var fieldType = field.FieldType;
                    if (fieldType.IsPrimitive || fieldType.IsEnum || fieldType == typeof(string))
                        continue;
                }
                catch
                {
                    continue;
                }

                result.Add(field);
            }

            return [.. result];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsOmniAssemblyType(Type type)
    {
        var name = type.Assembly.GetName().Name;
        return name != null && name.StartsWith("OmniToolbox", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOmniAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        return name != null && name.StartsWith("OmniToolbox", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>收集 Dalamud 服务对象上所有委托的订阅者实例作为遍历根节点。</summary>
    private static List<object> CollectDelegateRoots()
    {
        var roots = new List<object>();
        var services = new object?[]
        {
            DalamudServices.Framework,
            DalamudServices.AddonLifecycle,
            DalamudServices.ChatGUI,
            DalamudServices.CommandManager,
            DalamudServices.PluginInterface.UiBuilder,
        };

        foreach (var service in services)
        {
            if (service == null)
                continue;

            FieldInfo[] fields;
            try
            {
                fields = service.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
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
                    value = field.GetValue(service);
                }
                catch
                {
                    continue;
                }

                CollectFromValue(value, roots, 0);
            }
        }

        return roots;
    }

    private static void CollectFromValue(object? value, List<object> roots, int depth)
    {
        if (value == null || depth > 2)
            return;

        if (value is Delegate del)
        {
            foreach (var handler in del.GetInvocationList())
            {
                if (handler.Target is not { } target)
                    continue;
                var duplicate = false;
                foreach (var existing in roots)
                {
                    if (ReferenceEquals(existing, target))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    roots.Add(target);
            }

            return;
        }

        if (value is IDictionary dictionary)
        {
            foreach (var entry in dictionary.Values)
                CollectFromValue(entry, roots, depth + 1);
            return;
        }

        if (value is Array array && depth < 2)
        {
            foreach (var element in array)
                CollectFromValue(element, roots, depth + 1);
        }
    }

    private void ProbeService()
    {
        this.serviceType ??= FindWorldChatServiceType();
        if (this.serviceType == null)
        {
            this.serviceNote = "未找到国服频道服务类型（Omni 版本变化？）";
            this.status = "未找到国服频道服务类型。";
            return;
        }

        this.whisperEvent ??= this.serviceType.GetEvent("WhisperReceived", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        this.selfAliasIdProperty ??= this.serviceType.GetProperty("SelfAliasID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        this.connectedProperty ??= this.serviceType.GetProperty("Connected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // 优先用 Omni 自己的服务实例（与聊天框优化共用同一条频道连接）；
        // 若当前拿的是兜底实例，而 Omni 的实例这时出现了，就切换过去。
        if (this.serviceIsFallback && FindLiveInstance(this.serviceType) is { } shared)
        {
            this.UnsubscribeWhisper();
            this.service = shared;
            this.serviceIsFallback = false;
            this.serviceNote = "已切换到 Omni 的频道服务实例";
        }

        // 实例查找（静态字段 + 对象图遍历）有成本，按 10 秒节流，不跟着 5 秒探测周期白跑
        if (this.service == null && DateTime.UtcNow >= this.nextInstanceSearchUtc)
        {
            this.nextInstanceSearchUtc = DateTime.UtcNow.AddSeconds(10);
            this.service = FindLiveInstance(this.serviceType);
        }

        // Omni 自己的实例还没出现时，用传输层实例自建一个服务，保证本模块独立可用。
        // 对象图遍历有成本，失败后按 30 秒退避重试，不每周期白扫。
        if (this.service == null)
        {
            if (DateTime.UtcNow >= this.nextFallbackUtc)
            {
                this.nextFallbackUtc = DateTime.UtcNow.AddSeconds(30);
                this.service = this.CreateOwnServiceInstance(this.serviceType);
                this.serviceIsFallback = this.service != null;
            }
        }

        if (this.service == null)
        {
            this.serviceNote = "未拿到服务实例（Omni 聊天功能可能未初始化，或对象图里不可达）";
            this.status = "未拿到国服频道服务实例。";
            DalamudServices.PluginLog.Information("[LKami9WhisperLogout] 探测：类型已找到，但实例不可达");
            return;
        }

        if (this.whisperHandler == null && this.whisperEvent != null)
        {
            try
            {
                this.SubscribeWhisper();
                this.serviceNote = this.serviceIsFallback
                    ? "已用独立实例订阅私聊事件（Omni 聊天功能启用后将自动切换）"
                    : "已订阅私聊事件";
                this.status = "已订阅私聊事件。";
            }
            catch (Exception ex)
            {
                this.serviceNote = $"订阅私聊事件失败：{ex.Message}";
                this.status = $"订阅私聊事件失败：{ex.Message}";
                DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 订阅 WhisperReceived 失败：{ex}");
            }
        }
    }

    /// <summary>
    /// 订阅私聊事件。事件的 add 方法是 public，可跨程序集订阅；
    /// 委托签名要求与方法参数严格一致，因此用泛型方法闭合到具体 DTO 类型，
    /// 再转交接受 object 的处理方法（直接绑 object 参数会被 CreateDelegate 拒绝）。
    /// </summary>
    private void SubscribeWhisper()
    {
        var dtoType = this.whisperEvent!.EventHandlerType!.GetGenericArguments()[0];
        var closed = typeof(LKami9WhisperLogout)
            .GetMethod(nameof(this.OnWhisperGeneric), BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(dtoType);
        this.whisperHandler = Delegate.CreateDelegate(this.whisperEvent.EventHandlerType, this, closed);
        this.whisperEvent.AddEventHandler(this.service, this.whisperHandler);
    }

    private void OnWhisperGeneric<T>(T whisper) => this.HandleWhisper(whisper!);

    /// <summary>用世界频道传输层单例构造一个自己的服务实例（Omni 聊天功能未启用时的兜底）。</summary>
    private object? CreateOwnServiceInstance(Type type)
    {
        try
        {
            cachedTransportInterface ??= FindType(t => t.IsInterface && t.Name == "IOmniWorldChatTransport");
            if (cachedTransportInterface == null)
                return null;

            var transport = FindLiveInstance(cachedTransportInterface);
            if (transport == null)
                return null;

            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 1 &&
                                     cachedTransportInterface.IsAssignableFrom(c.GetParameters()[0].ParameterType));
            return constructor?.Invoke([transport]);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 自建服务实例失败：{ex.Message}");
            return null;
        }
    }

    private static Type? FindType(Func<Type, bool> predicate)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null)
                    continue;
                try
                {
                    if (predicate(type))
                        return type;
                }
                catch
                {
                    // 某些类型的特性检查可能抛异常，跳过
                }
            }
        }

        return null;
    }

    private void UnsubscribeWhisper()
    {
        if (this.whisperHandler == null || this.whisperEvent == null || this.service == null)
        {
            this.whisperHandler = null;
            return;
        }

        try
        {
            this.whisperEvent.RemoveEventHandler(this.service, this.whisperHandler);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 退订 WhisperReceived 失败：{ex}");
        }

        this.whisperHandler = null;
    }

    private void SubscribeFramework()
    {
        if (this.frameworkSubscribed)
            return;
        DalamudServices.Framework.Update += this.OnFrameworkUpdate;
        this.frameworkSubscribed = true;
    }

    private void UnsubscribeFramework()
    {
        if (!this.frameworkSubscribed)
            return;
        DalamudServices.Framework.Update -= this.OnFrameworkUpdate;
        this.frameworkSubscribed = false;
    }

    #endregion

    #region 私聊接收

    /// <summary>私聊事件在频道连接的后台线程触发，只做解析和入队，不碰游戏内状态。</summary>
    private void HandleWhisper(object whisper)
    {
        try
        {
            var text        = ReadString(whisper, "Text");
            var senderName  = ReadString(whisper, "SenderDisplayName");
            var senderAlias = ReadInt32(whisper, "SenderAliasID");
            var recipientId = ReadInt32(whisper, "RecipientAliasID");
            var selfId      = this.GetSelfAliasId();

            // 只处理发给自己的私聊（自己发出去的 Recipient 是对方）
            if (selfId > 0 && recipientId != selfId)
                return;

            if (string.IsNullOrWhiteSpace(text))
                return;
            if (!this.IsMatch(text))
                return;
            if (!this.SenderAllowed(senderAlias, senderName))
                return;

            this.hitQueue.Enqueue(new WhisperHit(senderName, senderAlias, text, false));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 处理私聊事件异常：{ex}");
        }
    }

    private static string ReadString(object whisper, string property)
    {
        try
        {
            var value = whisper.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(whisper);
            return value as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadInt32(object whisper, string property)
    {
        try
        {
            var value = whisper.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(whisper);
            return value is int i ? i : 0;
        }
        catch
        {
            return 0;
        }
    }

    private int GetSelfAliasId()
    {
        try
        {
            var value = this.selfAliasIdProperty?.GetValue(this.service!);
            return value is int i ? i : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool IsConnected()
    {
        try
        {
            return this.connectedProperty?.GetValue(this.service!) is true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region 匹配

    private bool IsMatch(string text)
    {
        this.RefreshCaches();

        if (this.keywordCache.Count == 0)
            return false;

        var probe = text.Trim();
        var comparison = this.config.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var keyword in this.keywordCache)
        {
            // 注意：不能用 switch 的 case when —— 标签命中而守卫不满足时会继续落到 default，
            // 会让「全等 / 前缀」退化成「包含」。这里用 if/else 明确分支。
            if (this.config.MatchMode == 1)
            {
                if (string.Equals(probe, keyword, comparison))
                    return true;
            }
            else if (this.config.MatchMode == 2)
            {
                if (probe.StartsWith(keyword, comparison))
                    return true;
            }
            else
            {
                if (probe.Contains(keyword, comparison))
                    return true;
            }
        }

        return false;
    }

    private bool SenderAllowed(int senderAlias, string senderName)
    {
        this.RefreshCaches();

        if (this.senderIdFilter.Count == 0 && this.senderNameFilter.Count == 0)
            return true;

        if (senderAlias != 0 && this.senderIdFilter.Contains(senderAlias))
            return true;
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            var comparison = this.config.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (this.senderNameFilter.Any(name => string.Equals(name, senderName.Trim(), comparison)))
                return true;
        }

        return false;
    }

    private void RefreshCaches()
    {
        if (!string.Equals(this.keywordCacheSource, this.config.Keywords, StringComparison.Ordinal))
        {
            this.keywordCacheSource = this.config.Keywords;
            this.keywordCache = SplitEntries(this.config.Keywords);
        }

        if (!string.Equals(this.senderFilterCacheSource, this.config.SenderFilter, StringComparison.Ordinal))
        {
            this.senderFilterCacheSource = this.config.SenderFilter;
            this.senderIdFilter = [];
            this.senderNameFilter = [];
            foreach (var entry in SplitEntries(this.config.SenderFilter))
            {
                if (int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aliasId))
                    this.senderIdFilter.Add(aliasId);
                else
                    this.senderNameFilter.Add(entry);
            }
        }
    }

    private static List<string> SplitEntries(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw
            .Split([',', '，', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .ToList();
    }

    #endregion

    #region 主循环

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.DrainHits();
        this.DriveLogoutConfirm();

        if (DateTime.UtcNow < this.nextProbeUtc)
            return;

        this.nextProbeUtc = DateTime.UtcNow.AddSeconds(5);

        if (this.service == null || this.whisperHandler == null)
        {
            this.ProbeService();
            return;
        }

        if (!this.IsConnected() && this.config.AutoConnect && this.connectAttempts < 5)
        {
            this.connectAttempts++;
            this.TryConnectService();
        }
    }

    private void DrainHits()
    {
        while (this.hitQueue.TryDequeue(out var hit))
        {
            this.matchCount++;
            this.lastHitSummary = $"{DateTime.Now:HH:mm:ss} {hit.SenderName}（化名 {hit.SenderAliasID}）：{hit.Text}";

            if (this.config.NotifyInChat)
                DalamudServices.ChatGUI.Print($"[频道私聊登出] 命中私聊：{hit.SenderName}：{hit.Text}");

            if (hit.FromTest)
            {
                this.status = "模拟命中已处理（未登出）。";
                continue;
            }

            if (this.config.LogoutOnMatch)
                this.BeginLogout($"命中关键词：{hit.Text}");
            else
                this.status = $"命中私聊（仅提示模式）：{hit.Text}";
        }
    }

    private void BeginLogout(string reason)
    {
        if (this.logoutPending)
            return;

        if (DalamudServices.PlayerState.ContentId == 0)
        {
            this.status = "当前未登录角色，跳过登出。";
            return;
        }

        try
        {
            DalamudServices.CommandManager.ProcessCommand("/logout");
        }
        catch (Exception ex)
        {
            this.status = $"发送 /logout 失败：{ex.Message}";
            DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 发送 /logout 失败：{ex}");
            return;
        }

        this.logoutPending = true;
        this.logoutDeadlineUtc = DateTime.UtcNow.AddSeconds(25);
        this.status = $"已发送 /logout（{reason}），等待确认框…";
    }

    private void DriveLogoutConfirm()
    {
        if (!this.logoutPending)
            return;

        if (DateTime.UtcNow >= this.logoutDeadlineUtc)
        {
            this.logoutPending = false;
            this.status = "登出确认框未出现（可能在战斗 / 副本中，或已被手动处理）。";
            return;
        }

        try
        {
            if (AddonSelectYesnoEvent.ClickYes())
            {
                this.logoutPending = false;
                this.status = "已确认登出。";
            }
        }
        catch (Exception ex)
        {
            this.logoutPending = false;
            this.status = $"点击登出确认框失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 未连接国服频道时尝试连接。服务类的连接方法名经过混淆，按签名找候选：
    /// 返回 void/Task、参数为 0/1/2 个 bool 的实例方法（跳过属性与事件访问器）。
    /// 若服务有 desiredConnected 字段，就逐个试到该字段被翻开为止——这样能确认
    /// 真的踩到了连接开关，而不是误调了别的布尔方法。
    /// </summary>
    private void TryConnectService()
    {
        if (this.service == null || this.serviceType == null)
            return;

        var desiredField = this.serviceType.GetField("desiredConnected", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var desiredBefore = desiredField?.GetValue(this.service) is true;

        var candidates = this.serviceType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(method =>
            {
                if (method.IsSpecialName)
                    return false;
                if (method.ReturnType != typeof(void) && method.ReturnType != typeof(Task))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 0
                    || (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                    || (parameters.Length == 2 && parameters[0].ParameterType == typeof(bool) && parameters[1].ParameterType == typeof(bool));
            })
            .ToList();

        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            object?[] arguments = parameters.Length switch
            {
                0 => [],
                1 => [true],
                _ => [true, true],
            };

            try
            {
                method.Invoke(this.service, arguments);
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Warning($"[LKami9WhisperLogout] 调用 {method.Name} 失败：{ex.Message}");
                continue;
            }

            DalamudServices.PluginLog.Information($"[LKami9WhisperLogout] 尝试连接国服频道（调用 {method.Name}）");

            // desiredConnected 被翻开 → 就是连接开关，收工
            if (desiredField != null && !desiredBefore && desiredField.GetValue(this.service) is true)
            {
                DalamudServices.PluginLog.Information($"[LKami9WhisperLogout] 确认 {method.Name} 为连接开关");
                return;
            }

            // 没有 desiredConnected 字段可验证时，保守地只调一个
            if (desiredField == null)
                return;
        }

        if (candidates.Count == 0)
            this.serviceNote = "未找到可调用的连接方法，请在 Omni 中手动连接国服频道。";
    }

    #endregion

    #region 命令与界面

    public override bool TryHandleCommand(string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "test":
                this.RefreshCaches();
                this.hitQueue.Enqueue(new WhisperHit("（测试）", 0, this.keywordCache.Count > 0 ? this.keywordCache[0] : "测试命中", true));
                this.status = "已模拟一次命中。";
                return true;

            case "logout":
                this.BeginLogout("手动命令");
                return true;

            case "status":
                this.ReportStatus();
                return true;

            default:
                return false;
        }
    }

    private void ReportStatus()
    {
        var state = this.service == null
            ? this.serviceNote
            : $"化名 {this.GetSelfAliasId()}，{(this.IsConnected() ? "已连接" : "未连接")}，命中 {this.matchCount} 次";
        DalamudServices.ChatGUI.Print($"[频道私聊登出] {state}");
    }

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextWrapped(this.status);
        if (!string.IsNullOrEmpty(this.serviceNote))
            ImGui.TextDisabled(this.serviceNote);

        ImGui.Spacing();

        // ── 服务状态 ──
        ImGui.Separator();
        if (this.service == null)
        {
            ImGui.TextColored(this.BadVec, "国服频道服务：未就绪");
        }
        else
        {
            var connected = this.IsConnected();
            ImGui.TextColored(connected ? this.GoodVec : this.WarnVec, connected ? "国服频道：已连接" : "国服频道：未连接（私聊送不进来）");
            ImGui.SameLine();
            ImGui.TextDisabled($"自己的化名 ID：{this.GetSelfAliasId()}");
        }

        ImGui.SameLine();
        if (ImGui.Button("刷新状态"))
        {
            this.nextInstanceSearchUtc = default;
            this.nextFallbackUtc = default;
            this.ProbeService();
            this.ReportStatus();
        }

        ImGui.Spacing();

        // ── 关键词 ──
        ImGui.Text("命中关键词（逗号 / 分号 / 换行分隔，留空则不触发）");
        var keywords = this.config.Keywords;
        if (ImGui.InputText("##whisperKeywords", ref keywords, 512))
        {
            this.config.Keywords = keywords;
            changed = true;
        }

        var matchMode = this.config.MatchMode;
        if (ImGui.Combo("匹配方式", ref matchMode, "包含\0全等（去首尾空白）\0开头匹配\0", 3))
        {
            this.config.MatchMode = matchMode;
            changed = true;
        }

        var ignoreCase = this.config.IgnoreCase;
        if (ImGui.Checkbox("忽略大小写", ref ignoreCase))
        {
            this.config.IgnoreCase = ignoreCase;
            changed = true;
        }

        ImGui.Spacing();

        // ── 动作 ──
        var logoutOnMatch = this.config.LogoutOnMatch;
        if (ImGui.Checkbox("命中后自动登出游戏", ref logoutOnMatch))
        {
            this.config.LogoutOnMatch = logoutOnMatch;
            changed = true;
        }

        var notifyInChat = this.config.NotifyInChat;
        if (ImGui.Checkbox("命中时在游戏聊天框提示", ref notifyInChat))
        {
            this.config.NotifyInChat = notifyInChat;
            changed = true;
        }

        var autoConnect = this.config.AutoConnect;
        if (ImGui.Checkbox("未连接时自动连接国服频道", ref autoConnect))
        {
            this.config.AutoConnect = autoConnect;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("连接后会出现在频道成员列表里；不想在线时可关掉本模块。");

        ImGui.Spacing();

        // ── 发送者过滤（可选）──
        ImGui.Text("只接受这些发送者（可选：化名 ID 或名字，逗号分隔，留空不限）");
        var senderFilter = this.config.SenderFilter;
        if (ImGui.InputText("##whisperSenderFilter", ref senderFilter, 256))
        {
            this.config.SenderFilter = senderFilter;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── 测试与记录 ──
        if (ImGui.Button("模拟命中（不登出）"))
            this.TryHandleCommand("test");

        ImGui.SameLine();
        if (ImGui.Button("测试完整流程（含登出）"))
        {
            this.BeginLogout("手动测试");
        }

        ImGui.SameLine();
        if (ImGui.Button("立即登出"))
            this.BeginLogout("手动命令");

        ImGui.Spacing();
        ImGui.TextDisabled($"命中次数：{this.matchCount}");
        ImGui.TextWrapped(this.lastHitSummary);

        return changed;
    }

    #endregion

    private readonly record struct WhisperHit(string SenderName, int SenderAliasID, string Text, bool FromTest);
}

/// <summary>模块配置（宿主按 Config 后缀成员绑定并保存）。</summary>
public sealed class LKami9WhisperLogoutConfig
{
    /// <summary>命中关键词，逗号 / 分号 / 换行分隔；留空则不触发任何动作。</summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>匹配方式：0 包含，1 全等（去首尾空白），2 开头匹配。</summary>
    public int MatchMode { get; set; }

    /// <summary>匹配时忽略大小写。</summary>
    public bool IgnoreCase { get; set; } = true;

    /// <summary>命中后执行 /logout 并点确认框；关闭则只在聊天框提示。</summary>
    public bool LogoutOnMatch { get; set; } = true;

    /// <summary>命中时在游戏聊天框打印一条提示。</summary>
    public bool NotifyInChat { get; set; } = true;

    /// <summary>国服频道未连接时，模块尝试驱动连接（会让自己出现在频道成员列表）。</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>发送者过滤：化名 ID 或显示名，逗号分隔；留空表示不限发送者。</summary>
    public string SenderFilter { get; set; } = string.Empty;
}

/// <summary>按引用比较对象，供对象图遍历去重（不用默认的 Equals/GetHashCode）。</summary>
internal sealed class ReferenceComparer : IEqualityComparer<object>
{
    public static readonly ReferenceComparer Instance = new();

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}
