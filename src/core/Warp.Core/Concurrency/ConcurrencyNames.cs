namespace Warp.Core.Concurrency;

/// <summary>The one rule for a concurrency key's name, shared by the admin manager and the pipeline's resolver.</summary>
internal static class ConcurrencyNames
{
    public const int MaxLength = 200;

    public static void Validate(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Name must be non-empty.", nameof(name));
        }

        if (name.Length > MaxLength)
        {
            throw new ArgumentException($"Name must be at most {MaxLength} characters.", nameof(name));
        }
    }
}
