using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using OmenTools.Interop.Game.AddonEvent;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI.Theme;

namespace LKami9.WhisperLogout;

/// <summary>
/// LKami9频道私聊登出
///
/// 监听游戏聊天框，收到 Omni 国服频道里「发送者 &gt;&gt; 内容」格式的私聊、
/// 且内容命中指定关键词时，自动 /logout 并确认，可远程让角色下线。
///
/// 实现思路（从简）：
/// - 不逆向 Omni 内部服务（那套反射 + 对象图遍历既复杂又脆弱，还会因干预宿主状态出事），
///   Omni 本来就会把频道私聊打印进游戏聊天框，直接监听 Dalamud 的 ChatMessage 事件即可，
///   纯 Dalamud API，零 Omni 内部依赖。
/// - 登出走游戏自己的文本命令执行器 RaptureShellModule.ExecuteCommandInner
///   （Dalamud 的 ICommandManager.ProcessCommand 只认自家命令，不会转发游戏，已实测证实），
///   确认框用 OmenTools 的 AddonSelectYesnoEvent.ClickYes()。
/// </summary>
public sealed unsafe class LKami9WhisperLogout : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9频道私聊登出",
        Description = "监听游戏聊天框里的 Omni 国服频道私聊，命中指定关键词时自动 /logout 并确认，可远程让角色下线。",
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

    // ── 运行时状态 ────────────────────────────────────────────────────────────
    private bool chatSubscribed;
    private bool frameworkSubscribed;
    private bool logoutPending;
    private bool logoutDialogSeen;
    private DateTime logoutDialogSeenUtc;
    private DateTime logoutDeadlineUtc;
    private string status = "尚未开始监听。";
    private string lastHitSummary = "还没有命中记录。";
    private int matchCount;

    private List<string> keywordCache = [];
    private string keywordCacheSource = "\u0000";
    private List<int> chatTypeFilter = [];
    private string chatTypeCacheSource = "\u0000";
    private List<string> senderNameFilter = [];
    private string senderCacheSource = "\u0000";

    // 诊断日志限流：每种 LogKind 只记前几条，避免公聊刷屏时把日志也刷爆
    private readonly Dictionary<XivChatType, int> diagnosticCounts = [];
    private DateTime nextDiagnosticResetUtc;

    private System.Numerics.Vector4 WarnVec => OmniTheme.Tokens.Warning;
    private System.Numerics.Vector4 GoodVec => OmniTheme.Tokens.Success;
    private System.Numerics.Vector4 BadVec  => OmniTheme.Tokens.Error;

    #region 生命周期

    protected override void OnEnable()
    {
        this.status = "正在监听聊天框…";
        this.SubscribeChat();
        this.SubscribeFramework();
    }

    protected override void OnDisable()
    {
        this.UnsubscribeChat();
        this.UnsubscribeFramework();
        this.logoutPending = false;
        this.status = "已停止监听。";
    }

    protected override void OnDispose()
    {
        this.UnsubscribeChat();
        this.UnsubscribeFramework();
    }

    #endregion

    #region 聊天框监听

    private void SubscribeChat()
    {
        if (this.chatSubscribed)
            return;
        DalamudServices.ChatGUI.ChatMessage += this.OnChatMessage;
        this.chatSubscribed = true;
    }

    private void UnsubscribeChat()
    {
        if (!this.chatSubscribed)
            return;
        DalamudServices.ChatGUI.ChatMessage -= this.OnChatMessage;
        this.chatSubscribed = false;
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

    /// <summary>
    /// 聊天框消息在框架线程触发，可直接处理。
    /// Omni 国服频道的消息格式为「发送者 &gt;&gt; 内容」，按分隔符拆开后匹配关键词。
    /// </summary>
    private void OnChatMessage(IHandleableChatMessage message)
    {
        try
        {
            if (message.IsHandled)
                return;

            var text = message.Message.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return;

            // 诊断：把频道格式的消息详情打出来，便于第一次使用时确认私聊/公聊的实际格式与 chat type
            this.LogDiagnostic(message.LogKind, text);

            if (!this.TryParseChannelMessage(text, out var sender, out var content))
                return;
            if (!this.ChatTypeAllowed(message.LogKind))
                return;
            if (!this.SenderAllowed(sender))
                return;
            if (!this.IsMatch(content))
                return;

            this.matchCount++;
            this.lastHitSummary = $"{DateTime.Now:HH:mm:ss} {sender}：{content}";

            if (this.config.NotifyInChat)
                PrintToChat($"[频道私聊登出] 命中：{sender}：{content}");

            if (!this.config.LogoutOnMatch)
            {
                this.status = $"命中（仅提示模式）：{content}";
                return;
            }

            this.BeginLogout($"命中关键词：{content}");
        }
        catch (Exception ex)
        {
            LogWarning($"[LKami9WhisperLogout] 处理聊天消息异常：{ex}");
        }
    }

    /// <summary>按「发送者 分隔符 内容」拆消息；拆不开说明不是频道消息。</summary>
    private bool TryParseChannelMessage(string text, out string sender, out string content)
    {
        sender = string.Empty;
        content = string.Empty;

        var separator = this.config.Separator;
        if (string.IsNullOrEmpty(separator))
            separator = " >> ";

        var index = text.IndexOf(separator, StringComparison.Ordinal);
        if (index <= 0)
            return false;

        sender = text[..index].Trim();
        content = text[(index + separator.Length)..].Trim();
        return sender.Length > 0 && content.Length > 0;
    }

    private void LogDiagnostic(XivChatType logKind, string text)
    {
        if (!this.config.Diagnostics)
            return;
        if (!text.Contains(" >> ", StringComparison.Ordinal))
            return;

        // 每 10 秒清一次计数，保证突发流量下每种类型仍能记到几条
        if (DateTime.UtcNow >= this.nextDiagnosticResetUtc)
        {
            this.nextDiagnosticResetUtc = DateTime.UtcNow.AddSeconds(10);
            this.diagnosticCounts.Clear();
        }

        var count = this.diagnosticCounts.GetValueOrDefault(logKind);
        if (count >= 3)
            return;
        this.diagnosticCounts[logKind] = count + 1;

        Log(
            $"[LKami9WhisperLogout][诊断] LogKind={(int)logKind}({logKind}) 文本={text}");
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

    /// <summary>发送者白名单：留空表示不限；支持名字（大小写按忽略大小写设置）。</summary>
    private bool SenderAllowed(string sender)
    {
        this.RefreshCaches();

        if (this.senderNameFilter.Count == 0)
            return true;

        var comparison = this.config.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return this.senderNameFilter.Any(name => string.Equals(name, sender, comparison));
    }

    /// <summary>ChatType 白名单：留空表示不限；填写后用日志里看到的 LogKind 数字收紧（如只收私聊）。</summary>
    private bool ChatTypeAllowed(XivChatType logKind)
    {
        this.RefreshCaches();

        if (this.chatTypeFilter.Count == 0)
            return true;

        return this.chatTypeFilter.Contains((int)logKind);
    }

    private void RefreshCaches()
    {
        if (!string.Equals(this.keywordCacheSource, this.config.Keywords, StringComparison.Ordinal))
        {
            this.keywordCacheSource = this.config.Keywords;
            this.keywordCache = SplitEntries(this.config.Keywords);
        }

        if (!string.Equals(this.senderCacheSource, this.config.SenderFilter, StringComparison.Ordinal))
        {
            this.senderCacheSource = this.config.SenderFilter;
            this.senderNameFilter = SplitEntries(this.config.SenderFilter);
        }

        if (!string.Equals(this.chatTypeCacheSource, this.config.ChatTypeFilter, StringComparison.Ordinal))
        {
            this.chatTypeCacheSource = this.config.ChatTypeFilter;
            this.chatTypeFilter = SplitEntries(this.config.ChatTypeFilter)
                .Where(entry => int.TryParse(entry, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .Select(int.Parse)
                .ToList();
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

    #region 登出

    private void OnFrameworkUpdate(IFramework framework) => this.DriveLogoutConfirm();

    private void BeginLogout(string reason)
    {
        if (this.logoutPending)
            return;

        if (!this.IsLoggedIn())
        {
            this.status = "当前未登录角色，跳过登出。";
            return;
        }

        try
        {
            // 必须走游戏自己的文本命令执行器：
            // Dalamud 的 ICommandManager.ProcessCommand 只查 Dalamud 自己注册的命令表，
            // 查不到就静默 return false，根本不会把 /logout 交给游戏（实测表现为毫无反应）。
            // RaptureShellModule.ExecuteCommandInner 就是游戏处理聊天框命令的那条路。
            var command = Utf8String.FromString("/logout");
            try
            {
                RaptureShellModule.Instance()->ExecuteCommandInner(command, UIModule.Instance());
            }
            finally
            {
                // Utf8String 的 Dispose 在 FCS 里是私有的，无法主动释放；
                // 一次性命令字符串的少量原生分配可忽略（不会每次调用都发生）。
            }
        }
        catch (Exception ex)
        {
            this.status = $"发送 /logout 失败：{ex.Message}";
            LogWarning($"[LKami9WhisperLogout] 发送 /logout 失败：{ex}");
            return;
        }

        this.logoutPending = true;
        this.logoutDialogSeen = false;
        this.logoutDialogSeenUtc = default;
        this.logoutDeadlineUtc = DateTime.UtcNow.AddSeconds(25);
        this.status = $"已发送 /logout（{reason}），等待确认框…";
        Log($"[LKami9WhisperLogout] 已通过游戏命令执行器发送 /logout（{reason}）");
    }

    private void DriveLogoutConfirm()
    {
        if (!this.logoutPending)
            return;

        if (DateTime.UtcNow >= this.logoutDeadlineUtc)
        {
            this.logoutPending = false;
            this.status = "登出确认框未出现（可能在战斗 / 副本中，或已被手动处理）。";
            LogWarning("[LKami9WhisperLogout] 等待登出确认框超时（25 秒未见 SelectYesno 或未能点击）");
            return;
        }

        try
        {
            // 对话框是否在场单独判断一次，便于区分“没弹窗”和“弹了没点上”
            var dialogVisible = OmenTools.Info.Game.Data.Addons.SelectYesno != null;
            if (dialogVisible && !this.logoutDialogSeen)
            {
                this.logoutDialogSeen = true;
                Log("[LKami9WhisperLogout] 检测到登出确认框（SelectYesno），尝试点击“是”");
            }

            if (AddonSelectYesnoEvent.ClickYes())
            {
                this.logoutPending = false;
                this.status = "已确认登出。";
                Log("[LKami9WhisperLogout] 已点击登出确认框的“是”");
            }
            else if (this.logoutDialogSeen && DateTime.UtcNow >= this.logoutDialogSeenUtc)
            {
                // 对话框在，但点了至少 3 秒还没点上——大概率是节点没就绪，继续重试到超时
                this.logoutDialogSeenUtc = DateTime.UtcNow.AddSeconds(3);
                Log("[LKami9WhisperLogout] 确认框在场但尚未点上，继续重试");
            }
        }
        catch (Exception ex)
        {
            this.logoutPending = false;
            this.status = $"点击登出确认框失败：{ex.Message}";
            LogWarning($"[LKami9WhisperLogout] 点击登出确认框异常：{ex}");
        }
    }

    #endregion

    #region 命令与界面

    public override bool TryHandleCommand(string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "test":
                this.RefreshCaches();
                this.status = "已模拟一次命中（未登出）。";
                PrintToChat($"[频道私聊登出] 模拟命中：{this.config.Keywords}（未登出）");
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

    /// <summary>游戏聊天框输出；ChatGui 不可用时静默。</summary>
    private static void PrintToChat(string message)
    {
        try
        {
            DalamudServices.ChatGUI?.Print(message);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>是否已登录角色；读不到状态时放行（让游戏的命令执行器自己去判断）。</summary>
    private bool IsLoggedIn()
    {
        try
        {
            return DalamudServices.PlayerState == null || DalamudServices.PlayerState.ContentId != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>安全日志：PluginLog 因故不可用时静默，绝不影响主流程、更别在 catch 里二次抛异常。</summary>
    private static void Log(string message)
    {
        try
        {
            DalamudServices.PluginLog?.Information(message);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>安全警告日志。</summary>
    private static void LogWarning(string message)
    {
        try
        {
            DalamudServices.PluginLog?.Warning(message);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 动作按钮点击冷却：ImGui 按钮在 ID 冲突 / 同帧重复渲染时会连续返回 true，
    /// “立即登出”这类按钮被误触发的后果比刷屏严重得多，统一挡一下。
    /// </summary>
    private bool ActionAllowed()
    {
        if (DateTime.UtcNow < this.nextActionUtc)
            return false;
        this.nextActionUtc = DateTime.UtcNow.AddMilliseconds(600);
        return true;
    }

    private void ReportStatus()
    {
        // 节流：按钮被重复触发（ImGui ID 冲突时会这样）时不刷屏
        if (DateTime.UtcNow < this.nextStatusPrintUtc)
            return;
        this.nextStatusPrintUtc = DateTime.UtcNow.AddSeconds(3);

        PrintToChat($"[频道私聊登出] 监听中，命中 {this.matchCount} 次");
    }

    public override bool DrawSettings()
    {
        var changed = false;

        ImGui.TextWrapped(this.status);
        ImGui.Spacing();

        // ── 匹配规则 ──
        ImGui.Separator();
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

        ImGui.Text("消息分隔符（Omni 频道消息格式：发送者 >> 内容）");
        var separator = this.config.Separator;
        if (ImGui.InputText("##whisperSeparator", ref separator, 32))
        {
            this.config.Separator = separator;
            changed = true;
        }

        ImGui.Spacing();

        // ── 收窄规则（可选）──
        ImGui.Separator();
        ImGui.Text("只接受这些发送者（可选：名字，逗号分隔，留空不限）");
        var senderFilter = this.config.SenderFilter;
        if (ImGui.InputText("##whisperSenderFilter", ref senderFilter, 256))
        {
            this.config.SenderFilter = senderFilter;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("公聊和私聊在聊天框里格式相同，若只想让私聊触发，可配合下面的 ChatType 过滤。");

        ImGui.Text("只接受这些 ChatType（可选：LogKind 数字，逗号分隔，留空不限）");
        var chatTypeFilter = this.config.ChatTypeFilter;
        if (ImGui.InputText("##whisperChatTypeFilter", ref chatTypeFilter, 128))
        {
            this.config.ChatTypeFilter = chatTypeFilter;
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("开启下方“诊断日志”后发一条私聊，日志里会打印该消息的 LogKind 数字，填到这里即可只收私聊。");

        var diagnostics = this.config.Diagnostics;
        if (ImGui.Checkbox("诊断日志（记录频道消息的 ChatType，便于收紧规则）", ref diagnostics))
        {
            this.config.Diagnostics = diagnostics;
            changed = true;
        }

        ImGui.Spacing();

        // ── 动作 ──
        ImGui.Separator();
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

        ImGui.Spacing();
        ImGui.Separator();

        // ── 测试与记录 ──
        if (ImGui.Button("模拟命中（不登出）##whisperTest") && this.ActionAllowed())
            this.TryHandleCommand("test");

        ImGui.SameLine();
        if (ImGui.Button("测试完整流程（含登出）##whisperTestFull") && this.ActionAllowed())
            this.BeginLogout("手动测试");

        ImGui.SameLine();
        if (ImGui.Button("立即登出##whisperLogoutNow") && this.ActionAllowed())
            this.BeginLogout("手动命令");

        ImGui.Spacing();
        ImGui.TextDisabled($"命中次数：{this.matchCount}");
        ImGui.TextWrapped(this.lastHitSummary);

        return changed;
    }

    #endregion

    private DateTime nextActionUtc;
    private DateTime nextStatusPrintUtc;
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

    /// <summary>Omni 频道消息里发送者与内容之间的分隔符，默认“ &gt;&gt; ”。</summary>
    public string Separator { get; set; } = " >> ";

    /// <summary>发送者白名单：只接受这些名字（逗号分隔）；留空表示不限发送者。</summary>
    public string SenderFilter { get; set; } = string.Empty;

    /// <summary>ChatType 白名单：只接受这些 LogKind 数字（逗号分隔）；留空表示不限。</summary>
    public string ChatTypeFilter { get; set; } = string.Empty;

    /// <summary>命中后执行 /logout 并点确认框；关闭则只在聊天框提示。</summary>
    public bool LogoutOnMatch { get; set; } = true;

    /// <summary>命中时在游戏聊天框打印一条提示。</summary>
    public bool NotifyInChat { get; set; } = true;

    /// <summary>打印频道消息的 ChatType 诊断日志（每种类型限流 3 条 / 10 秒）。</summary>
    public bool Diagnostics { get; set; } = true;
}
