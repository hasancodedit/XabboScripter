using System;
using System.Collections.Generic;

using Xabbo.Core;
using Xabbo.Core.GameData;
using Xabbo.Core.Tasks;

namespace Xabbo.Scripter.Scripting;

public partial class G
{
    /// <summary>
    /// Gets the user's own marketplace offers.
    /// </summary>
    /// <param name="timeout">The time in milliseconds to wait for a response from the server.</param>
    public IUserMarketplaceOffers GetUserMarketplaceOffers(int timeout = DEFAULT_TIMEOUT)
        => new GetUserMarketplaceOffersTask(Interceptor).Execute(timeout, Ct);

    /// <summary>
    /// Searches for open offers in the marketplace.
    /// </summary>
    /// <param name="searchText">The name of the item to search for.</param>
    /// <param name="from">The minimum offer price in credits.</param>
    /// <param name="to">The maximum offer price in credits.</param>
    /// <param name="sort">The order in which to sort the results.</param>
    /// <param name="combineLtds">Whether to combine LTD (limited edition) offers of the same item into a single entry.</param>
    /// <param name="timeout">The time in milliseconds to wait for a response from the server.</param>
    /// <returns>The list of matching marketplace offers.</returns>
    public IEnumerable<IMarketplaceOffer> SearchMarketplace(
        string? searchText = null, int? from = null, int? to = null,
        MarketplaceSortOrder sort = MarketplaceSortOrder.HighestPrice,
        bool combineLtds = false,
        int timeout = DEFAULT_TIMEOUT)
        => new SearchMarketplaceTask(Interceptor, searchText, from, to, sort, combineLtds).Execute(timeout, Ct);

    /// <summary>
    /// Gets the marketplace information for the specified item type and kind.
    /// </summary>
    public IMarketplaceItemInfo GetMarketplaceInfo(ItemType type, int kind, int timeout = DEFAULT_TIMEOUT)
        => new GetMarketplaceInfoTask(Interceptor, type, kind).Execute(timeout, Ct);

    /// <summary>
    /// Gets the marketplace information for the specified item.
    /// </summary>
    public IMarketplaceItemInfo GetMarketplaceInfo(IItem item, int timeout = DEFAULT_TIMEOUT)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new GetMarketplaceInfoTask(Interceptor, item.Type, item.Kind).Execute(timeout, Ct);
    }

    /// <summary>
    /// Gets the marketplace information for the specified furni.
    /// </summary>
    public IMarketplaceItemInfo GetMarketplaceInfo(FurniInfo furniInfo, int timeout = DEFAULT_TIMEOUT)
        => new GetMarketplaceInfoTask(Interceptor, furniInfo.Type, furniInfo.Kind).Execute(timeout, Ct);

    /// <summary>
    /// Gets the server's current marketplace rules and limits (commission, price bounds, expiration hours, etc).
    /// </summary>
    public IMarketplaceConfiguration GetMarketplaceConfiguration(int timeout = DEFAULT_TIMEOUT)
        => new GetMarketplaceConfigurationTask(Interceptor).Execute(timeout, Ct);

    /// <summary>
    /// Checks whether the user is currently able to make a new marketplace offer,
    /// and how many marketplace tokens they have available.
    /// </summary>
    public (int ResultCode, int TokenCount) GetMarketplaceCanMakeOffer(int timeout = DEFAULT_TIMEOUT)
        => new GetMarketplaceCanMakeOfferTask(Interceptor).Execute(timeout, Ct);

    /// <summary>
    /// Lists one or more inventory items of the same kind on the marketplace as a single offer.
    /// </summary>
    /// <param name="type">The type of the items (Floor or Wall).</param>
    /// <param name="price">The asking price in credits.</param>
    /// <param name="itemIds">The inventory item IDs (<see cref="IInventoryItem.ItemId"/>) to include in the offer.</param>
    /// <returns>The server's result code for the listing attempt.</returns>
    public int MakeMarketplaceOffer(ItemType type, int price, IEnumerable<long> itemIds, int timeout = DEFAULT_TIMEOUT)
        => new MakeMarketplaceOfferTask(Interceptor, type, price, itemIds).Execute(timeout, Ct);

    /// <summary>
    /// Lists a single inventory item on the marketplace.
    /// </summary>
    public int MakeMarketplaceOffer(IInventoryItem item, int price, int timeout = DEFAULT_TIMEOUT)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Note: item.Id, not item.ItemId - ItemId comes back negative for floor items
        // (a sign convention used elsewhere for the shared room item-id space), which
        // the marketplace composer rejects. Id is the raw positive item reference it expects.
        return new MakeMarketplaceOfferTask(Interceptor, item.Type, price, new[] { item.Id }).Execute(timeout, Ct);
    }

    /// <summary>
    /// Buys the specified marketplace offer.
    /// </summary>
    public IMarketplaceBuyOfferResult BuyMarketplaceOffer(int offerId, int timeout = DEFAULT_TIMEOUT)
        => new BuyMarketplaceOfferTask(Interceptor, offerId).Execute(timeout, Ct);

    /// <inheritdoc cref="BuyMarketplaceOffer(int, int)" />
    public IMarketplaceBuyOfferResult BuyMarketplaceOffer(IMarketplaceOffer offer, int timeout = DEFAULT_TIMEOUT)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new BuyMarketplaceOfferTask(Interceptor, (int)offer.Id).Execute(timeout, Ct);
    }

    /// <summary>
    /// Cancels the specified marketplace offer.
    /// </summary>
    public IMarketplaceCancelOfferResult CancelMarketplaceOffer(int offerId, int timeout = DEFAULT_TIMEOUT)
        => new CancelMarketplaceOfferTask(Interceptor, offerId).Execute(timeout, Ct);

    /// <inheritdoc cref="CancelMarketplaceOffer(int, int)" />
    public IMarketplaceCancelOfferResult CancelMarketplaceOffer(IMarketplaceOffer offer, int timeout = DEFAULT_TIMEOUT)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return new CancelMarketplaceOfferTask(Interceptor, (int)offer.Id).Execute(timeout, Ct);
    }

    /// <summary>
    /// Cancels all of the user's open marketplace offers.
    /// </summary>
    public IMarketplaceCancelAllOffersResult CancelAllMarketplaceOffers(int timeout = DEFAULT_TIMEOUT)
        => new CancelAllMarketplaceOffersTask(Interceptor).Execute(timeout, Ct);

    /// <summary>
    /// Requests that any credits waiting from sold marketplace offers be redeemed to the user's balance.
    /// Fire-and-forget; poll <see cref="GetUserMarketplaceOffers"/>'s <c>CreditsWaiting</c> or the wallet
    /// balance afterward to confirm.
    /// </summary>
    public void RedeemMarketplaceOfferCredits() => Interceptor.Send(Out.MarketplaceRedeemOfferCredits);
}
