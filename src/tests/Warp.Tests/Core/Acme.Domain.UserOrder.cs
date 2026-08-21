namespace Acme.Domain;

// Lives in a non-Warp namespace on purpose — the UTC ValueConverter convention must NOT
// apply to user-owned entities sharing the DbContext (see WarpStorageTypes).
internal sealed class UserOrder
{
    public int Id { get; set; }

    public DateTime PlacedAt { get; set; }

    public DateTime? ShippedAt { get; set; }
}
