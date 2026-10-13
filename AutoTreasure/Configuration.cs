using System;
using System.Collections.Generic;
using AutoTreasure.Automation;
using Dalamud.Configuration;

namespace AutoTreasure;

/// <summary>
/// Persisted plugin configuration. Stored by Dalamud as JSON in the plugin config directory.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ---------------------------------------------------------------- 7 总控
    /// <summary>Master switch. When false the automation loop does nothing.</summary>
    public bool AutomationEnabled { get; set; }

    /// <summary>Emergency stop. Set by the panic hotkey; must be cleared manually in the UI.</summary>
    public bool EmergencyStop { get; set; }

    /// <summary>Virtual-key code used for the emergency stop hotkey. Default: F12 (123).</summary>
    public int PanicHotkey { get; set; } = 123;

    /// <summary>Human-like delay range (milliseconds) inserted between automation actions.</summary>
    public int ActionDelayMinMs { get; set; } = 320;
    public int ActionDelayMaxMs { get; set; } = 900;

    /// <summary>Show a DTR (server info bar) entry with current status.</summary>
    public bool ShowDtrEntry { get; set; } = true;

    /// <summary>Write verbose diagnostics to the Dalamud log.</summary>
    public bool VerboseLogging { get; set; }

    // ------------------------------------------------------------ 1 买图阶段
    /// <summary>Only buy when Gil is at or above this amount.</summary>
    public long MinimumGilReserve { get; set; } = 100_000;

    /// <summary>Target number of maps to keep in stock before buying more.</summary>
    public int DesiredMapStock { get; set; } = 10;

    /// <summary>
    /// Item ids of the treasure maps the user selected by grade from the dropdown.
    /// Populated from the game's TreasureHuntRank sheet, never hardcoded, because the
    /// Chinese client may not have every grade the global client has.
    /// </summary>
    public List<uint> MapItemIds { get; set; } = new();

    /// <summary>
    /// Grade codes ("G1".."G18", "S1".."S5") the user ticked. Kept alongside the ids
    /// so the selection can be restored once the sheet is available again.
    /// </summary>
    public List<string> SelectedMapGrades { get; set; } = new();

    /// <summary>Display name of the chosen market-board city, or null for none.</summary>
    public string? SelectedMarketCityName { get; set; }

    /// <summary>Mirror of the city selection for convenience.</summary>
    public MarketCity? SelectedMarketCity => MarketCities.ByName(this.SelectedMarketCityName);

    // ------------------------------------------------------------ 2 读图阶段
    /// <summary>Keep at most this many maps in the player's own bags.</summary>
    public int MaxMapsInPlayerBags { get; set; } = 1;

    /// <summary>Keep at most this many maps in the chocobo saddlebag.</summary>
    public int MaxMapsInSaddlebag { get; set; } = 1;

    /// <summary>Automatically use a map to reveal the treasure coordinate.</summary>
    public bool AutoDecipherMaps { get; set; }

    // -------------------------------------------------------- 6 丢弃小垃圾
    /// <summary>Master switch for the junk-disposal module.</summary>
    public bool AutoDiscardEnabled { get; set; }

    /// <summary>Trigger a cleanup pass once free inventory slots fall below this.</summary>
    public int FreeSlotThreshold { get; set; } = 4;

    /// <summary>Also run a cleanup pass once per automation cycle.</summary>
    public bool DiscardEveryCycle { get; set; }

    /// <summary>Item IDs explicitly always discarded.</summary>
    public List<uint> DiscardBlacklist { get; set; } = new();

    /// <summary>
    /// Item ids selected from the per-map-grade drop dropdown. Stored separately from
    /// the manual blacklist so the UI can show which grade a rule came from.
    /// </summary>
    public List<uint> SelectedDropItemIds { get; set; } = new();

    /// <summary>Item IDs that must never be discarded, regardless of other rules.</summary>
    public List<uint> DiscardWhitelist { get; set; } = new();

    /// <summary>Discard items whose vendor sell price is at or below this value (0 = disable rule).</summary>
    public long VendorPriceThreshold { get; set; }

    /// <summary>Treat white-quality gear as junk.</summary>
    public bool DiscardWhiteGear { get; set; }

    /// <summary>Treat green-quality gear as junk.</summary>
    public bool DiscardGreenGear { get; set; }

    /// <summary>Never discard items that are marketable on the board.</summary>
    public bool ProtectMarketable { get; set; } = true;

    // ---------------------------------------------------------------- 收益统计
    public int TotalMapsDeciphered { get; set; }
    public int TotalRunsCompleted { get; set; }
    public int TotalItemsDiscarded { get; set; }

    /// <summary>Unix seconds of the last completed automation cycle.</summary>
    public long LastRunUnixSeconds { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
