using System;
using System.Collections.Generic;
using ECommons.Automation;
using ECommons.DalamudServices;
using static ECommons.GenericHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace HrothgarMakeCoin
{
  /// <summary>
  /// Low-level driver for the auto-list feature: locating stacks, counting free market slots,
  /// dedupe against current listings, and opening the game's "Put up for sale" dialog.
  ///
  /// The "Put up for sale" mechanic is verified in-game for BOTH player inventory (owner grid
  /// "InventoryLarge", a4=0) and retainer inventory (owner grid "RetainerGrid0", a4=0);
  /// "Put Up for Sale" is entry index 0 and dispatches as command=2. Mirrors Knack117/QuickTransfer.
  /// </summary>
  internal static class AutoListDriver
  {
    /// <summary>Market slots per retainer. Game constant — not declared in FFXIVClientStructs.</summary>
    public const int MaxMarketSlots = 20;

    /// <summary>Where listable stacks may live, in search order (player bags first, then retainer pages).</summary>
    public static readonly InventoryType[] SourceContainers =
    {
      InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
      InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
      InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };

    private static readonly string[] PlayerGridAddons =
    {
      "InventoryExpansion", "InventoryLarge", "Inventory",
      "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E",
      "InventoryGrid0", "InventoryGrid1", "InventoryGrid2", "InventoryGrid3", "InventoryGrid",
    };

    private static readonly string[] RetainerGridAddons =
    {
      "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4",
      "InventoryRetainer", "InventoryRetainerLarge",
    };

    /// <summary>
    /// Free market slots on the active retainer, or -1 if it can't be determined.
    ///
    /// Counts the live RetainerMarket container rather than RetainerManager's MarketItemCount.
    /// MarketItemCount is synced from the retainer LIST and is NOT updated as items are posted, so it
    /// goes stale the moment we list anything: a second run in the same session would read the count
    /// from before the first run and cheerfully over-book the retainer until the game refuses with
    /// "You cannot put any more items up for sale at this time." The container updates as we post.
    /// </summary>
    public static unsafe int GetFreeMarketSlots()
    {
      var inv = InventoryManager.Instance();
      if (inv != null)
      {
        var cont = inv->GetInventoryContainer(InventoryType.RetainerMarket);
        if (cont != null && cont->IsLoaded && cont->Size > 0)
        {
          var used = 0;
          for (int i = 0; i < cont->Size; i++)
          {
            var s = cont->GetInventorySlot(i);
            if (s != null && s->ItemId != 0)
              used++;
          }
          return Math.Max(0, MaxMarketSlots - used);
        }
      }

      // Fallback: the market container isn't loaded (no sell session open yet). MarketItemCount is
      // stale-prone as described above, but it's the only source here and it's right often enough
      // to answer "is this retainer already full?" before a run starts.
      var rm = RetainerManager.Instance();
      if (rm == null)
        return -1;

      var retainer = rm->GetActiveRetainer();
      if (retainer == null)
        return -1;

      return Math.Max(0, MaxMarketSlots - retainer->MarketItemCount);
    }

    /// <summary>True if a matching (itemId, hq) stack is already up on the retainer's market (mandatory dedupe).</summary>
    public static unsafe bool IsAlreadyListed(uint itemId, bool hq)
    {
      var inv = InventoryManager.Instance();
      if (inv == null)
        return false;

      var cont = inv->GetInventoryContainer(InventoryType.RetainerMarket);
      if (cont == null || !cont->IsLoaded)
        return false;

      for (int i = 0; i < cont->Size; i++)
      {
        var s = cont->GetInventorySlot(i);
        if (s == null || s->ItemId == 0) continue;
        if (s->ItemId == itemId && s->IsHighQuality() == hq)
          return true;
      }
      return false;
    }

    /// <summary>
    /// Finds a stack of (itemId, hq) in player/retainer inventory. Call this immediately before posting
    /// rather than caching the result: the match on ItemId+HQ is what guarantees the slot holds the
    /// intended item, and a cached (container, slot) can go stale and post something else entirely.
    /// </summary>
    public static unsafe bool TryFindStack(uint itemId, bool hq, out InventoryType container, out int slot, out int quantity)
    {
      container = default; slot = -1; quantity = 0;

      var inv = InventoryManager.Instance();
      if (inv == null)
        return false;

      foreach (var c in SourceContainers)
      {
        var cont = inv->GetInventoryContainer(c);
        if (cont == null || !cont->IsLoaded || cont->Size == 0) continue;

        for (int i = 0; i < cont->Size; i++)
        {
          var s = cont->GetInventorySlot(i);
          if (s == null || s->ItemId != itemId) continue;
          if (s->IsHighQuality() != hq) continue;

          container = c; slot = i; quantity = s->Quantity;
          return true;
        }
      }
      return false;
    }

    /// <summary>Opens the "Put up for sale" dialog for an inventory slot. Does NOT set price or confirm.</summary>
    public static unsafe bool TryOpenPutUpForSale(InventoryType inventoryType, int slot)
    {
      // GUARD: never call OpenForItemSlot for a type the agent doesn't own (can crash the client).
      if (!IsSellableInventoryType(inventoryType))
      {
        Svc.Log.Warning($"[AutoList] refusing OpenForItemSlot for non-sellable type {inventoryType}");
        return false;
      }

      var ownerIds = GetCandidateOwnerAddonIds(inventoryType);
      if (ownerIds.Count == 0)
      {
        Svc.Log.Warning($"[AutoList] no visible {(IsPlayerInventoryType(inventoryType) ? "player" : "retainer")} inventory grid — open that inventory.");
        return false;
      }

      var ctx = AgentInventoryContext.Instance();
      if (ctx == null) { Svc.Log.Warning("[AutoList] AgentInventoryContext.Instance() is null"); return false; }

      bool opened = false;
      foreach (var ownerId in ownerIds)
      {
        foreach (var a4 in new[] { 0, 1, 2 })
        {
          ctx->OpenForItemSlot(inventoryType, slot, a4, ownerId);
          if (ctx->ContextItemCount > 0)
          {
            Svc.Log.Debug($"[AutoList] context opened (type={inventoryType}, slot={slot}, a4={a4}, addonId={ownerId}, count={ctx->ContextItemCount})");
            opened = true;
            break;
          }
        }
        if (opened) break;
      }
      if (!opened) { Svc.Log.Warning("[AutoList] context menu did not populate for any owner/a4 combination"); return false; }

      int sellIndex = -1;
      int count = Math.Min(ctx->ContextItemCount, 64);
      for (int i = 0; i < count; i++)
      {
        var param = ctx->EventParams[ctx->ContexItemStartIndex + i]; // sic: FFXIVClientStructs spells it 'ContexItem...'
        if (param.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) || !param.String.HasValue)
          continue;

        var text = (param.String.ToString() ?? string.Empty).Trim();
        if (IsPutUpForSaleLabel(text)) { sellIndex = i; break; }
      }

      if (sellIndex < 0)
      {
        Svc.Log.Warning("[AutoList] 'Put up for sale' entry not found — is a retainer market/sell session active?");
        if (TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cm) && IsAddonReady(cm))
          cm->Close(true);
        return false;
      }

      if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var contextMenu) || !IsAddonReady(contextMenu))
      {
        Svc.Log.Warning("[AutoList] ContextMenu addon not ready to fire selection");
        return false;
      }

      // Equivalent to FireCallback(5, {0, index, 0u, 0, 0}). Don't close the menu — RetainerSell needs a frame.
      Callback.Fire(contextMenu, true, 0, sellIndex, 0u, 0, 0);
      return true;
    }

    public static bool IsPutUpForSaleLabel(string text)
    {
      if (string.IsNullOrWhiteSpace(text)) return false;
      if (text.Equals("Put Up for Sale", StringComparison.OrdinalIgnoreCase)) return true;
      // locale-agnostic fallback
      return text.Contains("Put", StringComparison.OrdinalIgnoreCase)
          && text.Contains("Sale", StringComparison.OrdinalIgnoreCase);
    }

    public static unsafe List<uint> GetCandidateOwnerAddonIds(InventoryType t)
    {
      var names = IsPlayerInventoryType(t) ? PlayerGridAddons : RetainerGridAddons;
      var ids = new List<uint>();
      foreach (var name in names)
      {
        if (TryGetAddonByName<AtkUnitBase>(name, out var addon) && IsAddonReady(addon) && addon->IsVisible)
        {
          if (!ids.Contains(addon->Id))
            ids.Add(addon->Id);
        }
      }
      return ids;
    }

    public static bool IsSellableInventoryType(InventoryType t) => IsPlayerInventoryType(t) || IsRetainerInventoryType(t);

    public static bool IsPlayerInventoryType(InventoryType t) =>
      t is InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4;

    public static bool IsRetainerInventoryType(InventoryType t) =>
      t is InventoryType.RetainerPage1 or InventoryType.RetainerPage2 or InventoryType.RetainerPage3
        or InventoryType.RetainerPage4 or InventoryType.RetainerPage5 or InventoryType.RetainerPage6
        or InventoryType.RetainerPage7;
  }
}
