using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using OmenTools.OmenService;
using Dalamud.Interface;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmniToolbox.Commands;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.Notifications;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;
using TreeTools.Lifecycle;

namespace OmniToolbox.TreeHouseOnline;

/// <summary>
///     本地好友列表：把好友记在自己的花名册里，不受游戏好友栏上限约束。
///     添加流程复刻游戏内「选人 → 加入」，窗口与控件沿用 Omni 公共组件。
/// </summary>
[ModuleApiVersion(1)]
public sealed class LKami9LocalFriendList : OmniModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Interface,
        Author = "LKami9",
        Commands = [new("CommandOpen", OmniCommandCatalog.FormatModule(nameof(LKami9LocalFriendList), "open"))]
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"] = "本地好友列表",
            ["Description"] = "在本地记录好友，不受游戏好友栏上限的约束。支持导出备份。",
            ["CommandOpen"] = "打开本地好友列表窗口",
            ["OpenWindow"] = "打开窗口",
            ["Close"] = "关闭",
            ["Collapse"] = "收起",
            ["Expand"] = "展开",
            ["Lock"] = "固定窗口",
            ["Unlock"] = "解除固定",
            ["SearchHint"] = "搜索名字、备注或分组",
            ["GroupAll"] = "全部分组",
            ["GroupNone"] = "未分组",
            ["OnlineOnly"] = "只看同屏",
            ["ColStatus"] = "状态",
            ["ColName"] = "名字",
            ["ColWorld"] = "服务器",
            ["ColGroup"] = "分组",
            ["ColLastSeen"] = "最后见面",
            ["ColActions"] = "操作",
            ["StatusNearby"] = "同屏",
            ["StatusOffline"] = "—",
            ["StatusQueried"] = "已查",
            ["StatusQueriedTip"] = "已于 {0} 在玩家搜索中查询，结果见游戏的「玩家搜索」窗口",
            ["QueriedHint"] = "查询结果在游戏「玩家搜索」窗口（仅当前服务器）",
            ["NeverSeen"] = "从未见面",
            ["JustNow"] = "刚刚",
            ["MinutesAgo"] = "{0} 分钟前",
            ["HoursAgo"] = "{0} 小时前",
            ["DaysAgo"] = "{0} 天前",
            ["Edit"] = "编辑",
            ["Delete"] = "删除",
            ["Save"] = "保存",
            ["Cancel"] = "取消",
            ["ConfirmDelete"] = "确认删除该好友？",
            ["GroupHint"] = "分组名，留空为未分组",
            ["ColorLabel"] = "好友名字颜色",
            ["ColorEnable"] = "启用好友颜色",
            ["Add"] = "添加",
            ["Added"] = "已加入本地好友：{0}",
            ["Deleted"] = "已从本地好友移除：{0}",
            ["Duplicate"] = "这条记录已经在花名册里了。",
            ["NameRequired"] = "请填写角色名。",
            ["WorldRequired"] = "找不到该服务器，请检查服务器名。",
            ["Exported"] = "已导出到：{0}",
            ["Imported"] = "已导入 {0} 条记录。",
            ["ImportNothing"] = "没有可导入的新记录。",
            ["FileError"] = "读写花名册失败，详见插件日志。",
            ["LoadFailed"] = "花名册读取失败，已备份原文件。",
            ["SelfSkip"] = "不能把自己加进好友列表。",
            ["MenuAdd"] = "加入本地好友",
            ["MenuAlready"] = "已在本地好友中",
            ["ResetFilters"] = "重置筛选",
            ["QueryOnline"] = "查询",
            ["ClickToCopy"] = "点击复制名字",
            ["Copied"] = "已复制：{0}",
            ["QuerySent"] = "已在玩家搜索中查询：{0}（仅当前服务器）",
            ["QueryFailed"] = "查询失败，请确认已登录角色。",
            ["Import"] = "导入",
            ["Export"] = "导出",
            ["ImportHint"] = "导入 JSON 文件的完整路径",
            ["AddSection"] = "添加好友",
            ["DataSection"] = "数据与备份",
            ["ManualWorldHint"] = "服务器名或 ID",
            ["NearbyEmpty"] = "附近没有可以添加的玩家。",
            ["NearbyMore"] = "还有 {0} 位未显示",
            ["Empty"] = "花名册还是空的。右键玩家选择「加入本地好友」，或点上方「添加好友」。",
            ["Summary"] = "共 {0} 人 · 同屏 {1} 人"
        });

    private const string WindowID = "##LocalFriendList";
    private const string CollapsedID = "##LocalFriendListCollapsed";
    private const float  ScanDistance = 40f;
    private const int    ScanIntervalFrames = 20;
    private const long   SaveDelayMs = 1200;
    private const long   LastSeenRefreshSeconds = 60;
    private const int    MaxNearbyRows = 4;

    /// <summary>
    ///     「只看未分组」的筛选标记。空字符串已经被「全部分组」占用，
    ///     直接拿 "" 当筛选值会被 <see cref="BuildRows" /> 当成「不筛选」，于是点「未分组」显示的是全部。
    /// </summary>
    private const string NoGroupMarker = "__none__";

    /// <summary>显示在设置面板底部，用来确认当前加载的确实是这份代码。</summary>
    private const string ModuleVersion = "1.10.4";

    /// <summary>Omni 菜单前缀用的颜色类型（粉）。</summary>
    private const int MenuPrefixColorType = 10;

    private readonly FriendStore store = new();
    private Settings config = new();

    private IObjectTable? objectTable;
    private IContextMenu? contextMenu;
    private IClientState? clientState;
    private INamePlateGui? namePlateGui;
    private ICommandManager? commandManager;
    private OmniRecordWindow? frame;

    private readonly HashSet<string> friendKeys = new(StringComparer.OrdinalIgnoreCase);
    private long friendKeyRevision = -1;
    private long namePlateRevision = -1;

    /// <summary>本次会话内每个好友的最近查询时间，仅用于界面反馈，不落盘。</summary>
    private readonly Dictionary<string, long> queriedAt = new(StringComparer.Ordinal);

    private int scanCounter;
    private string search = string.Empty;
    private string groupFilter = string.Empty;
    private bool onlineOnly;
    private bool windowOpen;
    private bool configDirty;
    private string importPath = string.Empty;
    private string manualName = string.Empty;
    private string manualWorld = string.Empty;
    private string editName = string.Empty;
    private string editGroup = string.Empty;
    private string editNote = string.Empty;
    private FriendRecord? editTarget;
    private FriendRecord? deleteTarget;
    private bool editPopupRequested;
    private bool deletePopupRequested;
    private bool addPopupRequested;
    private int popupFrame = -1;
    private readonly Queue<string> pendingNotices = new();
    private readonly Queue<string> pendingCommands = new();

    #region 生命周期

    /// <summary>
    ///     取服务时兜底。<c>Resolve&lt;T&gt;()</c> 在服务缺失时**抛异常**并中断整个 OnStart，
    ///     所以每次解析都要兜住，不能假设服务一定在 Dalamud 容器里。
    /// </summary>
    private T? ResolveService<T>() where T : class
    {
        try
        {
            return Resolve<T>();
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(
                $"LocalFriendList: service {typeof(T).Name} unavailable ({exception.Message})");
            return null;
        }
    }

    protected override void OnStart(FeatureLifetime activation)
    {
        objectTable = ResolveService<IObjectTable>();
        contextMenu = ResolveService<IContextMenu>();
        clientState = ResolveService<IClientState>();
        namePlateGui = ResolveService<INamePlateGui>();
        commandManager = ResolveService<ICommandManager>();

        store.Initialize(ResolveDataDirectory());
        if (!store.Load())
            ReportNotice(OmniLoc.Get(this, "LoadFailed"));

        DropLegacyNpcRecords();

        groupFilter = store.GroupFilter;
        // 旧版把「未分组」的显示名直接当筛选值存了，这里纠正成标记，否则升级后列表会变空。
        if (groupFilter.Length > 0 && groupFilter == OmniLoc.Get(this, "GroupNone"))
        {
            groupFilter = NoGroupMarker;
            store.GroupFilter = groupFilter;
        }

        onlineOnly = store.OnlineOnly;

        frame = new OmniRecordWindow(
            new WindowSettings(store),
            WindowID,
            CollapsedID,
            new Vector2(480f, 320f),
            () => store.Save());

        var framework = DalamudServices.Framework;
        activation.OnStopping(() => framework.Update -= OnFrameworkUpdate);
        framework.Update += OnFrameworkUpdate;

        var uiBuilder = DalamudServices.PluginInterface.UiBuilder;
        activation.OnStopping(() => uiBuilder.Draw -= OnDraw);
        uiBuilder.Draw += OnDraw;

        if (contextMenu is not null)
        {
            contextMenu.OnMenuOpened += OnContextMenuOpened;
            activation.OnStopping(() => contextMenu.OnMenuOpened -= OnContextMenuOpened);
        }

        if (namePlateGui is not null)
        {
            namePlateGui.OnNamePlateUpdate += OnNamePlateUpdate;
            activation.OnStopping(() => namePlateGui.OnNamePlateUpdate -= OnNamePlateUpdate);
        }
    }

    protected override void OnStop()
    {
        windowOpen = false;
        frame?.Reset();
        store.Flush();
    }

    #endregion

    #region 命令与设置面板

    public override bool HasSettings => true;

    public override bool TryHandleCommand(string arguments)
    {
        if (!string.Equals(arguments, "open", StringComparison.OrdinalIgnoreCase))
            return false;

        OpenWindow();
        return true;
    }

    public override bool DrawSettings()
    {
        DrawCompactPanel();
        var changed = configDirty;
        configDirty = false;
        return changed;
    }

    public override bool ResetSettings() => false;

    #endregion

    #region 运行时

    private void OpenWindow()
    {
        windowOpen = true;
        frame?.Open();
    }

    private void CloseWindow()
    {
        windowOpen = false;
        frame?.Close();
        store.Flush();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        DrainCommands();
        SyncNamePlate();
        store.TickSave();
        DrawNotices();

        if (++scanCounter < ScanIntervalFrames)
            return;

        scanCounter = 0;
        ScanNearby();
    }

    private void ScanNearby()
    {
        foreach (var record in store.Records)
            record.Nearby = false;

        if (objectTable is null)
            return;

        var local = objectTable.LocalPlayer;
        var localName = local?.Name.TextValue;
        var territory = CurrentTerritory();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 建一次索引，避免每个玩家都对全表做一次线性查找（好友多时是 O(n²)）。
        var index = new Dictionary<string, FriendRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in store.Records)
            index[record.Key] = record;

        foreach (var battle in objectTable.PlayerObjects)
        {
            if (battle is not IPlayerCharacter player)
                continue;

            if (player.CurrentDistance > ScanDistance)
                continue;

            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!string.IsNullOrEmpty(localName) &&
                string.Equals(name, localName, StringComparison.Ordinal))
                continue;

            // 跨服来访时 CurrentWorld 是本服，记录里存的是 HomeWorld，两个都要匹配，
            // 否则跨服好友永远标不出「同屏」。
            if (!index.TryGetValue(MakeKey(name, player.CurrentWorld.RowId), out var record) &&
                !index.TryGetValue(MakeKey(name, player.HomeWorld.RowId), out record))
                continue;

            record.Nearby = true;
            if (now - record.LastSeenAt < LastSeenRefreshSeconds)
                continue;

            record.LastSeenAt = now;
            record.LastSeenTerritory = territory;
            store.MarkDirty();
        }
    }

    /// <summary>
    ///     把好友颜色应用到游戏内名牌。走 Dalamud 官方名牌接口
    ///     （<see cref="INamePlateGui" />），不 hook、不碰原生内存布局；
    ///     <c>TextColor</c> 与 <c>NamePlateNumberArray.NameTextColor</c> 同为 0xAABBGGRR。
    /// </summary>
    private void OnNamePlateUpdate(INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!config.UseFriendColor)
            return;

        RebuildFriendKeys();
        if (friendKeys.Count == 0)
            return;

        var color = ImGui.ColorConvertFloat4ToU32(
            new Vector4(config.FriendColorR, config.FriendColorG, config.FriendColorB, 1f));

        foreach (var handler in handlers)
        {
            if (handler.PlayerCharacter is not { } player)
                continue;

            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // 好友可能站在自己的服务器，也可能跨服过来，两个世界都查一次。
            if (friendKeys.Contains(MakeKey(name, player.CurrentWorld.RowId)) ||
                friendKeys.Contains(MakeKey(name, player.HomeWorld.RowId)))
                handler.TextColor = color;
        }
    }

    /// <summary>
    ///     花名册一有变动就主动请求重绘名牌。名牌颜色只在收到更新事件时写入，
    ///     新加的好友如果名牌已经渲染过就不会再刷新，于是表现为「必须重新勾选才变色」。
    ///     在这里按版本号兜底，任何来源的增删改都不会漏。
    /// </summary>
    private void SyncNamePlate()
    {
        if (namePlateRevision == store.Revision)
            return;

        namePlateRevision = store.Revision;
        if (config.UseFriendColor)
            namePlateGui?.RequestRedraw();
    }

    private void RebuildFriendKeys()
    {
        if (friendKeyRevision == store.Revision)
            return;

        friendKeyRevision = store.Revision;
        friendKeys.Clear();

        foreach (var record in store.Records)
            friendKeys.Add(MakeKey(record.Name, record.WorldId));
    }

    private static string MakeKey(string name, uint worldId) => $"{name}@{worldId}";

    private void OnDraw()
    {
        if (!windowOpen || frame is null)
            return;

        frame.Draw(Title, OmniLoc.Get(this, "Expand"), DrawWindowBody, null);
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default)
            return;

        if (args.Target is not MenuTargetDefault target)
            return;

        var name = target.TargetName;
        if (string.IsNullOrWhiteSpace(name))
            return;

        var world = target.TargetHomeWorld.RowId;
        var isPlayer = target.TargetObject is IPlayerCharacter;
        if (!isPlayer && (target.TargetContentId == 0 || world == 0))
            return;

        var local = objectTable?.LocalPlayer;
        if (local is not null && string.Equals(name, local.Name.TextValue, StringComparison.Ordinal))
            return;

        var exists = store.Find(name, world) is not null;

        args.AddMenuItem(
            new MenuItem
            {
                Name = BuildMenuName(OmniLoc.Get(this, exists ? "MenuAlready" : "MenuAdd")),
                // Dalamud 的 Prefix 只能放一个 SeIconChar，两个字母必须写进 Name。
                UseDefaultPrefix = false,
                IsEnabled = !exists,
                Priority = 0,
                OnClicked = _ => AddFriend(name, world, target.TargetContentId, string.Empty, string.Empty)
            });
    }

    /// <summary>
    ///     拼出「OT 文本」：两个方框字母 O / T 上粉色，后面接菜单文本。
    ///     注意用 Dalamud 的 <see cref="SeStringBuilder" />（配 <c>AddUiForeground</c>），
    ///     不是 Lumina 那个（它才有 <c>PushColorType</c>，但产出的是 ReadOnlySeString）。
    /// </summary>
    private static SeString BuildMenuName(string text)
    {
        var builder = new SeStringBuilder();
        builder.AddUiForeground((ushort)MenuPrefixColorType);
        builder.AddText($"{(char)SeIconChar.BoxedLetterO}{(char)SeIconChar.BoxedLetterT}");
        builder.AddUiForegroundOff();
        builder.AddText($" {text}");
        return builder.Build();
    }

    #endregion

    #region 窗口

    private void DrawWindowBody()
    {
        using var theme = PushWindowTheme();

        DrawHeader();
        DrawToolbar();

        // 窗口带 NoScrollbar，内容必须精确填满可用高度，否则底部会被裁掉。
        var summaryHeight = ImGui.GetTextLineHeightWithSpacing();
        var tableHeight = MathF.Max(OmniTheme.Scale(90f), ImGui.GetContentRegionAvail().Y - summaryHeight);
        DrawTable(tableHeight);
        DrawSummary();
        DrawPopups();
    }

    /// <summary>
    ///     <see cref="OmniRecordWindow" /> 把窗口底色写死为深色玻璃（0.045, 0.047, 0.052），
    ///     与浅色主题下的设置面板并排会非常割裂。这里在绘制内容前重绘一层主题底色，
    ///     并把文字、边框、表格配色接回 <see cref="OmniTheme.Tokens" />，控件本身已跟随主题。
    /// </summary>
    private WindowThemeScope PushWindowTheme()
    {
        var tokens = OmniTheme.Tokens;
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        // 圆角必须取窗口当前值，否则填色与窗口形状不一致，四角会露出原来的深色底。
        ImGui.GetWindowDrawList().AddRectFilled(
            position,
            position + size,
            ImGui.GetColorU32(tokens.Background),
            ImGui.GetStyle().WindowRounding);

        ImGui.PushStyleColor(ImGuiCol.Text, tokens.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, tokens.Text with { W = 0.55f });
        ImGui.PushStyleColor(ImGuiCol.Border, tokens.Border);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, tokens.Surface);
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, tokens.Surface with { W = 0.45f });
        return default;
    }

    private void DrawHeader()
    {
        var rowTop = ImGui.GetCursorPosY();
        ImGui.AlignTextToFramePadding();
        OmniControls.SectionLabel(Title);
        ImGui.SameLine();

        var size = OmniTheme.Scale(new Vector2(22f, 20f));
        var spacing = OmniTheme.Scale(3f);
        var controlsWidth = size.X * 3f + spacing * 2f;

        // 右对齐到内容区右边界，并按文字行高把按钮垂直居中，避免只对齐顶边造成的歪斜。
        ImGui.SetCursorPosX(MathF.Max(
            ImGui.GetCursorPosX(),
            ImGui.GetWindowContentRegionMax().X - controlsWidth));
        // AlignTextToFramePadding 是按 frame 高度居中文字，所以按钮也要以 frame 高度算偏移。
        ImGui.SetCursorPosY(rowTop + MathF.Max(0f, (ImGui.GetFrameHeight() - size.Y) * 0.5f));

        // 折叠位置必须在按钮推进光标之前取，否则收起后的小图标会落到错误坐标。
        var collapsePosition = ImGui.GetCursorScreenPos();
        if (OmniRecordWindow.DrawHeaderButton(
                "##lflCollapse",
                FontAwesomeIcon.ChevronCircleUp,
                size,
                OmniLoc.Get(this, "Collapse")))
            frame?.Collapse(collapsePosition);
        ImGui.SameLine(0f, spacing);

        var locked = store.Window.Locked;
        if (OmniRecordWindow.DrawHeaderButton(
                "##lflLock",
                locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
                size,
                OmniLoc.Get(this, locked ? "Unlock" : "Lock")))
        {
            store.Window.Locked = !locked;
            store.Flush();
        }

        ImGui.SameLine(0f, spacing);
        if (OmniRecordWindow.DrawHeaderButton(
                "##lflClose",
                FontAwesomeIcon.Times,
                size,
                OmniLoc.Get(this, "Close")))
            CloseWindow();
    }

    #endregion

    #region 设置面板

    private void DrawCompactPanel()
    {
        if (OmniControls.SmallButton(OmniLoc.Get(this, "OpenWindow"), false))
            OpenWindow();

        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "Summary"), store.Records.Count, CountNearby()));

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, "ColorLabel"));
        var useColor = config.UseFriendColor;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "ColorEnable"), ref useColor))
        {
            config.UseFriendColor = useColor;
            configDirty = true;
            namePlateGui?.RequestRedraw();
        }

        ImGui.SameLine();
        var color = new Vector4(config.FriendColorR, config.FriendColorG, config.FriendColorB, 1f);
        if (OmniControls.ColorEdit("##lflFriendColor", ref color))
        {
            config.FriendColorR = color.X;
            config.FriendColorG = color.Y;
            config.FriendColorB = color.Z;
            configDirty = true;
            namePlateGui?.RequestRedraw();
        }

        ImGui.Spacing();
        OmniControls.SectionLabel(OmniLoc.Get(this, "DataSection"));
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Export"), false))
            Export();
        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Import"), false))
            Import();

        OmniControls.InputTextWithHint(
            "##lflImportPath",
            OmniLoc.Get(this, "ImportHint"),
            ref importPath,
            260,
            OmniTheme.Scale(300f));

        ImGui.Spacing();
        ImGui.TextDisabled($"LocalFriendList v{ModuleVersion}");
    }

    #endregion

    #region 工具栏与表格

    private void DrawToolbar()
    {
        var style = ImGui.GetStyle();
        // BeginCombo 内部会把宽度顶到不小于 preview 的实测宽度，给固定值会被撑宽并顶破右边界。
        var groupWidth = OmniControls.MeasureCombo(CurrentGroupLabel()).X;
        var searchWidth = MathF.Max(
            OmniTheme.Scale(90f),
            ImGui.GetContentRegionAvail().X - groupWidth - style.ItemSpacing.X);

        if (OmniControls.InputTextWithHint(
                "##lflSearch",
                OmniLoc.Get(this, "SearchHint"),
                ref search,
                96,
                searchWidth))
            configDirty = true;

        ImGui.SameLine();
        DrawGroupCombo(groupWidth);

        // 换行：搜索 + 排序 + 分组已占满一行，再挤两个按钮会溢出窗口。
        if (OmniControls.Checkbox(OmniLoc.Get(this, "OnlineOnly"), ref onlineOnly))
        {
            store.OnlineOnly = onlineOnly;
            store.MarkDirty();
            configDirty = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "ResetFilters"), false))
            ResetFilters();
        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "AddSection"), false))
            addPopupRequested = true;
    }

    private string CurrentGroupLabel() =>
        string.IsNullOrEmpty(groupFilter)
            ? OmniLoc.Get(this, "GroupAll")
            : groupFilter == NoGroupMarker ? OmniLoc.Get(this, "GroupNone") : groupFilter;

    private void DrawGroupCombo(float width)
    {
        var preview = CurrentGroupLabel();

        if (!OmniControls.BeginCombo("##lflGroup", preview, width))
            return;

        if (ImGui.Selectable(OmniLoc.Get(this, "GroupAll"), string.IsNullOrEmpty(groupFilter)))
            SetGroupFilter(string.Empty);

        var groups = store.CollectGroups();
        for (var index = 0; index < groups.Count; index++)
        {
            var raw = groups[index];
            // 空分组用专用标记参与筛选，否则和「全部分组」的二值撞在一起。
            var filterValue = raw.Length == 0 ? NoGroupMarker : raw;
            var group = raw.Length == 0 ? OmniLoc.Get(this, "GroupNone") : raw;
            if (ImGui.Selectable(
                    $"{group}##lflGroup{index}",
                    string.Equals(filterValue, groupFilter, StringComparison.Ordinal)))
                SetGroupFilter(filterValue);
        }

        ImGui.EndCombo();
    }

    private void SetGroupFilter(string group)
    {
        groupFilter = group;
        store.GroupFilter = group;
        store.MarkDirty();
        configDirty = true;
    }

    private void ResetFilters()
    {
        search = string.Empty;
        onlineOnly = false;
        groupFilter = string.Empty;
        store.OnlineOnly = false;
        store.GroupFilter = string.Empty;
        store.SortMode = "lastSeen";
        store.MarkDirty();
        configDirty = true;
    }

    private void DrawAddPopup()
    {
        if (addPopupRequested)
        {
            ImGui.OpenPopup("##lflAddPopup");
            addPopupRequested = false;
        }

        if (!ImGui.BeginPopup("##lflAddPopup"))
            return;

        var addWidth = OmniControls.CompactButtonSize(OmniLoc.Get(this, "Add")).X;
        var worldWidth = OmniTheme.Scale(120f);
        var nameWidth = MathF.Max(
            OmniTheme.Scale(100f),
            OmniTheme.Scale(300f) - worldWidth - addWidth - ImGui.GetStyle().ItemSpacing.X * 2f);

        OmniControls.InputTextWithHint("##lflManualName", OmniLoc.Get(this, "ColName"), ref manualName, 32, nameWidth);
        ImGui.SameLine();
        OmniControls.InputTextWithHint("##lflManualWorld", OmniLoc.Get(this, "ManualWorldHint"), ref manualWorld, 32, worldWidth);
        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Add"), false, new Vector2(addWidth, OmniTheme.SmallButtonSize().Y)))
            AddManual();

        ImGui.Separator();
        DrawNearbyList();

        ImGui.EndPopup();
    }

    private void DrawNearbyList()
    {
        if (objectTable is null)
            return;

        var local = objectTable.LocalPlayer;
        var localName = local?.Name.TextValue ?? string.Empty;
        var shown = 0;
        var hidden = 0;

        foreach (var battle in objectTable.PlayerObjects)
        {
            if (battle is not IPlayerCharacter player)
                continue;

            if (player.CurrentDistance > ScanDistance)
                continue;

            var name = player.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, localName, StringComparison.Ordinal))
                continue;

            // 窗口不可滚动，附近人多时必须限高，否则会把表格和统计行顶出窗口。
            if (shown >= MaxNearbyRows)
            {
                hidden++;
                continue;
            }

            shown++;
            var world = player.CurrentWorld.RowId;
            var exists = store.Find(name, world) is not null;

            ImGui.PushID($"lflNearby{name}{world}");
            ImGui.AlignTextToFramePadding();
            OmniControls.SectionLabel($"{name} · {WorldName(world)}");
            ImGui.SameLine(0f, OmniTheme.Scale(6f));
            if (exists)
                ImGui.TextDisabled(OmniLoc.Get(this, "MenuAlready"));
            else if (OmniControls.SmallButton(OmniLoc.Get(this, "Add"), false))
                AddFriend(name, world, 0, string.Empty, string.Empty);
            ImGui.PopID();
        }

        if (shown == 0)
            ImGui.TextDisabled(OmniLoc.Get(this, "NearbyEmpty"));
        else if (hidden > 0)
            ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "NearbyMore"), hidden));
    }

    private void DrawTable(float height)
    {
        var rows = BuildRows();
        if (rows.Count == 0)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, "Empty"));
            return;
        }

        var style = ImGui.GetStyle();
        var querySize = OmniControls.CompactButtonSize(OmniLoc.Get(this, "QueryOnline"), 52f, 12f);
        var editSize = OmniControls.CompactButtonSize(OmniLoc.Get(this, "Edit"), 52f, 12f);
        var deleteSize = OmniControls.CompactButtonSize(OmniLoc.Get(this, "Delete"), 52f, 12f);
        var actionsWidth = querySize.X + editSize.X + deleteSize.X + style.ItemSpacing.X * 2f;
        var rowHeight = MathF.Max(querySize.Y, ImGui.GetTextLineHeight()) + style.CellPadding.Y * 2f;
        var statusWidth = ImGui.CalcTextSize(OmniLoc.Get(this, "StatusNearby")).X;
        var timeWidth = ImGui.CalcTextSize(string.Format(OmniLoc.Get(this, "MinutesAgo"), 59)).X;

        string[] labels =
        [
            OmniLoc.Get(this, "ColStatus"),
            OmniLoc.Get(this, "ColName"),
            OmniLoc.Get(this, "ColWorld"),
            OmniLoc.Get(this, "ColGroup"),
            OmniLoc.Get(this, "ColLastSeen"),
            OmniLoc.Get(this, "ColActions")
        ];
        float[] minimum =
        [
            statusWidth, OmniTheme.Scale(70f), OmniTheme.Scale(56f), OmniTheme.Scale(56f), timeWidth, actionsWidth
        ];
        float[] desired =
        [
            statusWidth, OmniTheme.Scale(140f), OmniTheme.Scale(90f), OmniTheme.Scale(90f), timeWidth, actionsWidth
        ];

        using var table = OmniControls.DataTable(
            "##lflTable",
            labels,
            minimum,
            desired,
            out var detailLayout,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY |
            ImGuiTableFlags.SizingStretchProp,
            new Vector2(0f, height),
            1);
        if (!table)
            return;

        OmniControls.BeginTableHeaderRow();
        OmniControls.TableHeader(labels[0]);
        if (!detailLayout)
        {
            // 详情布局下表格只有一列，多画表头会越界。
            for (var index = 1; index < labels.Length; index++)
                OmniControls.TableHeader(labels[index]);
        }

        foreach (var record in rows)
        {
            ImGui.PushID(record.Key);
            ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

            OmniControls.NextTableField(labels[0], detailLayout);
            if (record.Nearby)
            {
                OmniControls.TableTextCentered(OmniLoc.Get(this, "StatusNearby"), rowHeight, OmniTheme.Tokens.Success);
            }
            else if (queriedAt.TryGetValue(record.Key, out var queryTime))
            {
                OmniControls.TableTextCentered(OmniLoc.Get(this, "StatusQueried"), rowHeight, OmniTheme.Tokens.Accent);
                if (ImGui.IsItemHovered())
                    OmniControls.HelpTooltip(
                        string.Format(OmniLoc.Get(this, "StatusQueriedTip"), FormatClock(queryTime)));
            }
            else
                OmniControls.TableTextCentered(OmniLoc.Get(this, "StatusOffline"), rowHeight, OmniTheme.Tokens.Secondary);

            OmniControls.NextTableField(labels[1], detailLayout);
            OmniControls.TableTextCentered(record.Name);

            // 点名字直接复制「名字@服务器」，省掉菜单里的复制项。
            if (ImGui.IsItemHovered())
            {
                var tip = string.IsNullOrWhiteSpace(record.Note)
                    ? OmniLoc.Get(this, "ClickToCopy")
                    : $"{record.Note}\n{OmniLoc.Get(this, "ClickToCopy")}";
                OmniControls.HelpTooltip(tip);
            }

            if (ImGui.IsItemClicked())
            {
                ImGui.SetClipboardText(FullName(record));
                ReportNotice(string.Format(OmniLoc.Get(this, "Copied"), FullName(record)));
            }

            OmniControls.NextTableField(labels[2], detailLayout);
            OmniControls.TableTextCentered(record.WorldId == 0 ? "—" : WorldName(record.WorldId));

            OmniControls.NextTableField(labels[3], detailLayout);
            OmniControls.TableTextCentered(
                string.IsNullOrEmpty(record.Group) ? OmniLoc.Get(this, "GroupNone") : record.Group);

            OmniControls.NextTableField(labels[4], detailLayout);
            OmniControls.TableTextCentered(FormatSince(record.LastSeenAt));

            OmniControls.NextTableField(labels[5], detailLayout);
            OmniControls.CenterTableItem(new Vector2(actionsWidth, querySize.Y), rowHeight);

            if (OmniControls.SmallButton($"{OmniLoc.Get(this, "QueryOnline")}##query", false, querySize))
                QueryOnline(record);

            ImGui.SameLine(0f, style.ItemSpacing.X);
            if (OmniControls.SmallButton($"{OmniLoc.Get(this, "Edit")}##edit", false, editSize))
                OpenEdit(record);

            ImGui.SameLine(0f, style.ItemSpacing.X);
            if (OmniControls.SmallButton($"{OmniLoc.Get(this, "Delete")}##delete", false, deleteSize))
            {
                deleteTarget = record;
                deletePopupRequested = true;
            }

            ImGui.PopID();
        }
    }

    private void DrawSummary()
    {
        var text = string.Format(OmniLoc.Get(this, "Summary"), store.Records.Count, CountNearby());
        if (queriedAt.Count > 0)
            text = $"{text}  ·  {OmniLoc.Get(this, "QueriedHint")}";

        ImGui.TextDisabled(text);
    }

    private void DrawPopups()
    {
        // 设置面板与独立窗口同帧都会走到这里，同一个 popup 不能重复 Begin。
        var frame = ImGui.GetFrameCount();
        if (popupFrame == frame)
            return;

        popupFrame = frame;
        DrawEditPopup();
        DrawDeletePopup();
        DrawAddPopup();
    }

    private void DrawEditPopup()
    {
        if (editPopupRequested)
        {
            ImGui.OpenPopup("##lflEdit");
            editPopupRequested = false;
        }

        if (editTarget is null)
            return;

        if (!ImGui.BeginPopup("##lflEdit"))
        {
            editTarget = null;
            return;
        }

        OmniControls.SectionLabel($"{editName} · {WorldName(editTarget.WorldId)}");

        OmniControls.InputTextWithHint("##lflEditGroup", OmniLoc.Get(this, "GroupHint"), ref editGroup, 32, OmniTheme.Scale(200f));
        OmniControls.InputTextMultiline(
            "##lflEditNote",
            ref editNote,
            256,
            new Vector2(OmniTheme.Scale(260f), OmniTheme.Scale(70f)));

        if (OmniControls.SmallButton(OmniLoc.Get(this, "Save"), false))
        {
            editTarget.Group = editGroup.Trim();
            editTarget.Note = editNote.Trim();
            store.MarkDirty();
            namePlateGui?.RequestRedraw();
            editTarget = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Cancel"), false))
            editTarget = null;

        ImGui.EndPopup();
    }

    private void DrawDeletePopup()
    {
        if (deletePopupRequested)
        {
            ImGui.OpenPopup("##lflDelete");
            deletePopupRequested = false;
        }

        if (deleteTarget is null)
            return;

        if (!ImGui.BeginPopup("##lflDelete"))
        {
            deleteTarget = null;
            return;
        }

        OmniControls.SectionLabel(string.Format(OmniLoc.Get(this, "ConfirmDelete"), deleteTarget.Name));
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Delete"), false))
        {
            var name = deleteTarget.Name;
            queriedAt.Remove(deleteTarget.Key);
            store.Remove(deleteTarget);
            namePlateGui?.RequestRedraw();
            deleteTarget = null;
            ReportNotice(string.Format(OmniLoc.Get(this, "Deleted"), name));
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "Cancel"), false))
            deleteTarget = null;

        ImGui.EndPopup();
    }

    private void DrawNotices()
    {
        while (pendingNotices.Count > 0)
            OmniNotifier.Chat(pendingNotices.Dequeue());
    }

    #endregion

    #region 业务

    private void AddManual()
    {
        var name = manualName.Trim();
        if (name.Length == 0)
        {
            ReportNotice(OmniLoc.Get(this, "NameRequired"));
            return;
        }

        var world = ResolveWorldId(manualWorld.Trim());
        if (world == 0)
        {
            ReportNotice(OmniLoc.Get(this, "WorldRequired"));
            return;
        }

        if (AddFriend(name, world, 0, string.Empty, string.Empty))
        {
            manualName = string.Empty;
            manualWorld = string.Empty;
        }
    }

    private bool AddFriend(string name, uint worldId, ulong contentId, string group, string note)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return false;

        var local = objectTable?.LocalPlayer;
        if (local is not null && string.Equals(trimmed, local.Name.TextValue, StringComparison.Ordinal))
        {
            ReportNotice(OmniLoc.Get(this, "SelfSkip"));
            return false;
        }

        if (store.Find(trimmed, worldId) is not null)
        {
            ReportNotice(OmniLoc.Get(this, "Duplicate"));
            return false;
        }

        store.Add(
            new FriendRecord
            {
                Name = trimmed,
                WorldId = worldId,
                ContentId = contentId,
                Group = group,
                Note = note,
                AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
        ReportNotice(string.Format(OmniLoc.Get(this, "Added"), $"{trimmed} · {WorldName(worldId)}"));
        return true;
    }

    private void OpenEdit(FriendRecord record)
    {
        editTarget = record;
        editPopupRequested = true;
        editName = record.Name;
        editGroup = record.Group;
        editNote = record.Note;
    }

    private void Export()
    {
        try
        {
            var path = store.Export();
            ReportNotice(string.Format(OmniLoc.Get(this, "Exported"), path));
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Error(exception, "LocalFriendList export failed.");
            ReportNotice(OmniLoc.Get(this, "FileError"));
        }
    }

    private void Import()
    {
        try
        {
            var count = string.IsNullOrWhiteSpace(importPath)
                ? store.ImportLatest()
                : store.Import(importPath.Trim());
            ReportNotice(
                count == 0
                    ? OmniLoc.Get(this, "ImportNothing")
                    : string.Format(OmniLoc.Get(this, "Imported"), count));
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Error(exception, "LocalFriendList import failed.");
            ReportNotice(OmniLoc.Get(this, "FileError"));
        }
    }

    private List<FriendRecord> BuildRows()
    {
        var keyword = search.Trim();
        var rows = new List<FriendRecord>();

        foreach (var record in store.Records)
        {
            if (onlineOnly && !record.Nearby)
                continue;

            if (!string.IsNullOrEmpty(groupFilter))
            {
                if (groupFilter == NoGroupMarker)
                {
                    if (!string.IsNullOrEmpty(record.Group))
                        continue;
                }
                else if (!string.Equals(record.Group, groupFilter, StringComparison.Ordinal))
                    continue;
            }

            if (keyword.Length > 0 &&
                record.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                record.Note.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                record.Group.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            rows.Add(record);
        }

        rows.Sort(
            (left, right) =>
            {
                var byTime = right.LastSeenAt.CompareTo(left.LastSeenAt);
                return byTime != 0
                    ? byTime
                    : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
            });

        return rows;
    }

    private int CountNearby()
    {
        var count = 0;
        foreach (var record in store.Records)
        {
            if (record.Nearby)
                count++;
        }

        return count;
    }

    /// <summary>
    ///     用游戏自带的玩家搜索（<c>/search</c>，别名 <c>/sea</c>）查询该好友。
    ///     只覆盖当前服务器的在线玩家；结果由游戏窗口展示，模块不解析搜索结果
    ///     —— 解析需要 unsafe 读 InfoProxySearch，在线模块的 unsafe 编译许可尚未确认。
    /// </summary>
    private void QueryOnline(FriendRecord record)
    {
        if (commandManager is null)
        {
            ReportNotice(OmniLoc.Get(this, "QueryFailed"));
            return;
        }

        var name = record.Name.Replace("\"", string.Empty, StringComparison.Ordinal);

        // 国服条件关键词是 `name`（部分匹配）。引号只在名字**含空格**时才加：
        // 无空格却加引号，引号会被当成名字的一部分，条件失效、退回默认全量列表（200/200）。
        var query = name.Contains(' ')
            ? $"/search name \"{name}\""
            : $"/search name {name}";

        pendingCommands.Enqueue(query);
        queriedAt[record.Key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ReportNotice(string.Format(OmniLoc.Get(this, "QuerySent"), record.Name));
    }

    /// <summary>
    ///     命令统一在 framework 线程执行。在 ImGui 绘制回调里直接执行会与界面帧冲突。
    ///     走宿主的 <see cref="ChatManager" />（Omni 自己的模块也用它发指令），
    ///     比 ICommandManager 更接近「用户手输」的效果；后者对原生命令不可靠。
    /// </summary>
    private void DrainCommands()
    {
        while (pendingCommands.Count > 0)
        {
            var command = pendingCommands.Dequeue();
            DalamudServices.PluginLog.Information($"[LocalFriendList] command: {command}");

            try
            {
                ChatManager.Instance().SendCommand(command);
                continue;
            }
            catch (Exception exception)
            {
                DalamudServices.PluginLog.Error(
                    exception,
                    "LocalFriendList: ChatManager.SendCommand failed, falling back to ICommandManager.");
            }

            try
            {
                commandManager?.ProcessCommand(command);
            }
            catch (Exception exception)
            {
                DalamudServices.PluginLog.Error(exception, $"LocalFriendList command failed: {command}");
                ReportNotice(OmniLoc.Get(this, "QueryFailed"));
            }
        }
    }
    private void ReportNotice(string message) => pendingNotices.Enqueue(message);

    /// <summary>
    ///     NPC 好友功能已移除，旧存档里可能残留分组为 <c>__npc__</c> 的记录。
    ///     这些记录没有世界信息，留着会变成没法操作的空条目，加载时直接清掉。
    /// </summary>
    private void DropLegacyNpcRecords()
    {
        List<FriendRecord>? stale = null;
        foreach (var record in store.Records)
        {
            if (string.Equals(record.Group, "__npc__", StringComparison.Ordinal))
                (stale ??= []).Add(record);
        }

        if (stale is null)
            return;

        foreach (var record in stale)
            store.Remove(record);
    }

    private string FullName(FriendRecord record) => $"{record.Name}@{WorldName(record.WorldId)}";

    private static string FormatClock(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private string FormatSince(long unix)
    {
        if (unix <= 0)
            return OmniLoc.Get(this, "NeverSeen");

        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix;
        if (elapsed < 60)
            return OmniLoc.Get(this, "JustNow");
        if (elapsed < 3600)
            return string.Format(OmniLoc.Get(this, "MinutesAgo"), elapsed / 60);
        if (elapsed < 86400)
            return string.Format(OmniLoc.Get(this, "HoursAgo"), elapsed / 3600);

        return string.Format(OmniLoc.Get(this, "DaysAgo"), elapsed / 86400);
    }

    private uint CurrentTerritory() => clientState is null ? 0u : (uint)clientState.TerritoryType;

    private static uint ResolveWorldId(string worldName)
    {
        if (worldName.Length == 0)
            return 0;

        if (uint.TryParse(worldName, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) &&
            numeric != 0)
            return numeric;

        foreach (var row in LuminaGetter.Get<Lumina.Excel.Sheets.World>())
        {
            if (string.Equals(row.Name.ExtractText(), worldName, StringComparison.OrdinalIgnoreCase))
                return row.RowId;
        }

        return 0;
    }

    private static string WorldName(uint worldId)
    {
        if (worldId == 0)
            return "-";

        return LuminaGetter.TryGetRow<Lumina.Excel.Sheets.World>(worldId, out var world)
            ? world.Name.ExtractText()
            : worldId.ToString(CultureInfo.InvariantCulture);
    }

    private static T? Resolve<T>() where T : class =>
        DalamudServices.PluginInterface.GetService(typeof(T)) as T;

    private static string ResolveDataDirectory() =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LocalFriendList");

    #endregion

    #region 配置与内部类型

    private readonly struct WindowThemeScope : IDisposable
    {
        public void Dispose() => ImGui.PopStyleColor(6);
    }

    private sealed class Settings
    {
        public int SchemaVersion { get; set; } = 1;

        /// <summary>是否把好友颜色应用到场景中的好友。</summary>
        public bool UseFriendColor { get; set; }

        public float FriendColorR { get; set; } = 0.35f;
        public float FriendColorG { get; set; } = 0.9f;
        public float FriendColorB { get; set; } = 0.45f;
    }

    private sealed class FriendRecord
    {
        public string Name { get; set; } = string.Empty;
        public uint WorldId { get; set; }
        public ulong ContentId { get; set; }
        public string Group { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public long AddedAt { get; set; }
        public long LastSeenAt { get; set; }
        public uint LastSeenTerritory { get; set; }

        [JsonIgnore]
        public string Key => MakeKey(Name, WorldId);

        [JsonIgnore]
        public bool Nearby { get; set; }
    }

    private sealed class FriendFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<FriendRecord> Friends { get; set; } = [];
        public string SortMode { get; set; } = "lastSeen";
        public string GroupFilter { get; set; } = string.Empty;
        public bool OnlineOnly { get; set; }
        public WindowState Window { get; set; } = new();
    }

    /// <summary>
    ///     窗口几何。System.Numerics.Vector2 的 X/Y 是字段，System.Text.Json 默认不写字段，
    ///     因此拆成标量保存，再由适配器转回 Vector2。
    /// </summary>
    private sealed class WindowState
    {
        public float PositionX { get; set; } = 160f;
        public float PositionY { get; set; } = 140f;
        public float SizeX { get; set; } = 560f;
        public float SizeY { get; set; } = 360f;
        public float CollapsedX { get; set; } = 80f;
        public float CollapsedY { get; set; } = 80f;
        public bool Collapsed { get; set; }
        public bool Locked { get; set; }
    }

    /// <summary>
    ///     把宿主 <see cref="OmniRecordWindow" /> 需要的几何读写接到花名册文件上。
    ///     宿主的 SaveHostConfig 是 internal，在线模块拿不到，保存动作由本模块自己完成。
    /// </summary>
    private sealed class WindowSettings(FriendStore store) : IOmniRecordWindowSettings
    {
        public bool Locked => store.Window.Locked;

        public bool Collapsed
        {
            get => store.Window.Collapsed;
            set
            {
                if (store.Window.Collapsed == value)
                    return;

                store.Window.Collapsed = value;
                store.MarkDirty();
            }
        }

        public Vector2 Position
        {
            get => new(store.Window.PositionX, store.Window.PositionY);
            set
            {
                store.Window.PositionX = value.X;
                store.Window.PositionY = value.Y;
                store.MarkDirty();
            }
        }

        public Vector2 CollapsedPosition
        {
            get => new(store.Window.CollapsedX, store.Window.CollapsedY);
            set
            {
                store.Window.CollapsedX = value.X;
                store.Window.CollapsedY = value.Y;
                store.MarkDirty();
            }
        }

        public Vector2 Size
        {
            get => new(store.Window.SizeX, store.Window.SizeY);
            set
            {
                store.Window.SizeX = value.X;
                store.Window.SizeY = value.Y;
                store.MarkDirty();
            }
        }

        public float GlobalScale => 1f;

        public float Opacity => 1f;
    }

    /// <summary>
    ///     花名册存储。宿主配置只在设置面板打开时落盘，独立窗口的改动不在那个时机上，
    ///     因此单独存文件：原子写入 + 变更防抖，并支持导出导入。
    /// </summary>
    private sealed class FriendStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly List<FriendRecord> records = [];
        private string directory = string.Empty;
        private string filePath = string.Empty;
        private bool dirty;
        private long dirtyAt;

        public IReadOnlyList<FriendRecord> Records => records;

        /// <summary>纪录变更计数，供名牌颜色缓存判断是否需要重建。</summary>
        public long Revision { get; private set; }

        public WindowState Window { get; private set; } = new();

        public string SortMode { get; set; } = "lastSeen";

        public string GroupFilter { get; set; } = string.Empty;

        public bool OnlineOnly { get; set; }

        public void Initialize(string path)
        {
            directory = path;
            filePath = Path.Combine(path, "friends.json");
        }

        public bool Load()
        {
            records.Clear();
            Revision++;
            if (!File.Exists(filePath))
                return true;

            try
            {
                var json = File.ReadAllText(filePath);
                var file = JsonSerializer.Deserialize<FriendFile>(json, JsonOptions);
                if (file?.Friends is null)
                    return true;

                foreach (var record in file.Friends)
                {
                    if (string.IsNullOrWhiteSpace(record.Name))
                        continue;

                    records.Add(record);
                }

                SortMode = file.SortMode;
                GroupFilter = file.GroupFilter;
                OnlineOnly = file.OnlineOnly;
                Window = file.Window ?? new WindowState();
                return true;
            }
            catch (Exception)
            {
                TryBackupCorrupt();
                return false;
            }
        }

        public void Save()
        {
            if (!dirty || directory.Length == 0)
                return;

            try
            {
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(CreateFile(), JsonOptions);
                var temp = filePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, filePath, true);
            }
            catch (Exception)
            {
                dirtyAt = Environment.TickCount64;
                return;
            }

            dirty = false;
        }

        public void Flush()
        {
            dirty = true;
            Save();
        }

        public void TickSave()
        {
            if (!dirty)
                return;

            if (Environment.TickCount64 - dirtyAt < SaveDelayMs)
                return;

            Save();
        }

        public void MarkDirty()
        {
            dirty = true;
            dirtyAt = Environment.TickCount64;
            Revision++;
        }

        public FriendRecord? Find(string name, uint worldId)
        {
            foreach (var record in records)
            {
                if (record.WorldId == worldId &&
                    string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase))
                    return record;
            }

            return null;
        }

        public void Add(FriendRecord record)
        {
            records.Add(record);
            MarkDirty();
        }

        public void Remove(FriendRecord record)
        {
            records.Remove(record);
            MarkDirty();
        }

        public List<string> CollectGroups()
        {
            var groups = new List<string>();
            foreach (var record in records)
            {
                if (!groups.Contains(record.Group))
                    groups.Add(record.Group);
            }

            groups.Sort(StringComparer.OrdinalIgnoreCase);
            return groups;
        }

        public string Export()
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"friends-export-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(CreateFile(), JsonOptions));
            return path;
        }

        public int Import(string path) => ImportFile(path);

        public int ImportLatest()
        {
            if (!Directory.Exists(directory))
                return 0;

            var candidates = new List<string>();
            foreach (var file in Directory.GetFiles(directory, "friends*.json"))
            {
                if (string.Equals(file, filePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                candidates.Add(file);
            }

            if (candidates.Count == 0)
                return 0;

            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            return ImportFile(candidates[^1]);
        }

        private FriendFile CreateFile() =>
            new()
            {
                Friends = records,
                SortMode = SortMode,
                GroupFilter = GroupFilter,
                OnlineOnly = OnlineOnly,
                Window = Window
            };

        private int ImportFile(string path)
        {
            if (!File.Exists(path))
                return 0;

            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<FriendFile>(json, JsonOptions);
            if (file?.Friends is null)
                return 0;

            var added = 0;
            foreach (var record in file.Friends)
            {
                if (string.IsNullOrWhiteSpace(record.Name))
                    continue;

                if (Find(record.Name, record.WorldId) is not null)
                    continue;

                record.Nearby = false;
                records.Add(record);
                added++;
            }

            if (added > 0)
                MarkDirty();

            return added;
        }

        private void TryBackupCorrupt()
        {
            try
            {
                if (!File.Exists(filePath))
                    return;

                File.Copy(filePath, filePath + ".bak", true);
            }
            catch (Exception)
            {
            }
        }
    }

    #endregion
}
