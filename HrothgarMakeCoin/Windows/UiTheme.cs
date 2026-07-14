using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;

namespace HrothgarMakeCoin.Windows;

/// <summary>
/// Small set of drawing helpers that give HrothgarMakeCoin a modern, cohesive look
/// (accent-colored underlined section headers, accent separators, FontAwesome status icons,
/// help markers) inspired by the LightlessClient UI. All helpers are built on vanilla
/// Dalamud ImGui + FontAwesome, so there is no global style state to leak.
/// </summary>
internal static class UiTheme
{
  /// <summary>
  /// A larger game font (AXIS 18px) actually baked at that size, used for section headers. Set by
  /// <see cref="Plugin"/> at startup and disposed on shutdown. Scaling the normal font up instead
  /// (font.Scale) upscales the baked glyph bitmaps and looks blurry, so we use a real font here.
  /// </summary>
  public static IFontHandle? HeaderFont { get; set; }

  // Accent palette (matches the LightlessClient family: soft pastel purple/blue with semantic warn/error).
  public static readonly Vector4 AccentPurple = new(0.678f, 0.541f, 0.961f, 1f); // #ad8af5
  public static readonly Vector4 AccentBlue = new(0.651f, 0.761f, 1.000f, 1f);   // #a6c2ff
  public static readonly Vector4 Warn = new(1.000f, 0.914f, 0.478f, 1f);         // #ffe97a
  public static readonly Vector4 Bad = new(0.831f, 0.267f, 0.267f, 1f);          // #d44444
  public static readonly Vector4 Good = new(0.400f, 0.800f, 0.400f, 1f);
  public static readonly Vector4 Muted = new(0.651f, 0.651f, 0.651f, 1f);

  public static Vector4 BoolColor(bool value) => value ? AccentBlue : Bad;

  /// <summary>Renders a FontAwesome glyph inline using the icon font.</summary>
  public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
  {
    var str = icon.ToIconString();
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
    {
      if (color.HasValue)
      {
        ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.TextUnformatted(str);
        ImGui.PopStyleColor();
      }
      else
        ImGui.TextUnformatted(str);
    }
  }

  /// <summary>Inline colored check/cross icon for boolean state.</summary>
  public static void BoolIcon(bool value)
  {
    Icon(value ? FontAwesomeIcon.Check : FontAwesomeIcon.Times, BoolColor(value));
  }

  /// <summary>
  /// A square FontAwesome icon button for the left navigation rail. The selected item gets a
  /// filled purple accent; the rest are transparent with a subtle purple hover.
  /// </summary>
  public static bool NavButton(FontAwesomeIcon icon, bool selected, float size, string tooltip, int id)
  {
    if (selected)
    {
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.40f));
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.55f));
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.65f));
      ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
    }
    else
    {
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.25f));
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(AccentPurple.X, AccentPurple.Y, AccentPurple.Z, 0.35f));
      ImGui.PushStyleColor(ImGuiCol.Text, Muted);
    }

    ImGui.PushID(id);
    bool clicked;
    using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
      clicked = ImGui.Button(icon.ToIconString(), new Vector2(size, size));
    ImGui.PopID();
    ImGui.PopStyleColor(4);

    if (!string.IsNullOrEmpty(tooltip))
      Tooltip(tooltip);

    return clicked;
  }

  /// <summary>
  /// The signature section header: an accent icon + accent-colored label with a full-width
  /// accent underline. Use this instead of plain <c>ImGui.Separator()</c> + text.
  /// </summary>
  public static void SectionHeader(string label, FontAwesomeIcon? icon = null, Vector4? color = null)
  {
    var accent = color ?? AccentBlue;

    ImGui.Dummy(new Vector2(0, 3f * ImGuiHelpers.GlobalScale));

    var headerStart = ImGui.GetCursorScreenPos();
    var fullWidth = ImGui.GetContentRegionAvail().X;
    var gap = 8f * ImGuiHelpers.GlobalScale;
    var useBigFont = HeaderFont is { Available: true };

    // Measure the icon (icon font) and label (header font) so the icon+text group can be centered.
    var iconSize = Vector2.Zero;
    if (icon.HasValue)
    {
      using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        iconSize = ImGui.CalcTextSize(icon.Value.ToIconString());
    }

    var measurePush = useBigFont ? HeaderFont!.Push() : null;
    var labelSize = ImGui.CalcTextSize(label);
    measurePush?.Dispose();

    var totalWidth = labelSize.X + (icon.HasValue ? iconSize.X + gap : 0f);
    var offsetX = MathF.Max(0f, (fullWidth - totalWidth) * 0.5f);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

    if (icon.HasValue)
    {
      Icon(icon.Value, accent);
      ImGui.SameLine(0, gap);
    }

    // Crisp accent-colored heading (falls back to the normal font until the larger one builds).
    var fontPush = useBigFont ? HeaderFont!.Push() : null;
    try
    {
      ImGui.PushStyleColor(ImGuiCol.Text, accent);
      ImGui.TextUnformatted(label);
      ImGui.PopStyleColor();
    }
    finally
    {
      fontPush?.Dispose();
    }

    // Full-width underline beneath the centered header.
    var y = ImGui.GetItemRectMax().Y + 2f * ImGuiHelpers.GlobalScale;
    ImGui.GetWindowDrawList().AddLine(new Vector2(headerStart.X, y), new Vector2(headerStart.X + fullWidth, y),
      ImGui.GetColorU32(accent), 1.5f * ImGuiHelpers.GlobalScale);

    ImGui.Dummy(new Vector2(0, 7f * ImGuiHelpers.GlobalScale));
  }

  /// <summary>Full-width accent-colored separator with a little vertical breathing room.</summary>
  public static void AccentSeparator(Vector4? color = null, float thickness = 1f)
  {
    var drawList = ImGui.GetWindowDrawList();
    var min = ImGui.GetCursorScreenPos();
    var width = ImGui.GetContentRegionAvail().X;
    drawList.AddLine(min, new Vector2(min.X + width, min.Y),
      ImGui.GetColorU32(color ?? Muted), thickness * ImGuiHelpers.GlobalScale);
    ImGui.Dummy(new Vector2(0, (thickness + 3f) * ImGuiHelpers.GlobalScale));
  }

  /// <summary>Grey "(?)" hint that shows <paramref name="text"/> on hover.</summary>
  public static void HelpMarker(string text)
  {
    ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
    ImGui.PushStyleColor(ImGuiCol.Text, Muted);
    ImGui.TextUnformatted("(?)");
    ImGui.PopStyleColor();
    Tooltip(text);
  }

  /// <summary>Shows a wrapped tooltip for the previous item when hovered.</summary>
  public static void Tooltip(string text)
  {
    if (!ImGui.IsItemHovered())
      return;

    ImGui.BeginTooltip();
    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
    ImGui.TextUnformatted(text);
    ImGui.PopTextWrapPos();
    ImGui.EndTooltip();
  }

  /// <summary>Colored, wrapped body text.</summary>
  public static void TextWrappedColored(Vector4 color, string text)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.PushTextWrapPos(0f);
    ImGui.TextUnformatted(text);
    ImGui.PopTextWrapPos();
    ImGui.PopStyleColor();
  }

}
