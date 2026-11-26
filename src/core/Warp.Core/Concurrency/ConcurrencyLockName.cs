namespace Warp.Core.Concurrency;

// The distributed lock name a concurrency key takes at a given effective limit.
// A limit of 1 and a limit above 1 use different prefixes so they can never meet. On PostgreSQL the semaphore
// provider takes a limit-1 name as one advisory lock and an N-slot name as `{name}:0..{name}:{N-1}`; with
// one shared prefix, `[Mutex("tenantA:0")]` locked `warp:concurrency:tenantA:0`, which is slot 0 of
// `[Semaphore("tenantA", N)]`, so a caller who controls a mutex key could take a semaphore's slot. On SQL
// Server both used one semaphore name with different counts. A slot name always starts
// `warp:concurrency:`, which a `warp:concurrency-mutex:` name never does.
internal static class ConcurrencyLockName
{
    public static string For(string key, int limit)
        => limit == 1 ? $"warp:concurrency-mutex:{key}" : $"warp:concurrency:{key}";
}
