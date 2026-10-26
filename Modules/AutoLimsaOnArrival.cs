using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;

namespace OmniToolbox.AutoLimsaOnArrival;

/// <summary>
/// 跨服 / 超域传送落地后，自动把你送到海都（利姆萨·罗敏萨）。
/// 只做传送，不模拟按键，也不碰移动寻路。
/// </summary>
public sealed class AutoLimsaOnArrival : ModuleBase
{
    private const string SlashCommand = "/L9HD";

    public override ModuleInfo Info { get; } = new()
    {
        Title = "LKami9农村人助手",
        Description = "跨服或超域传送落地后，自动传送到利姆萨·罗敏萨的大水晶。",
        Category = ModuleCategory.Automation,
        Author = "LKami9",
        Commands =
        [
            new("立即前往海都", "/L9HD GO"),
            new("重置服务器记录", "/L9HD rset"),
        ],
    };

    private AutoLimsaConfig config = new();

    private IClientState? clientState;
    private ICondition? condition;

    private bool pending;
    private DateTime pendingSince;
    private bool verifying;
    private DateTime teleportAt;
    private uint limsaAetheryte;
    private uint lastWorld;
    private bool commandRegistered;
    private string status = "尚未启用";

    public override bool HasSettings => true;

    protected override void OnEnable()
    {
        RegisterSlashCommand();

        clientState = DalamudServices.PluginInterface.GetService(typeof(IClientState)) as IClientState;
        condition = DalamudServices.PluginInterface.GetService(typeof(ICondition)) as ICondition;

        limsaAetheryte = ResolveLimsaAetheryte();
        lastWorld = LoadLastWorld();
        pending = false;
        verifying = false;

        if (clientState == null)
        {
            status = "拿不到客户端状态服务，模块无法工作";
            return;
        }

        status = limsaAetheryte == 0
            ? "找不到海都大水晶，检查游戏语言与数据版本"
            : "待命中，等待服务器变更";

        DalamudServices.Framework.Update += OnFrameworkUpdate;
    }

    protected override void OnDisable()
    {
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        UnregisterSlashCommand();
        pending = false;
        verifying = false;
        status = "已停用";
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
                status = $"{SlashCommand} 已被占用，请改用 /omni 命令";
                return;
            }

            DalamudServices.CommandManager.AddHandler(SlashCommand, new CommandInfo(OnSlashCommand)
            {
                HelpMessage = "GO 立即前往海都；rset 重置服务器记录",
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
            DalamudServices.ChatGUI.Print("[L9HD] 用法：/L9HD GO 立即前往海都 ；/L9HD rset 重置服务器记录");
        }

        return false;
    }

    private void ResetRecord()
    {
        lastWorld = 0;
        SaveLastWorld(0);
        pending = false;
        verifying = false;
        status = "已清除服务器记录，下次登录不会触发";
    }

    public override bool DrawSettings()
    {
        var dirty = false;

        var bad = status.Contains("失败")
                  || status.Contains("拿不到")
                  || status.Contains("找不到")
                  || status.Contains("未解锁")
                  || status.Contains("超时");
        var color = bad
            ? new Vector4(0.95f, 0.45f, 0.45f, 1f)
            : new Vector4(0.6f, 0.85f, 0.65f, 1f);
        ImGui.TextColored(color, status);

        ImGui.Separator();

        var auto = config.AutoTeleport;
        if (ImGui.Checkbox("落地后自动传送", ref auto))
        {
            config.AutoTeleport = auto;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("关掉后只提示，不动手");

        var onlyDataCenter = config.OnlyCrossDataCenter;
        if (ImGui.Checkbox("只在大区变更时触发", ref onlyDataCenter))
        {
            config.OnlyCrossDataCenter = onlyDataCenter;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("取消勾选则同大区跨服也触发");

        var delay = config.DelaySeconds;
        ImGui.SetNextItemWidth(-160f);
        if (ImGui.SliderFloat("##delay", ref delay, 0.5f, 10f, "%.1f 秒"))
        {
            config.DelaySeconds = delay;
            dirty = true;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("落地后等待");

        ImGui.Separator();

        var world = CurrentWorldId();
        ImGui.TextDisabled($"已记录服务器：{WorldName(lastWorld)}");
        ImGui.TextDisabled($"当前服务器：{WorldName(world)}");

        if (clientState is { IsLoggedIn: true })
        {
            ImGui.TextDisabled($"当前区域：{TerritoryName(clientState.TerritoryType)}（{clientState.TerritoryType}）");
        }
        else
        {
            ImGui.TextDisabled("当前未登录");
        }

        if (ImGui.Button("立即前往海都##goto"))
        {
            GoNow();
        }

        ImGui.SameLine();
        if (ImGui.Button("重置记录##resetworld"))
        {
            lastWorld = 0;
            SaveLastWorld(0);
            pending = false;
            status = "已清除服务器记录";
            dirty = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("重新识别水晶##reload"))
        {
            limsaAetheryte = ResolveLimsaAetheryte();
            status = limsaAetheryte == 0
                ? "仍然找不到海都大水晶"
                : $"已识别海都大水晶（ID {limsaAetheryte}）";
        }

        return dirty;
    }

    public override bool ResetSettings()
    {
        // 逐字段重置，不能替换 config 对象：宿主持有它的引用，换掉会让设置存不上。
        config.AutoTeleport = true;
        config.OnlyCrossDataCenter = true;
        config.DelaySeconds = 2f;
        // 刻意不动服务器记录：清掉它会让下次登录被判成跨服，白跑一趟海都。
        return true;
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
                status = "已到达海都";
                return;
            }

            if ((DateTime.UtcNow - teleportAt).TotalSeconds > 90d)
            {
                verifying = false;
                status = "传送后一直没到海都，可能被中断或水晶未解锁";
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
            status = $"已记录当前服务器（{WorldName(world)}），下次变更时触发";
            return;
        }

        if (world != lastWorld)
        {
            var previous = lastWorld;
            lastWorld = world;
            SaveLastWorld(world);

            if (config.OnlyCrossDataCenter && !IsCrossDataCenter(previous, world))
            {
                status = $"服务器变更为 {WorldName(world)}（同大区），按设置跳过";
                return;
            }

            pending = true;
            pendingSince = DateTime.UtcNow;
            status = $"检测到服务器变更 → {WorldName(world)}，等落地";
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
            status = "等待落地超时，已放弃这次触发";
            return;
        }

        if (elapsed < config.DelaySeconds)
        {
            return;
        }

        if (IsBusy())
        {
            status = "仍在加载，稍后重试";
            return;
        }

        pending = false;

        if (client.TerritoryType == 0)
        {
            status = "区域还没读到，跳过";
            return;
        }

        if (IsLimsa(client.TerritoryType))
        {
            status = "已经站在海都，不用传送";
            return;
        }

        if (!config.AutoTeleport)
        {
            status = $"已抵达 {TerritoryName(client.TerritoryType)}，自动传送已关闭";
            return;
        }

        if (TryTeleportToLimsa())
        {
            teleportAt = DateTime.UtcNow;
            verifying = true;
            status = "已发出传送请求，等待读条";
            return;
        }

        status = "传送失败：海都大水晶可能未解锁";
    }

    private void GoNow()
    {
        var client = clientState;
        if (client == null)
        {
            status = "拿不到客户端状态服务";
            return;
        }

        if (!client.IsLoggedIn)
        {
            status = "还没登录角色";
            return;
        }

        if (limsaAetheryte == 0)
        {
            limsaAetheryte = ResolveLimsaAetheryte();
        }

        if (limsaAetheryte == 0)
        {
            status = "找不到海都大水晶，无法传送";
            return;
        }

        if (IsLimsa(client.TerritoryType))
        {
            status = "已经站在海都，不用传送";
            return;
        }

        if (TryTeleportToLimsa())
        {
            teleportAt = DateTime.UtcNow;
            verifying = true;
            status = "已发出传送请求，等待读条";
            return;
        }

        status = "传送失败：海都大水晶可能未解锁";
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

    private static bool IsLimsa(uint territory)
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

    private static string WorldName(uint worldId)
    {
        if (worldId == 0)
        {
            return "（未记录）";
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

    private static string TerritoryName(uint territory)
    {
        if (territory == 0)
        {
            return "（未知）";
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
}

public sealed class AutoLimsaConfig
{
    public bool AutoTeleport { get; set; } = true;

    public bool OnlyCrossDataCenter { get; set; } = true;

    public float DelaySeconds { get; set; } = 2f;
}
