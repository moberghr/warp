using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// <see cref="IErrorGroupCommandService"/> over the user's <typeparamref name="TContext"/> (§8.29). Loads the
/// tracked <see cref="ErrorGroup"/>, sets the operator status + <c>StatusChangedAt</c>, and saves. A structured
/// <c>LogInformation</c> audit records the operator action. No mutex — a low-contention admin action where an
/// optimistic load-set-save is sufficient.
/// </summary>
public sealed class ErrorGroupCommandService<TContext> : IErrorGroupCommandService
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ErrorGroupCommandService<TContext>> _logger;

    public ErrorGroupCommandService(TContext context, TimeProvider timeProvider, ILogger<ErrorGroupCommandService<TContext>> logger)
    {
        _context = context;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> SetStatus(string fingerprint, ErrorGroupStatus status, CancellationToken ct)
    {
        var group = await _context.Set<ErrorGroup>()
            .Where(x => x.Fingerprint == fingerprint)
            .FirstOrDefaultAsync(ct);

        if (group is null)
        {
            return false;
        }

        group.Status = status;
        group.StatusChangedAt = _timeProvider.GetUtcNow().UtcDateTime;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Error group {Fingerprint} set to {Status}", fingerprint, status);

        return true;
    }
}
