using System.Collections.Generic;
using System.Linq;

namespace AutoTreasure.Automation;

/// <summary>
/// The cities a player can go to for a market board.
///
/// Every entry below is backed by two independent sources:
///   * the FFXIV console-games wiki "Market Board Locations" table, and
///   * territory ids cross-checked against the game's own <c>Map</c> sheet
///     (<c>Map.TerritoryType</c>), not from list indexing.
///
/// Several corrections that are easy to get wrong:
///   * Gridania's boards are ALL in <b>Old Gridania (133)</b> — New Gridania has none.
///   * Ul'dah's boards are in <b>Steps of Thal (131)</b>, NOT Steps of Nald (130).
///   * Radz-at-Han (963) has <b>no</b> market board (it is a Minor City).
///   * Solution Nine (1186) is also a Minor City and has none.
///   * The Crystarium (819) does have one, though it is not a starting city.
///
/// <see cref="TeleportArgument"/> is passed to Lifestream's <c>tp</c> command.
/// </summary>
public static class MarketCities
{
    public static readonly IReadOnlyList<MarketCity> All = new List<MarketCity>
    {
        new()
        {
            DisplayName = "利姆萨·罗敏萨下层甲板（海都）",
            TeleportArgument = "Limsa Lominsa",
            TerritoryTypeId = 129,
            FallbackMapX = 8.8f,
            FallbackMapY = 11.4f,
        },
        new()
        {
            DisplayName = "格里达尼亚旧街（森都）",
            TeleportArgument = "Gridania",
            TerritoryTypeId = 133,
            FallbackMapX = 14.4f,
            FallbackMapY = 9.2f,
        },
        new()
        {
            DisplayName = "乌尔达哈现世回廊（沙都）",
            TeleportArgument = "Ul'dah",
            TerritoryTypeId = 131,
            FallbackMapX = 13.7f,
            FallbackMapY = 9.3f,
        },
        new()
        {
            DisplayName = "伊修加德基础层",
            TeleportArgument = "Ishgard",
            TerritoryTypeId = 418,
            FallbackMapX = 8.1f,
            FallbackMapY = 10.3f,
        },
        new()
        {
            DisplayName = "水晶都",
            TeleportArgument = "The Crystarium",
            TerritoryTypeId = 819,
            FallbackMapX = 10.2f,
            FallbackMapY = 13.8f,
        },
        new()
        {
            DisplayName = "旧萨雷安",
            TeleportArgument = "Old Sharlayan",
            TerritoryTypeId = 962,
            FallbackMapX = 11.5f,
            FallbackMapY = 10.5f,
        },
        new()
        {
            DisplayName = "黄金港",
            TeleportArgument = "Kugane",
            TerritoryTypeId = 628,
            FallbackMapX = 11.2f,
            FallbackMapY = 12.2f,
        },
        new()
        {
            DisplayName = "图莱尤拉",
            TeleportArgument = "Tuliyollal",
            TerritoryTypeId = 1185,
            FallbackMapX = 13.4f,
            FallbackMapY = 13.6f,
        },
    };

    public static MarketCity? ByName(string? name)
        => name is null ? null : All.FirstOrDefault(c => c.DisplayName == name);
}
