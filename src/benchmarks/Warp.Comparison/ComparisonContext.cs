using Microsoft.EntityFrameworkCore;

namespace Warp.Comparison;

/// <summary>The application's DbContext, as a host would have one. It declares nothing; <c>AddWarp</c> adds Warp's tables to it.</summary>
public sealed class ComparisonContext(DbContextOptions<ComparisonContext> options) : DbContext(options);
