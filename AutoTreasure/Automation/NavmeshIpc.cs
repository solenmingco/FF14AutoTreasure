using System;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AutoTreasure.Automation;

/// <summary>
/// Thin wrapper over the vnavmesh plugin's IPC API.
///
/// All channel names and signatures below were verified against the vnavmesh
/// source (awgil/ffxiv_navmesh, vnavmesh/IPCProvider.cs), which registers every
/// channel under the <c>vnavmesh.</c> prefix:
///
///   SimpleMove.PathfindAndMoveTo(Vector3 dest, bool fly) -> bool
///   SimpleMove.PathfindAndMoveCloseTo(Vector3 dest, bool fly, float range) -> bool
///   SimpleMove.PathfindInProgress() -> bool
///   Nav.IsReady() -> bool
///   Path.IsRunning() -> bool
///   Path.Stop() -> void
///
/// vnavmesh takes world coordinates (it feeds the value straight to
/// <c>ObjectTable.LocalPlayer.Position</c>), so no map-to-world conversion is
/// needed here.
///
/// Every call is defensive: vnavmesh may be absent or mid-reload, and an IPC call
/// to an unregistered channel throws, so we never let that reach the game loop.
/// </summary>
public sealed class NavmeshIpc : IDisposable
{
    private const string Prefix = "vnavmesh.";

    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<Vector3, bool, bool> moveTo;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> moveInProgress;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object?> pathStop;

    public NavmeshIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        this.moveTo = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>(Prefix + "SimpleMove.PathfindAndMoveTo");
        this.moveCloseTo = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>(Prefix + "SimpleMove.PathfindAndMoveCloseTo");
        this.moveInProgress = pluginInterface.GetIpcSubscriber<bool>(Prefix + "SimpleMove.PathfindInProgress");
        this.isReady = pluginInterface.GetIpcSubscriber<bool>(Prefix + "Nav.IsReady");
        this.pathIsRunning = pluginInterface.GetIpcSubscriber<bool>(Prefix + "Path.IsRunning");
        this.pathStop = pluginInterface.GetIpcSubscriber<object?>(Prefix + "Path.Stop");
    }

    /// <summary>True when vnavmesh is installed, loaded and has a navmesh ready.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                return this.isReady.InvokeFunc();
            }
            catch
            {
                // Channel not registered => vnavmesh is not loaded.
                return false;
            }
        }
    }

    /// <summary>
    /// True while vnavmesh is still walking the player along a path.
    ///
    /// IMPORTANT: this deliberately keys off <c>Path.IsRunning</c>
    /// (i.e. the follow-path waypoint list is non-empty). The separate
    /// <c>SimpleMove.PathfindInProgress</c> flag only means "a pathfind
    /// computation is in flight"; it flips back to false as soon as the route is
    /// calculated, which happens long before the player finishes walking. Treating
    /// that flag as "still moving" would report arrival immediately.
    /// </summary>
    public bool IsMoving
    {
        get
        {
            try
            {
                return this.pathIsRunning.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True while vnavmesh is still computing a route.</summary>
    public bool IsPathfinding
    {
        get
        {
            try
            {
                return this.moveInProgress.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True when either walking or computing a route.</summary>
    public bool IsBusy => this.IsMoving || this.IsPathfinding;

    /// <summary>
    /// Ask vnavmesh to walk to a world position.
    /// Returns false when vnavmesh is unavailable or already busy.
    /// </summary>
    public bool MoveTo(Vector3 destination, bool fly = false)
    {
        try
        {
            return this.moveTo.InvokeFunc(destination, fly);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "调用 vnavmesh 寻路失败");
            return false;
        }
    }

    /// <summary>
    /// Walk to within <paramref name="range"/> yalms of a position. Useful because
    /// a market board is an interactable, so standing exactly on it is unnecessary
    /// and often unreachable.
    /// </summary>
    public bool MoveCloseTo(Vector3 destination, float range, bool fly = false)
    {
        try
        {
            return this.moveCloseTo.InvokeFunc(destination, fly, range);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "调用 vnavmesh 近距离寻路失败");
            return false;
        }
    }

    /// <summary>Cancel the current path.</summary>
    public void Stop()
    {
        try
        {
            this.pathStop.InvokeAction();
        }
        catch
        {
            // Nothing to stop, or vnavmesh went away.
        }
    }

    public void Dispose()
    {
        this.Stop();
    }
}
