using System;
using System.Collections.Generic;
using System.IO;
using AutoTreasure.Automation;
using AutoTreasure.Data;
using AutoTreasure.Windows;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoTreasure;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/autotreasure";
    private const string CommandAlias = "/at";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("AutoTreasure");
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    /// <summary>Shared inventory scanning helpers.</summary>
    public InventoryScanner Inventory { get; }

    /// <summary>Read-only catalog of game data (map grades, items).</summary>
    public GameDataCatalog Catalog { get; }

    /// <summary>vnavmesh IPC, for walking.</summary>
    public NavmeshIpc Navmesh { get; }

    /// <summary>Lifestream IPC, for teleporting between cities.</summary>
    public LifestreamIpc Lifestream { get; }

    /// <summary>Market board travel.</summary>
    public MarketBoardNavigator? MarketBoard { get; }

    /// <summary>The automation state machine.</summary>
    public AutomationController Automation { get; }

    /// <summary>Rolling in-memory log shown in the UI.</summary>
    public ActivityLog Activity { get; } = new();

    private IDtrBarEntry? dtrEntry;

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.Inventory = new InventoryScanner(this.Configuration, this.Activity);
        this.Catalog = new GameDataCatalog(DataManager, Log);
        this.Navmesh = new NavmeshIpc(PluginInterface, Log);
        this.Lifestream = new LifestreamIpc(PluginInterface, Log);
        this.MarketBoard = new MarketBoardNavigator(
            this.Configuration,
            this.Activity,
            this.Navmesh,
            this.Lifestream,
            ObjectTable,
            ClientState,
            DataManager);

        this.Automation = new AutomationController(this, this.Inventory, this.Activity);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开 FF14 自动挖宝助手主窗口。",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "FF14 自动挖宝助手（/autotreasure 的别名）。",
        });

        if (this.Configuration.ShowDtrEntry)
        {
            this.dtrEntry = DtrBar.Get("AutoTreasure");
            this.dtrEntry.Shown = true;
        }

        this.Activity.Add("插件已加载。");
        Log.Information("FF14 自动挖宝助手已加载。");
    }

    public void Dispose()
    {
        this.Automation.Dispose();
        this.Navmesh.Dispose();
        this.Lifestream.Dispose();

        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        if (this.dtrEntry is not null)
        {
            this.dtrEntry.Remove();
            this.dtrEntry = null;
        }

        this.WindowSystem.RemoveAllWindows();
        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "on":
            case "start":
                this.Automation.Start();
                break;

            case "off":
            case "stop":
                this.Automation.Stop();
                break;

            case "panic":
                this.Automation.EmergencyStop();
                break;

            case "mb":
            case "market":
                this.Activity.Add(this.TravelToMarketBoard());
                break;

            case "clearmb":
                this.MarketBoard?.Cancel();
                break;

            case "reload":
                this.Catalog.Invalidate();
                this.Activity.Success("游戏数据缓存已刷新。");
                break;

            case "clear":
                this.Activity.Clear();
                Chat.Print("[挖宝] 日志已清空。");
                break;

            case "":
            case "ui":
                this.MainWindow.Toggle();
                break;

            default:
                Chat.Print($"[挖宝] 未知参数 “{args}”。可用：on / off / panic / mb / clearmb / reload / clear / ui");
                break;
        }
    }

    /// <summary>
    /// Start travelling to the market board. Returns a description of what was done,
    /// which the caller shows in the activity log.
    /// </summary>
    public string TravelToMarketBoard()
    {
        if (this.MarketBoard is null)
        {
            return "市场布告板导航未初始化。";
        }

        return this.MarketBoard.TravelToMarketBoard();
    }

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();

    /// <summary>Refresh the server-info-bar text. Called from the automation tick.</summary>
    internal void UpdateDtr(string text)
    {
        if (this.dtrEntry is null)
        {
            return;
        }

        this.dtrEntry.Text = text;
        this.dtrEntry.Shown = this.Configuration.ShowDtrEntry;
    }

    /// <summary>Recreate the DTR entry when the user toggles it at runtime.</summary>
    internal void SyncDtrEntry()
    {
        if (this.Configuration.ShowDtrEntry && this.dtrEntry is null)
        {
            this.dtrEntry = DtrBar.Get("AutoTreasure");
        }

        if (this.dtrEntry is not null)
        {
            this.dtrEntry.Shown = this.Configuration.ShowDtrEntry;
        }
    }
}
