using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoTreasure.Data;

/// <summary>
/// A selectable treasure-map grade (G1..G18).
/// Built at runtime from the player's own game files rather than hardcoded, so it
/// works identically on the Chinese client and the global client.
/// </summary>
public sealed class MapGrade
{
    public required uint RankRowId { get; init; }

    /// <summary>"G12" / "S3" parsed from the item name, or null when absent.</summary>
    public string? GradeCode { get; init; }

    public required string DisplayName { get; init; }
    public required uint ItemId { get; init; }
    public required string ItemName { get; init; }

    /// <summary>Party size this map is designed for (TreasureHuntRank.MaxPartySize).</summary>
    public byte MaxPartySize { get; init; }

    public override string ToString() => this.DisplayName;
}

/// <summary>
/// Read-only catalog over the game's Excel sheets.
///
/// All lookups are lazy and tolerant: if a sheet or row is missing (e.g. an older
/// or partially-updated client) we return an empty list instead of throwing, so the
/// UI degrades gracefully rather than breaking the plugin.
/// </summary>
public sealed class GameDataCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private List<MapGrade>? mapGrades;

    public GameDataCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    private ExcelSheet<T>? Sheet<T>() where T : struct, IExcelRow<T>
    {
        try
        {
            return this.dataManager.GetExcelSheet<T>();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "无法读取表格 {Sheet}", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// Accessor for subrow sheets (e.g. TreasureSpot). In Lumina these are typed
    /// <c>SubrowExcelSheet&lt;T&gt;</c> and cannot use the plain row generic.
    /// </summary>
    private SubrowExcelSheet<T>? SubrowSheet<T>() where T : struct, IExcelSubrow<T>
    {
        try
        {
            return this.dataManager.GetSubrowExcelSheet<T>();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "无法读取子行表格 {Sheet}", typeof(T).Name);
            return null;
        }
    }

    /// <summary>
    /// Drop cached data so it is rebuilt on next access. Called after a game patch or
    /// when the user asks to refresh, since sheets are re-read from disk.
    /// </summary>
    public void Invalidate()
    {
        this.mapGrades = null;
    }

    /// <summary>
    /// All treasure-map grades, in game-rank order.
    ///
    /// <c>TreasureHuntRank</c> is the authoritative sheet: each non-empty row links
    /// to the Item representing that map. Important details:
    ///   * Rows 15/19/23/31 are EMPTY placeholders (ItemName == 0) and must be skipped.
    ///   * Row 0 duplicates row 1 and is unused.
    ///   * There is no numeric "grade" field. The G-number lives in the localized
    ///     item name itself (e.g. the Japanese name is "古ぼけた地図G1"), so the
    ///     grade is parsed out of the name when present.
    ///   * A parallel "S" series exists (S1..S5, the "Special" maps).
    /// </summary>
    public IReadOnlyList<MapGrade> MapGrades
    {
        get
        {
            if (this.mapGrades is not null)
            {
                return this.mapGrades;
            }

            var result = new List<MapGrade>();
            var sheet = this.Sheet<TreasureHuntRank>();
            if (sheet is null)
            {
                this.mapGrades = result;
                return result;
            }

            var rows = sheet.OrderBy(r => r.RowId).ToList();

            foreach (var row in rows)
            {
                var itemRef = row.ItemName;
                if (!itemRef.IsValid)
                {
                    continue;
                }

                var itemId = itemRef.RowId;
                if (itemId == 0)
                {
                    // Empty placeholder row (15/19/23/31).
                    continue;
                }

                // Row 0 is an unused duplicate of row 1.
                if (row.RowId == 0)
                {
                    continue;
                }

                var itemName = "?";
                var item = itemRef.Value;
                if (item.RowId != 0)
                {
                    var n = item.Name.ToString();
                    if (!string.IsNullOrWhiteSpace(n))
                    {
                        itemName = n;
                    }
                }

                // Parse "G12" / "S3" out of the name when present; otherwise fall back
                // to the rank row id so the entry is still selectable and unique.
                var grade = ExtractGradeCode(itemName);
                var label = grade is not null
                    ? $"{grade} · {itemName}"
                    : $"#{row.RowId} · {itemName}";

                result.Add(new MapGrade
                {
                    RankRowId = row.RowId,
                    GradeCode = grade,
                    DisplayName = label,
                    ItemId = itemId,
                    ItemName = itemName,
                    MaxPartySize = row.MaxPartySize,
                });
            }

            this.log.Information("已从游戏数据读取 {Count} 个藏宝图等级。", result.Count);
            this.mapGrades = result;
            return result;
        }
    }

    /// <summary>
    /// Pull a "G&lt;n&gt;" or "S&lt;n&gt;" token out of an item name, e.g.
    /// "陳舊的地圖G12" -> "G12". Returns null when the name carries no grade.
    ///
    /// NOTE: a <c>\b</c> word boundary must NOT be used here. CJK characters are
    /// word characters to .NET's regex engine, so "地图G1" has no boundary between
    /// "图" and "G" and <c>\b([GS]\d{1,2})\b</c> silently matches nothing. The
    /// negative lookahead is used instead to stop "G1" matching inside "G12".
    /// </summary>
    private static string? ExtractGradeCode(string itemName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(itemName, @"([GS]\d{1,2})(?![0-9])");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Find a grade by the item id of a map the player is holding.</summary>
    public MapGrade? FindGradeByItemId(uint itemId)
        => this.MapGrades.FirstOrDefault(g => g.ItemId == itemId);

    /// <summary>A single dig location for a map grade.</summary>
    public sealed class DigSpot
    {
        public required uint TerritoryTypeId { get; init; }
        public required string TerritoryName { get; init; }
        public required Vector3 WorldPosition { get; init; }

        /// <summary>Displayed map coordinates, as shown on the in-game map.</summary>
        public required Vector3 MapPosition { get; init; }

        public override string ToString() =>
            $"{this.TerritoryName} (X {this.MapPosition.X:F1}, Y {this.MapPosition.Y:F1})";
    }

    /// <summary>
    /// Dig locations for a map grade.
    ///
    /// <c>TreasureSpot</c> is keyed by the SAME row id as <c>TreasureHuntRank</c>
    /// (verified: both span 0..30), so a grade's dig points are the subrows of the
    /// matching row. Each subrow's <c>Location</c> points at a <c>Level</c> row whose
    /// <c>Type == 40</c> marks a treasure dig spot.
    /// </summary>
    public IReadOnlyList<DigSpot> GetDigSpots(MapGrade grade)
    {
        var results = new List<DigSpot>();

        var spotSheet = this.SubrowSheet<TreasureSpot>();
        var levelSheet = this.Sheet<Level>();
        var territorySheet = this.Sheet<TerritoryType>();
        var mapSheet = this.Sheet<Map>();

        if (spotSheet is null || levelSheet is null)
        {
            return results;
        }

        try
        {
            // TreasureSpot is a subrow sheet keyed by the same row id as the hunt
            // rank, so the grade's dig points are that row's subrows.
            var row = spotSheet.GetRowOrDefault(grade.RankRowId);
            if (row is null)
            {
                return results;
            }

            foreach (var spot in row.Value)
            {
                var levelRef = spot.Location;
                if (!levelRef.IsValid)
                {
                    continue;
                }

                var level = levelRef.Value;
                if (level.RowId == 0)
                {
                    continue;
                }

                // 40 identifies a treasure dig spot.
                if (level.Type != 40)
                {
                    continue;
                }

                var world = new Vector3(level.X, level.Y, level.Z);
                var territoryId = level.Territory.RowId;

                var territoryName = $"#{territoryId}";
                if (level.Territory.IsValid)
                {
                    var placeName = level.Territory.Value.PlaceName;
                    if (placeName.IsValid)
                    {
                        var n = placeName.Value.Name.ToString();
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            territoryName = n;
                        }
                    }
                }

                // Convert to displayed map coordinates when the map row is available.
                var mapPos = world;
                if (level.Map.IsValid && mapSheet is not null)
                {
                    try
                    {
                        var transient = this.Sheet<TerritoryTypeTransient>()?.GetRowOrDefault(territoryId);
                        mapPos = Dalamud.Utility.MapUtil.WorldToMap(
                            world,
                            level.Map.Value,
                            transient ?? default,
                            true);
                    }
                    catch
                    {
                        mapPos = world;
                    }
                }

                results.Add(new DigSpot
                {
                    TerritoryTypeId = territoryId,
                    TerritoryName = territoryName,
                    WorldPosition = world,
                    MapPosition = mapPos,
                });
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "读取藏宝点失败");
        }

        return results;
    }

    /// <summary>
    /// Search the full item sheet by name. This backs the discard-item picker.
    ///
    /// Why a search rather than a "drop list": the game has NO sheet describing what
    /// a given treasure-chest grade drops. The <c>Treasure</c> sheet only holds the
    /// localized noun for a coffer ("寶箱") plus its model path, and no
    /// <c>GatheringItem</c> row references any timeworn map. Published third-party
    /// drop data is demonstrably incomplete, so presenting it as authoritative would
    /// be wrong. Instead the user curates the list, aided by search.
    /// </summary>
    /// <param name="search">Substring to match, case-insensitive. Empty returns nothing.</param>
    /// <param name="limit">Maximum results to return.</param>
    public IReadOnlyList<(uint ItemId, string Name)> SearchItems(string search, int limit = 300)
    {
        var results = new List<(uint, string)>();
        if (string.IsNullOrWhiteSpace(search) || search.Length < 2)
        {
            return results;
        }

        var sheet = this.Sheet<Item>();
        if (sheet is null)
        {
            return results;
        }

        try
        {
            foreach (var row in sheet)
            {
                if (row.RowId == 0)
                {
                    continue;
                }

                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                results.Add((row.RowId, name));
                if (results.Count >= limit)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "搜索物品失败");
        }

        return results;
    }

    /// <summary>Localized display name of an item id, or null when unknown.</summary>
    public string? GetItemName(uint itemId)
    {
        try
        {
            var sheet = this.Sheet<Item>();
            var row = sheet?.GetRowOrDefault(itemId);
            if (row is null)
            {
                return null;
            }

            var n = row.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(n) ? null : n;
        }
        catch
        {
            return null;
        }
    }
}
