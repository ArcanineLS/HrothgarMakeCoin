using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace HrothgarMakeCoin
{
  internal unsafe sealed class MarketBoardHandler : IDisposable
  {
    private const int UnknownRequestId = -1;
    private const int ListingsPerBatch = 10;

    private readonly Lumina.Excel.ExcelSheet<Item> _items;
    private bool _newRequest;
    private bool _useHq;
    private bool _itemHq;
    private int _lastRequestId = UnknownRequestId;
    private int _pendingNoMatchRequestId = UnknownRequestId;
    private long _pendingNoMatchTimeoutAt;
    private long _pendingNoOfferingsTimeoutAt;

    // What the caller says the NEXT request is for, and what the in-flight request turned out to be for.
    // 0 / null means "uncorrelated": the pinch flow doesn't declare an item, and keeps the old behaviour of
    // taking whatever answer arrives and deriving quality matching from the global "Use HQ price" toggle.
    private uint _expectedItemId;
    private bool? _expectedQuality;
    private uint _inFlightItemId;
    private bool? _matchQuality;

    /// <summary>
    /// The (item, quality) the in-flight price request was made for, or null if the request was not
    /// correlated to one. Callers use this to prove a delivered price belongs to what they asked about.
    /// </summary>
    public (uint ItemId, bool Hq)? InFlightKey =>
      _inFlightItemId != 0 && _matchQuality is bool q ? (_inFlightItemId, q) : null;

    /// <summary>
    /// Declare what the next price request is for. Two effects, both required by auto-list:
    /// the market basis is quality-matched to <paramref name="hq"/> rather than to the pinch-oriented
    /// "Use HQ price" preference, and a response for any other item is ignored instead of adopted.
    /// </summary>
    public void ExpectPriceRequest(uint itemId, bool hq)
    {
      _expectedItemId = itemId;
      _expectedQuality = hq;
    }

    /// <summary>Drop back to uncorrelated pricing (the pinch flow's behaviour).</summary>
    public void ClearExpectedPriceRequest()
    {
      _expectedItemId = 0;
      _expectedQuality = null;
    }

    private int NewPrice
    {
      get => _newPrice;
      set
      {
        _newPrice = value;
        NewPriceReceived?.Invoke(this, new NewPriceEventArgs(NewPrice));
      }
    }
    private int _newPrice;

    public event EventHandler<NewPriceEventArgs>? NewPriceReceived;

    public MarketBoardHandler()
    {
      _items = Svc.Data.GetExcelSheet<Item>();

      Plugin.MarketBoard.OfferingsReceived += MarketBoardOnOfferingsReceived;
      Svc.Framework.Update += FrameworkOnUpdate;

      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    public void Dispose()
    {
      Plugin.MarketBoard.OfferingsReceived -= MarketBoardOnOfferingsReceived;
      Svc.Framework.Update -= FrameworkOnUpdate;
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    private void MarketBoardOnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
      if (!_newRequest)
        return;

      if (currentOfferings.RequestId == _lastRequestId)
        return;

      // Reject an answer about a different item than the one we asked about.
      //
      // A request abandoned by the no-offerings timeout can still be answered seconds later, and by then
      // _newRequest has been re-armed by the NEXT item — so without this check that late answer is adopted
      // as the next item's price and posted. The timeout can't defend itself by remembering the stale
      // RequestId, because a request that produced no offerings never told us its id; the ITEM is the only
      // identity the response carries. (Listings arrive price-ascending for a single item, so index 0's
      // ItemId identifies the whole response.)
      if (_inFlightItemId != 0 && currentOfferings.ItemListings.Count > 0
          && currentOfferings.ItemListings[0].ItemId != _inFlightItemId)
      {
        Svc.Log.Debug(
          $"Ignoring market board offerings for item {currentOfferings.ItemListings[0].ItemId}; " +
          $"the in-flight request is for {_inFlightItemId}");
        return;
      }

      ClearPendingNoOfferings();

      if (currentOfferings.ItemListings.Count == 0)
      {
        CompletePriceRequest(-1, currentOfferings.RequestId);
        return;
      }

      // Which qualities are NOT acceptable as the basis.
      //
      // When the caller declared a quality (auto-list), match it exactly in BOTH directions: an HQ entry
      // must never price off the NQ market, and an NQ entry must never price off the HQ market. Deriving
      // this from the global "Use HQ price" toggle instead — as the uncorrelated pinch path still does —
      // means that with the toggle off an HQ item prices against the cheapest listing of any quality,
      // which is normally the NQ one, and posts HQ goods at NQ money.
      //
      // If nothing matches, the item is skipped rather than priced off the wrong quality: see the
      // no-match handling below, which completes with -1.
      // ItemCanBeHq stays behind the && as it always has: it hits the item sheet and throws if the row is
      // missing, so it must not be evaluated for requests that don't care about quality.
      bool skipNq, skipHq;
      if (_matchQuality is bool wantHq)
      {
        skipNq = wantHq && ItemCanBeHq(currentOfferings.ItemListings[0].ItemId);
        skipHq = !wantHq;
      }
      else
      {
        skipNq = _useHq && ItemCanBeHq(currentOfferings.ItemListings[0].ItemId);
        skipHq = false;
      }

      var i = 0;
      while (i < currentOfferings.ItemListings.Count)
      {
        var listing = currentOfferings.ItemListings[i];
        if ((skipNq && !listing.IsHq) || (skipHq && listing.IsHq))
          i++;
        else
          break;
      }

      // offerings arrive in batches of 10; if no match in this batch, wait for the next
      if (i >= currentOfferings.ItemListings.Count)
      {
        if (currentOfferings.ItemListings.Count < ListingsPerBatch)
        {
          CompletePriceRequest(-1, currentOfferings.RequestId);
          return;
        }

        _pendingNoMatchRequestId = currentOfferings.RequestId;
        _pendingNoMatchTimeoutAt = Environment.TickCount64 + GetMarketBoardResultTimeoutMs();
        return;
      }

      ClearPendingNoMatch();

      int price;

      if (!Plugin.Configuration.UndercutSelf && IsOwnRetainer(currentOfferings.ItemListings[i].RetainerId))
        price = (int)currentOfferings.ItemListings[i].PricePerUnit;
      else if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
        price = Math.Max((int)currentOfferings.ItemListings[i].PricePerUnit - Plugin.Configuration.UndercutAmount, 1);
      else
        price = Math.Max((100 - Plugin.Configuration.UndercutAmount) * (int)currentOfferings.ItemListings[i].PricePerUnit / 100, 1);

      CompletePriceRequest(price, currentOfferings.RequestId);
    }

    private void FrameworkOnUpdate(object _)
    {
      if (!_newRequest)
        return;

      var now = Environment.TickCount64;
      if (_pendingNoMatchRequestId >= 0 && now >= _pendingNoMatchTimeoutAt)
      {
        Svc.Log.Debug("No matching market board listing received before timeout");
        CompletePriceRequest(-1, _pendingNoMatchRequestId);
        return;
      }

      if (_pendingNoOfferingsTimeoutAt > 0 && now >= _pendingNoOfferingsTimeoutAt)
      {
        Svc.Log.Debug("No market board offerings received before timeout");
        CompletePriceRequest(-1, UnknownRequestId);
      }
    }

    private void ItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
    {
      _newRequest = true;

      // Consume the caller's declaration into this request and reset it, so an expectation left behind by
      // a compare that never opened cannot silently govern somebody else's later request.
      _inFlightItemId = _expectedItemId;
      _matchQuality = _expectedQuality;
      ClearExpectedPriceRequest();

      _useHq = Plugin.Configuration.HQ && _itemHq;
      ClearPendingNoMatch();
      _pendingNoOfferingsTimeoutAt = Environment.TickCount64 + GetMarketBoardResultTimeoutMs();
    }

    private unsafe void AddonRetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
      string nodeText = ((AddonRetainerSell*)args.Addon.Address)->ItemName->NodeText.ToString();
      _itemHq = nodeText.Contains('\uE03C');
    }

    public void PopulateRetainerCache()
    {
      bool changed = false;
      var retainerManager = RetainerManager.Instance();

      for (uint i = 0; i < retainerManager->GetRetainerCount(); ++i)
      {
        if (!Plugin.Configuration.SeenRetainers.Contains(retainerManager->GetRetainerBySortedIndex(i)->RetainerId))
        {
          Plugin.Configuration.SeenRetainers.Add(retainerManager->GetRetainerBySortedIndex(i)->RetainerId);
          changed = true;
        }
        
      }

      if (changed)
        Plugin.Configuration.Save();
    }

    private bool ItemCanBeHq(uint itemId) => _items.Single(j => j.RowId == itemId).CanBeHq;

    private static bool IsOwnRetainer(ulong retainerId) => Plugin.Configuration.SeenRetainers.Contains(retainerId);

    private static int GetMarketBoardResultTimeoutMs() => Math.Max(Plugin.Configuration.MarketBoardKeepOpenMS, 1000);

    private void CompletePriceRequest(int price, int requestId)
    {
      ClearPendingNoMatch();
      ClearPendingNoOfferings();
      if (requestId >= 0)
        _lastRequestId = requestId;
      _newRequest = false;
      NewPrice = price;
    }

    private void ClearPendingNoMatch()
    {
      _pendingNoMatchRequestId = UnknownRequestId;
      _pendingNoMatchTimeoutAt = 0;
    }

    private void ClearPendingNoOfferings()
    {
      _pendingNoOfferingsTimeoutAt = 0;
    }
  }
}
