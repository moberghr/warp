namespace Warp.Test.Shared.Entities;

/// <summary>A catalog product. Stock is decremented as orders ship; the low-stock background service
/// watches it and the sales report reads prices.</summary>
public class Product
{
    public string Sku { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Stock { get; set; }

    public decimal Price { get; set; }
}
