using System.IO;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using OmniToolbox.Commands;

namespace OmniToolbox.AutoLimsaOnArrival;

/// <summary>
/// 跨服 / 超域传送落地后，自动把你送到海都（利姆萨·罗敏萨）。
/// 只做传送，不模拟按键，也不碰移动寻路。
/// </summary>
[ModuleApiVersion(1)]
public sealed class AutoLimsaOnArrival : OmniModuleBase
{
    private const string SlashCommand = "/L9HD";

    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Automation,
        Author = "LKami9",
        Commands =
        [
            new("CmdGo", OmniCommandCatalog.FormatModule(nameof(AutoLimsaOnArrival), "GO")),
            new("CmdReset", OmniCommandCatalog.FormatModule(nameof(AutoLimsaOnArrival), "rset")),
        ],
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"] = "LKami9农村人助手",
            ["Description"] = "跨服或超域传送落地后，自动传送到利姆萨·罗敏萨的大水晶。",

            ["CmdGo"] = "立即前往海都",
            ["CmdReset"] = "重置服务器记录",

            ["AutoTeleport"] = "落地后自动传送",
            ["AutoTeleportHint"] = "关掉后只提示，不动手",
            ["OnlyCrossDataCenter"] = "只在大区变更时触发",
            ["OnlyCrossDataCenterHint"] = "取消勾选则同大区跨服也触发",
            ["DelayFormat"] = "%.1f 秒",
            ["DelayHint"] = "落地后等待",
            ["RecordedWorld"] = "已记录服务器：{0}",
            ["CurrentWorld"] = "当前服务器：{0}",
            ["CurrentTerritory"] = "当前区域：{0}（{1}）",
            ["NotLoggedIn"] = "当前未登录",
            ["GoNowButton"] = "立即前往海都",
            ["ResetRecordButton"] = "重置记录",
            ["ReloadAetheryteButton"] = "重新识别水晶",
            ["UnknownWorld"] = "（未记录）",
            ["UnknownTerritory"] = "（未知）",
            ["SlashUsage"] = "[L9HD] 用法：/L9HD GO 立即前往海都 ；/L9HD rset 重置服务器记录",
            ["ShortCommandHelp"] = "GO 立即前往海都；rset 重置服务器记录",

            ["StatusNotStarted"] = "尚未启用",
            ["StatusIdle"] = "待命中，等待服务器变更",
            ["StatusStopped"] = "已停用",
            ["StatusNoClientState"] = "拿不到客户端状态服务，模块无法工作",
            ["StatusNoClientStateShort"] = "拿不到客户端状态服务",
            ["StatusAetheryteMissing"] = "找不到海都大水晶，检查游戏语言与数据版本",
            ["StatusAetheryteMissingGo"] = "找不到海都大水晶，无法传送",
            ["StatusAetheryteStillMissing"] = "仍然找不到海都大水晶",
            ["StatusAetheryteFound"] = "已识别海都大水晶（ID {0}）",
            ["StatusShortCommandTaken"] = "{0} 已被占用，请改用 /omni 命令",
            ["StatusRecordCleared"] = "已清除服务器记录，下次登录不会触发",
            ["StatusRecordClearedShort"] = "已清除服务器记录",
            ["StatusArrived"] = "已到达海都",
            ["StatusVerifyTimeout"] = "传送后一直没到海都，可能被中断或水晶未解锁",
            ["StatusWorldRecorded"] = "已记录当前服务器（{0}），下次变更时触发",
            ["StatusSameDataCenterSkipped"] = "服务器变更为 {0}（同大区），按设置跳过",
            ["StatusWorldChanged"] = "检测到服务器变更 → {0}，等落地",
            ["StatusPendingTimeout"] = "等待落地超时，已放弃这次触发",
            ["StatusBusy"] = "仍在加载，稍后重试",
            ["StatusTerritoryNotReady"] = "区域还没读到，跳过",
            ["StatusAlreadyInLimsa"] = "已经站在海都，不用传送",
            ["StatusAutoTeleportOff"] = "已抵达 {0}，自动传送已关闭",
            ["StatusTeleportSent"] = "已发出传送请求，等待读条",
            ["StatusTeleportFailed"] = "传送失败：海都大水晶可能未解锁",
            ["StatusNotLoggedIn"] = "还没登录角色",
        });

    private Settings config = new();

    private IClientState? clientState;
    private ICondition? condition;

    private bool pending;
    private DateTime pendingSince;
    private bool verifying;
    private DateTime teleportAt;
    private uint limsaAetheryte;
    private uint lastWorld;
    private bool commandRegistered;

    // 状态文字拆成「键 + 参数 + 是否异常」三件套，界面里再拼成当前语言的文案。
    private string statusKey = "StatusNotStarted";
    private object[] statusArgs = Array.Empty<object>();
    private bool statusBad;

    public override bool HasSettings => true;

    private string StatusText => string.Format(OmniLoc.Get(this, statusKey), statusArgs);

    private void SetStatus(string key, bool bad = false, params object[] args)
    {
        statusKey = key;
        statusArgs = args;
        statusBad = bad;
    }

    protected override void OnStart(FeatureLifetime activation)
    {
        // 每次启用都是新的 FeatureLifetime，本次启用的状态一律从头开始。
        pending = false;
        pendingSince = default;
        verifying = false;
        teleportAt = default;
        limsaAetheryte = 0;
        lastWorld = 0;
        SetStatus("StatusNotStarted");

        RegisterSlashCommand();
        activation.OnStopping(UnregisterSlashCommand);

        clientState = DalamudServices.PluginInterface.GetService(typeof(IClientState)) as IClientState;
        condition = DalamudServices.PluginInterface.GetService(typeof(ICondition)) as ICondition;

        limsaAetheryte = ResolveLimsaAetheryte();
        lastWorld = LoadLastWorld();

        if (clientState == null)
        {
            SetStatus("StatusNoClientState", true);
            return;
        }

        SetStatus(limsaAetheryte == 0 ? "StatusAetheryteMissing" : "StatusIdle", limsaAetheryte == 0);

        var framework = DalamudServices.Framework;
        activation.OnStopping(() => framework.Update -= OnFrameworkUpdate);
        framework.Update += OnFrameworkUpdate;
    }

    protected override void OnStop()
    {
        pending = false;
        pendingSince = default;
        verifying = false;
        SetStatus("StatusStopped");
    }

    /// <summary>注册独立斜杠命令。/omni 那条路照样能用，这里只是给个更短的入口。</summary>
    private void RegisterSlashCommand()
    {
        if (commandRegistered)
        {
            return;
        }

        try
        {
            if (DalamudServices.CommandManager.Commands.ContainsKey(SlashCommand))
            {
                DalamudServices.PluginLog.Warning($"[AutoLimsa] {SlashCommand} 已被其它插件占用，短命令不可用");
                SetStatus("StatusShortCommandTaken", false, SlashCommand);
                return;
            }

            DalamudServices.CommandManager.AddHandler(SlashCommand, new CommandInfo(OnSlashCommand)
            {
                HelpMessage = OmniLoc.Get(this, "ShortCommandHelp"),
                ShowInHelp = true,
            });
            commandRegistered = true;
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 注册斜杠命令失败");
        }
    }

    private void UnregisterSlashCommand()
    {
        if (!commandRegistered)
        {
            return;
        }

        try
        {
            DalamudServices.CommandManager.RemoveHandler(SlashCommand);
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 注销斜杠命令失败");
        }
        finally
        {
            commandRegistered = false;
        }
    }

    private void OnSlashCommand(string command, string arguments) => HandleAction(arguments, true);

    public override bool TryHandleCommand(string arguments) => HandleAction(arguments, false);

    private bool HandleAction(string arguments, bool fromSlash)
    {
        var value = (arguments ?? string.Empty).Trim();

        if (value.Equals("go", StringComparison.OrdinalIgnoreCase))
        {
            GoNow();
            return true;
        }

        // 用户写的是 rset，reset 也一起收下，免得打顺手了没反应。
        if (value.Equals("rset", StringComparison.OrdinalIgnoreCase)
            || value.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            ResetRecord();
            return true;
        }

        if (fromSlash)
        {
            DalamudServices.ChatGUI.Print(OmniLoc.Get(this, "SlashUsage"));
        }

        return false;
    }

    private void ResetRecord()
    {
        lastWorld = 0;
        SaveLastWorld(0);
        pending = false;
        verifying = false;
        SetStatus("StatusRecordCleared");
    }

    public override bool DrawSettings()
    {
        var dirty = false;

        var color = statusBad
            ? new Vector4(0.95f, 0.45f, 0.45f, 1f)
            : new Vector4(0.6f, 0.85f, 0.65f, 1f);
        ImGui.TextColored(color, StatusText);

        ImGui.Separator();

        var auto = config.AutoTeleport;
        if (ImGui.Checkbox(OmniLoc.Get(this, "AutoTeleport"), ref auto))
        {
            config.AutoTeleport = auto;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(OmniLoc.Get(this, "AutoTeleportHint"));

        var onlyDataCenter = config.OnlyCrossDataCenter;
        if (ImGui.Checkbox(OmniLoc.Get(this, "OnlyCrossDataCenter"), ref onlyDataCenter))
        {
            config.OnlyCrossDataCenter = onlyDataCenter;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(OmniLoc.Get(this, "OnlyCrossDataCenterHint"));

        var delay = config.DelaySeconds;
        ImGui.SetNextItemWidth(-160f);
        if (ImGui.SliderFloat("##delay", ref delay, 0.5f, 10f, OmniLoc.Get(this, "DelayFormat")))
        {
            config.DelaySeconds = delay;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled(OmniLoc.Get(this, "DelayHint"));

        ImGui.Separator();

        var world = CurrentWorldId();
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "RecordedWorld"), WorldName(lastWorld)));
        ImGui.TextDisabled(string.Format(OmniLoc.Get(this, "CurrentWorld"), WorldName(world)));

        if (clientState is { IsLoggedIn: true })
        {
            ImGui.TextDisabled(string.Format(
                OmniLoc.Get(this, "CurrentTerritory"),
                TerritoryName(clientState.TerritoryType),
                clientState.TerritoryType));
        }
        else
        {
            ImGui.TextDisabled(OmniLoc.Get(this, "NotLoggedIn"));
        }

        if (ImGui.Button(OmniLoc.Get(this, "GoNowButton") + "##goto"))
        {
            GoNow();
        }

        ImGui.SameLine();
        if (ImGui.Button(OmniLoc.Get(this, "ResetRecordButton") + "##resetworld"))
        {
            lastWorld = 0;
            SaveLastWorld(0);
            pending = false;
            SetStatus("StatusRecordClearedShort");
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button(OmniLoc.Get(this, "ReloadAetheryteButton") + "##reload"))
        {
            limsaAetheryte = ResolveLimsaAetheryte();
            if (limsaAetheryte == 0)
            {
                SetStatus("StatusAetheryteStillMissing", true);
            }
            else
            {
                SetStatus("StatusAetheryteFound", false, limsaAetheryte);
            }
        }

        return dirty;
    }

    public override bool ResetSettings()
    {
        // 逐字段重置，不能替换 config 对象：宿主持有它的引用，换掉会让设置存不上。
        var changed = !config.AutoTeleport
                      || !config.OnlyCrossDataCenter
                      || Math.Abs(config.DelaySeconds - 2f) > 0.0001f;

        config.AutoTeleport = true;
        config.OnlyCrossDataCenter = true;
        config.DelaySeconds = 2f;
        // 刻意不动服务器记录：清掉它会让下次登录被判成跨服，白跑一趟海都。
        return changed;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Error(exception, "[AutoLimsa] 更新时出错");
        }
    }

    private void Tick()
    {
        var client = clientState;
        var player = DalamudServices.PlayerState;

        if (client == null || !client.IsLoggedIn || !player.IsLoaded)
        {
            return;
        }

        if (verifying)
        {
            if (IsLimsa(client.TerritoryType))
            {
                verifying = false;
                SetStatus("StatusArrived");
                return;
            }

            if ((DateTime.UtcNow - teleportAt).TotalSeconds > 90d)
            {
                verifying = false;
                SetStatus("StatusVerifyTimeout", true);
                return;
            }
        }

        var world = player.CurrentWorld.RowId;
        if (world == 0)
        {
            return;
        }

        if (lastWorld == 0)
        {
            lastWorld = world;
            SaveLastWorld(world);
            SetStatus("StatusWorldRecorded", false, WorldName(world));
            return;
        }

        if (world != lastWorld)
        {
            var previous = lastWorld;
            lastWorld = world;
            SaveLastWorld(world);

            if (config.OnlyCrossDataCenter && !IsCrossDataCenter(previous, world))
            {
                SetStatus("StatusSameDataCenterSkipped", false, WorldName(world));
                return;
            }

            pending = true;
            pendingSince = DateTime.UtcNow;
            SetStatus("StatusWorldChanged", false, WorldName(world));
            return;
        }

        if (!pending)
        {
            return;
        }

        var elapsed = (DateTime.UtcNow - pendingSince).TotalSeconds;

        if (elapsed > 120d)
        {
            pending = false;
            SetStatus("StatusPendingTimeout", true);
            return;
        }

        if (elapsed < config.DelaySeconds)
        {
            return;
        }

        if (IsBusy())
        {
            SetStatus("StatusBusy");
            return;
        }

        pending = false;

        if (client.TerritoryType == 0)
        {
            SetStatus("StatusTerritoryNotReady");
            return;
        }

        if (IsLimsa(client.TerritoryType))
        {
            SetStatus("StatusAlreadyInLimsa");
            return;
        }

        if (!config.AutoTeleport)
        {
            SetStatus("StatusAutoTeleportOff", false, TerritoryName(client.TerritoryType));
            return;
        }

        if (TryTeleportToLimsa())
        {
            teleportAt = DateTime.UtcNow;
            verifying = true;
            SetStatus("StatusTeleportSent");
            return;
        }

        SetStatus("StatusTeleportFailed", true);
    }

    private void GoNow()
    {
        var client = clientState;
        if (client == null)
        {
            SetStatus("StatusNoClientStateShort", true);
            return;
        }

        if (!client.IsLoggedIn)
        {
            SetStatus("StatusNotLoggedIn");
            return;
        }

        if (limsaAetheryte == 0)
        {
            limsaAetheryte = ResolveLimsaAetheryte();
        }

        if (limsaAetheryte == 0)
        {
            SetStatus("StatusAetheryteMissingGo", true);
            return;
        }

        if (IsLimsa(client.TerritoryType))
        {
            SetStatus("StatusAlreadyInLimsa");
            return;
        }

        if (TryTeleportToLimsa())
        {
            teleportAt = DateTime.UtcNow;
            verifying = true;
            SetStatus("StatusTeleportSent");
            return;
        }

        SetStatus("StatusTeleportFailed", true);
    }

    /// <summary>传送到海都大水晶。走游戏原生 Telepo，不依赖其它模块。</summary>
    private unsafe bool TryTeleportToLimsa()
    {
        if (limsaAetheryte == 0)
        {
            return false;
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            return false;
        }

        telepo->UpdateAetheryteList();

        foreach (var entry in telepo->TeleportList)
        {
            if (entry.AetheryteId != limsaAetheryte)
            {
                continue;
            }

            return telepo->Teleport(entry.AetheryteId, entry.SubIndex);
        }

        return false;
    }

    private bool IsBusy()
    {
        var state = condition;
        if (state == null)
        {
            return false;
        }

        return state[ConditionFlag.BetweenAreas]
               || state[ConditionFlag.OccupiedInEvent]
               || state[ConditionFlag.OccupiedInQuestEvent]
               || state[ConditionFlag.WatchingCutscene]
               || state[ConditionFlag.OccupiedInCutSceneEvent];
    }

    private static uint CurrentWorldId()
    {
        try
        {
            var player = DalamudServices.PlayerState;
            return player is { IsLoaded: true } ? player.CurrentWorld.RowId : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool IsLimsa(uint territory)
    {
        var name = TerritoryName(territory);
        return name.Contains("利姆萨", StringComparison.Ordinal)
               || name.Contains("Limsa", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCrossDataCenter(uint from, uint to)
    {
        if (from == 0 || to == 0)
        {
            return false;
        }

        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<World>();
            var previous = sheet.GetRowOrDefault(from);
            var current = sheet.GetRowOrDefault(to);
            if (previous == null || current == null)
            {
                return false;
            }

            return previous.Value.DataCenter.RowId != current.Value.DataCenter.RowId;
        }
        catch
        {
            return false;
        }
    }

    private string WorldName(uint worldId)
    {
        if (worldId == 0)
        {
            return OmniLoc.Get(this, "UnknownWorld");
        }

        try
        {
            var row = DalamudServices.DataManager.GetExcelSheet<World>().GetRowOrDefault(worldId);
            var name = row?.Name.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? worldId.ToString() : name;
        }
        catch
        {
            return worldId.ToString();
        }
    }

    private string TerritoryName(uint territory)
    {
        if (territory == 0)
        {
            return OmniLoc.Get(this, "UnknownTerritory");
        }

        try
        {
            var row = DalamudServices.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory);
            var name = row?.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(name) ? territory.ToString() : name;
        }
        catch
        {
            return territory.ToString();
        }
    }

    /// <summary>
    /// 服务器记录存在自己的文件里。超域传送是「关客户端 → 网页申请 → 重开客户端」的流程，
    /// 插件会跟着重启，放在内存或宿主配置里都可能丢，丢了就等于这个功能在最需要的场景失效。
    /// </summary>
    private string StatePath()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.GetPluginConfigDirectory();
            return string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : Path.Combine(directory, "AutoLimsa.state");
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 取配置目录失败，本次不持久化");
            return string.Empty;
        }
    }

    private uint LoadLastWorld()
    {
        try
        {
            var path = StatePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }

            var text = File.ReadAllText(path).Trim();
            return uint.TryParse(text, out var value) ? value : 0;
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 读取服务器记录失败");
            return 0;
        }
    }

    private void SaveLastWorld(uint world)
    {
        try
        {
            var path = StatePath();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, world.ToString());
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 写入服务器记录失败");
        }
    }

    /// <summary>海都大水晶的 Aetheryte 行 ID。按名称找，避免硬编码在版本更新后失效。</summary>
    private static uint ResolveLimsaAetheryte()
    {
        try
        {
            var sheet = DalamudServices.DataManager.GetExcelSheet<Aetheryte>();
            var candidates = new List<string>();

            foreach (var row in sheet)
            {
                if (!row.IsAetheryte)
                {
                    continue;
                }

                var name = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
                candidates.Add($"{row.RowId}={name}");

                if (name.Contains("利姆萨", StringComparison.Ordinal)
                    || name.Contains("Limsa", StringComparison.OrdinalIgnoreCase))
                {
                    return row.RowId;
                }
            }

            // 认不出来时把候选全打出来：用户贴一行日志就能定位是译名变了还是表结构变了。
            DalamudServices.PluginLog.Warning(
                $"[AutoLimsa] 没匹配到海都大水晶，候选 {candidates.Count} 个：{string.Join(" / ", candidates)}");
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Warning(exception, "[AutoLimsa] 解析海都大水晶失败");
        }

        return 0;
    }

    /// <summary>模块设置：宿主整体序列化，属性名就是配置 JSON 的键，别改名。</summary>
    private sealed class Settings
    {
        public bool AutoTeleport { get; set; } = true;

        public bool OnlyCrossDataCenter { get; set; } = true;

        public float DelaySeconds { get; set; } = 2f;
    }
}
