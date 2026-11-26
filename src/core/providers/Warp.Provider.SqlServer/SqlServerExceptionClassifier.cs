using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Warp.Core.Data;

namespace Warp.Provider.SqlServer;

internal sealed class SqlServerExceptionClassifier : IDatabaseExceptionClassifier
{
    // SQL Server error numbers 2627 (unique key violation) and 2601 (duplicate key in unique
    // index) both mean "row already exists". Any other DbUpdateException surfaces.
    public bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        return ex.InnerException is SqlException { Number: 2627 or 2601 };
    }

    public bool IsTransientDeadlock(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException { Number: 1205 })
            {
                return true;
            }
        }

        return false;
    }

    // SqlException carries no SQLSTATE, so Warp's generic rules cannot tell "could not connect" from "the server
    // refused the statement": decided here by error number. Connection-level failures (-2 timeout, -1/2/53/40/121
    // network, 233/10053/10054/10060/10061/11001 transport, 4060 database not available yet), the managed SNI's
    // connect-phase errors (Linux and macOS: 35 "an internal exception was caught" around the socket failure, 26
    // "error locating server/instance"), and Azure SQL's "not currently available" family. Login failures (18456)
    // and permission errors are deliberately absent.
    public bool IsConnectionFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql.Number is -2 or -1 or 2 or 26 or 35 or 40 or 53 or 121 or 233 or 4060 or 10053 or 10054 or 10060 or 10061 or 11001
                    or 40143 or 40197 or 40501 or 40613 or 42108 or 42109;
            }
        }

        return false;
    }
}
