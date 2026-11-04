using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Excel.Sheets;

using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;

namespace LKami9.SpotDiff;

/// <summary>
/// LKami9 图标找茬（在线模块版 · 独立窗口）
///
/// 左右两幅用游戏内物品图标拼成的图，右图偷偷改掉几处，找出全部差异过关。
/// 素材全部来自游戏本体的物品图标（TextureProvider.GetFromGameIcon），
/// 不依赖任何外部图片、不需要写文件、不联网。
///
/// 差异类型：换图标 / 整体染色 / 缩小 / 加框。
/// 点击判定按格子 + 中心容错半径，点错扣时间。
/// </summary>
[ModuleApiVersion(1)]
public sealed class LKami9SpotDiff : OmniModuleBase
{
    private const string WindowId   = "###LKami9SpotDiffMain";
    private const string CollapseId = "##LKami9SpotDiffCollapse";
    private const string CloseId    = "##LKami9SpotDiffClose";
    private const string BoardId    = "###LKami9SpotDiffBoard";

    private Settings config = new();

    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Interface,
        Author   = "LKami9",
        Commands = [],
    };

    public override bool HasSettings => true;

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"]       = "FF14找茬",
            ["Description"] = "用游戏内物品图标拼成左右两幅图，找出右图被改掉的那几处。素材全部取自游戏本体，纯本地、不联网。",

            ["WindowTitle"]  = "FF14找茬",
            ["SettingsHint"] = "主界面在独立窗口中（点下方「打开窗口」按钮）。素材取自游戏内物品图标，不依赖外部图片。",

            ["OpenWindow"]  = "打开窗口",
            ["CloseWindow"] = "关闭窗口",
            ["WindowState"]  = "窗口状态：{0}",
            ["StateOpen"]    = "已打开",
            ["StateClosed"]  = "已关闭",

            ["StartButton"]  = "开始游戏",
            ["HistoryButton"] = "历史记录",
            ["HistoryEmpty"]  = "还没有记录。关掉窗口就会记一局。",
            ["HistoryRow"]    = "{0}　{1} 分　第 {2} 关　点错 {3} 次",
            ["AgainButton"]  = "再来一局",
            ["HintButton"]   = "提示（-10 秒）",
            ["PauseButton"]  = "暂停",
            ["ResumeButton"] = "继续",
            ["PhasePaused"]  = "已暂停 · 计时停止，点击「继续」回到游戏",

            ["PhaseIdle"]    = "点「开始游戏」开一局。左右两幅图有 {0} 处不同。",
            ["PhasePlaying"] = "第 {0} 关 · 找齐剩下 {1} 处差异",
            ["PhaseCleared"] = "过关！本关得分 {0}，进入第 {1} 关。",
            ["PhaseFailed"]  = "时间到。本局 {0} 分，最高 {1} 分。",
            ["PhaseEnded"]   = "本局已结束。得分 {0}，最高 {1} 分。",
            ["PoolFailed"]   = "读取物品图标失败，无法开始。",

            ["StatLine"]   = "第 {0} 关　得分 {1}　最高 {2}　点错 {3} 次",
            ["TimeLeft"]   = "剩余 {0:mm\\:ss}",
            ["TimeUrgent"] = "剩余 {0:mm\\:ss}（快没时间了）",

            ["GridSize"]   = "每边格子数",
            ["DiffCount"]  = "每关差异数",
            ["Seconds"]    = "单关时间（秒）",
            ["ResetButton"] = "重置记录",
            ["ResetDone"]   = "最高分已清零。",
            ["ShowBoard"]   = "显示格子底纹",
            ["SoundToggle"]     = "音效反馈",
            ["SoundChatToggle"] = "用聊天提示音通道（编号 1~16）",
            ["SoundPreview"]    = "试听",
            ["SeHitLabel"]      = "命中音效",
            ["SeMissLabel"]     = "点错音效",
            ["SeClearLabel"]    = "过关音效",
            ["SeHintLabel"]     = "提示音效",
            ["SeIdHint"]        = "编号：聊天通道 1~16；通用通道常用 23（开窗）/ 24（关窗）",

            ["BoardHint"]      = "左键点差异处。点错扣 5 秒。",
            ["FeedbackGain"]    = "+{0}",
            ["FeedbackPenalty"] = "-{0} 秒",
        });

    private sealed class Settings
    {
        public bool WindowOpen { get; set; }
        public int  GridSize   { get; set; } = 6;
        public int  DiffCount  { get; set; } = 3;
        public int  Seconds    { get; set; } = 90;
        public bool ShowBoard  { get; set; } = true;
        public bool Sound      { get; set; } = true;
        public bool SoundChat  { get; set; } = true;   // true = 聊天提示音通道（1~16），false = 通用 UI 音效
        public int  SeHit      { get; set; } = 1;
        public int  SeMiss     { get; set; } = 24;
        public int  SeClear    { get; set; } = 3;
        public int  SeHint     { get; set; } = 9;
        public int  ConfigVersion { get; set; }
    }

    private enum Phase
    {
        Idle,
        Playing,
        Cleared,
        Failed,
        Ended,
    }

    private sealed class Cell
    {
        public uint IconId;
        public uint Category;
        public int  Variant;   // 0 = 正常，1 = 水平镜像，2 = 旋转 180°
    }

    private sealed class Level
    {
        public int    Grid;
        public Cell[] Base  = [];
        public Cell[] Right = [];
        public bool[] IsDiff = [];
        public bool[] Found  = [];
        public int    DiffTotal;
    }

    private const int ScorePerHit          = 120;
    private const int WrongPenaltySeconds  = 5;
    private const int WrongPenaltyScore    = 20;

    private static readonly Vector4 FoundColor = new(0.32f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 WrongColor = new(1.00f, 0.38f, 0.38f, 1f);

    private sealed class Feedback
    {
        public int      Index;
        public bool     Correct;
        public DateTime Until;
        public string   Text = string.Empty;
    }

    private readonly List<Feedback> feedbacks = [];
    private string statsCache = string.Empty;
    private int    statsKey   = -1;
    private readonly Random rng = new();
    private readonly List<(uint IconId, uint Category)> iconPool = [];
    private readonly Dictionary<uint, ISharedImmediateTexture> iconCache = [];

    private Level? level;
    private Phase  phase = Phase.Idle;
    private int    levelNo = 1;
    private int    score;
    private int    best;
    private int    wrongClicks;
    private int    lastGain;
    private DateTime deadline = DateTime.MinValue;
    private double remainSeconds;
    private bool   paused;
    private double pausedRemain;
    private int    hintIndex = -1;
    private DateTime hintUntil = DateTime.MinValue;

    private bool focusRequested;
    private bool wasOpen;
    private bool windowDrawLogged;
    private bool collapsed;
    private bool resizePending;
    private Vector2 expandedSize = new(900f, 570f);

    private readonly List<RunRecord> history = [];
    private bool showHistory;

    private string HistoryFilePath =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LKami9SpotDiff.history.json");

    private string BestFilePath =>
        Path.Combine(DalamudServices.PluginInterface.GetPluginConfigDirectory(), "LKami9SpotDiff.best.json");

    #region 生命周期

    protected override void OnStart(FeatureLifetime activation)
    {
        var uiBuilder = DalamudServices.PluginInterface.UiBuilder;
        var framework = DalamudServices.Framework;

        activation.OnStopping(() => uiBuilder.Draw -= this.DrawWindow);
        uiBuilder.Draw += this.DrawWindow;

        activation.OnStopping(() => framework.Update -= this.OnFrameworkUpdate);
        framework.Update += this.OnFrameworkUpdate;

        this.MigrateConfig();
        this.LoadBest();
        this.LoadHistory();

        DalamudServices.PluginLog.Information(
            $"[LKami9SpotDiff] enabled: windowOpen={this.config.WindowOpen}, best={this.best}");
    }

    private void MigrateConfig()
    {
        if (this.config.ConfigVersion >= 4) return;

        if (this.config.ConfigVersion < 1)
        {
            this.config.GridSize  = Math.Clamp(this.config.GridSize, 4, 10);
            this.config.DiffCount = Math.Clamp(this.config.DiffCount, 2, 8);
            this.config.Seconds   = Math.Clamp(this.config.Seconds, 30, 300);
        }

        // v2/v3：窗口此前没有任何入口（无命令、默认关闭），升级后主动打开一次并置顶
        if (!this.config.WindowOpen)
        {
            this.config.WindowOpen = true;
            this.resizePending     = true;
            this.focusRequested    = true;
        }

        if (this.config.ConfigVersion < 4)
        {
            if (this.config.SeHit   <= 0) this.config.SeHit   = 1;
            if (this.config.SeMiss  <= 0) this.config.SeMiss  = 24;
            if (this.config.SeClear <= 0) this.config.SeClear = 3;
            if (this.config.SeHint  <= 0) this.config.SeHint  = 9;
        }

        this.config.ConfigVersion = 4;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.phase != Phase.Playing || this.paused) return;

        var left = (this.deadline - DateTime.UtcNow).TotalSeconds;
        this.remainSeconds = left > 0 ? left : 0;

        if (this.remainSeconds <= 0)
        {
            this.phase = Phase.Failed;
            this.level = null;
            this.RecordRun();
        }
    }

    public override bool ResetSettings()
    {
        var before = Snapshot();

        this.config.WindowOpen = false;
        this.config.GridSize   = 6;
        this.config.DiffCount  = 3;
        this.config.Seconds    = 90;
        this.config.ShowBoard  = true;
        this.collapsed         = false;
        this.resizePending     = true;

        return before != Snapshot();

        (bool, int, int, int, bool, bool, bool) Snapshot() => (
            this.config.WindowOpen,
            this.config.GridSize,
            this.config.DiffCount,
            this.config.Seconds,
            this.config.ShowBoard,
            this.collapsed,
            this.resizePending);
    }

    #endregion

    #region 设置页

    public override bool DrawSettings()
    {
        var changed = false;
        var scale   = OmniTheme.Scale(1f);

        ImGui.TextUnformatted(OmniLoc.Get(this, "WindowTitle"));
        ImGui.TextDisabled(OmniLoc.Get(this, "SettingsHint"));
        ImGui.Spacing();

        if (OmniControls.SmallButton(
                OmniLoc.Get(this, this.config.WindowOpen ? "CloseWindow" : "OpenWindow"), false))
        {
            this.config.WindowOpen = !this.config.WindowOpen;
            if (this.config.WindowOpen)
            {
                this.resizePending  = true;
                this.focusRequested = true;
            }
            changed = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "HistoryButton"), this.showHistory))
            this.showHistory = !this.showHistory;

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "ResetButton"), false))
        {
            this.best = 0;
            this.SaveBest();
            changed = true;
        }

        ImGui.Spacing();

        var grid = this.config.GridSize;
        if (OmniControls.SliderInt(OmniLoc.Get(this, "GridSize"), ref grid, 4, 10, "%d", 220f * scale))
        {
            this.config.GridSize = Math.Clamp(grid, 4, 10);
            changed              = true;
        }

        var diffs = this.config.DiffCount;
        if (OmniControls.SliderInt(OmniLoc.Get(this, "DiffCount"), ref diffs, 2, 8, "%d", 220f * scale))
        {
            this.config.DiffCount = Math.Clamp(diffs, 2, 8);
            changed               = true;
        }

        var secs = this.config.Seconds;
        if (OmniControls.SliderInt(OmniLoc.Get(this, "Seconds"), ref secs, 30, 300, "%d", 220f * scale))
        {
            this.config.Seconds = Math.Clamp(secs, 30, 300);
            changed             = true;
        }

        var showBoard = this.config.ShowBoard;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "ShowBoard"), ref showBoard))
        {
            this.config.ShowBoard = showBoard;
            changed               = true;
        }

        var sound = this.config.Sound;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "SoundToggle"), ref sound))
        {
            this.config.Sound = sound;
            changed           = true;
        }

        var soundChat = this.config.SoundChat;
        if (OmniControls.Checkbox(OmniLoc.Get(this, "SoundChatToggle"), ref soundChat))
        {
            this.config.SoundChat = soundChat;
            changed               = true;
        }

        changed |= this.DrawSoundRow("SeHitLabel",   this.config.SeHit,   v => this.config.SeHit   = v);
        changed |= this.DrawSoundRow("SeMissLabel",  this.config.SeMiss,  v => this.config.SeMiss  = v);
        changed |= this.DrawSoundRow("SeClearLabel", this.config.SeClear, v => this.config.SeClear = v);
        changed |= this.DrawSoundRow("SeHintLabel",  this.config.SeHint,  v => this.config.SeHint  = v);

        ImGui.TextDisabled(OmniLoc.Get(this, "SeIdHint"));

        ImGui.Spacing();
        ImGui.TextDisabled(string.Format(
            OmniLoc.Get(this, "WindowState"),
            OmniLoc.Get(this, this.config.WindowOpen ? "StateOpen" : "StateClosed")));
        ImGui.TextDisabled(string.Format(
            OmniLoc.Get(this, "StatLine"), this.levelNo, this.score, this.best, this.wrongClicks));

        if (this.showHistory) this.DrawHistory();

        return changed;
    }

    #endregion

    private string BuildStatsLine()
    {
        var key = HashCode.Combine(this.levelNo, this.score, this.best, this.wrongClicks);
        if (key == this.statsKey) return this.statsCache;

        this.statsKey   = key;
        this.statsCache = string.Format(
            OmniLoc.Get(this, "StatLine"), this.levelNo, this.score, this.best, this.wrongClicks);
        return this.statsCache;
    }

    private void DrawHistory()
    {
        ImGui.Spacing();

        if (this.history.Count == 0)
        {
            ImGui.TextDisabled(OmniLoc.Get(this, "HistoryEmpty"));
            return;
        }

        foreach (var run in this.history)
            ImGui.TextDisabled(string.Format(
                OmniLoc.Get(this, "HistoryRow"),
                run.At.ToString("MM-dd HH:mm"), run.Score, run.Level, run.Wrong));
    }

    private bool DrawSoundRow(string labelKey, int current, Action<int> apply)
    {
        var changed = false;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(OmniLoc.Get(this, labelKey));
        ImGui.SameLine(OmniTheme.Scale(110f));

        ImGui.SetNextItemWidth(OmniTheme.Scale(70f));
        var value = current;
        if (ImGui.InputInt($"##{labelKey}", ref value))
        {
            value = Math.Clamp(value, 1, 255);
            apply(value);
            changed = true;
        }

        ImGui.SameLine();
        if (OmniControls.SmallButton(OmniLoc.Get(this, "SoundPreview"), false))
            Sfx.Play((uint)Math.Max(1, current), this.config.SoundChat);

        return changed;
    }

    #region 窗口

    private unsafe void DrawWindow()
    {
        // 关窗 = 结束本局；开窗 = 直接开始新的一局
        if (!this.config.WindowOpen)
        {
            if (this.wasOpen)
            {
                this.wasOpen = false;
                this.AbortRun();
            }
            return;
        }

        if (!this.wasOpen)
        {
            this.wasOpen = true;
            this.StartCampaign();
        }


        var viewport = ImGui.GetMainViewport();
        var open     = this.config.WindowOpen;

        // 折叠态：约束也要一起放松，否则会被 MainWindowMinSize 撑回原尺寸（这就是折叠后仍是一整块的 bug）
        var collapsedSize = new Vector2(OmniTheme.Scale(420f), OmniTheme.TitleBarHeight() + OmniTheme.Scale(8f));
        var targetSize    = this.collapsed ? collapsedSize : OmniTheme.ClampWindowSize(this.expandedSize);

        if (this.collapsed)
        {
            ImGui.SetNextWindowSizeConstraints(collapsedSize, viewport.WorkSize);
            ImGui.SetNextWindowSize(collapsedSize, ImGuiCond.Always);
        }
        else
        {
            ImGui.SetNextWindowSizeConstraints(OmniTheme.MainWindowMinSize(), viewport.WorkSize);

            if (this.resizePending)
            {
                ImGui.SetNextWindowSize(targetSize, ImGuiCond.Always);
                this.resizePending = false;
            }
            else
            {
                ImGui.SetNextWindowSize(targetSize, ImGuiCond.FirstUseEver);
            }
        }

        ImGui.SetNextWindowPos(
            OmniTheme.ClampWindowPosition(
                viewport.WorkPos + viewport.WorkSize - targetSize,
                targetSize),
            ImGuiCond.FirstUseEver);

        if (this.focusRequested)
        {
            this.focusRequested = false;
            ImGui.SetNextWindowFocus();
        }

        if (ImGui.Begin(WindowId, ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoDocking  | ImGuiWindowFlags.NoScrollbar))
        {
            var framePos  = ImGui.GetWindowPos();
            var frameSize = ImGui.GetWindowSize();

            var dragged = OmniWindowChrome.DragTitleBar(WindowId, framePos, frameSize);
            if (dragged != framePos)
            {
                ImGui.SetWindowPos(dragged);
                framePos = dragged;
            }

            var tokens = OmniTheme.Tokens;
            var radius = OmniTheme.Scale(tokens.BorderRadius);
            var draw   = ImGui.GetWindowDrawList();

            draw.AddRectFilled(framePos, framePos + frameSize,
                ImGui.GetColorU32(Opaque(tokens.Background)), radius);

            var chrome = OmniWindowChrome.Draw(
                framePos, frameSize, this.collapsed,
                OmniLoc.Get(this, "WindowTitle"), CollapseId, CloseId, true);

            if (chrome.CloseClicked)
            {
                this.windowDrawLogged  = false;
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

            if (!this.windowDrawLogged)
            {
                this.windowDrawLogged = true;
                DalamudServices.PluginLog.Information(
                    $"[LKami9SpotDiff] window drawn: pos={framePos}, size={frameSize}, collapsed={this.collapsed}, phase={this.phase}");
            }

            if (!this.collapsed)
            {
                var (contentPos, contentSize) = OmniWindowChrome.GetContentBounds(framePos, frameSize);
                if (contentSize.X > 1f && contentSize.Y > 1f)
                {
                    ImGui.SetCursorScreenPos(contentPos);
                    ImGui.BeginChild(BoardId, contentSize, false,
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);

                    this.DrawContent();

                    ImGui.EndChild();
                }
            }
        }

        ImGui.End();

        if (!open) this.config.WindowOpen = false;
    }

    private void DrawContent()
    {
        var tokens = OmniTheme.Tokens;

        ImGui.TextUnformatted(this.BuildStatsLine());

        if (this.phase == Phase.Playing)
        {
            var urgent = this.remainSeconds <= 10;
            var timeText = string.Format(
                OmniLoc.Get(this, urgent ? "TimeUrgent" : "TimeLeft"),
                TimeSpan.FromSeconds(Math.Max(0, this.remainSeconds)));

            // 右对齐：光标此时在行首，可用宽度就是整行宽度
            var right = ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(timeText).X - OmniTheme.Scale(4f);
            if (right > 0f) ImGui.SameLine(right);

            ImGui.TextColored(
                urgent ? tokens.Accent : tokens.Text with { W = tokens.Text.W * 0.75f },
                timeText);
        }

        ImGui.Spacing();

        switch (this.phase)
        {
            case Phase.Playing when this.level != null:
                if (this.paused)
                    ImGui.TextColored(tokens.Accent, OmniLoc.Get(this, "PhasePaused"));

                this.DrawBoard(this.level);
                ImGui.Spacing();

                if (ImGui.Button(OmniLoc.Get(this, this.paused ? "ResumeButton" : "PauseButton")))
                    this.TogglePause();
                ImGui.SameLine();
                if (ImGui.Button(OmniLoc.Get(this, "HintButton"))) this.UseHint();
                break;

            case Phase.Cleared:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseCleared"), this.lastGain, this.levelNo));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            case Phase.Failed:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseFailed"), this.score, this.best));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            case Phase.Ended:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseEnded"), this.score, this.best));
                if (ImGui.Button(OmniLoc.Get(this, "AgainButton"))) this.StartCampaign();
                break;

            default:
                ImGui.TextUnformatted(string.Format(
                    OmniLoc.Get(this, "PhaseIdle"), this.config.DiffCount));
                if (ImGui.Button(OmniLoc.Get(this, "StartButton"))) this.StartCampaign();
                break;
        }

        if (this.poolFailed)
        {
            ImGui.Spacing();
            ImGui.TextColored(tokens.Accent, OmniLoc.Get(this, "PoolFailed"));
        }

    }

    #endregion

    #region 棋盘

    private void DrawBoard(Level lv)
    {
        var tokens = OmniTheme.Tokens;
        var scale  = OmniTheme.Scale(1f);

        var avail = ImGui.GetContentRegionAvail();
        var gap   = 16f * scale;

        var panelW = MathF.Min((avail.X - gap) * 0.5f, MathF.Max(120f, avail.Y - 24f * scale));
        panelW = MathF.Max(panelW, 120f);

        var boardH = panelW;
        var boardW = panelW * 2f + gap;

        ImGui.Dummy(new Vector2(boardW, boardH));
        var origin = ImGui.GetItemRectMin();
        var draw   = ImGui.GetWindowDrawList();

        var cellW = panelW / lv.Grid;

        if (this.config.ShowBoard)
        {
            for (var panel = 0; panel < 2; panel++)
            {
                var px = origin.X + panel * (panelW + gap);
                draw.AddRectFilled(
                    new Vector2(px, origin.Y),
                    new Vector2(px + panelW, origin.Y + boardH),
                    ImGui.GetColorU32(tokens.Surface with { W = 1f }),
                    6f * scale);
            }
        }

        var mouseInside = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)
                       && ImGui.IsMouseHoveringRect(origin, origin + new Vector2(boardW, boardH));

        for (var row = 0; row < lv.Grid; row++)
        {
            for (var col = 0; col < lv.Grid; col++)
            {
                var idx = row * lv.Grid + col;

                for (var panel = 0; panel < 2; panel++)
                {
                    var cell = panel == 0 ? lv.Base[idx] : lv.Right[idx];
                    var px   = origin.X + panel * (panelW + gap) + col * cellW;
                    var py   = origin.Y + row * cellW;

                    if (lv.Found[idx] && panel == 1)
                    {
                        var rMin = new Vector2(px + 2f, py + 2f);
                        var rMax = new Vector2(px + cellW - 2f, py + cellW - 2f);

                        draw.AddRectFilled(rMin, rMax, ImGui.GetColorU32(FoundColor with { W = 0.22f }), 4f * scale);
                        draw.AddRect(rMin, rMax, ImGui.GetColorU32(FoundColor), 4f * scale,
                            ImDrawFlags.None, 3.5f * scale);

                        // 左上角常驻对勾角标，不依赖飘字
                        var badge = new Vector2(px + cellW * 0.19f, py + cellW * 0.19f);
                        var br    = cellW * 0.14f;
                        draw.AddCircleFilled(badge, br, ImGui.GetColorU32(FoundColor), 20);

                        var ink = ImGui.GetColorU32(new Vector4(0.04f, 0.16f, 0.06f, 1f));
                        draw.AddLine(badge + new Vector2(-br * 0.5f, 0f),
                                     badge + new Vector2(-br * 0.12f, br * 0.45f), ink, 2.4f * scale);
                        draw.AddLine(badge + new Vector2(-br * 0.12f, br * 0.45f),
                                     badge + new Vector2(br * 0.55f, -br * 0.45f), ink, 2.4f * scale);
                    }

                    var size = cellW * 0.86f;
                    var pad  = (cellW - size) * 0.5f;

                    this.DrawIcon(cell.IconId, new Vector2(px + pad, py + pad), new Vector2(size, size), cell.Variant);
                }
            }
        }

        for (var i = this.feedbacks.Count - 1; i >= 0; i--)
        {
            var fb  = this.feedbacks[i];
            var now = DateTime.UtcNow;
            if (now > fb.Until)
            {
                this.feedbacks.RemoveAt(i);
                continue;
            }

            var row = fb.Index / lv.Grid;
            var col = fb.Index % lv.Grid;
            var px  = origin.X + (panelW + gap) + col * cellW;
            var py  = origin.Y + row * cellW;

            var left  = (float)(fb.Until - now).TotalMilliseconds;
            var total = fb.Correct ? 1000f : 800f;
            var k     = Math.Clamp(left / total, 0f, 1f);

            var color = fb.Correct ? FoundColor : WrongColor;
            var a     = color with { W = 0.35f + 0.65f * k };
            var thick = (3f + 3f * k) * scale;

            draw.AddRect(
                new Vector2(px + 1f, py + 1f),
                new Vector2(px + cellW - 1f, py + cellW - 1f),
                ImGui.GetColorU32(a), 3f * scale, ImDrawFlags.None, thick);

            if (fb.Correct)
            {
                var c  = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);
                var r  = cellW * 0.22f;
                draw.AddLine(c + new Vector2(-r, 0f), c + new Vector2(-r * 0.25f, r * 0.7f), ImGui.GetColorU32(a), thick);
                draw.AddLine(c + new Vector2(-r * 0.25f, r * 0.7f), c + new Vector2(r, -r * 0.7f), ImGui.GetColorU32(a), thick);
            }
            else
            {
                var c = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);
                var r = cellW * 0.2f;
                draw.AddLine(c + new Vector2(-r, -r), c + new Vector2(r, r), ImGui.GetColorU32(a), thick);
                draw.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), ImGui.GetColorU32(a), thick);
            }

            if (fb.Correct)
                draw.AddText(new Vector2(px + cellW * 0.15f, py - 6f * scale),
                    ImGui.GetColorU32(a), fb.Text);
        }

        if (this.level != null && this.hintIndex >= 0 && DateTime.UtcNow < this.hintUntil)
        {
            var row = this.hintIndex / lv.Grid;
            var col = this.hintIndex % lv.Grid;
            var px  = origin.X + (panelW + gap) + col * cellW;
            var py  = origin.Y + row * cellW;
            var c   = new Vector2(px + cellW * 0.5f, py + cellW * 0.5f);

            var t     = (float)ImGui.GetTime();
            var pulse = 0.55f + 0.45f * MathF.Sin(t * 7f);
            var hint  = new Vector4(1f, 0.86f, 0.25f, 1f);

            draw.AddRectFilled(
                new Vector2(px + 1f, py + 1f),
                new Vector2(px + cellW - 1f, py + cellW - 1f),
                ImGui.GetColorU32(hint with { W = 0.16f + 0.20f * pulse }), 4f * scale);

            draw.AddRect(
                new Vector2(px + 2f, py + 2f),
                new Vector2(px + cellW - 2f, py + cellW - 2f),
                ImGui.GetColorU32(hint), 4f * scale, ImDrawFlags.None, (2.5f + 2.5f * pulse) * scale);

            draw.AddCircle(c, cellW * (0.32f + 0.10f * pulse),
                ImGui.GetColorU32(hint), 40, 3f * scale);

            // 上方跳动的箭头
            var bob = MathF.Sin(t * 9f) * cellW * 0.07f;
            var tip = new Vector2(c.X, py + cellW * 0.26f + bob);
            draw.AddTriangleFilled(
                new Vector2(tip.X, tip.Y + cellW * 0.13f),
                new Vector2(tip.X - cellW * 0.10f, tip.Y),
                new Vector2(tip.X + cellW * 0.10f, tip.Y),
                ImGui.GetColorU32(hint));
        }

        if (this.phase == Phase.Playing && !this.paused && mouseInside
            && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            this.HandleClick(origin, panelW, gap, cellW, lv);

        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + boardH + 6f * scale));

    }

    private void HandleClick(Vector2 origin, float panelW, float gap, float cellW, Level lv)
    {
        var m = ImGui.GetMousePos();

        var panel = 0;
        var relX  = m.X - origin.X;
        if (relX > panelW + gap * 0.5f)
        {
            panel = 1;
            relX -= panelW + gap;
        }

        if (relX < 0f || relX >= panelW) return;

        var col = (int)(relX / cellW);
        var row = (int)((m.Y - origin.Y) / cellW);
        if (col < 0 || col >= lv.Grid || row < 0 || row >= lv.Grid) return;

        var idx = row * lv.Grid + col;

        if (lv.IsDiff[idx] && !lv.Found[idx])
        {
            lv.Found[idx] = true;
            this.feedbacks.Add(new Feedback
            {
                Index   = idx,
                Correct = true,
                Until   = DateTime.UtcNow.AddMilliseconds(1000),
                Text    = string.Format(OmniLoc.Get(this, "FeedbackGain"), ScorePerHit),
            });
            this.PlayHit();
            this.score   += ScorePerHit;
            if (this.score > this.best) this.best = this.score;   // 「最高」要跟得上当前局，否则显示很怪
            this.CheckCleared(lv);
        }
        else if (!lv.IsDiff[idx])
        {
            this.PlayMiss();
            this.wrongClicks++;
            this.score = Math.Max(0, this.score - WrongPenaltyScore);
            this.deadline = this.deadline.AddSeconds(-WrongPenaltySeconds);
            this.feedbacks.Add(new Feedback
            {
                Index   = idx,
                Correct = false,
                Until   = DateTime.UtcNow.AddMilliseconds(800),
                Text    = string.Format(OmniLoc.Get(this, "FeedbackPenalty"), WrongPenaltySeconds),
            });
        }

        _ = panel;
    }

    private void CheckCleared(Level lv)
    {
        for (var i = 0; i < lv.Found.Length; i++)
            if (lv.IsDiff[i] && !lv.Found[i]) return;

        this.PlayClear();
        var remain = Math.Max(0, (int)(this.deadline - DateTime.UtcNow).TotalSeconds);
        this.lastGain = lv.DiffTotal * ScorePerHit + remain * 4;
        this.score   += remain * 4;

        if (this.score > this.best)
        {
            this.best = this.score;
            this.SaveBest();
        }

        this.levelNo++;
        this.phase  = Phase.Cleared;
        this.level  = null;
    }

    private void PlayHit()  => this.PlaySe(this.config.SeHit);
    private void PlayMiss() => this.PlaySe(this.config.SeMiss);
    private void PlayClear() => this.PlaySe(this.config.SeClear);
    private void PlayHint() => this.PlaySe(this.config.SeHint);

    private void PlaySe(int id)
    {
        if (!this.config.Sound || id <= 0) return;
        Sfx.Play((uint)id, this.config.SoundChat);
    }

    private void TogglePause()
    {
        if (this.phase != Phase.Playing) return;

        if (this.paused)
        {
            this.deadline = DateTime.UtcNow.AddSeconds(this.pausedRemain);
            this.paused   = false;
        }
        else
        {
            this.pausedRemain = Math.Max(0, (this.deadline - DateTime.UtcNow).TotalSeconds);
            this.paused       = true;
        }
    }

    private void AbortRun()
    {
        if (this.phase == Phase.Playing || this.phase == Phase.Cleared)
        {
            if (this.score > this.best) this.best = this.score;
            this.SaveBest();
            this.RecordRun();
        }

        this.paused = false;
        this.level  = null;
        this.phase  = Phase.Ended;
        this.feedbacks.Clear();
    }

    private void UseHint()
    {
        if (this.level == null) return;

        for (var i = 0; i < this.level.IsDiff.Length; i++)
        {
            if (!this.level.IsDiff[i] || this.level.Found[i]) continue;
            this.PlayHint();
            this.hintIndex = i;
            this.hintUntil = DateTime.UtcNow.AddSeconds(3.5);
            this.deadline  = this.deadline.AddSeconds(-10);
            return;
        }
    }

    #endregion

    #region 关卡生成

    private void StartCampaign()
    {
        this.EnsureIconPool();
        if (this.iconPool.Count < 200)
        {
            this.poolFailed = true;
            this.phase      = Phase.Idle;
            return;
        }

        // 长局会积累上千张贴图句柄，超过阈值就清一次，避免一直涨
        if (this.iconCache.Count > 1500) this.iconCache.Clear();

        this.poolFailed  = false;
        this.levelNo     = 1;
        this.score       = 0;
        this.wrongClicks = 0;
        this.hintIndex   = -1;

        this.BeginLevel();
    }

    private void BeginLevel()
    {
        var grid  = Math.Clamp(this.config.GridSize + (this.levelNo - 1) / 2, 4, 10);
        var diffs = Math.Clamp(this.config.DiffCount + (this.levelNo - 1) / 2, 2, Math.Min(8, grid * grid / 6));

        var level = this.BuildLevel(grid, diffs);
        if (level == null) return;

        this.level         = level;
        this.phase         = Phase.Playing;
        this.deadline      = DateTime.UtcNow.AddSeconds(this.config.Seconds);
        this.remainSeconds = this.config.Seconds;
        this.hintIndex     = -1;
    }

    private Level? BuildLevel(int grid, int diffs)
    {
        var n = grid * grid;
        if (this.iconPool.Count < n + diffs + 8) return null;

        var level = new Level
        {
            Grid   = grid,
            Base   = new Cell[n],
            Right  = new Cell[n],
            IsDiff = new bool[n],
            Found  = new bool[n],
        };

        var used = new HashSet<int>();
        for (var i = 0; i < n; i++)
        {
            var k    = this.PickUnused(used);
            var pick = this.iconPool[k];
            level.Base[i]  = new Cell { IconId = pick.IconId, Category = pick.Category };
            level.Right[i] = new Cell { IconId = pick.IconId, Category = pick.Category };
        }

        var picked = new HashSet<int>();
        var guard  = 0;
        while (picked.Count < diffs && guard++ < diffs * 40)
        {
            var i = this.rng.Next(n);
            if (!picked.Add(i)) continue;

            var cell = level.Right[i];
            level.IsDiff[i] = true;

            // 有相当一部分图标是左右镜像对称的（圆盾、圆盘、铃铛），
            // 翻转/旋转之后完全看不出来，所以换图标占大头，翻转变形只作点缀
            var roll = this.rng.Next(10);
            var kind = roll < 6 ? 0 : roll < 8 ? 1 : 2;

            switch (kind)
            {
                case 0:
                    // 换到「不同 UI 类别」的图标，形状差异最大，也最不容易看走眼
                    var alt = this.PickDifferentCategory(used, cell.Category);
                    if (alt >= 0)
                    {
                        cell.IconId   = this.iconPool[alt].IconId;
                        cell.Category = this.iconPool[alt].Category;
                    }
                    else
                    {
                        cell.Variant = 1;
                    }
                    break;

                case 1:
                    cell.Variant = 1;   // 水平镜像
                    break;

                default:
                    cell.Variant = 2;   // 旋转 180°
                    break;
            }
        }

        if (picked.Count == 0) return null;

        level.DiffTotal = picked.Count;
        return level;
    }

    private int PickDifferentCategory(HashSet<int> used, uint avoidCategory)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var k = this.rng.Next(this.iconPool.Count);
            if (this.iconPool[k].Category == avoidCategory) continue;
            if (!used.Add(k)) continue;
            return k;
        }

        return -1;
    }

    private int PickUnused(HashSet<int> used)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var k = this.rng.Next(this.iconPool.Count);
            if (used.Add(k)) return k;
        }

        return this.rng.Next(this.iconPool.Count);
    }

    private bool poolFailed;

    private void EnsureIconPool()
    {
        if (this.iconPool.Count > 0) return;

        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var seenIcon  = new HashSet<uint>();
            var seenModel = new HashSet<ulong>();

            foreach (var item in DalamudServices.DataManager.GetExcelSheet<Item>())
            {
                if (item.RowId == 0) continue;

                var icon = item.Icon;
                if (icon == 0) continue;
                if (string.IsNullOrWhiteSpace(item.Name.ToString())) continue;
                if (!seenIcon.Add(icon)) continue;

                // 同模型换色的物品图标 ID 不同但外形一模一样，按模型再筛一遍
                var model = item.ModelMain;
                if (model != 0 && !seenModel.Add(model)) continue;

                this.iconPool.Add((icon, item.ItemUICategory.RowId));
            }

            watch.Stop();
            DalamudServices.PluginLog.Information(
                $"[LKami9SpotDiff] icon pool: {this.iconPool.Count} 个，耗时 {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            this.poolFailed = true;
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] build icon pool failed");
        }
    }

    private void DrawIcon(uint iconId, Vector2 pos, Vector2 size, int variant = 0)
    {
        if (!this.iconCache.TryGetValue(iconId, out var shared))
        {
            try
            {
                shared = DalamudServices.TextureProvider
                    .GetFromGameIcon(new GameIconLookup(iconId));
                this.iconCache[iconId] = shared;
            }
            catch
            {
                return;
            }
        }

        // 纹理是异步加载的：没准备好时 GetWrapOrDefault 返回 null，等下一帧再来。
        // 绝不能缓存空 wrap，否则那个图标会永远画不出来。
        var wrap = shared.GetWrapOrDefault();
        if (wrap == null) return;

        if (variant == 0)
        {
            ImGui.SetCursorScreenPos(pos);
            ImGui.Image(wrap.Handle, size, Vector2.Zero, Vector2.One, Vector4.One);
            return;
        }

        // 只交换 uv，不改尺寸：镜像翻左右，旋转 180° 再上下也翻一次
        var draw = ImGui.GetWindowDrawList();
        var (u0, u1, u2, u3) = variant == 1
            ? (new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f))
            : (new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(1f, 0f));

        draw.AddImageQuad(
            wrap.Handle,
            pos,
            pos + new Vector2(size.X, 0f),
            pos + size,
            pos + new Vector2(0f, size.Y),
            u0, u1, u2, u3,
            ImGui.GetColorU32(Vector4.One));
    }

    #endregion

    #region 最高分持久化

    private void RecordRun()
    {
        if (this.score <= 0) return;

        this.history.Insert(0, new RunRecord
        {
            At    = DateTime.Now,
            Score = this.score,
            Level = this.levelNo,
            Wrong = this.wrongClicks,
        });

        if (this.history.Count > 20)
            this.history.RemoveRange(20, this.history.Count - 20);

        this.SaveHistory();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(this.HistoryFilePath)) return;
            var payload = JsonSerializer.Deserialize<List<RunRecord>>(File.ReadAllText(this.HistoryFilePath));
            if (payload == null) return;

            this.history.Clear();
            var count = Math.Min(payload.Count, 20);
            for (var i = 0; i < count; i++) this.history.Add(payload[i]);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] load history failed");
        }
    }

    private void SaveHistory()
    {
        try
        {
            var dir = Path.GetDirectoryName(this.HistoryFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(this.HistoryFilePath, JsonSerializer.Serialize(this.history));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] save history failed");
        }
    }

    private void LoadBest()
    {
        try
        {
            if (!File.Exists(this.BestFilePath)) return;
            var payload = JsonSerializer.Deserialize<BestPayload>(File.ReadAllText(this.BestFilePath));
            if (payload != null) this.best = Math.Max(0, payload.Best);
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] load best failed");
        }
    }

    private void SaveBest()
    {
        try
        {
            var dir = Path.GetDirectoryName(this.BestFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(this.BestFilePath,
                JsonSerializer.Serialize(new BestPayload { Best = this.best }));
        }
        catch (Exception ex)
        {
            DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] save best failed");
        }
    }

    private sealed class BestPayload
    {
        public int Best { get; set; }
    }

    private sealed class RunRecord
    {
        public DateTime At     { get; set; }
        public int      Score  { get; set; }
        public int      Level  { get; set; }
        public int      Wrong  { get; set; }
    }

    #endregion

    /// <summary>
    /// 直接播游戏自带音效。两条正确入口（都在 ClientStructs 的 UIGlobals 上）：
    ///   PlayChatSoundEffect(id) —— 聊天提示音，合法 id 1~16
    ///   PlaySoundEffect(id)     —— 通用 UI 音效，窗口开关是 23/24
    /// 用错入口（比如拿 1~4 去调通用接口）不会报错，只会静默无声。
    /// </summary>
    private static class Sfx
    {
        public static unsafe void Play(uint id, bool chat)
        {
            try
            {
                if (chat) FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect(id);
                else      FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(id);
            }
            catch (Exception ex)
            {
                DalamudServices.PluginLog.Warning(ex, "[LKami9SpotDiff] PlaySe failed");
            }
        }
    }

    private static Vector4 Opaque(Vector4 c) => c with { W = 1f };
}
