using Microsoft.EntityFrameworkCore;
using Warp.Core.Data.Entities;

namespace Warp.Core;

// Records a detected deploy (§8.23): the first instance of an application to register with a version not seen
// before for that application + environment. Called once per process at instance registration —
// WarpServerRegistration for servers, ApplicationHeartbeatHost for non-server processes — never on the worker path.
// Insert-if-absent: an existence probe skips the common case (another instance of this version already started), and
// the unique (Application, Environment, Version) index settles the race between two instances of a NEW version
// registering at once — the loser's duplicate-key failure is re-checked and read as "already recorded". The re-check
// is provider-agnostic on purpose: a non-server AddWarp process may run without a provider's exception classifier.
internal static class ApplicationDeployRecorder
{
    // Returns true when this call inserted the deploy row; false when the feature is off for this process
    // (no ApplicationName or no ApplicationVersion) or the version was already recorded. Saves on the given context,
    // which must carry no other pending changes, and clears its change tracker after a refused insert.
    // beforeInsert is a test-only gate between the probe and the insert, so a test can pin two registrations in the
    // race window with BarrierSignal (§4.7).
    internal static async Task<bool> RecordAsync(
        DbContext context,
        WarpConfiguration configuration,
        Guid instanceId,
        string? instanceName,
        DateTime now,
        CancellationToken ct,
        Func<CancellationToken, Task>? beforeInsert = null)
    {
        if (configuration.ApplicationName is not { } application
            || string.IsNullOrWhiteSpace(configuration.ApplicationVersion))
        {
            return false;
        }

        var version = configuration.ApplicationVersion;
        var environment = configuration.ApplicationEnvironment ?? string.Empty;

        if (await ExistsAsync(context, application, environment, version, ct))
        {
            return false;
        }

        if (beforeInsert is not null)
        {
            await beforeInsert(ct);
        }

        context.Set<ApplicationDeploy>().Add(new ApplicationDeploy
        {
            Application = application,
            Version = version,
            Environment = environment,
            FirstSeenAt = now,
            InstanceId = instanceId,
            InstanceName = instanceName,
        });

        try
        {
            await context.SaveChangesAsync(ct);

            return true;
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            if (await ExistsAsync(context, application, environment, version, ct))
            {
                return false;
            }

            throw;
        }
    }

    private static Task<bool> ExistsAsync(DbContext context, string application, string environment, string version, CancellationToken ct)
    {
        return context.Set<ApplicationDeploy>()
            .AsNoTracking()
            .Where(x => x.Application == application)
            .Where(x => x.Environment == environment)
            .Where(x => x.Version == version)
            .AnyAsync(ct);
    }
}
