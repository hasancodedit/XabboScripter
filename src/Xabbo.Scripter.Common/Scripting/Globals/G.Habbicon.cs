using System;

using Xabbo.Interceptor;

namespace Xabbo.Scripter.Scripting;

public partial class G
{
    /// <summary>
    /// Triggers (uses) the specified habbicon as a reaction in the current room.
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void TriggerHabbicon(int habbiconId) => Interceptor.Send(Out["TriggerHabbicon"], habbiconId);

    private int _habbiconSendSequence;

    /// <summary>
    /// Sends the specified habbicon to a friend in the messenger.
    /// </summary>
    /// <param name="friendId">The ID of the friend to send the habbicon to.</param>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void SendHabbicon(long friendId, int habbiconId)
        => Interceptor.Send(Out["SendHabbicon"], (int)friendId, habbiconId, _habbiconSendSequence++);

    /// <summary>
    /// Requests the habbicon shop's catalog data (collections and their habbicons/prices).
    /// Listen for the raw <c>HabbiconShopData</c> message to receive the response.
    /// </summary>
    public void GetHabbiconShopData() => Interceptor.Send(Out["GetHabbiconShopData"]);

    /// <summary>
    /// Requests info on the specified habbicon.
    /// Listen for the raw <c>HabbiconInfo</c> message to receive the response.
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void GetHabbiconInfo(int habbiconId) => Interceptor.Send(Out["GetHabbiconInfo"], habbiconId);

    /// <summary>
    /// Purchases the specified habbicon.
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void BuyHabbicon(int habbiconId) => Interceptor.Send(Out["BuyHabbicon"], habbiconId);

    /// <summary>
    /// Purchases the specified habbicon collection.
    /// </summary>
    /// <param name="collectionId">The collection's ID.</param>
    public void BuyHabbiconCollection(int collectionId) => Interceptor.Send(Out["BuyHabbiconCollection"], collectionId);

    /// <summary>
    /// Claims the specified habbicon (e.g. a reward that has been unlocked but not yet claimed).
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void ClaimHabbicon(int habbiconId) => Interceptor.Send(Out["ClaimHabbicon"], habbiconId);

    /// <summary>
    /// Marks the specified habbicon as a favourite.
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void FavoriteHabbicon(int habbiconId) => Interceptor.Send(Out["FavoriteHabbicon"], habbiconId);

    /// <summary>
    /// Unmarks the specified habbicon as a favourite.
    /// </summary>
    /// <param name="habbiconId">The habbicon's ID.</param>
    public void UnfavoriteHabbicon(int habbiconId) => Interceptor.Send(Out["UnfavoriteHabbicon"], habbiconId);
}
