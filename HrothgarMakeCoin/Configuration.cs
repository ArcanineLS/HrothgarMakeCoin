using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HrothgarMakeCoin;

public enum UndercutMode
{
  FixedAmount,
  Percentage
}

[Serializable]
public sealed class ItemPriceLimit
{
  public uint ItemId { get; set; }

  public int MinPrice { get; set; } = 0;

  public int MaxPrice { get; set; } = 0;

  public int Apply(int price)
  {
    var minPrice = Math.Max(MinPrice, 0);
    var maxPrice = Math.Max(MaxPrice, 0);

    if (minPrice > 0 && price < minPrice)
      price = minPrice;

    if (maxPrice > 0)
    {
      if (minPrice > 0 && maxPrice < minPrice)
        maxPrice = minPrice;

      if (price > maxPrice)
        price = maxPrice;
    }

    return price;
  }
}

public enum AutoListPriceMode
{
  MarketUndercut,
  FixedMinPrice
}

/// <summary>
/// A whitelist entry for the (opt-in) auto-list feature: an item HrothgarMakeCoin may post to a
/// retainer's market board. See HrothgarMakeCoin-AutoList-Design.md for the full behaviour.
/// </summary>
[Serializable]
public sealed class AutoListItem
{
  public uint ItemId { get; set; }

  public bool Enabled { get; set; } = true;

  /// <summary>Post HQ stacks (else NQ). Defaulted from the item the user added.</summary>
  public bool ListHq { get; set; }

  public AutoListPriceMode PriceMode { get; set; } = AutoListPriceMode.MarketUndercut;

  /// <summary>How many to list. 0 = the whole stack; otherwise this many, clamped to the stack size.</summary>
  public int Quantity { get; set; } = 0;

  /// <summary>
  /// Spread a stack across multiple listings: keep posting batches of <see cref="Quantity"/> until the
  /// stack is empty or the retainer runs out of free slots (e.g. a 99 stack with Quantity 5 -> 5x20).
  /// Requires <see cref="Quantity"/> &gt; 0. Spread entries intentionally create multiple listings, so
  /// the "already listed" dedupe does not apply to them.
  /// </summary>
  public bool Spread { get; set; } = false;

  /// <summary>Fixed price (FixedMinPrice) or mandatory floor (MarketUndercut). Required (&gt; 0) to post.</summary>
  public int MinPrice { get; set; } = 0;

  /// <summary>Optional ceiling (0 = none). Must be 0 or &gt;= MinPrice.</summary>
  public int MaxPrice { get; set; } = 0;

  /// <summary>Safe to post: enabled, has a floor, and a consistent price window.</summary>
  public bool IsValid => Enabled && MinPrice > 0 && (MaxPrice == 0 || MaxPrice >= MinPrice);
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
  public int Version { get; set; } = 0;

  public bool HQ { get; set; } = true;

  public int GetMBPricesDelayMS { get; set; } = 3000;

  public int MarketBoardKeepOpenMS { get; set; } = 1000;

  public bool ShowErrorsInChat { get; set; } = true;

  public bool EnablePinchKey { get; set; } = false;

  public VirtualKey PinchKey { get; set; } = VirtualKey.Q;

  public bool EnablePostPinchkey { get; set; } = true;

  public VirtualKey PostPinchKey { get; set; } = VirtualKey.SHIFT;

  public UndercutMode UndercutMode { get; set; } = UndercutMode.FixedAmount;

  public int DefaultAmount { get; set; } = 0;

  public int UndercutAmount { get; set; } = 1;

  public float MaxUndercutPercentage { get; set; } = 100.0f;

  public bool UndercutSelf { get; set; } = false;

  public bool UseUniversalisDataCenterPrices { get; set; } = false;

  /// <summary>
  /// When enabled, HrothgarMakeCoin automatically re-prices a retainer's listings
  /// right after AutoRetainer finishes that retainer's ventures (post-venture hook).
  /// </summary>
  public bool AutoPinchAfterAutoRetainer { get; set; } = false;

  /// <summary>
  /// When <see cref="AutoPinchAfterAutoRetainer"/> is on, only auto-pinch retainers that are
  /// enabled in <see cref="EnabledRetainerNames"/> (the same selection used by the manual "Auto Pinch" button).
  /// </summary>
  public bool AutoRetainerRespectRetainerSelection { get; set; } = true;

  public bool ShowPriceAdjustmentsMessages { get; set; } = true;

  public bool ShowRetainerNames { get; set; } = true;

  public bool TTSWhenAllDone { get; set; } = false;

  public string TTSWhenAllDoneMsg { get; set; } = "Finished auto pinching all retainers";

  public bool TTSWhenEachDone { get; set; } = false;

  public string TTSWhenEachDoneMsg { get; set; } = "Auto Pinch done";

  public int TTSVolume { get; set; } = 20;

  public bool DontUseTTS { get; set; } = false;

  public List<ulong> SeenRetainers { get; set; } = [];

  public bool ShowInventoryContextMenuEntry { get; set; } = true;

  public List<ItemPriceLimit> ItemPriceLimits { get; set; } = [];

  /// <summary>Master switch for the opt-in auto-list feature.</summary>
  public bool AutoListEnabled { get; set; } = false;

  /// <summary>
  /// When on (the default), auto-list computes and reports what it WOULD post but cancels instead of
  /// confirming — nothing is actually listed. Turn off only once you trust the prices it picks.
  /// </summary>
  public bool AutoListDryRun { get; set; } = true;

  /// <summary>Delay between auto-list steps (open -> compare -> confirm), in milliseconds.</summary>
  public int AutoListStepDelayMS { get; set; } = 300;

  /// <summary>
  /// How long to wait for the market price after clicking Compare Prices, in milliseconds.
  ///
  /// Sized for HQ. Listings arrive price-ascending in batches of ten across BOTH qualities, so an HQ entry
  /// can only be priced once the batch past the NQ listings lands — measured at ~1450ms in game. This is
  /// NOT capped by the market board's own timeout: that only bounds the FIRST batch, and each further
  /// batch re-arms it. Too low and the answer arrives after this wait has already given up on the item.
  /// </summary>
  public int AutoListPriceWaitMS { get; set; } = 2000;

  /// <summary>The auto-list whitelist (items that may be posted to the market board).</summary>
  public List<AutoListItem> AutoListItems { get; set; } = [];

  /// <summary>
  /// Set of retainer names that are enabled for auto pinch.
  /// If empty or null, all retainers are enabled by default.
  /// If contains ALL_DISABLED_SENTINEL, all retainers are disabled.
  /// </summary>
  public const string ALL_DISABLED_SENTINEL = "__ALL_DISABLED__";
  
  public HashSet<string> EnabledRetainerNames { get; set; } = [];

  /// <summary>
  /// List of retainer names that were last fetched from the game.
  /// Used to display retainer selection even when the retainer list is not open.
  /// </summary>
  public List<string> LastKnownRetainerNames { get; set; } = [];

  public ItemPriceLimit? GetItemPriceLimit(uint itemId)
  {
    return ItemPriceLimits.FirstOrDefault(limit => limit.ItemId == itemId);
  }

  public ItemPriceLimit GetOrAddItemPriceLimit(uint itemId)
  {
    var limit = GetItemPriceLimit(itemId);
    if (limit != null)
      return limit;

    limit = new ItemPriceLimit { ItemId = itemId };
    ItemPriceLimits.Add(limit);
    return limit;
  }

  /// <summary>
  /// An auto-list entry is identified by (ItemId, ListHq), never by ItemId alone: NQ and HQ of the same
  /// item are two independent entries with their own prices, and they post from different stacks. Keying
  /// on ItemId alone made the second quality silently resolve to the first one's entry, so it could never
  /// be added ("already in the auto-list") and would have posted at the wrong quality's price.
  /// </summary>
  public AutoListItem? GetAutoListItem(uint itemId, bool hq)
  {
    return AutoListItems.FirstOrDefault(x => x.ItemId == itemId && x.ListHq == hq);
  }

  /// <summary>
  /// No default for <paramref name="hq"/> on purpose: quality is half of an entry's identity, so a caller
  /// that omits it would silently mean NQ — which is exactly how the "HQ won't register" bug read.
  /// </summary>
  public AutoListItem GetOrAddAutoListItem(uint itemId, bool hq)
  {
    var entry = GetAutoListItem(itemId, hq);
    if (entry != null)
      return entry;

    entry = new AutoListItem { ItemId = itemId, ListHq = hq };
    AutoListItems.Add(entry);
    return entry;
  }

  public void Save()
  {
    Plugin.PluginInterface.SavePluginConfig(this);
  }
}
