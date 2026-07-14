using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using HrothgarMakeCoin.Windows;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace HrothgarMakeCoin;

public sealed class Plugin : IDalamudPlugin
{
  private const string CommandMain = "/hrothgarmakecoin";
  private const string CommandShort = "/hmc";
  private const string CommandLegacy = "/dagobert";

  [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
  [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
  [PluginService] public static IMarketBoard MarketBoard { get; private set; } = null!;
  [PluginService] public static IKeyState KeyState { get; private set; } = null!;
  [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
  [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
  [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
  [PluginService] public static IDataManager DataManager { get; private set; } = null!;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
  public static Configuration Configuration { get; private set; } // will never be null
  public static DalamudLinkPayload ConfigLinkPayload { get; private set; } = null!;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

  private readonly AutoPinch _autoPinch;

  public readonly WindowSystem WindowSystem = new("HrothgarMakeCoin");
  private ConfigWindow ConfigWindow { get; init; }

  public Plugin()
  {
    // ECommons must be initialized before anything that uses Svc.* (config migration logs, AutoPinch, IPC).
    ECommonsMain.Init(PluginInterface, this);

    Configuration = LoadConfiguration();
    ConfigWindow = new ConfigWindow();
    WindowSystem.AddWindow(ConfigWindow);

    var mainCommand = new CommandInfo(OnCommand)
    {
      HelpMessage = "Opens the HrothgarMakeCoin configuration window"
    };
    CommandManager.AddHandler(CommandMain, mainCommand);
    CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
    {
      HelpMessage = "Alias for /hrothgarmakecoin"
    });
    CommandManager.AddHandler(CommandLegacy, new CommandInfo(OnCommand)
    {
      HelpMessage = "Legacy alias for /hrothgarmakecoin",
      ShowInHelp = false
    });

    // Register chat link handler for clickable config link
    ConfigLinkPayload = ChatGui.AddChatLinkHandler(0, (id, _) => ToggleConfigUI());

    PluginInterface.UiBuilder.Draw += DrawUI;
    PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUI;
    PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
    ContextMenu.OnMenuOpened += OnContextMenuOpened;

    // Crisp, larger font for section headers (baked at size, not upscaled).
    UiTheme.HeaderFont = PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
      new GameFontStyle(GameFontFamily.Axis, 16f));

    _autoPinch = new AutoPinch();
    WindowSystem.AddWindow(_autoPinch);
    AutoRetainerIPC.Initialize(_autoPinch);
  }

  public void Dispose()
  {
    AutoRetainerIPC.Dispose();
    ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    PluginInterface.UiBuilder.Draw -= DrawUI;
    PluginInterface.UiBuilder.OpenMainUi -= ToggleConfigUI;
    PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUI;
    ChatGui.RemoveChatLinkHandler();
    CommandManager.RemoveHandler(CommandMain);
    CommandManager.RemoveHandler(CommandShort);
    CommandManager.RemoveHandler(CommandLegacy);
    WindowSystem.RemoveAllWindows();
    _autoPinch.Dispose();
    UiTheme.HeaderFont?.Dispose();
    UiTheme.HeaderFont = null;
    ECommonsMain.Dispose();
  }

  /// <summary>
  /// Loads the plugin configuration. On first launch after the Dagobert -> HrothgarMakeCoin
  /// rename, imports the legacy Dagobert config (stored next to our config file) so users keep
  /// their settings.
  /// </summary>
  private static Configuration LoadConfiguration()
  {
    if (PluginInterface.GetPluginConfig() is Configuration existing)
      return existing;

    try
    {
      var configDir = PluginInterface.ConfigFile.Directory?.FullName;
      if (configDir != null)
      {
        var legacyPath = Path.Combine(configDir, "Dagobert.json");
        if (File.Exists(legacyPath))
        {
          var json = File.ReadAllText(legacyPath);
          // TypeNameHandling.None (default): the legacy "$type" markers (e.g. "Dagobert.Configuration")
          // are ignored, and the JSON is deserialized straight into our identically-shaped types.
          var migrated = JsonConvert.DeserializeObject<Configuration>(json);
          if (migrated != null)
          {
            migrated.Save();
            Svc.Log.Information("Imported legacy Dagobert configuration into HrothgarMakeCoin.");
            return migrated;
          }
        }
      }
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "Failed to import legacy Dagobert configuration; starting with defaults.");
    }

    return new Configuration();
  }

  private void OnCommand(string command, string args)
  {
    // in response to the slash command, just toggle the display status of our main ui
    ToggleConfigUI();
  }

  private void OnContextMenuOpened(IMenuOpenedArgs args)
  {
    if (!Configuration.ShowInventoryContextMenuEntry)
      return;

    if (args.MenuType != ContextMenuType.Inventory)
      return;

    var itemId = (args.Target as MenuTargetInventory)?.TargetItem?.BaseItemId ?? 0u;
    if (itemId == 0)
      return;

    if (!DataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item))
      return;

    var isConfigured = Configuration.ItemPriceLimits.Any(limit => limit.ItemId == itemId);
    args.AddMenuItem(new MenuItem
    {
      Name = isConfigured ? "Configure HrothgarMakeCoin price limits" : "Add HrothgarMakeCoin price limits",
      PrefixChar = 'H',
      IsEnabled = !item.IsUntradable,
      OnClicked = GetPriceLimitMenuItemClickedHandler(itemId),
    });
  }

  private Action<IMenuItemClickedArgs> GetPriceLimitMenuItemClickedHandler(uint itemId)
  {
    return _ =>
    {
      try
      {
        var added = Configuration.GetItemPriceLimit(itemId) == null;
        Configuration.GetOrAddItemPriceLimit(itemId);
        Configuration.Save();
        ConfigWindow.IsOpen = true;

        var message = added ? ": Added to HrothgarMakeCoin price limits." : ": Already in HrothgarMakeCoin price limits.";
        ChatGui.Print(new SeStringBuilder()
          .AddItemLink(itemId, false)
          .AddText(message)
          .Build());
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, $"Failed to add item {itemId} to HrothgarMakeCoin price limits");
      }
    };
  }

  private void DrawUI()
  {
    WindowSystem.Draw();
  }

  public void ToggleConfigUI() => ConfigWindow.Toggle();
}
