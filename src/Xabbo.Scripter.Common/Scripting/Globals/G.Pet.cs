using System;

using Xabbo.Core;

namespace Xabbo.Scripter.Scripting;

public partial class G
{
    /// <summary>
    /// Places the specified pet from the pet inventory into the room.
    /// </summary>
    public void PlacePet(IInventoryPet pet, Point location)
    {
        ArgumentNullException.ThrowIfNull(pet);
        PlacePet(pet.Id, location);
    }

    /// <summary>
    /// Places the pet with the specified inventory ID into the room.
    /// </summary>
    public void PlacePet(long petInventoryId, Point location)
        => Interceptor.Send(Out.PlacePetToFlat, (int)petInventoryId, location.X, location.Y);

    /// <summary>
    /// Picks up the specified placed pet.
    /// </summary>
    public void PickupPet(IPet pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        PickupPet(pet.Id);
    }

    /// <summary>
    /// Picks up the placed pet with the specified room entity ID.
    /// </summary>
    public void PickupPet(long petId) => Interceptor.Send(Out.RemovePetFromFlat, (int)petId);

    /// <summary>
    /// Moves the specified placed pet to a new tile.
    /// </summary>
    public void MovePet(IPet pet, Point location, int direction = 0)
    {
        ArgumentNullException.ThrowIfNull(pet);
        MovePet(pet.Id, location, direction);
    }

    /// <summary>
    /// Moves the placed pet with the specified room entity ID to a new tile.
    /// </summary>
    public void MovePet(long petId, Point location, int direction = 0)
        => Interceptor.Send(Out.MovePetInFlat, (int)petId, location.X, location.Y, direction);

    /// <summary>
    /// Toggles mounting/riding the specified pet.
    /// </summary>
    public void MountPet(IPet pet, bool mount = true)
    {
        ArgumentNullException.ThrowIfNull(pet);
        MountPet(pet.Id, mount);
    }

    /// <summary>
    /// Toggles mounting/riding the pet with the specified room entity ID.
    /// </summary>
    public void MountPet(long petId, bool mount = true) => Interceptor.Send(Out.MountPet, (int)petId, mount);

    /// <summary>
    /// Removes the saddle from the specified pet (dismounts and unequips the saddle).
    /// </summary>
    public void RemoveSaddleFromPet(IPet pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        RemoveSaddleFromPet(pet.Id);
    }

    /// <inheritdoc cref="RemoveSaddleFromPet(IPet)" />
    public void RemoveSaddleFromPet(long petId) => Interceptor.Send(Out.RemoveSaddle, (int)petId);

    /// <summary>
    /// Gives respect to the specified pet.
    /// </summary>
    public void RespectPet(IPet pet)
    {
        ArgumentNullException.ThrowIfNull(pet);
        RespectPet(pet.Id);
    }

    /// <inheritdoc cref="RespectPet(IPet)" />
    public void RespectPet(long petId) => Interceptor.Send(Out.RespectPet, (int)petId);

    /// <summary>
    /// Gives a supplement/treat item (that the user is currently holding) to the specified pet.
    /// </summary>
    public void GiveSupplementToPet(IPet pet, int supplementItemId)
    {
        ArgumentNullException.ThrowIfNull(pet);
        GiveSupplementToPet(pet.Id, supplementItemId);
    }

    /// <inheritdoc cref="GiveSupplementToPet(IPet, int)" />
    public void GiveSupplementToPet(long petId, int supplementItemId) => Interceptor.Send(Out.GiveSupplementToPet, (int)petId, supplementItemId);
}
