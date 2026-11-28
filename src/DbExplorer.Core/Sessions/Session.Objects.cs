using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Sessions;

// Views and routines: read on demand (never while connecting), off the caller's thread, each on its own connection.
public partial class Session
{
    private readonly SemaphoreSlim _routineGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>The routines once <see cref="LoadRoutinesAsync"/> has read them; null before, and after a schema refresh.</summary>
    public IReadOnlyList<RoutineInfo>? Routines { get; private set; }

    /// <summary>Every procedure and function of the database, read once and kept until the schema is refreshed.</summary>
    public async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(CancellationToken ct)
    {
        if (Routines is { } known) return known;
        await _routineGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Routines ?? (Routines = await ReadAsync(Driver.LoadRoutinesAsync, ct).ConfigureAwait(false));
        }
        finally
        {
            try { _routineGate.Release(); }
            catch (ObjectDisposedException) { /* the session closed during the read: nobody waits on the gate any more */ }
        }
    }

    /// <summary>
    /// Reads the tables, columns and keys again from the database (what the ER diagram reloads). Catalog queries only;
    /// <see cref="Schema"/>, which the navigator shows, is left as it is.
    /// </summary>
    public Task<IReadOnlyList<TableInfo>> ReadSchemaAsync(CancellationToken ct) => ReadAsync(Driver.LoadSchemaAsync, ct);

    /// <summary>The source text of a view. Nothing is executed.</summary>
    public Task<string> LoadViewDefinitionAsync(TableRef view, CancellationToken ct) =>
        ReadAsync((c, token) => Driver.LoadViewDefinitionAsync(c, view, token), ct);

    /// <summary>The source text of a procedure or function. Nothing is executed.</summary>
    public Task<string> LoadRoutineDefinitionAsync(RoutineInfo routine, CancellationToken ct) =>
        ReadAsync((c, token) => Driver.LoadRoutineDefinitionAsync(c, routine, token), ct);

    // Cancelled by the caller or by the session closing. A provider may report a cancel with its own exception type.
    private async Task<T> ReadAsync<T>(Func<DbConnection, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        try
        {
            return await Task.Run(async () =>
            {
                await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
                return await read(connection, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        }
        catch (Exception e) when (token.IsCancellationRequested && e is not OperationCanceledException)
        {
            throw new OperationCanceledException("The read was cancelled.", e, token);
        }
    }
}
