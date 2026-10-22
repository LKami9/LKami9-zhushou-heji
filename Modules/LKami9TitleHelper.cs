using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

using Lumina.Excel.Sheets;

using AchRow = Lumina.Excel.Sheets.Achievement;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace LKami9.TitleHelper;

/// <summary>
/// LKami9 称号助手（在线模块版 · 独立窗口）
///
/// 独立窗口 + Omni 本体主题（OmniTheme.Tokens / OmniWindowChrome），不占用模块设置页。
/// 功能：
///   · 检索全部称号（称号名 / 成就名 / 成就描述 / 分类 / ID）
///   · 区分已解锁 / 未解锁；已解锁一键佩戴
///   · 未解锁显示获取方式（关联成就、成就分类、点数、描述）
///
/// 接口（全公开，不写内存）：
///   UIState.Instance()->TitleList.IsTitleUnlocked(ushort)
///   UIState.Instance()->TitleController.SendTitleIdUpdate(ushort)
///   UIState.Instance()->TitleList.RequestTitleList()
/// </summary>
public sealed class LKami9TitleHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "LKami9称号助手",
        Description = "独立窗口：检索称号、一键佩戴、查看未解锁称号的获取方式。界面与配色跟随 Omni 本体主题。",
        Category    = ModuleCategory.Interface,
        Author      = "LKami9",
        Commands =
        [
            new("打开 / 关闭窗口", "/L9CHH"),
            new("按关键词搜索",   "/L9CHH search 关键字"),
            new("按 ID 佩戴称号", "/L9CHH equip 123"),
            new("随机佩戴已解锁称号", "/L9CHH random"),
            new("立即检测已解锁称号", "/L9CHH detect"),
        ],
    };

    public override bool HasSettings => true;

    private const string WindowId      = "###LKami9TitleMainWindow";
    private const string CollapseId    = "###LKami9TitleCollapse";
    private const string CloseId       = "###LKami9TitleClose";
    private const string ShortCommand  = "/L9CHH";
    private const string UnlockSnapshotFileName = "LKami9TitleHelper.unlocked.json";
    private ICommandManager shortCommandManager;
    private bool shortCommandRegistered;


    private LKami9TitleHelperConfig config = new();

    private sealed class TitleEntry
    {
        public uint   Id;
        public string Name    = string.Empty;
        public string NameF   = string.Empty;
        public bool   IsPrefix;
        public ushort Order;
        public string AchName = string.Empty;
        public string AchDesc = string.Empty;
        public string AchCat  = string.Empty;
        public byte   AchPoints;
        public uint   AchId;
        public bool   Unlocked;
        public string Label           = string.Empty;
        public string AchievementLine = string.Empty;
        public string SearchKey       = string.Empty;
    }

    private readonly List<TitleEntry> all  = [];
    private readonly List<TitleEntry> view = [];
    private readonly Dictionary<uint, TitleEntry> byId = [];
    private readonly HashSet<uint> favoriteIds = [];
    private bool unlockDetectionPending;
    private DateTime nextUnlockDetectionCheck = DateTime.MinValue;
    private DateTime nextUnlockDetectionRequest = DateTime.MinValue;
    private DateTimeOffset? unlockSnapshotSavedAt;

    private Dalamud.Interface.ManagedFontAtlas.IFontHandle omniFont;
    private int page = 1;
    private int jumpPage = 1;
    private readonly List<uint> expanded = [];
    private readonly List<string> categories = [];

    private bool   built;
    private string search  = string.Empty;
    private string status  = "尚未检测。点击「立即检测」读取并保存已解锁称号。";
    private string lastEquipped = string.Empty;

    private bool    unlockedOnly;
    private bool    lockedOnly;
    private bool    prefixOnly;
    private bool    suffixOnly;
    private int     unlockedCount;

    private bool                 collapsed;
    private bool                 resizePending;
    private System.Numerics.Vector2 expandedSize = new(820f, 560f);
    private System.Numerics.Vector2 lastPos;

    #region 生命周期

    protected override void OnEnable()
    {
        DalamudServices.PluginInterface.UiBuilder.Draw += this.DrawWindow;
        DalamudServices.Framework.Update                += this.OnFrameworkUpdate;

        this.MigrateConfig();
        this.RefreshFavoriteCache();
        this.RegisterShortCommand();
        DalamudServices.PluginLog.Information(
            $"[LKami9TitleHelper] enabled local-manual: windowOpen={this.config.WindowOpen}, shortCommand={this.shortCommandRegistered}");
        if (this.config.WindowOpen) this.EnsureDataReady();
    }

    protected override void OnDisable()
    {
        this.unlockDetectionPending = false;
        this.UnregisterShortCommand();
        DalamudServices.PluginInterface.UiBuilder.Draw -= this.DrawWindow;
        DalamudServices.Framework.Update                -= this.OnFrameworkUpdate;
    }

    private void RegisterShortCommand()
    {
        try
        {
            this.shortCommandManager =
                DalamudServices.PluginInterface.GetService(typeof(ICommandManager)) as ICommandManager;
            if (this.shortCommandManager == null)
            {
                this.status = "/L9CHH 注册失败：找不到命令服务。";
                return;
            }

            this.shortCommandRegistered = this.shortCommandManager.AddHandler(
                ShortCommand,
                new CommandInfo(this.OnShortCommand)
                {
                    HelpMessage = "LKami9 称号助手：/L9CHH [search 关键词|equip ID|random]",
                });
        }
        catch (Exception ex)
        {
            this.shortCommandRegistered = false;
            DalamudServices.PluginLog.Warning(ex, "[LKami9TitleHelper] register /L9CHH failed");
        }
    }

    private void UnregisterShortCommand()
    {
        if (!this.shortCommandRegistered) return;
        try { this.shortCommandManager?.RemoveHandler(ShortCommand); }
        catch { }
        this.shortCommandRegistered = false;
        this.shortCommandManager = null;
    }

    private void OnShortCommand(string command, string arguments) =>
        this.TryHandleCommand(arguments);

    public override bool ResetSettings()
    {
        this.config.WindowOpen = false;
        this.config.Ascending  = true;
        this.config.ScanOnOpen = false;
        this.config.DockToOmni = true;
        this.config.AutoExpandLocked = false;
        this.config.PageSize = 100;
        this.collapsed         = false;
        this.resizePending     = true;
        return true;
    }

    public override bool TryHandleCommand(string arguments)
    {
        var text = (arguments ?? string.Empty).Trim();
        if (text.Length == 0) { this.ToggleWindow(true); return true; }

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd   = parts[0].ToLowerInvariant();
        var arg   = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "window":
                this.ToggleWindow(!this.config.WindowOpen);
                return true;

            case "refresh":
                this.EnsureDataReady(force: true);
                this.ToggleWindow(true);
                return true;

            case "search":
                this.EnsureDataReady();
                this.search = arg;
                this.Rebuild();
                this.ToggleWindow(true);
                this.status = $"搜索「{arg}」，命中 {this.view.Count} 条。";
                return true;

            case "random":
                this.EquipRandom();
                this.ToggleWindow(true);
                return true;

            case "detect":
                this.RequestUnlockDetection();
                return true;

            case "equip":
                if (!uint.TryParse(arg, out var id)) { this.status = "用法：/L9CHH equip <称号ID>"; return true; }
                this.Equip(id);
                return true;

            default:
                return false;
        }
    }

    /// <summary>旧配置迁移：v2.13 起取消自动解锁扫描，改为手动检测。</summary>
    private void MigrateConfig()
    {
        if (this.config.ConfigVersion >= 5) return;

        if (this.config.ConfigVersion < 2)
        {
            this.config.DockToOmni  = false;
            if (this.config.PageSize > 60) this.config.PageSize = 40;
        }

        this.config.ScanOnOpen       = false;
        this.config.AutoExpandLocked = false;
        this.config.PageSize         = 100;
        this.config.ConfigVersion    = 5;
    }

    private void ToggleWindow(bool open)
    {
        if (open) this.EnsureDataReady();
        this.config.WindowOpen = open;
        if (open && this.collapsed) { this.collapsed = false; this.resizePending = true; }
    }

    private void EnsureDataReady(bool force = false)
    {
        if (force || !this.built) this.Build();
    }

    #endregion

    #region 数据

    private void Build()
    {
        this.all.Clear();
        this.byId.Clear();

        try
        {
            var achievements = DalamudServices.DataManager.GetExcelSheet<AchRow>();
            var titles       = DalamudServices.DataManager.GetExcelSheet<Title>();
            if (achievements == null || titles == null) { this.status = "取不到称号 / 成就数据表。"; return; }

            var byTitle = new Dictionary<uint, (string Name, string Desc, string Cat, byte Points, uint AchId)>(achievements.Count);
            foreach (var ach in achievements)
            {
                try
                {
                    var tid = ach.Title.RowId;
                    if (tid == 0 || byTitle.ContainsKey(tid)) continue;

                    var cat = string.Empty;
                    try { cat = ach.AchievementCategory.ValueNullable?.Name.ToString() ?? string.Empty; } catch { }
                    byTitle[tid] = (ach.Name.ToString(), Condense(ach.Description.ToString()), cat, ach.Points, ach.RowId);
                }
                catch { }
            }

            this.all.Capacity = Math.Max(this.all.Capacity, titles.Count);
            foreach (var t in titles)
            {
                try
                {
                    var name = t.Masculine.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var e = new TitleEntry
                    {
                        Id       = t.RowId,
                        Name     = name,
                        NameF    = t.Feminine.ToString(),
                        IsPrefix = t.IsPrefix,
                        Order    = t.Order,
                    };

                    if (byTitle.TryGetValue(t.RowId, out var a))
                    {
                        e.AchName   = a.Name;
                        e.AchDesc   = a.Desc;
                        e.AchCat    = a.Cat;
                        e.AchPoints = a.Points;
                        e.AchId     = a.AchId;
                    }

                    PrepareEntry(e);
                    this.all.Add(e);
                    this.byId[e.Id] = e;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            this.status = "载入失败：" + ex.Message;
            DalamudServices.PluginLog.Warning(ex, "[LKami9TitleHelper] build failed");
            return;
        }

        this.categories.Clear();
        this.categories.AddRange(this.all
            .Select(x => x.AchCat)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal));

        this.built = true;
        this.LoadUnlockSnapshot();
        this.Rebuild();
        this.UpdateLoadedStatus();
        DalamudServices.PluginLog.Information(
            $"[LKami9TitleHelper] build complete: titles={this.all.Count}, savedUnlocked={this.unlockedCount}");
    }

    private static void PrepareEntry(TitleEntry e)
    {
        var kind = e.IsPrefix ? "前缀" : "后缀";
        var id   = e.Id.ToString();
        var tail = string.Concat("    (", kind, " · ", id, ")");

        e.Label     = string.Concat(e.Name, tail);
        e.SearchKey = string.Concat(e.Name, "\n", e.NameF, "\n", e.AchName, "\n",
            e.AchDesc, "\n", e.AchCat, "\n", id);

        if (e.AchName.Length > 0 || e.AchDesc.Length > 0)
        {
            var line = string.Concat("获取：", e.AchName.Length > 0 ? "【" + e.AchName + "】" : string.Empty);
            if (e.AchCat.Length > 0) line += "   " + e.AchCat;
            if (e.AchPoints > 0) line += string.Concat("   ", e.AchPoints.ToString(), " 点");
            e.AchievementLine = line;
        }
    }

    private unsafe bool ScanUnlockedNow()
    {
        var ui = UIState.Instance();
        if (ui == null) return false;

        this.unlockedCount = 0;
        foreach (var e in this.all)
        {
            bool ok;
            try { ok = ui->TitleList.IsTitleUnlocked((ushort)e.Id); }
            catch { ok = false; }

            e.Unlocked = ok;
            if (ok) this.unlockedCount++;
        }

        DalamudServices.PluginLog.Information(
            $"[LKami9TitleHelper] manual detect complete: unlocked={this.unlockedCount}");

        return true;
    }

    private unsafe void RequestUnlockDetection()
    {
        this.EnsureDataReady();
        if (!DalamudServices.PlayerState.IsLoaded)
        {
            this.unlockDetectionPending = true;
            this.status = "等待角色载入后检测称号…";
            return;
        }

        var ui = UIState.Instance();
        if (ui == null)
        {
            this.status = "无法读取称号数据：UIState 不可用。";
            return;
        }

        if (!ui->TitleList.DataReceived)
        {
            this.unlockDetectionPending = true;
            this.nextUnlockDetectionRequest = DateTime.UtcNow.AddSeconds(2);
            try { ui->TitleList.RequestTitleList(); } catch { }
            this.status = "正在请求称号数据，收到后会自动完成检测…";
            return;
        }

        this.CompleteUnlockDetection();
    }

    private void CompleteUnlockDetection()
    {
        this.unlockDetectionPending = false;
        if (!this.ScanUnlockedNow())
        {
            this.status = "检测失败：无法读取称号状态。";
            return;
        }

        this.SaveUnlockSnapshot();
        this.Rebuild();
        this.UpdateLoadedStatus();
    }

    private string UnlockSnapshotPath()
    {
        var directory = DalamudServices.PluginInterface.ConfigDirectory;
        Directory.CreateDirectory(directory.FullName);
        return Path.Combine(directory.FullName, UnlockSnapshotFileName);
    }

    private void LoadUnlockSnapshot()
    {
        this.unlockSnapshotSavedAt = null;
        this.unlockedCount = 0;
        foreach (var entry in this.all) entry.Unlocked = false;

        try
        {
            var path = this.UnlockSnapshotPath();
            if (!File.Exists(path)) return;

            var snapshot = JsonSerializer.Deserialize<LKami9TitleUnlockSnapshot>(File.ReadAllText(path));
            if (snapshot?.UnlockedTitleIds == null) return;

            var ids = snapshot.UnlockedTitleIds.ToHashSet();
            foreach (var entry in this.all)
            {
                entry.Unlocked = ids.Contains(entry.Id);
                if (entry.Unlocked) this.unlockedCount++;
            }

            this.unlockSnapshotSavedAt = snapshot.SavedAt;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9TitleHelper] load unlock snapshot failed");
            this.status = "读取上次检测数据失败，请重新点击「立即检测」。";
        }
    }

    private void SaveUnlockSnapshot()
    {
        try
        {
            var snapshot = new LKami9TitleUnlockSnapshot
            {
                SavedAt = DateTimeOffset.Now,
                UnlockedTitleIds = this.all
                    .Where(x => x.Unlocked)
                    .Select(x => x.Id)
                    .OrderBy(x => x)
                    .ToList(),
            };

            var path = this.UnlockSnapshotPath();
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            File.Move(temp, path, true);
            this.unlockSnapshotSavedAt = snapshot.SavedAt;
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9TitleHelper] save unlock snapshot failed");
            this.status = "检测完成，但保存本地数据失败。";
        }
    }

    private static string Condense(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    private readonly Dictionary<uint, (uint Cur, uint Max)> progress = [];
    private double nextProgressRequest;
    private readonly Dictionary<ushort, bool> watchedCache = [];
    private double nextWatchedRefresh;

    private unsafe bool TryGetProgress(uint achId, out uint cur, out uint max)
    {
        cur = 0;
        max = 0;

        if (this.progress.TryGetValue(achId, out var cached) && cached.Max > 0)
        {
            cur = cached.Cur;
            max = cached.Max;
            return true;
        }

        var ach = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
        if (ach != null && ach->ProgressAchievementId == achId && ach->ProgressMax > 0)
        {
            this.progress[achId] = (ach->ProgressCurrent, ach->ProgressMax);
            cur = ach->ProgressCurrent;
            max = ach->ProgressMax;
            return true;
        }

        return false;
    }

    private unsafe void RequestProgress(uint achId)
    {
        var now = ImGui.GetTime();
        if (now < this.nextProgressRequest) return;
        this.nextProgressRequest = now + 0.25;

        var ach = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
        if (ach == null) return;

        ach->RequestAchievementProgress(achId);
    }

    private unsafe bool IsWatched(uint achId)
    {
        if (achId == 0 || achId > ushort.MaxValue) return false;

        var id  = (ushort)achId;
        var now = ImGui.GetTime();
        if (now >= this.nextWatchedRefresh)
        {
            this.watchedCache.Clear();
            this.nextWatchedRefresh = now + 5.0;
        }

        if (this.watchedCache.TryGetValue(id, out var cached)) return cached;

        var module = AchievementListModule.Instance();
        var watched = module != null && module->IsOnWatchlist(id);
        this.watchedCache[id] = watched;
        return watched;
    }

    private unsafe bool ToggleWatch(uint achId)
    {
        if (achId == 0 || achId > ushort.MaxValue) return false;

        var module = AchievementListModule.Instance();
        if (module == null) return false;

        var id = (ushort)achId;
        if (module->IsOnWatchlist(id))
        {
            module->RemoveFromWatchlist(id);
            this.watchedCache[id] = false;
            this.status = "已移出成就监视。";
            return false;
        }

        if (module->IsWatchlistFull())
        {
            this.status = "成就监视已满（上限 30）。";
            return false;
        }

        module->AddToWatchlist(id);
        this.watchedCache[id] = true;
        this.status = "已加入成就监视。";
        return true;
    }

    private uint lastRandomId;

    private void EquipRandom()
    {
        TitleEntry pick = null;
        var candidates = 0;

        foreach (var e in this.all)
        {
            if (!e.Unlocked || e.Id == this.lastRandomId) continue;
            candidates++;
            if (Random.Shared.Next(candidates) == 0) pick = e;
        }

        if (pick == null)
        {
            this.status = "没有可用的已解锁称号。";
            return;
        }

        this.lastRandomId = pick.Id;
        this.Equip(pick.Id);
        this.status = string.Format("随机称号：{0}（{1}）", pick.Name, pick.Id);
    }

    private unsafe void Equip(uint id)
    {
        try
        {
            var ui = UIState.Instance();
            if (ui == null) { this.status = "还没进入游戏世界。"; return; }

            if (!ui->TitleList.IsTitleUnlocked((ushort)id))
            {
                this.status = $"称号 {id} 未解锁，无法佩戴。";
                return;
            }

            ui->TitleController.SendTitleIdUpdate((ushort)id);
            this.byId.TryGetValue(id, out var e);
            this.lastEquipped = e != null ? $"{e.Name}（{id}）" : id.ToString();
            this.status       = "已佩戴：" + this.lastEquipped;
        }
        catch (Exception ex)
        {
            this.status = "佩戴失败：" + ex.Message;
        }
    }

    private void Rebuild()
    {
        this.view.Clear();

        var kw = this.search.Trim();
        foreach (var e in this.all)
        {
            var ok = e.Unlocked;

            if (this.unlockedOnly && !ok) continue;
            if (this.lockedOnly && ok) continue;
            if (this.prefixOnly && !e.IsPrefix) continue;
            if (this.suffixOnly && e.IsPrefix) continue;
            if (kw.Length > 0 && !e.SearchKey.Contains(kw, StringComparison.OrdinalIgnoreCase)) continue;

            if (this.config.CategoryFilter.Length > 0 &&
                !string.Equals(e.AchCat, this.config.CategoryFilter, StringComparison.Ordinal))
                continue;

            this.view.Add(e);
        }

        // 收藏优先，其次按 Order
        this.view.Sort((a, b) =>
        {
            var fa = this.favoriteIds.Contains(a.Id);
            var fb = this.favoriteIds.Contains(b.Id);
            if (fa != fb) return fa ? -1 : 1;

            return this.config.Ascending ? a.Order.CompareTo(b.Order) : b.Order.CompareTo(a.Order);
        });
    }

    private void RefreshFavoriteCache()
    {
        this.favoriteIds.Clear();
        foreach (var id in this.config.Favorites) this.favoriteIds.Add(id);
    }

    private void ToggleFavorite(uint id, bool starred)
    {
        if (starred)
        {
            this.favoriteIds.Remove(id);
            this.config.Favorites.RemoveAll(x => x == id);
        }
        else if (this.favoriteIds.Add(id))
        {
            this.config.Favorites.Add(id);
        }

        this.Rebuild();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!this.unlockDetectionPending) return;
        if (!this.built) return;
        if (DateTime.UtcNow < this.nextUnlockDetectionCheck) return;
        this.nextUnlockDetectionCheck = DateTime.UtcNow.AddMilliseconds(500);

        if (!DalamudServices.PlayerState.IsLoaded)
        {
            this.status = "等待角色载入后检测称号…";
            return;
        }

        unsafe
        {
            var ui = UIState.Instance();
            if (ui == null)
            {
                this.unlockDetectionPending = false;
                this.status = "检测失败：无法读取称号状态。";
                return;
            }

            if (!ui->TitleList.DataReceived)
            {
                if (DateTime.UtcNow >= this.nextUnlockDetectionRequest)
                {
                    try { ui->TitleList.RequestTitleList(); } catch { }
                    this.nextUnlockDetectionRequest = DateTime.UtcNow.AddSeconds(2);
                    DalamudServices.PluginLog.Information(
                        $"[LKami9TitleHelper] manual detect waiting: received=false, requested={ui->TitleList.DataRequested}, pending={ui->TitleList.DataPending}");
                }
                return;
            }
        }

        this.CompleteUnlockDetection();
    }

    private void UpdateLoadedStatus()
    {
        this.status = this.unlockSnapshotSavedAt is { } savedAt
            ? $"已载入 {this.all.Count} 个称号 · 上次检测解锁 {this.unlockedCount} 个 · {savedAt:MM-dd HH:mm:ss}"
            : $"已载入 {this.all.Count} 个称号 · 尚未检测，请点击「立即检测」。";
    }

    #endregion

    #region 窗口

    /// <summary>扫当前 ImGui 上下文，取名字含 Omni、本帧活跃、面积最大的根窗口。</summary>
    private static unsafe ImGuiWindowPtr FindOmniMainWindow()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.Handle == null) return ImGuiWindowPtr.Null;

        var list  = ctx.Handle->Windows;
        var frame = ctx.Handle->FrameCount;
        var best  = ImGuiWindowPtr.Null;

        for (var i = 0; i < list.Size; i++)
        {
            var w = list[i];
            if (w.IsNull) continue;
            if (w.Handle->Hidden != 0 || w.Handle->IsFallbackWindow != 0) continue;
            if (w.Handle->ParentWindow != null) continue;
            if (w.Handle->LastFrameActive != frame) continue;

            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)w.Handle->Name) ?? string.Empty;
            if (!name.Contains("Omni", StringComparison.OrdinalIgnoreCase)) continue;

            if (best.IsNull ||
                w.Handle->Size.X * w.Handle->Size.Y > best.Handle->Size.X * best.Handle->Size.Y)
                best = w;
        }

        return best;
    }

    private unsafe System.Numerics.Vector2 ComputeDockedPosition(
        ImGuiWindowPtr omni, System.Numerics.Vector2 mySize)
    {
        var vp       = ImGui.GetMainViewport();
        var gap      = OmniTheme.Scale(12f);
        var fallback = OmniTheme.ClampWindowPosition(
            vp.WorkPos + vp.WorkSize - mySize - new System.Numerics.Vector2(gap, gap), mySize);

        if (omni.IsNull) return fallback;

        var oPos = omni.Handle->Pos;
        var oSz  = omni.Handle->Size;
        var candidates = new[]
        {
            new System.Numerics.Vector2(oPos.X + oSz.X + gap, oPos.Y),
            new System.Numerics.Vector2(oPos.X - mySize.X - gap, oPos.Y),
            new System.Numerics.Vector2(oPos.X, oPos.Y + oSz.Y + gap),
        };

        foreach (var cand in candidates)
        {
            var clamped = OmniTheme.ClampWindowPosition(cand, mySize);
            if (MathF.Abs(clamped.X - cand.X) < 1f &&
                MathF.Abs(clamped.Y - cand.Y) < 1f &&
                !Overlaps(clamped, mySize, oPos, oSz))
                return clamped;
        }

        return fallback;
    }

    private static bool Overlaps(
        System.Numerics.Vector2 aPos, System.Numerics.Vector2 aSize,
        System.Numerics.Vector2 bPos, System.Numerics.Vector2 bSize) =>
        aPos.X < bPos.X + bSize.X && aPos.X + aSize.X > bPos.X &&
        aPos.Y < bPos.Y + bSize.Y && aPos.Y + aSize.Y > bPos.Y;

    private static void DrawThemedRule(System.Numerics.Vector4 color)
    {
        var pos   = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var y     = pos.Y + OmniTheme.Scale(1f);
        ImGui.GetWindowDrawList().AddLine(pos with { Y = y }, new System.Numerics.Vector2(pos.X + width, y),
            ImGui.GetColorU32(color));
        ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.ContentGap()));
    }

    private static void WrappedTextColored(System.Numerics.Vector4 color, string text)
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    private static void FillCurrentWindow(System.Numerics.Vector4 color, float radius)
    {
        var pos  = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size,
            ImGui.GetColorU32(Opaque(color)), radius);
    }

    private unsafe void DrawWindow()
    {
        if (!this.config.WindowOpen) return;

        var viewport = ImGui.GetMainViewport();
        var open     = this.config.WindowOpen;

        var targetSize = this.collapsed
            ? new System.Numerics.Vector2(OmniTheme.Scale(420f), OmniTheme.TitleBarHeight() + OmniTheme.Scale(14f))
            : OmniTheme.ClampWindowSize(this.expandedSize);

        ImGui.SetNextWindowSizeConstraints(OmniTheme.MainWindowMinSize(), viewport.WorkSize);

        if (this.resizePending)
        {
            ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
            this.resizePending = false;
        }
        else if (!this.collapsed)
        {
            ImGui.SetNextWindowSize(OmniTheme.ClampWindowSize(this.expandedSize), ImGuiCond.FirstUseEver);
        }

        var omni = this.config.DockToOmni ? FindOmniMainWindow() : ImGuiWindowPtr.Null;

        if (this.config.DockToOmni)
        {
            // 贴靠模式：每帧跟随 Omni 主窗口
            var dockPos = this.ComputeDockedPosition(omni, targetSize);
            OmniWindowChrome.SetNextWindowPosition(WindowId, dockPos);
            ImGui.SetNextWindowPos(dockPos, ImGuiCond.Always);
        }
        else
        {
            // 自由模式：位置完全交给 ImGui 自己管理（按窗口名持久化），不每帧强制 → 可拖动
            ImGui.SetNextWindowPos(
                OmniTheme.ClampWindowPosition(
                    viewport.WorkPos + viewport.WorkSize - targetSize,
                    targetSize),
                ImGuiCond.FirstUseEver);
        }

        var self = ImGuiWindowPtr.Null;

        // Omni 字体 + 配色（含滚动条）：RAII 作用域，Dispose 必定按计数弹回
        using var style = new OmniStyleScope();
        PushOmniStyle(style);

        this.omniFont ??= OmniFonts.GetUIFont();
        IDisposable fontScope = this.omniFont is { Available: true } ? this.omniFont.Push() : null;

        try
        {
        if (ImGui.Begin(WindowId, ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoDocking  | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            self = ImGuiP.GetCurrentWindow();

            var framePos  = ImGui.GetWindowPos();
            var frameSize = ImGui.GetWindowSize();
            this.lastPos  = framePos;
            var tokens    = OmniTheme.Tokens;
            var radius    = OmniTheme.Scale(tokens.BorderRadius);
            var draw = ImGui.GetWindowDrawList();

            draw.AddRectFilled(framePos, framePos + frameSize,
                ImGui.GetColorU32(Opaque(tokens.Background)), radius);

            var chrome = OmniWindowChrome.Draw(
                framePos, frameSize, this.collapsed,
                "LKami9 称号助手", CollapseId, CloseId, true);

            if (this.config.DockToOmni)
            {
                // 贴靠是每帧强制位置；只要用户在标题栏按下并拖动，就自动解除贴靠
                var titleHeight = OmniTheme.TitleBarHeight();
                var inTitle = ImGui.IsWindowHovered()
                           && ImGui.GetIO().MousePos.Y < framePos.Y + titleHeight;

                if (inTitle && ImGui.IsMouseDragging(ImGuiMouseButton.Left, OmniTheme.Scale(3f)))
                    this.config.DockToOmni = false;
            }

            if (chrome.CloseClicked)
            {
                this.config.WindowOpen = false;
                this.collapsed         = false;
                this.resizePending     = true;
            }

            if (chrome.ToggleCollapse)
            {
                if (this.collapsed)
                {
                    this.collapsed     = false;
                    this.resizePending = true;
                }
                else
                {
                    this.expandedSize  = frameSize;
                    this.collapsed     = true;
                    this.resizePending = true;
                }
            }
            else if (!this.collapsed)
            {
                this.expandedSize = frameSize;
            }

            if (!this.collapsed)
            {
                var (contentPos, contentSize) = OmniWindowChrome.GetContentBounds(framePos, frameSize);
                if (contentSize.X > 1f && contentSize.Y > 1f)
                {
                    var panel = PanelColor(tokens);

                    ImGui.SetCursorScreenPos(contentPos);
                    ImGui.BeginChild("###LKami9TitleContent", contentSize, false,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
                    FillCurrentWindow(panel, radius);
                    this.DrawContent();
                    ImGui.EndChild();
                }
            }
        }

        ImGui.End();
        }
        finally
        {
            fontScope?.Dispose();
        }

        if (!self.IsNull && this.config.DockToOmni && !omni.IsNull)
            ImGuiP.BringWindowToDisplayBehind(self, omni);

        if (!open) this.config.WindowOpen = false;
    }

    /// <summary>Omni 外观作用域：自计数 push，Dispose 按计数 pop —— 结构上不可能泄漏。</summary>
    private sealed class OmniStyleScope : IDisposable
    {
        private int colors;
        private int vars;

        public void Color(ImGuiCol idx, System.Numerics.Vector4 c)
        {
            ImGui.PushStyleColor(idx, c);
            this.colors++;
        }

        public void Var(ImGuiStyleVar v, System.Numerics.Vector2 val)
        {
            ImGui.PushStyleVar(v, val);
            this.vars++;
        }

        public void Var(ImGuiStyleVar v, float val)
        {
            ImGui.PushStyleVar(v, val);
            this.vars++;
        }

        public void Dispose()
        {
            if (this.vars   > 0) ImGui.PopStyleVar(this.vars);
            if (this.colors > 0) ImGui.PopStyleColor(this.colors);
            this.vars   = 0;
            this.colors = 0;
        }
    }

    /// <summary>Omni 本体外观：32 个颜色 + 8 个样式变量（含滚动条），全部交给 scope 回收。</summary>
    private static void PushOmniStyle(OmniStyleScope style)
    {
        var t = OmniTheme.Tokens;

        var textDisabled    = t.Text with { W = t.Text.W * 0.55f };
        var titleBackground = System.Numerics.Vector4.Lerp(t.Background, t.Primary, 0.35f);
        var titleActive     = System.Numerics.Vector4.Lerp(t.Background, t.Accent,  0.65f);

        style.Color(ImGuiCol.WindowBg, Opaque(t.Background));
        style.Color(ImGuiCol.ChildBg, System.Numerics.Vector4.Zero);
        style.Color(ImGuiCol.PopupBg, Opaque(t.Background));
        style.Color(ImGuiCol.Border, t.Border);
        style.Color(ImGuiCol.Text, t.Text);
        style.Color(ImGuiCol.TextDisabled, textDisabled);
        style.Color(ImGuiCol.Button, t.Surface);
        style.Color(ImGuiCol.ButtonHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ButtonActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Header, t.Surface);
        style.Color(ImGuiCol.HeaderHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.HeaderActive, t.Accent);
        style.Color(ImGuiCol.FrameBg, t.Surface);
        style.Color(ImGuiCol.FrameBgHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.FrameBgActive, OmniTheme.ActiveBackground);
        style.Color(ImGuiCol.Tab, t.Surface);
        style.Color(ImGuiCol.TabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.TabActive, t.Accent);
        style.Color(ImGuiCol.TitleBg, titleBackground);
        style.Color(ImGuiCol.TitleBgActive, titleActive);
        style.Color(ImGuiCol.TitleBgCollapsed, titleBackground);
        style.Color(ImGuiCol.ScrollbarBg, Opaque(t.Background));
        style.Color(ImGuiCol.ScrollbarGrab, t.Secondary);
        style.Color(ImGuiCol.ScrollbarGrabHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ScrollbarGrabActive, t.Accent);
        style.Color(ImGuiCol.CheckMark, t.Success);
        style.Color(ImGuiCol.SliderGrab, t.Secondary);
        style.Color(ImGuiCol.SliderGrabActive, t.Accent);
        style.Color(ImGuiCol.Separator, t.Border);
        style.Color(ImGuiCol.ResizeGrip, t.Surface);
        style.Color(ImGuiCol.ResizeGripHovered, OmniTheme.HoverBackground);
        style.Color(ImGuiCol.ResizeGripActive, t.Accent);

        style.Var(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        style.Var(ImGuiStyleVar.WindowRounding, OmniTheme.Scale(t.BorderRadius));
        style.Var(ImGuiStyleVar.WindowBorderSize, OmniTheme.BorderThickness());
        style.Var(ImGuiStyleVar.FrameRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.PopupRounding, OmniTheme.Scale(t.BorderRadius));
        style.Var(ImGuiStyleVar.ScrollbarRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.GrabRounding, OmniTheme.Scale(t.ButtonRadius));
        style.Var(ImGuiStyleVar.FramePadding, OmniTheme.Scale(t.FramePadding));
        style.Var(ImGuiStyleVar.ItemSpacing, OmniTheme.Scale(t.ItemSpacing));
    }

    private void DrawContent()
    {
        var tokens  = OmniTheme.Tokens;
        var pad     = MathF.Max(OmniTheme.Scale(8f), OmniTheme.WindowInset());
        var panel   = PanelColor(tokens);
        var cText   = Opaque(tokens.Text);

        ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.ContentGap()));
        ImGui.Indent(pad);

        // 工具条：搜索 + 分类（Omni 控件）
        var sText = this.search;
        if (OmniControls.InputTextWithHint("###titlesearch", "搜索称号 / 成就 / 分类 / ID",
                ref sText, 120, OmniTheme.Scale(230f)))
        {
            this.search = sText;
            this.page   = 1;
            this.Rebuild();
        }

        ImGui.SameLine();
        var preview = this.config.CategoryFilter.Length == 0 ? "全部分类" : this.config.CategoryFilter;
        if (OmniControls.BeginCombo("###titlecat", preview, OmniTheme.Scale(150f)))
        {
            if (OmniControls.RoundedMenuItem("全部分类", true))
            {
                this.config.CategoryFilter = string.Empty;
                this.page = 1;
                this.Rebuild();
            }

            foreach (var cat in this.categories)
            {
                if (!OmniControls.RoundedMenuItem(cat, true)) continue;
                this.config.CategoryFilter = cat;
                this.page = 1;
                this.Rebuild();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton("检测", false)) this.RequestUnlockDetection();

        ImGui.SameLine();
        ImGui.BeginDisabled(this.search.Length == 0 && this.config.CategoryFilter.Length == 0);
        if (OmniControls.SmallButton("清空", false))
        {
            this.search = string.Empty;
            this.config.CategoryFilter = string.Empty;
            this.page = 1;
            this.Rebuild();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (OmniControls.SmallButton("随机称号", false,
                new System.Numerics.Vector2(OmniTheme.Scale(92f), OmniTheme.Scale(24f))))
            this.EquipRandom();

        // 过滤
        ImGui.Spacing();
        var uo = this.unlockedOnly;
        if (OmniControls.Checkbox("仅已解锁", ref uo)) { this.unlockedOnly = uo; if (uo) this.lockedOnly = false; this.page = 1; this.Rebuild(); }
        ImGui.SameLine();
        var lo = this.lockedOnly;
        if (OmniControls.Checkbox("仅未解锁", ref lo)) { this.lockedOnly = lo; if (lo) this.unlockedOnly = false; this.page = 1; this.Rebuild(); }
        ImGui.SameLine();
        var po = this.prefixOnly;
        if (OmniControls.Checkbox("仅前缀", ref po)) { this.prefixOnly = po; if (po) this.suffixOnly = false; this.page = 1; this.Rebuild(); }
        ImGui.SameLine();
        var so = this.suffixOnly;
        if (OmniControls.Checkbox("仅后缀", ref so)) { this.suffixOnly = so; if (so) this.prefixOnly = false; this.page = 1; this.Rebuild(); }
        ImGui.SameLine();
        var asc = this.config.Ascending;
        if (OmniControls.Checkbox("正序", ref asc)) { this.config.Ascending = asc; this.Rebuild(); }

        // 摘要
        ImGui.Spacing();
        ImGui.TextColored(cText,
            string.Format("共 {0} 个称号 · 已解锁 {1} · 当前 {2} 条", this.all.Count, this.unlockedCount, this.view.Count));
        if (this.lastEquipped.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(cText, "· 上次佩戴：" + this.lastEquipped);
        }

        DrawThemedRule(tokens.Border);

        // 列表（分页）
        var pagerHeight = OmniTheme.Scale(34f);
        var listHeight  = MathF.Max(OmniTheme.Scale(160f), ImGui.GetContentRegionAvail().Y - pagerHeight);
        var pageSize    = Math.Max(20, this.config.PageSize);
        var pageCount   = Math.Max(1, (this.view.Count + pageSize - 1) / pageSize);
        if (this.page > pageCount) this.page = pageCount;
        if (this.page < 1) this.page = 1;
        var rowHeight     = OmniTheme.Scale(24f);
        var btnWidth      = OmniTheme.Scale(54f);
        var favoriteWidth = OmniControls.FavoriteButtonSize().X;

        using (var listScope = new OmniStyleScope())
        {
            listScope.Color(ImGuiCol.ChildBg, panel);
            listScope.Color(ImGuiCol.Border, tokens.Border);
            // 行本身（RoundedSelectable）用的是 Header 色，这里也换成底板色，
            // 否则每行会自带一层亮色背景，看起来就是"中间白、两边深"
            listScope.Color(ImGuiCol.Header, panel);
            listScope.Color(ImGuiCol.HeaderHovered, OmniTheme.HoverBackground);
            listScope.Color(ImGuiCol.HeaderActive, panel);
            listScope.Color(ImGuiCol.TableRowBg, panel);
            listScope.Color(ImGuiCol.TableRowBgAlt, panel);
            listScope.Color(ImGuiCol.TableHeaderBg, panel);
            listScope.Var(ImGuiStyleVar.ChildRounding, OmniTheme.Scale(tokens.BorderRadius));
            listScope.Var(ImGuiStyleVar.ChildBorderSize, OmniTheme.BorderThickness());

            ImGui.BeginChild("###LKami9TitleList", new System.Numerics.Vector2(0f, listHeight), true,
                ImGuiWindowFlags.None);

        var from = (this.page - 1) * pageSize;
        var to   = Math.Min(this.view.Count, from + pageSize);
        if (ImGui.BeginTable(
                "###LKami9TitleGrid",
                3,
                ImGuiTableFlags.SizingStretchSame |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersInnerV))
        {
            var column = 0;
            for (var i = from; i < to; i++)
            {
                if (column == 0) ImGui.TableNextRow();
                ImGui.TableNextColumn();
                this.DrawRow(this.view[i], tokens, cText, rowHeight, btnWidth, favoriteWidth);
                column = (column + 1) % 3;
            }

            ImGui.EndTable();
        }

            ImGui.EndChild();
        }

        // Omni 原生分页器
        var pageLocal = this.page;
        var jumpLocal = this.jumpPage;
        OmniPager.Draw("###LKami9TitlePager", ref pageLocal, ref jumpLocal, this.view.Count, pageSize);
        this.page     = pageLocal;
        this.jumpPage = jumpLocal;

        ImGui.TextColored(cText, this.status);
        ImGui.Unindent(pad);
    }

    /// <summary>面板色：沿用 Omni 主内容区正在使用的深色底板，列表不再退回浅色 Background。</summary>
    private static System.Numerics.Vector4 PanelColor(OmniToolbox.UI.Theme.ThemeTokens t)
    {
        return System.Numerics.Vector4.Lerp(Opaque(t.Background), Opaque(t.Surface), 0.5f);
    }

    private void DrawRow(TitleEntry e,
                         ThemeTokens tokens,
                         System.Numerics.Vector4 cText,
                         float rowHeight,
                         float btnWidth,
                         float favoriteWidth)
    {
        var ok      = e.Unlocked;
        var starred = this.favoriteIds.Contains(e.Id);

        ImGui.PushID((int)e.Id);

        var spacing   = ImGui.GetStyle().ItemSpacing.X;
        var actionGap = spacing * (ok ? 2f : 1f);
        var rowWidth  = MathF.Max(OmniTheme.Scale(56f),
            ImGui.GetContentRegionAvail().X - favoriteWidth - (ok ? btnWidth : 0f) - actionGap);
        if (OmniControls.RoundedSelectable(e.Label, this.expanded.Contains(e.Id),
                ImGuiSelectableFlags.None, new System.Numerics.Vector2(rowWidth, rowHeight)))
        {
            if (!this.expanded.Remove(e.Id)) this.expanded.Add(e.Id);
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && ok)
            this.Equip(e.Id);

        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            ImGui.SetNextWindowSizeConstraints(
                new System.Numerics.Vector2(OmniTheme.Scale(156f), 0f),
                new System.Numerics.Vector2(OmniTheme.Scale(240f), float.MaxValue));
        }

        if (ImGui.BeginPopupContextItem("###ctx"))
        {
            using var popupScope = new OmniStyleScope();
            var panel = PanelColor(tokens);
            popupScope.Color(ImGuiCol.PopupBg, panel);
            popupScope.Color(ImGuiCol.Text, cText);
            popupScope.Color(ImGuiCol.Header, panel);
            popupScope.Color(ImGuiCol.HeaderHovered, OmniTheme.HoverBackground);
            popupScope.Color(ImGuiCol.HeaderActive, panel);
            popupScope.Var(ImGuiStyleVar.ItemSpacing,
                OmniTheme.Scale(new System.Numerics.Vector2(8f, 3f)));

            var popupPos    = ImGui.GetWindowPos();
            var popupSize   = ImGui.GetWindowSize();
            var popupRadius = OmniTheme.Scale(tokens.BorderRadius);
            var popupDraw   = ImGui.GetWindowDrawList();
            popupDraw.AddRectFilled(popupPos, popupPos + popupSize,
                ImGui.GetColorU32(Opaque(panel)), popupRadius);
            popupDraw.AddRect(popupPos, popupPos + popupSize,
                ImGui.GetColorU32(tokens.Border), popupRadius,
                ImDrawFlags.None, OmniTheme.BorderThickness());

            var popupPad = OmniTheme.Scale(8f);
            ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.Scale(3f)));
            ImGui.Indent(popupPad);

            if (ok && OmniControls.RoundedMenuItem("佩戴")) this.Equip(e.Id);
            if (e.AchId is > 0 and <= ushort.MaxValue)
            {
                var watched = IsWatched(e.AchId);
                if (OmniControls.RoundedMenuItem(watched ? "移出成就监视" : "加入成就监视"))
                    this.ToggleWatch(e.AchId);
            }
            if (OmniControls.RoundedMenuItem("复制称号名")) ImGui.SetClipboardText(e.Name);
            if (OmniControls.RoundedMenuItem(starred ? "取消收藏" : "收藏"))
                this.ToggleFavorite(e.Id, starred);

            ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.Scale(3f)));
            ImGui.Unindent(popupPad);
            ImGui.EndPopup();
        }

        ImGui.SameLine();
        if (OmniControls.FavoriteButton("###fav", starred, starred ? "取消收藏" : "收藏并置顶"))
            this.ToggleFavorite(e.Id, starred);

        if (ok)
        {
            ImGui.SameLine();
            if (OmniControls.SmallButton("佩戴", false,
                    new System.Numerics.Vector2(btnWidth, MathF.Min(rowHeight, OmniTheme.Scale(22f)))))
                this.Equip(e.Id);
        }

        var showDetail = this.expanded.Contains(e.Id) ||
                         (this.config.AutoExpandLocked && !ok);
        if (showDetail)
        {
            ImGui.Indent(OmniTheme.Scale(26f));

            if (e.AchName.Length > 0 || e.AchDesc.Length > 0)
            {
                ImGui.TextColored(cText, e.AchievementLine);

                if (e.AchDesc.Length > 0) WrappedTextColored(cText, e.AchDesc);
            }
            else
            {
                ImGui.TextColored(cText, "（数据表里没有关联成就，可能是特殊来源）");
            }

            if (!ok && e.AchId is > 0 and <= ushort.MaxValue)
            {
                if (this.TryGetProgress(e.AchId, out var cur, out var max))
                {
                    ImGui.TextColored(cText, string.Format("进度 {0} / {1}", cur, max));
                    ImGui.SameLine();
                    var frac = max > 0 ? Math.Clamp((float)cur / max, 0f, 1f) : 0f;
                    ImGui.ProgressBar(frac,
                        new System.Numerics.Vector2(OmniTheme.Scale(150f), OmniTheme.Scale(8f)), string.Empty);
                }
                else if (OmniControls.SmallButton("查询进度", false))
                {
                    this.RequestProgress(e.AchId);
                }

                if (this.IsWatched(e.AchId)) { ImGui.SameLine(); ImGui.TextColored(cText, "· 已监视"); }
            }

            ImGui.Unindent(OmniTheme.Scale(26f));
        }

        ImGui.Dummy(new System.Numerics.Vector2(0f, OmniTheme.Scale(4f)));
        ImGui.PopID();
    }

    #endregion

    #region 设置页（极简）


    public override bool DrawSettings()
    {
        ImGui.TextUnformatted("LKami9 称号助手");
        ImGui.TextDisabled("主界面在独立窗口中，配色与窗口样式跟随 Omni 本体主题。");

        var changed = false;

        ImGui.Spacing();
        if (ImGui.Button(this.config.WindowOpen ? "关闭窗口" : "打开窗口"))
        {
            this.ToggleWindow(!this.config.WindowOpen);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("立即检测")) this.RequestUnlockDetection();

        var autoExpand = this.config.AutoExpandLocked;
        if (ImGui.Checkbox("默认展开未解锁称号", ref autoExpand))
        {
            this.config.AutoExpandLocked = autoExpand;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextDisabled(this.status);

        // 按官方规范：只有配置真的变了才返回 true，让宿主保存
        return changed;
    }


    private static System.Numerics.Vector4 Opaque(System.Numerics.Vector4 c) => c with { W = 1f };

    private static float Lum(System.Numerics.Vector4 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    private static float Contrast(System.Numerics.Vector4 a, System.Numerics.Vector4 b)
    {
        var la = Lum(a) + 0.05f;
        var lb = Lum(b) + 0.05f;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>把前景色推到与背景对比度 ≥ 4.5（浅底推暗、深底推亮）。</summary>
    private static System.Numerics.Vector4 Readable(System.Numerics.Vector4 fg, System.Numerics.Vector4 bg)
    {
        fg = Opaque(fg);
        var lightBg = Lum(bg) > 0.5f;

        for (var i = 0; i < 24 && Contrast(fg, bg) < 4.5f; i++)
        {
            fg = lightBg
                ? new System.Numerics.Vector4(fg.X * 0.82f, fg.Y * 0.82f, fg.Z * 0.82f, 1f)
                : System.Numerics.Vector4.Lerp(fg, System.Numerics.Vector4.One, 0.18f);
        }

        return fg;
    }



    #endregion
}

public sealed class LKami9TitleHelperConfig
{
    public bool WindowOpen { get; set; }
    public bool Ascending  { get; set; } = true;
    public bool ScanOnOpen { get; set; }
    public bool DockToOmni { get; set; } = false;
    public bool AutoExpandLocked { get; set; }
    public int  ConfigVersion { get; set; }
    public int  PageSize   { get; set; } = 100;
    public string CategoryFilter { get; set; } = string.Empty;
    public List<uint> Favorites { get; set; } = [];
}

public sealed class LKami9TitleUnlockSnapshot
{
    public int Version { get; set; } = 1;
    public DateTimeOffset SavedAt { get; set; }
    public List<uint> UnlockedTitleIds { get; set; } = [];
}
