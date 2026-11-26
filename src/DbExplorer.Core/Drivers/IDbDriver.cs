using System.Data.Common;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Drivers;

/// <summary>Hides dialect differences between database engines.</summary>
public interface IDbDriver
{
    DbKind Kind { get; }

    /// <summary>Prefix of command parameter names (e.g. "@").</summary>
    string ParamPrefix { get; }

    /// <summary>Quotes a single identifier, escaping embedded quote characters.</summary>
    string Quote(string identifier);

    string QuoteTable(TableRef t);

    /// <summary>Expression casting an already-quoted column to text.</summary>
    string CastToText(string quotedColumn);

    /// <summary>
    /// Escapes LIKE wildcards in <paramref name="text"/> with a backslash. The LIKE clause must declare
    /// <c>ESCAPE '\'</c>.
    /// </summary>
    string EscapeLike(string text);

    /// <summary>
    /// Wraps <paramref name="selectSql"/> with ordering and paging. <paramref name="orderBy"/> is the column
    /// list WITHOUT the ORDER BY keyword; the driver emits ORDER BY itself (always for SQL Server, only when
    /// <paramref name="orderBy"/> is non-empty for the other engines). Offset and limit are inlined as
    /// validated integers.
    /// </summary>
    string PageSql(string selectSql, string orderBy, int offset, int limit);

    /// <summary>Creates an unopened connection to the database described by <paramref name="cfg"/>.</summary>
    DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword);

    Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct);

    // ---- views and routines, read on demand (the defaults describe an engine that has none) ----

    /// <summary>True when the engine has stored procedures and functions.</summary>
    bool HasRoutines => false;

    Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RoutineInfo>>([]);

    /// <summary>
    /// The source text of a view. Throws <see cref="DefinitionUnavailableException"/> when the database gives none.
    /// </summary>
    Task<string> LoadViewDefinitionAsync(DbConnection c, TableRef view, CancellationToken ct) =>
        throw new DefinitionUnavailableException($"The definition of {view} cannot be read on this engine.");

    /// <summary>As <see cref="LoadViewDefinitionAsync"/>, for a procedure or function.</summary>
    Task<string> LoadRoutineDefinitionAsync(DbConnection c, RoutineInfo routine, CancellationToken ct) =>
        throw new DefinitionUnavailableException($"The definition of {routine.Ref} cannot be read on this engine.");
}
