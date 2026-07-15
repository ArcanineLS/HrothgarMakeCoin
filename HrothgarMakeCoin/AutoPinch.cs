using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using static ECommons.UIHelpers.AtkReaderImplementations.ReaderContextMenu;

namespace HrothgarMakeCoin
{
  internal sealed class AutoPinch : Window, IDisposable
  {
    private readonly MarketBoardHandler _mbHandler;
    private readonly UniversalisPriceProvider _universalisPriceProvider;
    private int? _oldPrice;
    private int? _newPrice;

    /// <summary>
    /// The (item, quality) <see cref="_newPrice"/> was actually produced for, or null if it came from an
    /// uncorrelated request (the pinch flow). Auto-list refuses to post a price it can't attribute.
    /// </summary>
    private (uint ItemId, bool Hq)? _newPriceKey;

    /// <summary>
    /// The (item, quality) the in-flight auto-list price request is about. Price sources that carry their
    /// own identity check — the Universalis request id, and the per-item cache read — stamp
    /// <see cref="_newPriceKey"/> from this; the market board path uses the handler's own correlation
    /// instead, which is stronger. Null outside an auto-list batch.
    /// </summary>
    private (uint ItemId, bool Hq)? _autoListPriceKey;

    private bool _newPriceFromUniversalis;
    private bool _skipCurrentItem = false;
    private readonly TaskManager _taskManager;
    private Dictionary<string, CachedPrice> _cachedPrices = [];
    private bool _cachedPricesUseUniversalisDataCenterPrices;
    private int _universalisPriceRequestId;
    private bool _disposed;
    private CancellationTokenSource? _universalisPriceRequestCts;

    // Set while an AutoRetainer-triggered pinch is running; invoked once the pinch queue drains
    // (normally or via abort) to hand control back to AutoRetainer.
    private Action? _pendingAutoRetainerFinish;

    public AutoPinch()
      : base("HrothgarMakeCoin AutoPinch", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize, true)
    {
      _mbHandler = new MarketBoardHandler();
      _mbHandler.NewPriceReceived += MBHandler_NewPriceReceived;
      _universalisPriceProvider = new UniversalisPriceProvider();
      _cachedPricesUseUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;

      // window
      Position = new System.Numerics.Vector2(0, 0);
      IsOpen = true;
      ShowCloseButton = false;
      RespectCloseHotkey = false;
      DisableWindowSounds = true;
      SizeConstraints = new WindowSizeConstraints()
      {
        MaximumSize = new System.Numerics.Vector2(0, 0),
      };

      _taskManager = new TaskManager
      {
        TimeLimitMS = 10000,
        AbortOnTimeout = true
      };
      // Fails on non-windows
      try
      {
        var tts = new SpeechSynthesizer();
        tts.SelectVoice(tts.Voice.Name);
        Plugin.Configuration.DontUseTTS = false;
        Plugin.Configuration.Save();
      }
      catch
      {
        Plugin.Configuration.DontUseTTS = true;
        Plugin.Configuration.Save();
      }

      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
    }

    public void Dispose()
    {
      _disposed = true;
      CancelUniversalisPriceRequest();
      _universalisPriceProvider.Dispose();
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
      _mbHandler.NewPriceReceived -= MBHandler_NewPriceReceived;
      _mbHandler.Dispose();
    }

    public override void Draw()
    {
      // Runs every frame: releases AutoRetainer once an AR-triggered pinch has finished or aborted.
      ProcessPendingAutoRetainerFinish();

      try
      {
        ClearCachedPricesIfUniversalisSettingChanged();
        DrawForRetainerList();
        DrawForRetainerSellList();
      }
      catch (Exception ex)
      {
        _taskManager.Abort();
        Svc.Log.Error(ex, "Error while auto pinching");
        if (Plugin.Configuration.ShowErrorsInChat)
          Svc.Chat.PrintError($"Error while auto pinching: {ex.Message}");

        RemoveTalkAddonListeners();
      }
    }

    // Screen-space offsets (before UI scale) for the RetainerList "Auto Pinch" button. It sits in
    // the bottom-left footer area, clear of the header's gil total and the window's close button.
    // Tweak these two values if your UI layout needs it nudged.
    private const float RetainerListButtonOffsetX = 15f;
    private const float RetainerListButtonOffsetBottom = 6f;

    private void DrawForRetainerList()
    {
      unsafe
      {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
          if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
            PinchAllRetainers();

          var root = addon->RootNode;
          if (root == null)
            return;

          // Anchor to the addon's bottom-left footer instead of a header node, so the button no
          // longer overlaps the retainer gil total and the window controls.
          var rootPos = GetNodePosition(root);
          var rootScale = GetNodeScale(root);
          var rootSize = new Vector2(root->Width, root->Height) * rootScale;
          var position = rootPos + new Vector2(RetainerListButtonOffsetX * rootScale.X, rootSize.Y - RetainerListButtonOffsetBottom * rootScale.Y);

          var oldSize = ImGuiSetup(position, rootScale, new Vector2(1f, 1f), "###AutoPinchRetainerList");
          DrawAutoPinchButton(PinchAllRetainers);
          ImGuiPostSetup(oldSize);
        }
      }
    }

    private void DrawForRetainerSellList()
    {
      unsafe
      {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
          if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
            PinchAllRetainerItems();

          var node = addon->UldManager.NodeList[17];

          if (node == null)
            return;

          var oldSize = ImGuiSetup(node);
          DrawAutoPinchButton(PinchAllRetainerItems);
          DrawAutoListButton();
          ImGuiPostSetup(oldSize);
        }
      }
    }

    /// <summary>
    /// "Auto List" button beside "Auto Pinch" on the retainer sell list. Only shown when auto-list is
    /// enabled; amber when it will post for real, green-ish while in dry run.
    /// </summary>
    private void DrawAutoListButton()
    {
      if (!Plugin.Configuration.AutoListEnabled || _taskManager.IsBusy)
        return;

      ImGui.SameLine();

      var dry = Plugin.Configuration.AutoListDryRun;
      var baseColor = dry
        ? new System.Numerics.Vector4(0.28f, 0.46f, 0.34f, 0.92f)
        : new System.Numerics.Vector4(0.64f, 0.42f, 0.16f, 0.92f);
      var hoverColor = dry
        ? new System.Numerics.Vector4(0.36f, 0.58f, 0.44f, 1f)
        : new System.Numerics.Vector4(0.78f, 0.54f, 0.22f, 1f);

      ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, hoverColor);
      ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 1f, 1f));

      if (ImGui.Button(dry ? "Auto List (dry)" : "Auto List"))
        StartAutoList();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip(dry
          ? "Dry run: reports what it WOULD post to chat. Nothing is listed."
          : "Posts whitelisted items into this retainer's free market slots.\r\nPosts are immediate and IRREVERSIBLE.");

      ImGui.PopStyleColor(4);
    }

    private unsafe float ImGuiSetup(AtkResNode* node)
    {
      var position = GetNodePosition(node);
      var scale = GetNodeScale(node);
      var size = new Vector2(node->Width, node->Height) * scale;

      // Content grows rightwards from this anchor, so a second button runs off the addon's right edge.
      // Shift left by exactly what Auto List will occupy: the group's right edge then lands where the
      // lone Auto Pinch button's right edge used to sit, keeping the tuned position.
      position.X -= GetAutoListReservedWidth(scale);

      return ImGuiSetup(position, scale, size, $"###AutoPinch{node->NodeId}");
    }

    /// <summary>
    /// Horizontal space the "Auto List" button needs, including its <c>SameLine</c> gap — measured the
    /// way <see cref="ImGuiSetup(Vector2, Vector2, Vector2, string)"/> will actually draw it: text at the
    /// node's scale, plus the FramePadding pushed there. 0 when the button is hidden.
    /// Deliberately ignores the busy state, which only hides Auto List, so the group doesn't jump
    /// sideways for the duration of every run.
    /// </summary>
    private static float GetAutoListReservedWidth(Vector2 scale)
    {
      if (!Plugin.Configuration.AutoListEnabled)
        return 0f;

      var label = Plugin.Configuration.AutoListDryRun ? "Auto List (dry)" : "Auto List";
      return (ImGui.CalcTextSize(label).X * scale.X) + (8f.Scale() * 2f) + ImGui.GetStyle().ItemSpacing.X;
    }

    private float ImGuiSetup(Vector2 position, Vector2 scale, Vector2 minSize, string windowId)
    {
      ImGuiHelpers.ForceNextWindowMainViewport();
      ImGuiHelpers.SetNextWindowPosRelativeMainViewport(position);

      ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
      var oldSize = ImGui.GetFont().Scale;
      ImGui.GetFont().Scale *= scale.X;
      ImGui.PushFont(ImGui.GetFont());
      ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f.Scale());
      ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8f.Scale(), 4f.Scale()));
      ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f.Scale(), 0f.Scale()));
      ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f.Scale());
      ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, minSize);
      ImGui.Begin(windowId, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
          | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

      return oldSize;
    }

    private static void ImGuiPostSetup(float oldSize)
    {
      ImGui.End();
      ImGui.PopStyleVar(5);
      ImGui.GetFont().Scale = oldSize;
      ImGui.PopFont();
      ImGui.PopStyleColor();
    }

    private void DrawAutoPinchButton(Action specificPinchFunction)
    {
      var busy = _taskManager.IsBusy;

      // Accent-themed button (purple for Auto Pinch, red for Cancel) so it reads cleanly over the
      // game UI rather than as a plain grey box.
      var baseColor = busy
        ? new System.Numerics.Vector4(0.62f, 0.22f, 0.22f, 0.92f)
        : new System.Numerics.Vector4(0.42f, 0.34f, 0.62f, 0.92f);
      var hoverColor = busy
        ? new System.Numerics.Vector4(0.74f, 0.28f, 0.28f, 1f)
        : new System.Numerics.Vector4(0.52f, 0.42f, 0.74f, 1f);

      ImGui.PushStyleColor(ImGuiCol.Button, baseColor);
      ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoverColor);
      ImGui.PushStyleColor(ImGuiCol.ButtonActive, hoverColor);
      ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 1f, 1f, 1f));

      if (busy)
      {
        if (ImGui.Button("Cancel"))
        {
          _taskManager.Abort();
          AutoRetainerIPC.Suppressed(false);
          RemoveTalkAddonListeners();
        }
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip("Cancels the auto pinching process");
      }
      else
      {
        if (ImGui.Button("Auto Pinch"))
          specificPinchFunction();
        if (ImGui.IsItemHovered())
          ImGui.SetTooltip("Starts auto pinching\r\nPlease do not interact with the game while this process is running");
      }

      ImGui.PopStyleColor(4);
    }

    private unsafe void PinchAllRetainers()
    {
      if (_taskManager.IsBusy)
        return;
  
      ClearState();
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        AutoRetainerIPC.Suppressed(true);

        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

        // we cache the number of retainers because AddonMaster will be disposed once the RetainerList addon is closed.
        var retainerList = new AddonMaster.RetainerList(addon);
        var retainers = retainerList.Retainers;
        var num = retainers.Length;
        
        // Check if all are disabled (sentinel present)
        bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
        
        // If all are disabled, skip all retainers and notify user
        if (allDisabled)
        {
          Communicator.PrintAllRetainersDisabled();
        }
        else
        {
          // If no retainers are explicitly enabled, enable all by default
          bool allEnabled = Plugin.Configuration.EnabledRetainerNames.Count == 0;
          
          for (int i = 0; i < num; i++)
          {
            var retainerName = retainers[i].Name;
            
            // Skip retainers that are excluded in configuration
            if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
            {
              Svc.Log.Debug($"Skipping retainer '{retainerName}' (excluded by user configuration)");
              continue;
            }
            EnqueueSingleRetainer(i);
          }
          
          _taskManager.Enqueue(RemoveTalkAddonListeners);
          if (Plugin.Configuration.TTSWhenAllDone)
            _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenAllDoneMsg), "SpeakTTSAll");
        }

        _taskManager.Enqueue(() => AutoRetainerIPC.Suppressed(false));
      }
    }

    private void EnqueueSingleRetainer(int index)
    {
      _taskManager.Enqueue(() => ClickRetainer(index), $"ClickRetainer{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(ClickSellItems, $"ClickSellItems{index}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), $"EnqueueAllRetainerItems{index}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(CloseRetainerSellList, $"CloseRetainerSellList{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(CloseRetainer, $"CloseRetainer{index}");
      _taskManager.DelayNext(100);
    }

    private static unsafe bool? ClickRetainer(int index)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        Communicator.PrintRetainerName(new AddonMaster.RetainerList(addon).Retainers[index].Name);
        ECommons.Automation.Callback.Fire(addon, true, 2, index);
        return true;
      }
      else
        return false;
    }

    private static unsafe bool? ClickSellItems()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        new AddonMaster.SelectString(addon).Entries[2].Select();
        return true;
      }
      else
        return false;
    }

    private static unsafe bool? CloseRetainerSellList()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        addon->Close(true);
        return true;
      }
      else
        return false;
    }

    private static unsafe bool? CloseRetainer()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        addon->Close(true);
        return true;
      }
      else
        return false;
    }

    private unsafe void PinchAllRetainerItems()
    {
      _mbHandler.PopulateRetainerCache();
      if (_taskManager.IsBusy)
        return;

      ClearState();
      EnqueueAllRetainerItems(EnqueueSingleItem, false);
    }

    /// <summary>
    /// Entry point for the AutoRetainer integration. Invoked from AutoRetainer's
    /// "ready to post-process" hook while the retainer's action menu (SelectString) is open,
    /// right after that retainer's ventures finished. Re-prices the retainer's listings and
    /// then invokes <paramref name="onComplete"/> to hand control back to AutoRetainer.
    /// </summary>
    /// <param name="retainerName">The retainer AutoRetainer just processed (for logging).</param>
    /// <param name="onComplete">Callback that releases AutoRetainer (must always run exactly once).</param>
    public void StartAutoRetainerPinch(string retainerName, Action onComplete)
    {
      if (_taskManager.IsBusy)
      {
        Svc.Log.Warning($"AutoRetainer post-venture pinch for '{retainerName}' requested while busy; skipping.");
        onComplete();
        return;
      }

      Svc.Log.Debug($"AutoRetainer post-venture pinch starting for '{retainerName}'");
      _mbHandler.PopulateRetainerCache();
      ClearState();
      _pendingAutoRetainerFinish = onComplete;

      // The retainer's action menu is already open (AutoRetainer selected it), so we start at
      // "Sell items" rather than clicking the retainer from the RetainerList first.
      _taskManager.Enqueue(ClickSellItems, "AR_ClickSellItems");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), "AR_EnqueueAllRetainerItems");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(CloseRetainerSellList, "AR_CloseRetainerSellList");
      _taskManager.DelayNext(100);
      // Normal completion path: runs on the framework tick once the queue reaches it.
      _taskManager.Enqueue(() => { ReleaseAutoRetainer(); return true; }, "AR_FinishPostProcess");
    }

    /// <summary>
    /// Per-frame safety net: if an AR-triggered pinch was aborted or cancelled before its final
    /// task ran, release AutoRetainer once the task queue is idle so it never hangs.
    /// </summary>
    private void ProcessPendingAutoRetainerFinish()
    {
      if (_pendingAutoRetainerFinish != null && !_taskManager.IsBusy)
        ReleaseAutoRetainer();
    }

    /// <summary>
    /// Hands control back to AutoRetainer exactly once per AR-triggered pinch, whether the chain
    /// completed normally or was aborted. Idempotent.
    /// </summary>
    private void ReleaseAutoRetainer()
    {
      var finish = _pendingAutoRetainerFinish;
      if (finish == null)
        return;

      _pendingAutoRetainerFinish = null;
      try
      {
        Svc.Log.Debug("AutoRetainer post-venture pinch complete; releasing AutoRetainer");
        finish();
      }
      catch (Exception ex)
      {
        Svc.Log.Error(ex, "Error releasing AutoRetainer after post-venture pinch");
      }
    }

    // ---------- Auto-list: post whitelisted items into free market slots ----------

    private int _autoListQuantity;

    /// <summary>
    /// Market basis resolved for each (item, HQ) during the CURRENT auto-list run, so every Spread batch
    /// of an item posts at the same price. Re-querying per batch would read a board that already contains
    /// the batch we just posted; with UndercutSelf on, each batch would then undercut the previous one and
    /// walk the price down to MinPrice. Keyed by (itemId, hq) rather than by name, unlike _cachedPrices.
    /// </summary>
    private readonly Dictionary<(uint ItemId, bool Hq), int> _autoListRunPrices = [];

    /// <summary>Guards against spamming the "market is full" notice once per remaining batch.</summary>
    private bool _autoListFullWarned;

    /// <summary>
    /// Posts whitelisted items into the active retainer's free market slots. Honours the dry-run
    /// switch (reports what it WOULD post, without confirming). Guardrails per the auto-list design:
    /// whitelist-only, mandatory price floor, no-guess pricing, mandatory dedupe, slot re-resolve,
    /// read-back before confirm, and the 20-slot cap.
    /// </summary>
    public void StartAutoList()
    {
      if (_taskManager.IsBusy)
      {
        Svc.Chat.PrintError("[HrothgarMakeCoin] Busy - wait for the current operation to finish.");
        return;
      }

      if (!Plugin.Configuration.AutoListEnabled)
      {
        Svc.Chat.PrintError("[HrothgarMakeCoin] Auto-list is off. Enable it in /hmc -> Min/Max Prices -> Auto-List Whitelist.");
        return;
      }

      var free = AutoListDriver.GetFreeMarketSlots();
      if (free < 0)
      {
        Svc.Chat.PrintError("[HrothgarMakeCoin] No active retainer - open a retainer's market session first.");
        return;
      }
      if (free == 0)
      {
        Svc.Chat.Print("[HrothgarMakeCoin] Retainer market is full (20/20); nothing to post.");
        return;
      }

      ClearState();
      _mbHandler.PopulateRetainerCache();

      var planned = 0;
      foreach (var entry in Plugin.Configuration.AutoListItems)
      {
        if (planned >= free)
          break;

        if (!entry.Enabled)
          continue;

        var name = AutoListLabel(entry);

        if (!entry.IsValid)
        {
          Svc.Log.Information($"[AutoList] skip {name}: needs a Min price (and Max >= Min)");
          continue;
        }

        // Dedupe is mandatory for normal entries, but Spread deliberately makes several listings of
        // the same item, so it opts out (it self-terminates when the stack runs dry instead).
        var spreading = entry.Spread && entry.Quantity > 0;
        if (!spreading && AutoListDriver.IsAlreadyListed(entry.ItemId, entry.ListHq))
        {
          Svc.Log.Information($"[AutoList] skip {name}: already listed");
          continue;
        }

        if (!AutoListDriver.TryFindStack(entry.ItemId, entry.ListHq, out _, out _, out var stack))
        {
          Svc.Log.Information($"[AutoList] skip {name}: no matching stack in inventory");
          continue;
        }

        // Spread: one listing per batch of Quantity until the stack is gone or slots run out.
        var batches = spreading ? (int)Math.Ceiling(stack / (double)entry.Quantity) : 1;
        batches = Math.Min(batches, free - planned);
        if (spreading)
          Svc.Log.Information($"[AutoList] {name}: spreading stack of {stack} into {batches} listing(s) of {entry.Quantity}");

        for (var b = 0; b < batches; b++)
        {
          EnqueueAutoListItem(entry, b);
          planned++;
        }
      }

      if (planned == 0)
      {
        Svc.Chat.Print("[HrothgarMakeCoin] Auto-list: nothing eligible to post.");
        return;
      }

      var dry = Plugin.Configuration.AutoListDryRun ? "DRY RUN - " : string.Empty;
      Svc.Chat.Print($"[HrothgarMakeCoin] Auto-list: {dry}processing {planned} listing(s) into {free} free slot(s). Please don't interact with the game.");
    }

    /// <summary>Item name tagged with the quality this entry targets — NQ and HQ are separate entries.</summary>
    private static string AutoListLabel(AutoListItem entry) =>
      $"{ItemNameResolver.GetItemName(entry.ItemId)} ({(entry.ListHq ? "HQ" : "NQ")})";

    private void EnqueueAutoListItem(AutoListItem entry, int batch)
    {
      // Quality belongs in the id: an item's NQ and HQ batches are distinct work, and sharing a task
      // name between them makes the run log ambiguous about which one acted.
      var id = $"{entry.ItemId}{(entry.ListHq ? "hq" : "nq")}#{batch}";
      _taskManager.Enqueue(() => AutoListOpen(entry), $"AL_Open{id}");
      _taskManager.DelayNext(Plugin.Configuration.AutoListStepDelayMS);
      _taskManager.Enqueue(() => AutoListComparePrice(entry), $"AL_Compare{id}");
      _taskManager.DelayNext(Plugin.Configuration.AutoListPriceWaitMS);
      _taskManager.Enqueue(() => AutoListSetPriceAndConfirm(entry), $"AL_Confirm{id}");
      _taskManager.DelayNext(Plugin.Configuration.AutoListStepDelayMS);
    }

    /// <summary>
    /// Price discovery for one auto-list batch. Reuses the basis this run already resolved for the item
    /// instead of asking the market board again — see <see cref="_autoListRunPrices"/> for why that matters.
    /// </summary>
    private bool? AutoListComparePrice(AutoListItem entry)
    {
      if (_skipCurrentItem)
        return true;

      var key = (entry.ItemId, entry.ListHq);
      _autoListPriceKey = key;

      if (_autoListRunPrices.TryGetValue(key, out var basis))
      {
        _newPrice = basis;
        _newPriceKey = key;
        return true;
      }

      // The market board request declares itself from _autoListPriceKey at the point it is actually
      // issued (see ClickComparePrice) rather than here — arming it up front would leave an expectation
      // behind on the paths that return without ever opening a search.
      return ClickComparePrice();
    }

    private bool? AutoListOpen(AutoListItem entry)
    {
      _newPrice = null;
      _newPriceKey = null;
      _autoListPriceKey = null;
      _skipCurrentItem = false;

      // Invalidate any Universalis request still in flight from the previous batch. Starting a request
      // cancels the previous one, but a batch that reuses the run's cached basis never starts one — so
      // without this the previous batch's answer could land mid-batch and be stamped as this item's price.
      CancelUniversalisPriceRequest();

      var name = AutoListLabel(entry);

      // Re-check capacity per batch. The whole run is planned up-front against one slot count, and that
      // number is only a prediction: our own earlier batches, another plugin, or a manual post can all
      // consume slots in between. Without this the run keeps posting until the GAME refuses.
      if (AutoListDriver.GetFreeMarketSlots() == 0)
      {
        Svc.Log.Information($"[AutoList] {name}: retainer market is full - skipping.");
        if (!_autoListFullWarned)
        {
          _autoListFullWarned = true;
          Svc.Chat.Print("[HrothgarMakeCoin] Auto-list: retainer market is full (20/20); skipping the rest.");
        }
        _skipCurrentItem = true;
        return true;
      }

      // Locate the stack fresh, right before posting. This is what makes it safe to post at all (the
      // slot is matched on ItemId+HQ this instant, so there is no stale-slot window in which we could
      // post the wrong item), and it is what lets Spread follow a stack as each batch shrinks it.
      if (!AutoListDriver.TryFindStack(entry.ItemId, entry.ListHq, out var container, out var slot, out var stack))
      {
        // Expected for Spread: the last batch runs out. Skip this one, don't kill the whole run.
        Svc.Log.Information($"[AutoList] {name}: no stack left - skipping.");
        _skipCurrentItem = true;
        return true;
      }

      // 0 = whole stack. Always clamp to what's actually there: the game clamps the quantity input to
      // the stack size, so an unclamped request would fail the read-back verify and abort the run.
      var want = entry.Quantity <= 0 ? stack : Math.Min(entry.Quantity, stack);
      _autoListQuantity = Math.Max(1, want);
      if (entry.Quantity > 0 && entry.Quantity > stack)
        Svc.Log.Information($"[AutoList] {name}: only {stack} in stack, listing {_autoListQuantity} (asked for {entry.Quantity})");

      if (!AutoListDriver.TryOpenPutUpForSale(container, slot))
      {
        Svc.Log.Warning($"[AutoList] {name}: could not open 'Put up for sale' - skipping.");
        _skipCurrentItem = true;
        return true;
      }

      return true;
    }

    private unsafe bool? AutoListSetPriceAndConfirm(AutoListItem entry)
    {
      if (_skipCurrentItem)
      {
        _skipCurrentItem = false;
        return true;
      }

      if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) || !GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
        return false; // dialog not up yet - retry

      // Close the compare-prices results window so it doesn't linger between items.
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchResult))
        searchResult->Close(true);

      var name = AutoListLabel(entry);

      // ---- price ----
      int price;
      if (entry.PriceMode == AutoListPriceMode.FixedMinPrice)
      {
        price = entry.MinPrice;
      }
      else
      {
        // MarketBoardHandler has already applied the undercut. NOTE: it only spares our own listings when
        // UndercutSelf is OFF — with it on, our own listing is undercut like anyone else's. That's why the
        // basis is resolved once per run and reused (_autoListRunPrices) rather than re-read per batch.
        var market = _newPrice ?? -1;
        if (market <= 0)
        {
          Svc.Log.Warning($"[AutoList] {name}: no market price found - skipping (never guessing).");
          Svc.Chat.PrintError($"[HrothgarMakeCoin] Auto-list skipped {name}: no market price found.");
          CancelRetainerSell(addon);
          return true;
        }

        // Prove the price belongs to THIS entry before posting against it. The confirm step runs on a
        // fixed delay rather than on the arrival of its own answer, so "a price is present" is not the
        // same as "our price is present". An unattributable price is skipped, never guessed at.
        var key = (entry.ItemId, entry.ListHq);
        if (_newPriceKey != key)
        {
          Svc.Log.Warning(
            $"[AutoList] {name}: price {market:N0} was requested for {_newPriceKey?.ToString() ?? "an uncorrelated request"} - skipping.");
          Svc.Chat.PrintError($"[HrothgarMakeCoin] Auto-list skipped {name}: could not confirm the price belongs to this item.");
          CancelRetainerSell(addon);
          return true;
        }

        // First batch of this item resolved the basis: pin it for the rest of the run.
        _autoListRunPrices.TryAdd(key, market);

        // A ceiling BELOW the market must never clamp the price down: that would post the item at a
        // massive discount (e.g. market 110 capped to Max 5). Skip instead of dumping.
        if (entry.MaxPrice > 0 && market > entry.MaxPrice)
        {
          Svc.Log.Warning($"[AutoList] {name}: market {market:N0} is above Max {entry.MaxPrice:N0} - skipping (posting at Max would dump it far under value).");
          Svc.Chat.PrintError($"[HrothgarMakeCoin] Auto-list skipped {name}: market {market:N0} gil is above your Max of {entry.MaxPrice:N0} gil.");
          CancelRetainerSell(addon);
          return true;
        }

        price = Math.Max(market, entry.MinPrice); // floor applied LAST - always wins
      }

      if (price <= 0)
      {
        Svc.Log.Warning($"[AutoList] {name}: computed price <= 0 - skipping.");
        CancelRetainerSell(addon);
        return true;
      }

      var qty = Math.Max(1, _autoListQuantity);

      if (Plugin.Configuration.AutoListDryRun)
      {
        Svc.Log.Information($"[AutoList][DRY RUN] would post {name} x{qty} @ {price:N0} gil ({entry.PriceMode})");
        Svc.Chat.Print($"[HrothgarMakeCoin] [DRY RUN] would post {name} x{qty} @ {price:N0} gil");
        CancelRetainerSell(addon);
        return true;
      }

      addon->Quantity->SetValue(qty);
      addon->AskingPrice->SetValue(price);

      // MANDATORY read-back: the dialog pre-fills defaults and a set can silently no-op, which would
      // otherwise confirm the GAME's price/quantity instead of ours.
      var actualQty = addon->Quantity->Value;
      var actualPrice = addon->AskingPrice->Value;
      if (actualQty != qty || actualPrice != price)
      {
        Svc.Log.Error($"[AutoList] {name}: read-back mismatch (qty {actualQty} vs {qty}, price {actualPrice} vs {price}) - aborting run.");
        Svc.Chat.PrintError($"[HrothgarMakeCoin] Auto-list aborted on {name}: price/quantity did not stick.");
        CancelRetainerSell(addon);
        _taskManager.Abort();
        return true;
      }

      ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 0); // confirm
      Svc.Log.Information($"[AutoList] posted {name} x{qty} @ {price:N0} gil");
      Svc.Chat.Print($"[HrothgarMakeCoin] Posted {name} x{qty} @ {price:N0} gil");
      return true;
    }

    private static unsafe void CancelRetainerSell(AddonRetainerSell* addon)
    {
      ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 1); // cancel
      addon->AtkUnitBase.Close(true);
    }

    private unsafe bool? EnqueueAllRetainerItems(Action<int> enqueueFunc, bool reverseOrder)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
        var listComponent = (AtkComponentList*)listNode->Component;
        int num = listComponent->ListLength;
        if (reverseOrder)
        {
          for (int i = num - 1; i >= 0; i--)
          {
            enqueueFunc(i);
          }
        }
        else
        {
          for (int i = 0; i < num; i++)
          {
            enqueueFunc(i);
          }
        }
        if (Plugin.Configuration.TTSWhenEachDone)
          _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenEachDoneMsg), "SpeakTTSEach");

        return true;
      }
      else
        return false;
    }

    private void EnqueueSingleItem(int index)
    {
      _taskManager.Enqueue(() => OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(ClickAdjustPrice, $"ClickAdjustPrice{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(DelayMarketBoard, $"DelayMB{index}");
      _taskManager.Enqueue(ClickComparePrice, $"ClickComparePrice{index}");
      _taskManager.DelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);
      _taskManager.Enqueue(SetNewPrice, $"SetNewPrice{index}");
    }

    private void InsertSingleItem(int index)
    {
      // reverse order because we INSERT
      _taskManager.Insert(SetNewPrice, $"SetNewPrice{index}");
      _taskManager.InsertDelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);
      _taskManager.Insert(ClickComparePrice, $"ClickComparePrice{index}");
      _taskManager.Insert(DelayMarketBoard, $"DelayMB{index}");
      _taskManager.InsertDelayNext(100);
      _taskManager.Insert(ClickAdjustPrice, $"ClickAdjustPrice{index}");
      _taskManager.InsertDelayNext(100);
      _taskManager.Insert(() => OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    }

    private static unsafe bool? OpenItemContextMenu(int itemIndex)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        Svc.Log.Debug($"Clicking item {itemIndex}");
        ECommons.Automation.Callback.Fire(addon, true, 0, itemIndex, 1); // click item
        return true;
      }

      return false;
    }

    private unsafe bool? ClickAdjustPrice()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        var reader = new ReaderContextMenu(addon);
        if (!IsItemMannequin(reader.Entries))
        {
          Svc.Log.Debug($"Clicking adjust price");
          ECommons.Automation.Callback.Fire(addon, true, 0, 0, 0, 0, 0); // click adjust price
        }
        else
        {
          Svc.Log.Debug("Current item is a mannequin item and will be skipped");
          _skipCurrentItem = true;
          addon->Close(true);
        }

        return true;
      }

      return false;
    }

    /// <summary>
    /// Checks if an item is a mannequin item, by checking if there is
    /// the "adjust price" entry in the given <paramref name="contextMenuEntries"/>.
    /// </summary>
    /// <param name="contextMenuEntries">Context menu entries to check.</param>
    /// <returns>True if item is a mannequin item, false otherwise.</returns>
    private static bool IsItemMannequin(List<ContextMenuEntry> contextMenuEntries)
    {
      return !contextMenuEntries.Any((e) => e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));
    }

    private unsafe bool? DelayMarketBoard()
    {
      if (_skipCurrentItem)
        return true;

      if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
      {
        var itemName = GetRetainerSellItemName(addon);
        var rawItemName = GetRetainerSellRawItemName(addon);
        if (Plugin.Configuration.UseUniversalisDataCenterPrices && _universalisPriceProvider.CanResolveItem(itemName, rawItemName))
          return true;

        if (!_cachedPrices.TryGetValue(itemName, out var cachedPrice) || cachedPrice.Value <= 0)
        {
          Svc.Log.Debug($"{itemName} has no cached price (or that price was <= 0), delaying next mb open");
          _taskManager.InsertDelayNext(Plugin.Configuration.GetMBPricesDelayMS);
        }

        return true;
      }

      return false;
    }

    private unsafe bool? ClickComparePrice()
    {
      if (_skipCurrentItem)
        return true;

      if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
      {
        // if we have a cached price, dont click compare
        var itemName = GetRetainerSellItemName(addon);
        var rawItemName = GetRetainerSellRawItemName(addon);
        if (_cachedPrices.TryGetValue(itemName, out var cachedPrice) && cachedPrice.Value > 0)
        {
          Svc.Log.Debug($"{itemName}: using cached price");
          _newPrice = cachedPrice.Value;
          // Read synchronously for the item whose dialog is open, so it belongs to the current batch.
          _newPriceKey = _autoListPriceKey;
          _newPriceFromUniversalis = cachedPrice.FromUniversalis;
          return true;
        }
        else
        {
          if (Plugin.Configuration.UseUniversalisDataCenterPrices && _universalisPriceProvider.CanResolveItem(itemName, rawItemName))
          {
            Svc.Log.Debug($"{itemName}: requesting Universalis data center price");
            StartUniversalisPriceRequest(itemName, rawItemName);
            return true;
          }

          // Declare what this request is for at the only place a request is actually issued. Arming it
          // earlier would strand an expectation whenever a branch above returns without opening a search,
          // and the next request to open one — a pinch, possibly for a different item — would consume it
          // and have its own answer rejected as foreign.
          //
          // The pinch flow declares nothing (_autoListPriceKey is null) and clears explicitly, so it can
          // never inherit auto-list's correlation and keeps its uncorrelated behaviour.
          if (_autoListPriceKey is { } declared)
            _mbHandler.ExpectPriceRequest(declared.ItemId, declared.Hq);
          else
            _mbHandler.ClearExpectedPriceRequest();

          Svc.Log.Debug($"Clicking compare prices");
          ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
          return true;
        }
      }

      return false;
    }

    private unsafe bool? SetNewPrice()
    {
      try
      {
        if (_skipCurrentItem)
          return true;

        if (!_newPrice.HasValue)
          return false;

        // close compare price window
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon))
          addon->Close(true);

        if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
        {
          var ui = &retainerSell->AtkUnitBase;
          var itemName = GetRetainerSellItemName(retainerSell);
          _oldPrice = retainerSell->AskingPrice->Value;
          if (!(_newPrice > 0))
          {
            if (Plugin.Configuration.DefaultAmount == 0)
            {
              Svc.Log.Warning("SetNewPrice: No price to set");
              Communicator.PrintNoPriceToSetError(itemName);
              ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 1); // cancel
              ui->Close(true);
              return true;
            }
	    Svc.Log.Warning("SetNewPrice: Using default amount");
            _newPrice = Plugin.Configuration.DefaultAmount;
            _newPriceFromUniversalis = false;
	    Communicator.PrintUsingDefaultAmountWarning(itemName, _newPrice.Value);
          }

          var rawItemName = GetRetainerSellRawItemName(retainerSell);
          var limitedPrice = ApplyItemPriceLimits(itemName, rawItemName, _newPrice.Value);
          if (limitedPrice != _newPrice.Value)
          {
            Svc.Log.Debug($"{itemName}: item price limit adjusted {_newPrice.Value} to {limitedPrice}");
            _newPrice = limitedPrice;
          }

          var cutPercentage = ((float)_newPrice.Value - _oldPrice.Value) / _oldPrice.Value * 100f;
          if (cutPercentage >= -Plugin.Configuration.MaxUndercutPercentage)
          {
            Svc.Log.Debug($"Setting new price");
            _cachedPrices.TryAdd(itemName, new CachedPrice(_newPrice.Value, _newPriceFromUniversalis));
            retainerSell->AskingPrice->SetValue(_newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, _oldPrice.Value, _newPrice.Value, cutPercentage, _newPriceFromUniversalis);
          }
          else
            Communicator.PrintAboveMaxCutError(itemName);

          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0); // confirm
          ui->Close(true);

          return true;
        }
        else
          return false;
      }
      finally
      {
        _oldPrice = null;
        _newPrice = null;
        _newPriceFromUniversalis = false;
        _skipCurrentItem = false;
      }
    }

    private void MBHandler_NewPriceReceived(object? sender, NewPriceEventArgs e)
    {
      Svc.Log.Debug($"New price received: {e.NewPrice}");
      _newPrice = e.NewPrice;
      // Record what this price was actually asked about; null for the pinch flow, which doesn't declare.
      _newPriceKey = _mbHandler.InFlightKey;
      _newPriceFromUniversalis = false;
    }

    private static unsafe string GetRetainerSellItemName(AddonRetainerSell* addon)
    {
      return addon->ItemName->NodeText.GetText();
    }

    private static unsafe string GetRetainerSellRawItemName(AddonRetainerSell* addon)
    {
      return addon->ItemName->NodeText.ToString();
    }

    private static int ApplyItemPriceLimits(string itemName, string rawItemName, int price)
    {
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
        return price;

      var limit = Plugin.Configuration.GetItemPriceLimit(itemId);
      return limit?.Apply(price) ?? price;
    }

    private void StartUniversalisPriceRequest(string itemName, string rawItemName)
    {
      CancelUniversalisPriceRequest();

      var requestId = ++_universalisPriceRequestId;
      _newPriceFromUniversalis = false;
      _universalisPriceRequestCts = new CancellationTokenSource();

      // Capture the declaration now, with the batch that is asking. Reading it when the response lands
      // would attribute the price to whatever batch happened to be current by then.
      var declared = _autoListPriceKey;
      _ = CompleteUniversalisPriceRequest(itemName, rawItemName, declared, requestId, _universalisPriceRequestCts.Token);
    }

    private async Task CompleteUniversalisPriceRequest(string itemName, string rawItemName,
      (uint ItemId, bool Hq)? declared, int requestId, CancellationToken cancellationToken)
    {
      var price = -1;

      try
      {
        // Pass the declared (item, quality) so the price is fetched for exactly what this batch is
        // posting, rather than for a name re-resolved from the dialog and a quality decided by the
        // pinch-oriented "Use HQ price" preference; null leaves the pinch flow's behaviour alone.
        price = await _universalisPriceProvider
          .GetNewPrice(itemName, rawItemName, declared, cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, $"Failed to fetch Universalis price for {itemName}");
      }

      await Svc.Framework.RunOnFrameworkThread(() =>
      {
        if (_disposed || requestId != _universalisPriceRequestId)
          return;

        Svc.Log.Debug($"New Universalis price received: {price}");
        _newPrice = price;
        // Attribute the price to the batch that actually requested it — captured at request time, and
        // quality-matched by GetNewPrice above, so this is evidence rather than an assumption.
        _newPriceKey = declared;
        _newPriceFromUniversalis = price > 0;
      });
    }

    private void CancelUniversalisPriceRequest()
    {
      _universalisPriceRequestId++;
      _universalisPriceRequestCts?.Cancel();
      _universalisPriceRequestCts?.Dispose();
      _universalisPriceRequestCts = null;
    }

    private unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
    {
      // fallback for when something was improperly cleaned up
      if (!_taskManager.IsBusy)
        RemoveTalkAddonListeners();
      else
      {
        if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
          new AddonMaster.Talk(args.Addon).Click();
      }
    }

    private void RetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
      if (_taskManager.IsBusy)
        return;

      if (Plugin.Configuration.EnablePostPinchkey && Plugin.KeyState[Plugin.Configuration.PostPinchKey])
      {
        // The only pinch entry point that doesn't go through ClearState, so it has to drop the previous
        // flow's pricing state itself. The correlation key left over from the last auto-list batch would
        // otherwise make this pinch price a different item — or the same item at the other quality — and
        // the stale _newPrice would be read as this item's price if the compare doesn't answer in time.
        _autoListPriceKey = null;
        _newPriceKey = null;
        _newPrice = null;
        _newPriceFromUniversalis = false;

        _taskManager.Enqueue(ClickComparePrice, $"ClickComparePricePosted");
        _taskManager.DelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);
        _taskManager.Enqueue(SetNewPrice, $"SetNewPricePosted");
      }
    }
    private void RemoveTalkAddonListeners()
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
    }

    private static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
      var pos = new Vector2(node->X, node->Y);
      var par = node->ParentNode;
      while (par != null)
      {
        pos *= new Vector2(par->ScaleX, par->ScaleY);
        pos += new Vector2(par->X, par->Y);
        par = par->ParentNode;
      }

      return pos;
    }

    private static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
      if (node == null) return new Vector2(1, 1);
      var scale = new Vector2(node->ScaleX, node->ScaleY);
      while (node->ParentNode != null)
      {
        node = node->ParentNode;
        scale *= new Vector2(node->ScaleX, node->ScaleY);
      }

      return scale;
    }

    private static bool? SpeakTTS(string msg)
    {
      if (!Plugin.Configuration.DontUseTTS)
      {
        SpeechSynthesizer tts = new()
        {
          Volume = Plugin.Configuration.TTSVolume
        };
        tts.SpeakAsync(msg);
        tts.SpeakCompleted += (o, e) =>
        {
          tts.Dispose();
          Svc.Log.Verbose($"Finished message: {msg} - tts disposed");
        };
      }
      return true;
    }

    private void ClearState()
    {
      _newPrice = null;
      _newPriceKey = null;
      _autoListPriceKey = null;
      _mbHandler.ClearExpectedPriceRequest();
      _newPriceFromUniversalis = false;
      _cachedPrices = [];
      _cachedPricesUseUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;
      _skipCurrentItem = false;
      _autoListRunPrices.Clear();
      _autoListFullWarned = false;
      CancelUniversalisPriceRequest();
    }

    private void ClearCachedPricesIfUniversalisSettingChanged()
    {
      var useUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;
      if (_cachedPricesUseUniversalisDataCenterPrices == useUniversalisDataCenterPrices)
        return;

      _cachedPrices.Clear();
      _cachedPricesUseUniversalisDataCenterPrices = useUniversalisDataCenterPrices;
      Svc.Log.Debug("Use Universalis data center prices setting changed; cleared cached prices");
    }

    private readonly record struct CachedPrice(int Value, bool FromUniversalis);
  }
}
