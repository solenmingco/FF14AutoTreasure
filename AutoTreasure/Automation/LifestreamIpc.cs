using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AutoTreasure.Automation;

/// <summary>
/// Thin wrapper over the Lifestream plugin's IPC API, used for teleporting to a
/// city before walking to its market board.
///
/// Verified against Lifestream source (NightmareXIV/Lifestream, Lifestream/IPC/IPCProvider.cs).
/// Its InternalName is <c>Lifestream</c> and it initialises IPC with
/// <c>EzIPC.Init(this)</c> and no prefix, so every channel is
/// <c>Lifestream.&lt;MethodName&gt;</c>.
///
/// The most useful entry point is <see cref="ExecuteCommand"/>, which runs the same
/// code path as the in-game <c>/li</c> command. Lifestream ships a built-in
/// <c>mb</c> shortcut ("go to market board") and <c>tp &lt;city&gt;</c> for
/// teleporting, which is exactly what this plugin needs.
/// </summary>
public sealed class LifestreamIpc : IDisposable
{
    private const string Prefix = "Lifestream.";

    private readonly IPluginLog log;

    private readonly ICallGateSubscriber<string, object?> executeCommand;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object?> abort;
    private readonly ICallGateSubscriber<string, bool> aethernetTeleport;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        this.executeCommand = pluginInterface.GetIpcSubscriber<string, object?>(Prefix + "ExecuteCommand");
        this.isBusy = pluginInterface.GetIpcSubscriber<bool>(Prefix + "IsBusy");
        this.abort = pluginInterface.GetIpcSubscriber<object?>(Prefix + "Abort");
        this.aethernetTeleport = pluginInterface.GetIpcSubscriber<string, bool>(Prefix + "AethernetTeleport");
        this.teleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>(Prefix + "Teleport");
    }

    /// <summary>True when Lifestream is installed and loaded.</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                _ = this.isBusy.InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>True while Lifestream is executing a task (teleporting, moving, etc).</summary>
    public bool IsBusy
    {
        get
        {
            try
            {
                return this.isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Run a Lifestream command exactly as if typed as <c>/li &lt;arguments&gt;</c>.
    /// e.g. "mb" to go to the market board, or "tp limsa" to teleport.
    /// </summary>
    public bool ExecuteCommand(string arguments)
    {
        try
        {
            this.executeCommand.InvokeAction(arguments);
            return true;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "调用 Lifestream 命令失败：{Args}", arguments);
            return false;
        }
    }

    /// <summary>Teleport to an aetheryte by its Aetheryte sheet row id.</summary>
    public bool TeleportToAetheryte(uint aetheryteRowId, byte subIndex = 0)
    {
        try
        {
            return this.teleport.InvokeFunc(aetheryteRowId, subIndex);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "调用 Lifestream 传送失败");
            return false;
        }
    }

    /// <summary>
    /// Aethernet hop by destination name. Requires being within aetheryte range.
    /// </summary>
    public bool AethernetTeleport(string destination)
    {
        try
        {
            return this.aethernetTeleport.InvokeFunc(destination);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "调用 Lifestream 以太之光传送失败");
            return false;
        }
    }

    public void Abort()
    {
        try
        {
            this.abort.InvokeAction();
        }
        catch
        {
            // Lifestream not loaded.
        }
    }

    public void Dispose()
    {
    }
}
