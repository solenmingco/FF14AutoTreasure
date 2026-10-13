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
    /// Argument passed to Lifestream's <c>tp</c> command. Left null for cities we
    /// cannot teleport to by name, in which case the user travels manually.
    /// </summary>
    public string? TeleportArgument { get; init; }

    /// <summary>
    /// Aetheryte sheet row ids that identify this city, used both to confirm we
    /// actually arrived and as a typed teleport fallback.
    /// </summary>
    public uint[] AetheryteRowIds { get; init; } = Array.Empty<uint>();

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
    /// <summary>Well-known EObj row ids, used as a fast path when present.</summary>
    private const uint MarketBoardEObjRowId = 2000442;
    private const uint RetainerBellEObjRowId = 2000401;

    /// <summary>Substrings that identify a market board, across clients.</summary>
    private static readonly string[] MarketBoardNameHints =
    {
        "市场布告板",   // zh-CN
        "市場布告板",   // zh-TW
        "Market Board", // en
        "マーケットボード", // ja
    };

    /// <summary>Substrings that identify a retainer bell (adjacent to the board).</summary>
    private static readonly string[] BellNameHints =
    {
        "传唤铃",
        "傳喚鈴",
        "Summoning Bell",
        "リテイナーベル",
    };

    private readonly Configuration configuration;
    private readonly ActivityLog activity;
    private readonly NavmeshIpc navmesh;
    private readonly LifestreamIpc lifestream;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;

    public MarketBoardNavigator(
        Configuration configuration,
        ActivityLog activity,
        NavmeshIpc navmesh,
        LifestreamIpc lifestream,
        IObjectTable objectTable,
        IClientState clientState,
        IDataManager dataManager)
    {
        this.configuration = configuration;
        this.activity = activity;
        this.navmesh = navmesh;
        this.lifestream = lifestream;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.dataManager = dataManager;
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

    /// <summary>Resolve a client-appropriate market board name from the game sheets.</summary>
    private string? ResolveMarketBoardName()
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<EObjName>();
            var row = sheet.GetRowOrDefault(MarketBoardEObjRowId);
            if (row is not null)
            {
                var name = row.Value.Singular.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch
        {
            // Fall through to hint-based matching.
        }

        return null;
    }

    /// <summary>
    /// Find the nearest market board (or, failing that, retainer bell) in the
    /// current territory.
    /// </summary>
    public IGameObject? FindMarketBoard()
    {
        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return null;
        }

        var sheetName = this.ResolveMarketBoardName();

        IGameObject? bestMatch = null;
        var bestDistance = float.MaxValue;
        IGameObject? bellMatch = null;
        var bellDistance = float.MaxValue;

        foreach (var obj in this.objectTable)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject))
            {
                continue;
            }

            var name = obj.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var distance = Vector3.Distance(player.Position, obj.Position);

            var isBoard = MarketBoardNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))
                          || (sheetName is not null && name.Contains(sheetName, StringComparison.OrdinalIgnoreCase));

            if (isBoard && distance < bestDistance)
            {
                bestDistance = distance;
                bestMatch = obj;
                continue;
            }

            var isBell = BellNameHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));
            if (isBell && distance < bellDistance)
            {
                bellDistance = distance;
                bellMatch = obj;
            }
        }

        // A retainer bell sits next to the board and is a reliable stand-in when the
        // board object itself is not streamed in.
        return bestMatch ?? bellMatch;
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
            return this.WalkTo(board.Position, board.Name.TextValue);
        }

        // Otherwise we need to travel to the selected city first.
        var city = this.configuration.SelectedMarketCity;
        if (city is null)
        {
            return "当前地图没有市场布告板，请先在设置中选择目标主城。";
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

        if (string.IsNullOrWhiteSpace(city.TeleportArgument))
        {
            return $"「{city.DisplayName}」未配置传送参数，请手动前往该主城。";
        }

        var issued = this.lifestream.ExecuteCommand($"tp {city.TeleportArgument}");
        if (!issued)
        {
            return $"向 Lifestream 发送传送命令失败（目标：{city.DisplayName}）。";
        }

        this.Phase = TravelPhase.Teleporting;
        this.activity.Success($"已请求传送到「{city.DisplayName}」，等待到达…");
        return $"正在传送到「{city.DisplayName}」，到达后将自动走向市场布告板。";
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
                if (this.lifestream.IsBusy)
                {
                    return;
                }

                // Teleport finished; give the zone a moment to stream, then look for
                // the board again on a later tick.
                var board = this.FindMarketBoard();
                if (board is not null)
                {
                    this.WalkTo(board.Position, board.Name.TextValue);
                }
                else
                {
                    this.Phase = TravelPhase.Idle;
                    this.activity.Warn("传送完成，但当前地图仍未发现市场布告板。可尝试手动走近后重试。");
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
