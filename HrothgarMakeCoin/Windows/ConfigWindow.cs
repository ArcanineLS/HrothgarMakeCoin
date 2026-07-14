using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using ECommons.UIHelpers.AddonMasterImplementations;
using static ECommons.GenericHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HrothgarMakeCoin.Windows;

public sealed class ConfigWindow : Window
{
  private static readonly string[] _virtualKeyStrings = Enum.GetNames<VirtualKey>();

  private int _selectedTab;

  private const string RepoUrl = "https://github.com/SHOEGAZEssb/Dagobert";

  public ConfigWindow()
    : base("HrothgarMakeCoin",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(540, 500),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
  }

  public override void Draw()
  {
    var scale = ImGuiHelpers.GlobalScale;

    DrawCustomTitleBar(scale);

    var railWidth = 48f * scale;
    var barHeight = ImGui.GetTextLineHeightWithSpacing() + 8f * scale;
    var bodyHeight = ImGui.GetContentRegionAvail().Y - barHeight;

    // Left icon navigation rail (LightlessClient-style).
    ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1f, 1f, 1f, 0.03f));
    ImGui.BeginChild("##nav", new Vector2(railWidth, bodyHeight), true);
    ImGui.PopStyleColor();
    DrawNavButton(FontAwesomeIcon.Coins, 0, "General");
    DrawNavButton(FontAwesomeIcon.Robot, 1, "AutoRetainer");
    DrawNavButton(FontAwesomeIcon.Tags, 2, "Min / Max Prices");
    ImGui.EndChild();

    ImGui.SameLine();

    // Right content pane.
    ImGui.BeginChild("##content", new Vector2(0, bodyHeight));
    UiTheme.TextWrappedColored(UiTheme.Muted, "Hrothgar make coin. Coin good.");
    ImGui.Dummy(new Vector2(0, 3f * scale));
    switch (_selectedTab)
    {
      case 1:
        DrawAutoRetainerConfig();
        break;
      case 2:
        DrawItemPriceLimits();
        break;
      default:
        DrawGeneralConfig();
        break;
    }
    ImGui.EndChild();

    DrawStatusBar();
  }

  private static void DrawStatusBar()
  {
    UiTheme.AccentSeparator(UiTheme.AccentPurple, 1f);

    // Left: AutoRetainer detection + auto-pinch state.
    var arInstalled = AutoRetainerIPC.Installed;
    UiTheme.Icon(FontAwesomeIcon.Robot, arInstalled ? UiTheme.Good : UiTheme.Muted);
    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
    ImGui.TextColored(arInstalled ? UiTheme.Good : UiTheme.Muted, arInstalled ? "AutoRetainer" : "No AutoRetainer");

    ImGui.SameLine(0, 10f * ImGuiHelpers.GlobalScale);
    ImGui.TextColored(UiTheme.Muted, "|");
    ImGui.SameLine(0, 10f * ImGuiHelpers.GlobalScale);

    var autoOn = arInstalled && Plugin.Configuration.AutoPinchAfterAutoRetainer;
    UiTheme.Icon(FontAwesomeIcon.Coins, autoOn ? UiTheme.AccentPurple : UiTheme.Muted);
    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
    ImGui.TextColored(autoOn ? UiTheme.AccentBlue : UiTheme.Muted, autoOn ? "Auto-pinch ON" : "Auto-pinch off");

    // Right: version, right-aligned.
    var version = $"v{Plugin.PluginInterface.Manifest.AssemblyVersion}";
    var textWidth = ImGui.CalcTextSize(version).X;
    ImGui.SameLine(0, 0);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - textWidth);
    ImGui.TextColored(UiTheme.Muted, version);
  }

  private void DrawNavButton(FontAwesomeIcon icon, int index, string label)
  {
    var size = ImGui.GetContentRegionAvail().X;
    if (UiTheme.NavButton(icon, _selectedTab == index, size, label, index))
      _selectedTab = index;
    ImGui.Spacing();
  }

  /// <summary>
  /// Fully custom title bar (the window uses <see cref="ImGuiWindowFlags.NoTitleBar"/>). Draws a
  /// purple header band with the title, a draggable region, and reimplemented link + close buttons.
  /// </summary>
  private void DrawCustomTitleBar(float scale)
  {
    var drawList = ImGui.GetWindowDrawList();
    var style = ImGui.GetStyle();

    var origin = ImGui.GetCursorScreenPos();            // content top-left (inside window padding)
    var winPos = ImGui.GetWindowPos();
    var winSize = ImGui.GetWindowSize();
    var contentWidth = ImGui.GetContentRegionAvail().X;

    // Compact title row. The band spans flush to the window's top and side edges (up into the
    // padding) so it reads as a real title bar; interactive elements are centered in the full bar.
    var contentRowHeight = ImGui.GetTextLineHeight() + 4f * scale;
    var bandMin = winPos;
    var bandMax = new Vector2(winPos.X + winSize.X, origin.Y + contentRowHeight);
    var barTop = winPos.Y;
    var barHeight = bandMax.Y - barTop;

    drawList.PushClipRect(winPos, new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y), false);
    drawList.AddRectFilled(bandMin, bandMax, ImGui.GetColorU32(new Vector4(0.18f, 0.14f, 0.27f, 1f)),
      style.WindowRounding, ImDrawFlags.RoundCornersTop);
    drawList.AddLine(new Vector2(bandMin.X, bandMax.Y), new Vector2(bandMax.X, bandMax.Y),
      ImGui.GetColorU32(UiTheme.AccentPurple), 1.5f * scale);
    drawList.PopClipRect();

    var btnSize = barHeight - 6f * scale;
    var spacing = 4f * scale;
    var buttonsWidth = (btnSize + spacing) * 2f + spacing;

    // Drag handle across the bar (excluding the right button cluster).
    ImGui.SetCursorScreenPos(new Vector2(origin.X, barTop));
    ImGui.InvisibleButton("##titleDrag", new Vector2(MathF.Max(1f, contentWidth - buttonsWidth), barHeight));
    if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
      ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta);

    // Title: coins icon + name, centered in the full bar height.
    var iconStr = FontAwesomeIcon.Coins.ToIconString();
    Vector2 iconSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      iconSize = ImGui.CalcTextSize(iconStr);
    var titleSize = ImGui.CalcTextSize("HrothgarMakeCoin");

    var iconPos = new Vector2(origin.X + 2f * scale, barTop + (barHeight - iconSize.Y) * 0.5f);
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(iconPos, ImGui.GetColorU32(UiTheme.AccentPurple), iconStr);

    var titlePos = new Vector2(iconPos.X + iconSize.X + 8f * scale, barTop + (barHeight - titleSize.Y) * 0.5f);
    drawList.AddText(titlePos, ImGui.GetColorU32(UiTheme.AccentBlue), "HrothgarMakeCoin");

    // Right-side buttons: link, then close (right-aligned, centered vertically in the bar).
    var contentRight = origin.X + contentWidth;
    var btnY = barTop + (barHeight - btnSize) * 0.5f;
    var closePos = new Vector2(contentRight - btnSize, btnY);
    var linkPos = new Vector2(closePos.X - btnSize - spacing, btnY);

    if (TitleIconButton("##titleLink", FontAwesomeIcon.Link, linkPos, btnSize,
          new Vector4(UiTheme.AccentPurple.X, UiTheme.AccentPurple.Y, UiTheme.AccentPurple.Z, 0.45f)))
      Util.OpenLink(RepoUrl);
    UiTheme.Tooltip("Open the repository");

    if (TitleIconButton("##titleClose", FontAwesomeIcon.Times, closePos, btnSize,
          new Vector4(UiTheme.Bad.X, UiTheme.Bad.Y, UiTheme.Bad.Z, 0.6f)))
      IsOpen = false;
    UiTheme.Tooltip("Close");

    // Content starts below the band.
    ImGui.SetCursorScreenPos(new Vector2(origin.X, bandMax.Y + 6f * scale));
  }

  // Draw-list based icon button so the glyph is centered by its real size and never clipped by
  // frame padding (which is what cut off the icon when using ImGui.Button at a small size).
  private static bool TitleIconButton(string id, FontAwesomeIcon icon, Vector2 pos, float size, Vector4 hoverColor)
  {
    ImGui.SetCursorScreenPos(pos);
    ImGui.InvisibleButton(id, new Vector2(size, size));
    var hovered = ImGui.IsItemHovered();
    var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

    var drawList = ImGui.GetWindowDrawList();
    if (hovered)
      drawList.AddRectFilled(pos, new Vector2(pos.X + size, pos.Y + size), ImGui.GetColorU32(hoverColor), 4f);

    var iconStr = icon.ToIconString();
    Vector2 glyphSize;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      glyphSize = ImGui.CalcTextSize(iconStr);

    var glyphPos = new Vector2(pos.X + (size - glyphSize.X) * 0.5f, pos.Y + (size - glyphSize.Y) * 0.5f);
    var iconColor = hovered ? new Vector4(1f, 1f, 1f, 1f) : UiTheme.Muted;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      drawList.AddText(glyphPos, ImGui.GetColorU32(iconColor), iconStr);

    return clicked;
  }

  /// <summary>Config-bound checkbox that saves on change and shows a hover tooltip.</summary>
  private static void ConfigCheckbox(string label, Func<bool> get, Action<bool> set, string tooltip)
  {
    bool value = get();
    if (ImGui.Checkbox(label, ref value))
    {
      set(value);
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip(tooltip);
  }

  private static void DrawGeneralConfig()
  {
    DrawPricingSection();
    DrawTimingSection();
    DrawRetainerSection();
    DrawNotificationsSection();
    DrawHotkeysSection();
    DrawTtsSection();
  }

  private static void DrawPricingSection()
  {
    UiTheme.SectionHeader("Pricing", FontAwesomeIcon.Coins);

    ConfigCheckbox("Use HQ price",
      () => Plugin.Configuration.HQ,
      v => Plugin.Configuration.HQ = v,
      "If checked, will use the HQ price (if item is HQ; will fail if there is no HQ price on the MB).");

    // Undercut mode
    ImGui.AlignTextToFramePadding();
    ImGui.Text("Undercut Mode:");
    ImGui.SameLine();
    var enumValues = Enum.GetNames<UndercutMode>();
    int index = Array.IndexOf(enumValues, Plugin.Configuration.UndercutMode.ToString());
    ImGui.SetNextItemWidth(200f);
    if (ImGui.Combo("##undercutModeCombo", ref index, enumValues, enumValues.Length))
    {
      var value = Enum.Parse<UndercutMode>(enumValues[index]);
      if (value == UndercutMode.Percentage && Plugin.Configuration.UndercutAmount >= 100)
        Plugin.Configuration.UndercutAmount = 1;

      Plugin.Configuration.UndercutMode = value;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Defines whether to undercut by a fixed Gil amount or use a percentage.");

    // Undercut amount
    ImGui.AlignTextToFramePadding();
    ImGui.Text("Undercut amount:");
    ImGui.SameLine();
    int amount = Plugin.Configuration.UndercutAmount;
    ImGui.SetNextItemWidth(200f);
    if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
    {
      if (ImGui.InputInt("##undercutAmountFixed", ref amount))
      {
        Plugin.Configuration.UndercutAmount = Math.Clamp(amount, 1, int.MaxValue);
        Plugin.Configuration.Save();
      }
    }
    else
    {
      if (ImGui.SliderInt("##undercutAmountPercentage", ref amount, 1, 99))
      {
        Plugin.Configuration.UndercutAmount = amount;
        Plugin.Configuration.Save();
      }
    }
    ImGui.SameLine();
    ImGui.Text($"{(Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount ? "Gil" : "%")}");
    UiTheme.Tooltip("Sets the amount by which to undercut.");

    // Max undercut percentage
    ImGui.AlignTextToFramePadding();
    ImGui.Text("Max Undercut percentage:");
    ImGui.SameLine();
    float maxUndercut = Plugin.Configuration.MaxUndercutPercentage;
    ImGui.SetNextItemWidth(200f);
    if (ImGui.SliderFloat("##maximumUndercutAmountPercentage", ref maxUndercut, 0.1f, 99.9f, "%.1f"))
    {
      Plugin.Configuration.MaxUndercutPercentage = MathF.Round(maxUndercut, 1);
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.Text("%");
    UiTheme.Tooltip("Sets the max amount of percentage allowed to be undercut.");

    ConfigCheckbox("Undercut Self",
      () => Plugin.Configuration.UndercutSelf,
      v => Plugin.Configuration.UndercutSelf = v,
      "If checked, your own retainer listings will be undercut.");

    ConfigCheckbox("Use Universalis data center prices",
      () => Plugin.Configuration.UseUniversalisDataCenterPrices,
      v => Plugin.Configuration.UseUniversalisDataCenterPrices = v,
      "If checked, price checks use the cheapest listing on your current data center from Universalis.");

    // Default amount
    ImGui.AlignTextToFramePadding();
    ImGui.Text("Default amount:");
    ImGui.SameLine();
    int defaultAmount = Plugin.Configuration.DefaultAmount;
    ImGui.SetNextItemWidth(200f);
    if (ImGui.InputInt("##defaultAmount", ref defaultAmount))
    {
      Plugin.Configuration.DefaultAmount = Math.Clamp(defaultAmount, 0, int.MaxValue);
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.Text("Gil");
    UiTheme.Tooltip("Default amount in case of error (0 = disabled).");
  }

  private static void DrawTimingSection()
  {
    UiTheme.SectionHeader("Timing", FontAwesomeIcon.Clock);

    int currentMBDelay = Plugin.Configuration.GetMBPricesDelayMS;
    ImGui.Text("Market Board Price Check Delay (ms)");
    ImGui.SetNextItemWidth(-1);
    if (ImGui.SliderInt("###sliderMBDelay", ref currentMBDelay, 1, 10000))
    {
      Plugin.Configuration.GetMBPricesDelayMS = currentMBDelay;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Delay in milliseconds before opening the market board price list.\r\n" +
            "Lower delay means faster auto pinching but may also cause market board price data to be unable to load.\r\n" +
            "Recommended to keep between 3000 and 4000ms. Reduce at your own risk!");

    int currentMBKeepOpenDelay = Plugin.Configuration.MarketBoardKeepOpenMS;
    ImGui.Text("Market Board Keep Open Time (ms)");
    ImGui.SetNextItemWidth(-1);
    if (ImGui.SliderInt("###sliderMBKeepOpen", ref currentMBKeepOpenDelay, 1, 10000))
    {
      Plugin.Configuration.MarketBoardKeepOpenMS = currentMBKeepOpenDelay;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("Time in milliseconds to keep the marketboard open when fetching prices.\r\n" +
            "Lower delay means faster auto pinching but may also cause market board price data to be unable to load.\r\n" +
            "Recommended to keep between 1000 and 2000ms. Reduce at your own risk!");
  }

  private static void DrawRetainerSection()
  {
    UiTheme.SectionHeader("Retainers", FontAwesomeIcon.Users);

    ImGui.TextUnformatted("Retainer Selection");
    UiTheme.Tooltip("Select which retainers should be included in auto pinch.\r\n" +
            "Unchecked retainers will be skipped when using 'Auto Pinch' on all retainers.\r\n" +
            "Note: Open the retainer list in-game to see and configure your retainers.");

    DrawRetainerSelection();

    ImGui.Dummy(new Vector2(0, 4));
    if (ImGui.Button("Clear retainer Cache"))
      Plugin.Configuration.SeenRetainers.Clear();
    UiTheme.Tooltip("Clears the list of seen retainers from your other characters.");
  }

  private static unsafe void DrawRetainerSelection()
  {
    string[]? retainerNameArray = null;
    bool namesUpdated = false;

    if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && IsAddonReady(addon))
    {
      try
      {
        var retainerList = new AddonMaster.RetainerList(addon);
        retainerNameArray = [.. retainerList.Retainers.Select(r => r.Name)];

        var currentNames = new HashSet<string>(retainerNameArray);
        var storedNames = new HashSet<string>(Plugin.Configuration.LastKnownRetainerNames);

        if (!currentNames.SetEquals(storedNames))
        {
          Plugin.Configuration.LastKnownRetainerNames = [.. retainerNameArray];
          Plugin.Configuration.EnabledRetainerNames.RemoveWhere(name => !currentNames.Contains(name) && name != Configuration.ALL_DISABLED_SENTINEL);
          Plugin.Configuration.Save();
          namesUpdated = true;
        }
      }
      catch
      {
        // Fallback if we can't read retainer names
      }
    }

    var namesToDisplay = retainerNameArray ?? [.. Plugin.Configuration.LastKnownRetainerNames];

    if (namesToDisplay.Length == 0)
    {
      UiTheme.TextWrappedColored(UiTheme.Warn, "Open retainer list in-game to configure retainer selection");
      return;
    }

    for (int i = 0; i < namesToDisplay.Length; i++)
    {
      string retainerName = namesToDisplay[i];

      bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
      bool enabled = !allDisabled && (Plugin.Configuration.EnabledRetainerNames.Count == 0 || Plugin.Configuration.EnabledRetainerNames.Contains(retainerName));

      string label = $"{retainerName}##retainer{i}";
      if (ImGui.Checkbox(label, ref enabled))
      {
        Plugin.Configuration.EnabledRetainerNames.Remove(Configuration.ALL_DISABLED_SENTINEL);

        if (enabled)
        {
          Plugin.Configuration.EnabledRetainerNames.Add(retainerName);
          if (Plugin.Configuration.EnabledRetainerNames.Count == namesToDisplay.Length)
            Plugin.Configuration.EnabledRetainerNames.Clear();
        }
        else
        {
          if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
          {
            foreach (string name in namesToDisplay)
            {
              if (name != retainerName)
                Plugin.Configuration.EnabledRetainerNames.Add(name);
            }
          }
          else
          {
            Plugin.Configuration.EnabledRetainerNames.Remove(retainerName);
            if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
              Plugin.Configuration.EnabledRetainerNames.Add(Configuration.ALL_DISABLED_SENTINEL);
          }
        }
        Plugin.Configuration.Save();
      }

      if (i % 2 == 0 && i < namesToDisplay.Length - 1)
        ImGui.SameLine(0, 150);
    }

    if (retainerNameArray == null && !namesUpdated)
      UiTheme.TextWrappedColored(UiTheme.Muted, "(Using cached retainer list - open retainer list to refresh)");
  }

  private static void DrawNotificationsSection()
  {
    UiTheme.SectionHeader("Chat & Notifications", FontAwesomeIcon.Bell);

    ConfigCheckbox("Show errors in chat",
      () => Plugin.Configuration.ShowErrorsInChat,
      v => Plugin.Configuration.ShowErrorsInChat = v,
      "If enabled shows pinching errors in the chat.");

    ConfigCheckbox("Show Price Adjustments",
      () => Plugin.Configuration.ShowPriceAdjustmentsMessages,
      v => Plugin.Configuration.ShowPriceAdjustmentsMessages = v,
      "If enabled shows detailed price adjustments messages in the chat.");

    ImGui.SameLine(0, 40);

    ConfigCheckbox("Show Retainer Names",
      () => Plugin.Configuration.ShowRetainerNames,
      v => Plugin.Configuration.ShowRetainerNames = v,
      "If enabled, when pinching all retainers, the name of the retainer will be printed in the chat.");

    ConfigCheckbox("Show inventory context menu entry",
      () => Plugin.Configuration.ShowInventoryContextMenuEntry,
      v => Plugin.Configuration.ShowInventoryContextMenuEntry = v,
      "If enabled, adds a HrothgarMakeCoin entry to inventory item context menus for configuring per-item price limits.");
  }

  private static void DrawHotkeysSection()
  {
    UiTheme.SectionHeader("Hotkeys", FontAwesomeIcon.Keyboard);

    bool enablePostPinchKey = Plugin.Configuration.EnablePostPinchkey;
    if (ImGui.Checkbox("Enable Post Pinch Hotkey", ref enablePostPinchKey))
    {
      Plugin.Configuration.EnablePostPinchkey = enablePostPinchKey;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("If enabled allows you to hold a specified key to automatically get the lowest price when posting an item to the market board.");

    if (enablePostPinchKey)
    {
      ImGui.AlignTextToFramePadding();
      ImGui.Text("Auto Post Pinch Key:");
      ImGui.SameLine();
      int index = Array.IndexOf(_virtualKeyStrings, Plugin.Configuration.PostPinchKey.ToString());
      ImGui.SetNextItemWidth(200f);
      if (ImGui.Combo("##postPinchKeyCombo", ref index, _virtualKeyStrings, _virtualKeyStrings.Length))
      {
        Plugin.Configuration.PostPinchKey = Enum.Parse<VirtualKey>(_virtualKeyStrings[index]);
        Plugin.Configuration.Save();
      }
      UiTheme.Tooltip("The key to hold to start the auto pinching process for the newly posted item.\r\n" +
              "Be aware that the configured key still does every other hotkey action it is configured for.");
    }

    bool enablePinchKey = Plugin.Configuration.EnablePinchKey;
    if (ImGui.Checkbox("Enable Pinch Hotkey", ref enablePinchKey))
    {
      Plugin.Configuration.EnablePinchKey = enablePinchKey;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("If enabled allows you to press a specified key to start the auto pinching process.");

    if (enablePinchKey)
    {
      ImGui.AlignTextToFramePadding();
      ImGui.Text("Auto Pinch Key:");
      ImGui.SameLine();
      int index = Array.IndexOf(_virtualKeyStrings, Plugin.Configuration.PinchKey.ToString());
      ImGui.SetNextItemWidth(200f);
      if (ImGui.Combo("##pinchKeyCombo", ref index, _virtualKeyStrings, _virtualKeyStrings.Length))
      {
        Plugin.Configuration.PinchKey = Enum.Parse<VirtualKey>(_virtualKeyStrings[index]);
        Plugin.Configuration.Save();
      }
      UiTheme.Tooltip("The key to press to start the auto pinching process.\r\n" +
              "Be aware that the configured key still does every other hotkey action it is configured for.");
    }
  }

  private static void DrawTtsSection()
  {
    if (Plugin.Configuration.DontUseTTS)
      return;

    UiTheme.SectionHeader("Text-to-Speech", FontAwesomeIcon.VolumeUp);

    bool ttsall = Plugin.Configuration.TTSWhenAllDone;
    if (ImGui.Checkbox("All", ref ttsall))
    {
      Plugin.Configuration.TTSWhenAllDone = ttsall;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    string ttsallmsg = Plugin.Configuration.TTSWhenAllDoneMsg;
    ImGui.SetNextItemWidth(-1);
    if (ImGui.InputText("##ttsallmsg", ref ttsallmsg, 256, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
    {
      Plugin.Configuration.TTSWhenAllDoneMsg = ttsallmsg;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("If checked, will use Windows TTS to say the configured phrase once Auto Pinch has processed all retainers.");

    bool ttseach = Plugin.Configuration.TTSWhenEachDone;
    if (ImGui.Checkbox("Each", ref ttseach))
    {
      Plugin.Configuration.TTSWhenEachDone = ttseach;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    string ttseachmsg = Plugin.Configuration.TTSWhenEachDoneMsg;
    ImGui.SetNextItemWidth(-1);
    if (ImGui.InputText("##ttseachmsg", ref ttseachmsg, 256, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
    {
      Plugin.Configuration.TTSWhenEachDoneMsg = ttseachmsg;
      Plugin.Configuration.Save();
    }
    UiTheme.Tooltip("If checked, will use Windows TTS to say the configured phrase once Auto Pinch has processed the current retainer's listings.");

    ImGui.AlignTextToFramePadding();
    ImGui.Text("TTS Volume:");
    ImGui.SameLine();
    int volume = Plugin.Configuration.TTSVolume;
    ImGui.SetNextItemWidth(200f);
    if (ImGui.SliderInt("##ttsVolumeAmount", ref volume, 1, 99))
    {
      Plugin.Configuration.TTSVolume = volume;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.Text("%");
    UiTheme.Tooltip("Sets the volume of the Text-to-speech message.");
  }

  private static void DrawAutoRetainerConfig()
  {
    UiTheme.SectionHeader("AutoRetainer Integration", FontAwesomeIcon.Robot);

    if (!AutoRetainerIPC.Installed)
    {
      UiTheme.Icon(FontAwesomeIcon.ExclamationTriangle, UiTheme.Warn);
      ImGui.SameLine();
      UiTheme.TextWrappedColored(UiTheme.Muted,
        "AutoRetainer is not installed or not loaded. Install AutoRetainer to enable automatic pinching after ventures.");
      return;
    }

    UiTheme.Icon(FontAwesomeIcon.CheckCircle, UiTheme.Good);
    ImGui.SameLine();
    UiTheme.TextWrappedColored(UiTheme.Good, "AutoRetainer detected.");

    ImGui.Dummy(new Vector2(0, 4));
    UiTheme.TextWrappedColored(UiTheme.Muted,
      "When enabled, HrothgarMakeCoin re-prices each retainer's market board listings automatically, " +
      "right after AutoRetainer finishes that retainer's ventures. Just run your normal AutoVenture.");
    ImGui.Dummy(new Vector2(0, 6));

    ConfigCheckbox("Auto pinch after AutoRetainer ventures",
      () => Plugin.Configuration.AutoPinchAfterAutoRetainer,
      v => Plugin.Configuration.AutoPinchAfterAutoRetainer = v,
      "Master switch. When on, retainers are re-priced as part of your AutoVenture run using AutoRetainer's post-processing hook.");

    if (!Plugin.Configuration.AutoPinchAfterAutoRetainer)
      ImGui.BeginDisabled();

    ConfigCheckbox("Respect retainer selection",
      () => Plugin.Configuration.AutoRetainerRespectRetainerSelection,
      v => Plugin.Configuration.AutoRetainerRespectRetainerSelection = v,
      "Only auto-pinch retainers that are enabled in the Retainers section of the General tab.");

    if (!Plugin.Configuration.AutoPinchAfterAutoRetainer)
      ImGui.EndDisabled();

    ImGui.Dummy(new Vector2(0, 6));
    UiTheme.AccentSeparator(UiTheme.Warn);
    UiTheme.Icon(FontAwesomeIcon.InfoCircle, UiTheme.Muted);
    ImGui.SameLine();
    UiTheme.TextWrappedColored(UiTheme.Muted,
      "Pricing, undercut, timing and per-item limit settings from the other tabs are applied to the automatic pinch as well.");
  }

  private static void DrawItemPriceLimits()
  {
    UiTheme.SectionHeader("Per-Item Price Limits", FontAwesomeIcon.Tags);

    UiTheme.TextWrappedColored(UiTheme.Muted, "Minimum and maximum prices per item. 0 means no limit.");

    if (Plugin.Configuration.ItemPriceLimits.Count == 0)
    {
      ImGui.Dummy(new Vector2(0, 4));
      UiTheme.TextWrappedColored(UiTheme.Muted, "Right-click an inventory item to add it here.");
      return;
    }

    ImGui.Dummy(new Vector2(0, 4));

    ItemPriceLimit? limitToRemove = null;
    var tableFlags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchProp;
    if (ImGui.BeginTable("##itemPriceLimitsTable", 4, tableFlags))
    {
      ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn("Min", ImGuiTableColumnFlags.WidthFixed, 120f);
      ImGui.TableSetupColumn("Max", ImGuiTableColumnFlags.WidthFixed, 120f);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 70f);
      ImGui.TableHeadersRow();

      foreach (var limit in Plugin.Configuration.ItemPriceLimits
            .OrderBy(limit => ItemNameResolver.GetItemName(limit.ItemId))
            .ThenBy(limit => limit.ItemId)
            .ToList())
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(ItemNameResolver.GetItemName(limit.ItemId));
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip($"Item ID: {limit.ItemId}");

        ImGui.TableNextColumn();
        var minPrice = limit.MinPrice;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##itemPriceLimitMin{limit.ItemId}", ref minPrice))
        {
          limit.MinPrice = Math.Clamp(minPrice, 0, int.MaxValue);
          if (limit.MaxPrice > 0 && limit.MaxPrice < limit.MinPrice)
            limit.MaxPrice = limit.MinPrice;
          Plugin.Configuration.Save();
        }

        ImGui.TableNextColumn();
        var maxPrice = limit.MaxPrice;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputInt($"##itemPriceLimitMax{limit.ItemId}", ref maxPrice))
        {
          limit.MaxPrice = Math.Clamp(maxPrice, 0, int.MaxValue);
          if (limit.MaxPrice > 0 && limit.MinPrice > limit.MaxPrice)
            limit.MinPrice = limit.MaxPrice;
          Plugin.Configuration.Save();
        }

        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"Remove##itemPriceLimitRemove{limit.ItemId}"))
          limitToRemove = limit;
      }

      ImGui.EndTable();
    }

    if (limitToRemove != null)
    {
      Plugin.Configuration.ItemPriceLimits.Remove(limitToRemove);
      Plugin.Configuration.Save();
    }
  }
}
