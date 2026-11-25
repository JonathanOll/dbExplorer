using System.Data;
using System.Data.Common;

namespace DbExplorer.Core.Querying;

/// <summary>An UPDATE or DELETE matched no row: it was changed or deleted since the user loaded it.</summary>
public sealed class StaleRowException(PlannedStatement stmt)
    : Exception("No row matches the key of the edited row (it was deleted or its key changed since it was loaded); nothing was saved.")
{
    public PlannedStatement Statement { get; } = stmt;
}

/// <summary>An UPDATE or DELETE planned from one row matched several rows (duplicate rows, non-unique or case-insensitive key).</summary>
public sealed class AmbiguousRowException(PlannedStatement stmt, int affected)
    : Exception($"The key of the edited row matched {affected} rows; nothing was written. Define a more specific key.")
{
    public PlannedStatement Statement { get; } = stmt;
    public int Affected { get; } = affected;
}

/// <summary>
/// An UPDATE or DELETE planned from one row reported a negative rows-affected count (SQL Server with <c>SET NOCOUNT ON</c>):
/// whether exactly one row was touched cannot be verified, so the save is rolled back.
/// </summary>
public sealed class RowCountUnknownException(PlannedStatement stmt)
    : Exception("The server does not report rows affected (SET NOCOUNT ON?), so a single-row change cannot be verified; nothing was saved.")
{
    public PlannedStatement Statement { get; } = stmt;
}

public static class EditApplier
{
    /// <summary>
    /// Executes <paramref name="stmts"/> in one transaction, in order. If an UPDATE/DELETE affects 0 rows everything is
    /// rolled back and <see cref="StaleRowException"/> is thrown; if it affects more than 1,
    /// <see cref="AmbiguousRowException"/>; any other failure (including cancellation) also
    /// rolls back. Only <see cref="PlannedStatement.Command"/> is executed, never the preview.
    /// The caller owns <paramref name="c"/>; it is opened if closed but not closed afterwards.
    /// </summary>
    public static async Task ApplyAsync(DbConnection c, IReadOnlyList<PlannedStatement> stmts, CancellationToken ct)
    {
        if (stmts.Count == 0) return;
        if (c.State != ConnectionState.Open) await c.OpenAsync(ct);

        await using var tx = await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var stmt in stmts)
            {
                ct.ThrowIfCancellationRequested();
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = stmt.Command.Sql;
                CommandTimeouts.Unbounded(cmd);   // a lock wait ends when the lock frees or the user cancels
                foreach (var p in stmt.Command.Params)
                    cmd.Parameters.Add(ParameterTyping.Create(cmd, p));

                var affected = await cmd.ExecuteNonQueryAsync(ct);
                if (affected == 0) throw new StaleRowException(stmt);
                if (stmt.ExpectsSingleRow)
                {
                    // Fail closed: a negative count (NOCOUNT ON) must not pass as "one row".
                    if (affected < 0) throw new RowCountUnknownException(stmt);
                    if (affected > 1) throw new AmbiguousRowException(stmt, affected);
                }
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await RollbackQuietlyAsync(tx);
            throw;
        }
    }

    private static async Task RollbackQuietlyAsync(DbTransaction tx)
    {
        try { await tx.RollbackAsync(CancellationToken.None); }
        catch { /* the original failure is the one worth reporting */ }
    }
}
