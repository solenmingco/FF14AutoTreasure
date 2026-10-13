using System;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoTreasure.Data;

/// <summary>
/// Conversion between the coordinates players see on screen (e.g. "X 12.3, Y 34.5")
/// and the world coordinates the game and vnavmesh use.
///
/// The forward direction mirrors <c>Dalamud.Utility.MapUtil</c> exactly, which is
/// derived from the game's own routine:
///
///     mapCoord = (0.02 * offset) + (2048 / scale) + (0.02 * worldCoord) + 1.0
///
/// where X and Z use <c>Map.OffsetX</c> / <c>Map.OffsetY</c> with
/// <c>Map.SizeFactor</c>, and the world Y axis uses <c>TerritoryTypeTransient.OffsetZ</c>.
///
/// Note the axis swap: the map's "Y" (north-south) is the world's Z.
/// Dalamud only ships the world -> map direction, so the inverse used here is
/// derived from that same formula.
/// </summary>
public static class MapCoordinates
{
    private const float Scale = 0.02f;
    private const float Base = 2048f;

    /// <summary>World X or Z to the displayed map coordinate.</summary>
    public static float WorldToMapCoord(float value, uint sizeFactor, int offset)
        => (Scale * offset) + (Base / sizeFactor) + (Scale * value) + 1.0f;

    /// <summary>Displayed map coordinate back to world X or Z (inverse of the above).</summary>
    public static float MapToWorldCoord(float value, uint sizeFactor, int offset)
        => (value - 1.0f - (Base / sizeFactor) - (Scale * offset)) / Scale;

    /// <summary>
    /// Convert a displayed map position plus a world height into a world position.
    /// </summary>
    /// <param name="mapX">Displayed X (east-west).</param>
    /// <param name="mapY">Displayed Y (north-south).</param>
    /// <param name="map">The Lumina map row for the current territory.</param>
    /// <param name="height">World Y (height). When unknown, pass the player's current height.</param>
    public static Vector3 MapToWorld(float mapX, float mapY, Map map, float height)
    {
        var worldX = MapToWorldCoord(mapX, map.SizeFactor, map.OffsetX);
        var worldZ = MapToWorldCoord(mapY, map.SizeFactor, map.OffsetY);
        return new Vector3(worldX, height, worldZ);
    }

    /// <summary>World position to the displayed map coordinates, as (X, Y, Z).</summary>
    public static Vector3 WorldToMap(Vector3 world, Map map, short zOffset, bool correctZOffset = true)
    {
        if (zOffset == -10000 && correctZOffset)
        {
            zOffset = 0;
        }

        return new Vector3(
            WorldToMapCoord(world.X, map.SizeFactor, map.OffsetX),
            WorldToMapCoord(world.Z, map.SizeFactor, map.OffsetY),
            (world.Y - zOffset) / 100f);
    }
}

/// <summary>Resolves the Lumina <c>Map</c> row and z-offset for a territory.</summary>
public sealed class TerritoryMaps
{
    private readonly IDataManager dataManager;

    public TerritoryMaps(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public Map? GetMapForTerritory(uint territoryTypeRowId)
    {
        try
        {
            var maps = this.dataManager.GetExcelSheet<Map>();
            foreach (var map in maps)
            {
                var tt = map.TerritoryType;
                if (tt.IsValid && tt.RowId == territoryTypeRowId)
                {
                    return map;
                }
            }
        }
        catch
        {
            // Sheet unavailable.
        }

        return null;
    }

    public short GetZOffset(uint territoryTypeRowId)
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<TerritoryTypeTransient>();
            var row = sheet.GetRowOrDefault(territoryTypeRowId);
            return row?.OffsetZ ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
