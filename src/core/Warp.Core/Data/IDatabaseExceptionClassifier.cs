using Microsoft.EntityFrameworkCore;

namespace Warp.Core.Data;

/// <summary>
/// Classifies provider-specific database exceptions into Core-recognisable categories.
/// Registered by provider packages (Warp.PostgreSql, Warp.SqlServer) via their
/// <c>UsePostgreSql</c>/<c>UseSqlServer</c> builder extensions. Core uses the classifier
/// to keep provider types out of its own dependency graph.
/// </summary>
public interface IDatabaseExceptionClassifier
{
    /// <summary>
    /// True if the given <see cref="DbUpdateException"/> is a unique / primary-key constraint
    /// violation (PG SQLSTATE 23505, SQL Server error 2627/2601).
    /// </summary>
    bool IsUniqueConstraintViolation(DbUpdateException ex);

    /// <summary>
    /// True if the given exception (anywhere in its <see cref="Exception.InnerException"/> chain)
    /// is a transient deadlock or serialization conflict that the caller can resolve by retrying
    /// the transaction: SQL Server error 1205 (deadlock victim), PG SQLSTATE 40P01
    /// (deadlock_detected) or 40001 (serialization_failure). Walks the chain because EF Core
    /// wraps provider exceptions in <see cref="DbUpdateException"/> for SaveChanges-paths but
    /// not for <c>BeginTransactionAsync</c> / <c>CommitAsync</c>.
    /// </summary>
    bool IsTransientDeadlock(Exception ex);

    /// <summary>
    /// True if the given exception (anywhere in its chain) means the database could not be reached: a connection
    /// that could not be opened or was lost, a server not accepting connections yet, a database that does not exist
    /// yet. Not an error the database reported about a statement, and not authentication or permission. Read by
    /// <see cref="WarpStartupGate"/> for <see cref="WarpConfiguration.RequireDatabaseAtStartup"/>, on top of its own
    /// provider-agnostic rules (sockets, timeouts, transient provider exceptions, connection-class SQLSTATEs).
    /// Default <c>false</c>, so a custom provider keeps compiling and gets those rules only.
    /// </summary>
    /// <param name="ex">The exception a startup step threw.</param>
    /// <returns>Whether the step should be retried once the database is reachable.</returns>
    bool IsConnectionFailure(Exception ex) => false;
}
