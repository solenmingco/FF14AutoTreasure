using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AutoTreasure.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AutoTreasure.Automation;

/// <summary>Where a market board can be found.</summary>
public sealed class MarketCity
{
    /// <summary>Display name shown in the dropdown.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Argument passed to Lifestream's <c>tp</c> command. Null for cities we cannot
    /// teleport to by name, in which case the user travels manually.
    /// </summary>
    public string? TeleportArgument { get; init; }

    /// <summary>
    /// The territory this city is in, used to tell whether the player is already
    /// there. Verified against the game's Map sheet.
    /// </summary>
    public uint TerritoryTypeId { get; init; }

    /// <summary>
    /// Published market board map coordinates, used only as a last-resort fallback
    /// when no board object is streamed in. The runtime object scan is preferred
    /// because it reflects the real, current world position.
    /// </summary>
    public float? FallbackMapX { get; init; }
    public float? FallbackMapY { get; init; }

    public override string ToString() => this.DisplayName;
}

/// <summary>
/// Takes the player to a market board: teleport to the city (via Lifestream) and
/// then walk to the board (via vnavmesh).
///
/// Design notes, all verified against plugin sources:
///   * The market board is located at runtime by scanning <c>IObjectTable</c> for a
///     matching event object and reading its real <c>Position</c>. No hardcoded
///     world coordinates are used, because those cannot be verified here and would
///     silently break on a game update.
///   * Matching is done on the object's localized name, resolved from the game's own
///     sheets, so it works on the Chinese client without hardcoding Chinese strings
///     for every city.
///   * Only Ul'dah's market board EObj row id (2000442) and the retainer bell
///     (2000401) are publicly documented; the name-based scan covers the rest.
/// </summary>
public sealed class MarketBoardNavigator
{
    /// <summary>Well-known EObj row ids for market boards, used as a fast path.</summary>
    private static readonly uint[] MarketBoardEObjRowIds = { 2000073, 2000402, 2000440, 2000442 };

    /// <summary>Well-known EObj row ids for summoning bells (adjacent to boards).</summary>
    private static readonly uint[] BellEObjRowIds = { 2000072, 2000401, 2000403, 2000439, 2000441 };

    /// <summary>
    /// Substrings identifying a market board, matched case-insensitively.
    ///
    /// The Chinese strings are listed first because this build targets the Chinese
    /// client. The English/Japanese forms are kept so the plugin still works if run
    /// on another client. The Chinese terms could not be verified against a primary
    /// source (huijiwiki blocks scrapers), so the EObj-name lookup below is the
    /// primary mechanism and these hints are only a fallback.
    /// </summary>
    private static readonly string[] MarketBoardNameHints =
    {
        "市场布告板",   // zh-CN
        "市場布告板",   // zh-TW
        "market board", // en (the game stores these lower-case)
        "マーケットボード", // ja
    };

    /// <summary>Substrings identifying a retainer bell.</summary>
    private static readonly string[] BellNameHints =
    {
        "传唤铃",
        "傳喚鈴",
        "summoning bell",
        "リテイナーベル",
    };

    /// <summary>
    /// Consecutive idle ticks required before we believe Lifestream has finished.
    /// Guards against the case where it never queued a task and reports idle
    /// immediately.
    /// </summary>
    private const int TeleportSettleTicks = 5;

    /// <summary>Minimum wall-clock wait, in seconds, before judging a teleport done.</summary>
    private const int TeleportMinWaitSeconds = 3;

    /// <summary>Hard cap so a stuck teleport cannot spin forever.</summary>
    private const int TeleportTimeoutSeconds = 90;

    private DateTime teleportStartedAt;
    private int teleportSettledTicks;

    private readonly Configuration configuration;
    private readonly ActivityLog activity;
    private readonly NavmeshIpc navmesh;
    private readonly LifestreamIpc lifestream;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    public MarketBoardNavigator(
        Configuration configuration,
        ActivityLog activity,
        NavmeshIpc navmesh,
        LifestreamIpc lifestream,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.activity = activity;
        this.navmesh = navmesh;
        this.lifestream = lifestream;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.log = log;
    }

    public enum TravelPhase
    {
        Idle,
        Teleporting,
        Walking,
        Arrived,
    }

    public TravelPhase Phase { get; private set; } = TravelPhase.Idle;

    /// <summary>Where we told vnavmesh to walk, for status display.</summary>
    public Vector3? LastDestination { get; private set; }

    /// <summary>
    /// Resolve the localized market board / bell names from the game's EObjName sheet.
    ///
    /// IMPORTANT: <c>EObjName.Name</c> is EMPTY for these rows; the actual text lives
    /// in <c>Singular</c> / <c>Plural</c> (verified: row 2000073 -> "market board",
    /// row 2000072 -> "summoning bell"). Matching on <c>Name</c> silently finds nothing.
    /// </summary>
    private (string? Board, string? Bell) ResolveEObjNames()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<EObjName>();

            string? Singular(uint rowId)
            {
                var row = sheet.GetRowOrDefault(rowId);
                if (row is null)
                {
                    return null;
                }

                var s = row.Value.Singular.ToString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            return (Singular(MarketBoardEObjRowIds[0]), Singular(BellEObjRowIds[0]));
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Find the nearest market board — or, failing that, retainer bell — in the
    /// current territory.
    ///
    /// Matching is done in two passes so it stays locale-independent:
    ///   1. by the object's EObj DataId (verified ids for boards and bells), and
    ///   2. by name, using the localized name read from EObjName plus the
    ///      hardcoded hints.
    /// A retainer bell is accepted as a stand-in because it sits beside the board and
    /// is often the only one of the pair streamed in.
    /// </summary>
    public IGameObject? FindMarketBoard()
    {
        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        var (sheetBoardName, sheetBellName) = this.ResolveEObjNames();

        IGameObject? bestBoard = null;
        var bestBoardDistance = float.MaxValue;
        IGameObject? bestBell = null;
        var bestBellDistance = float.MaxValue;

        foreach (var obj in this.objectTable)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, obj.Position);
            var name = obj.Name.TextValue ?? string.Empty;
            // BaseId is the current name; DataId was the pre-API-15 spelling.
            var dataId = obj.BaseId;

            var isBoardById = MarketBoardEObjRowIds.Contains(dataId);
            var isBellById = BellEObjRowIds.Contains(dataId);

            var isBoardByName = name.Length > 0
                && (MarketBoardNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))
                    || (sheetBoardName is not null
                        && name.Contains(sheetBoardName, StringComparison.OrdinalIgnoreCase)));

            var isBellByName = name.Length > 0
                && (BellNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))
                    || (sheetBellName is not null
                        && name.Contains(sheetBellName, StringComparison.OrdinalIgnoreCase)));

            if ((isBoardById || isBoardByName) && distance < bestBoardDistance)
            {
                bestBoardDistance = distance;
                bestBoard = obj;
                continue;
            }

            if ((isBellById || isBellByName) && distance < bestBellDistance)
            {
                bestBellDistance = distance;
                bestBell = obj;
            }
        }

        return bestBoard ?? bestBell;
    }

    public string GetAvailabilityText()
    {
        if (this.objectTable.LocalPlayer is null)
        {
            return "角色尚未加载";
        }

        if (!this.navmesh.IsAvailable)
        {
            return "vnavmesh 不可用（未安装或未加载）";
        }

        var board = this.FindMarketBoard();
        if (board is not null)
        {
            return $"已发现：{board.Name.TextValue}";
        }

        return "当前地图未发现市场布告板";
    }

    /// <summary>
    /// Full flow: if not already in the selected city, teleport there, then walk to
    /// the market board. Returns a human-readable description of what happened.
    /// </summary>
    public string TravelToMarketBoard()
    {
        if (this.objectTable.LocalPlayer is null)
        {
            return "角色尚未加载，无法行动。";
        }

        // If a board is already reachable in this zone, just walk to it.
        var board = this.FindMarketBoard();
        if (board is not null)
        {
            var name = string.IsNullOrWhiteSpace(board.Name.TextValue)
                ? "市场布告板"
                : board.Name.TextValue;
            var distance = Vector3.Distance(this.objectTable.LocalPlayer.Position, board.Position);
            this.activity.Add($"已定位到「{name}」，距离 {distance:F1} yalms。");
            return this.WalkTo(board.Position, name);
        }

        // Nothing found in this zone: report what we looked for, so the reason is
        // visible instead of a bare "not found".
        var hint = this.ResolveEObjNames().Board;
        this.activity.Add(
            $"当前地图（TerritoryType {this.clientState.TerritoryType}）未发现市场布告板。" +
            (hint is null ? string.Empty : $"识别依据：ID {string.Join("/", MarketBoardEObjRowIds)} 或名称「{hint}」。"));

        // Otherwise we need to travel to the selected city first.
        var city = this.configuration.SelectedMarketCity;
        if (city is null)
        {
            return "当前地图没有市场布告板，请先在设置中选择目标主城。";
        }

        // Already in the right zone but no board found: walking there would not help.
        if (city.TerritoryTypeId != 0 && this.clientState.TerritoryType == city.TerritoryTypeId)
        {
            return $"已在「{city.DisplayName}」，但没有找到市场布告板对象。请走近一些后重试。";
        }

        if (!this.lifestream.IsAvailable)
        {
            this.activity.Warn(
                $"当前地图没有市场布告板，需要传送到「{city.DisplayName}」，但未检测到 Lifestream 插件。");
            return $"需要传送到「{city.DisplayName}」，但 Lifestream 不可用，请手动传送。";
        }

        if (this.lifestream.IsBusy)
        {
            return "Lifestream 正在执行其他任务，请稍候。";
        }

        var issued = this.TryTeleport(city, out var how);
        if (!issued)
        {
            return $"向 Lifestream 发送传送命令失败（目标：{city.DisplayName}）。";
        }

        this.Phase = TravelPhase.Teleporting;
        this.teleportStartedAt = DateTime.UtcNow;
        this.teleportSettledTicks = 0;
        this.activity.Success($"已请求传送到「{city.DisplayName}」（{how}），等待到达…");
        return $"正在传送到「{city.DisplayName}」，到达后将自动走向市场布告板。";
    }

    /// <summary>
    /// Teleport to the selected city, preferring a locale-independent method.
    ///
    /// Lifestream's <c>tp &lt;name&gt;</c> matches the destination with
    /// <c>Contains</c> against the aetheryte's *localized* name, so an English name
    /// such as "Old Sharlayan" silently matches nothing on a Chinese client (whose
    /// aetheryte is named 旧萨雷安). Nothing is queued, <c>IsBusy()</c> stays false,
    /// and the caller would wrongly conclude the teleport had finished.
    ///
    /// So we resolve the aetheryte id ourselves from the game's own sheets by
    /// matching <c>Aetheryte.Territory</c> against the city's territory id, which is
    /// language-independent, and call the typed <c>Teleport</c> IPC. The name-based
    /// command is kept only as a fallback.
    /// </summary>
    private bool TryTeleport(MarketCity city, out string how)
    {
        var aetheryteId = this.FindAetheryteForTerritory(city.TerritoryTypeId);
        if (aetheryteId is not null)
        {
            if (this.lifestream.TeleportToAetheryte(aetheryteId.Value, 0))
            {
                how = $"以太之光 ID {aetheryteId.Value}";
                return true;
            }
        }

        // Fallback: Lifestream matches the destination by substring against the
        // aetheryte's localized name, so hand it the name the *local client* uses
        // rather than an English literal that cannot match on a Chinese client.
        var localName = this.FindAetheryteNameForTerritory(city.TerritoryTypeId);
        var argument = localName ?? city.TeleportArgument;

        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (this.lifestream.ExecuteCommand($"tp {argument}"))
            {
                how = $"按名称传送（{argument}）";
                return true;
            }
        }

        how = "无可用方式";
        return false;
    }

    /// <summary>Localized name of a territory's main aetheryte, for the name fallback.</summary>
    private string? FindAetheryteNameForTerritory(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
        {
            return null;
        }

        try
        {
            var sheet = this.dataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || !row.IsAetheryte)
                {
                    continue;
                }

                var territory = row.Territory;
                if (!territory.IsValid || territory.RowId != territoryTypeId)
                {
                    continue;
                }

                // Prefer the aethernet name, then the place name.
                var placeName = row.AethernetName.IsValid
                    ? row.AethernetName.Value.Name.ToString()
                    : row.PlaceName.IsValid
                        ? row.PlaceName.Value.Name.ToString()
                        : null;

                return string.IsNullOrWhiteSpace(placeName) ? null : placeName;
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "读取以太之光名称失败");
        }

        return null;
    }

    /// <summary>
    /// Resolve the main aetheryte id for a territory from the game's Aetheryte sheet.
    /// Returns null when the territory has no usable aetheryte.
    /// </summary>
    private uint? FindAetheryteForTerritory(uint territoryTypeId)
    {
        if (territoryTypeId == 0)
        {
            return null;
        }

        try
        {
            var sheet = this.dataManager.GetExcelSheet<Aetheryte>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0 || !row.IsAetheryte)
                {
                    continue;
                }

                var territory = row.Territory;
                if (territory.IsValid && territory.RowId == territoryTypeId)
                {
                    return row.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "读取以太之光表失败");
        }

        return null;
    }

    /// <summary>Walk to an explicit world position.</summary>
    private string WalkTo(Vector3 destination, string what)
    {
        if (!this.navmesh.IsAvailable)
        {
            return "vnavmesh 未就绪，无法寻路。请确认已安装并加载 vnavmesh。";
        }

        if (this.navmesh.IsBusy)
        {
            this.navmesh.Stop();
        }

        // Stop a couple of yalms short: the board is an interactable with collision,
        // so demanding an exact arrival is less reliable than getting within reach.
        var started = this.navmesh.MoveCloseTo(destination, 2.0f) || this.navmesh.MoveTo(destination);
        if (!started)
        {
            return "vnavmesh 拒绝了寻路请求（可能仍在处理上一个任务）。";
        }

        this.Phase = TravelPhase.Walking;
        this.LastDestination = destination;
        this.activity.Success($"开始走向{what}（目标 {destination.X:F1}, {destination.Z:F1}）。");
        return $"正在走向{what}。";
    }

    public void Cancel()
    {
        this.navmesh.Stop();
        this.lifestream.Abort();
        this.Phase = TravelPhase.Idle;
        this.LastDestination = null;
        this.activity.Add("已取消前往市场布告板。");
    }

    /// <summary>Driven from the framework tick to advance the travel state machine.</summary>
    public void Tick()
    {
        switch (this.Phase)
        {
            case TravelPhase.Teleporting:
                // Hard cap so a stuck teleport cannot spin forever.
                if (DateTime.UtcNow - this.teleportStartedAt
                    > TimeSpan.FromSeconds(TeleportTimeoutSeconds))
                {
                    this.Phase = TravelPhase.Idle;
                    this.activity.Error(
                        $"传送超过 {TeleportTimeoutSeconds} 秒仍未完成，已放弃。请检查 Lifestream 状态。");
                    return;
                }

                // While Lifestream reports work in progress, keep waiting.
                if (this.lifestream.IsBusy)
                {
                    this.teleportSettledTicks = 0;
                    return;
                }

                // Lifestream reports idle. Two cases:
                //   * it already finished, or
                //   * it never actually queued anything (e.g. the name did not match,
                //     which is exactly what happened on the CN client with English
                //     city names), leaving IsBusy false from the very first tick.
                // Require the idle state to persist for several consecutive ticks AND
                // a minimum wall-clock delay before concluding, so a race cannot be
                // mistaken for a completed teleport.
                this.teleportSettledTicks++;
                var elapsed = DateTime.UtcNow - this.teleportStartedAt;

                if (this.teleportSettledTicks < TeleportSettleTicks
                    || elapsed < TimeSpan.FromSeconds(TeleportMinWaitSeconds))
                {
                    return;
                }

                // Give the newly loaded zone time to stream objects in before we
                // decide there is no board.
                var board = this.FindMarketBoard();
                if (board is not null)
                {
                    var boardName = string.IsNullOrWhiteSpace(board.Name.TextValue)
                        ? "市场布告板"
                        : board.Name.TextValue;
                    this.WalkTo(board.Position, boardName);
                    break;
                }

                // Still nothing: confirm we actually changed zone before complaining.
                var target = this.configuration.SelectedMarketCity;
                var arrived = target is not null
                              && target.TerritoryTypeId != 0
                              && this.clientState.TerritoryType == target.TerritoryTypeId;

                this.Phase = TravelPhase.Idle;

                if (arrived)
                {
                    this.activity.Warn(
                        $"已到达「{target!.DisplayName}」，但未识别到市场布告板对象。" +
                        "可手动走近后重试（布告板对象在较远处可能未加载）。");
                }
                else
                {
                    this.activity.Warn(
                        "传送似乎没有生效（当前地图未变化）。请确认已在游戏内解锁该主城的以太之光，" +
                        "或手动传送一次后再试。");
                }

                break;

            case TravelPhase.Walking:
                if (!this.navmesh.IsMoving)
                {
                    this.Phase = TravelPhase.Arrived;
                    this.activity.Success("已到达市场布告板附近。");
                }

                break;
        }
    }
}
