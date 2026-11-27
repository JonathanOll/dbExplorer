using DbExplorer.Core.Metadata;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// What the object search can do besides opening a table. Without it only tables and views are listed.
/// </summary>
/// <param name="OpenRoutine">Opens the definition of a procedure or function.</param>
/// <param name="OpenColumn">Opens a table positioned on a column.</param>
/// <param name="LoadRoutines">Reads the routines of a session that does not have them yet; null: only those already read.</param>
public sealed record QuickOpenObjects(
    Func<Session, RoutineInfo, Task> OpenRoutine,
    Func<Session, TableRef, string, Task> OpenColumn,
    Func<Session, Task<IReadOnlyList<RoutineInfo>>>? LoadRoutines = null);

// The database searches: one connection (live schema) or all of them (metadata cache).
public sealed partial class QuickOpenViewModel
{
    /// <summary>Done when the routines that were not read yet have been added to the list.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Tables and views of the LIVE schema of an open session, labelled <c>schema.table</c>; with
    /// <paramref name="objects"/>, its procedures, functions and columns too. With <paramref name="elsewhere"/>, a query
    /// naming another connection searches the cached objects of the others as well.
    /// </summary>
    public static QuickOpenViewModel ForTables(Session session, Func<Session, TableRef, Task> openTable, StatusModel? status = null,
        string initialQuery = "", QuickOpenObjects? objects = null, QuickOpenElsewhere? elsewhere = null)
    {
        var name = session.Config.Name;
        var schema = session.Schema;
        var here = QuickOpenScope.For(session.Config, elsewhere?.Groups, isActive: true);
        var items = schema.Select(t => TableItem(t.Ref, t.IsView, t.Ref.ToString(), () => openTable(session, t.Ref), name, null)
            with { Scope = here }).ToList();
        Lazy<QuickOpenColumn[]>? columns = null;
        if (objects is not null)
        {
            if (session.Routines is { } known) items.AddRange(RoutineItems(session, known, objects, here));
            columns = new Lazy<QuickOpenColumn[]>(() => schema.SelectMany(t =>
            {
                var owner = new QuickOpenColumnOwner(t.Ref.ToString(), null, name, column => objects.OpenColumn(session, t.Ref, column))
                    { Scope = here };
                return t.Columns.Select(c => new QuickOpenColumn(owner, c.Name));
            }).ToArray());
        }

        var title = objects is null ? $"Go to table in {name}" : $"Go to table or object in {name}";
        if (session.Config.Environment != DbEnvironment.None) title += $" ({session.Config.Environment.Label()})";
        var vm = new QuickOpenViewModel(title, items, status, initialQuery: initialQuery);
        if (columns is null && elsewhere is null) return vm;

        var source = new QuickOpenSource(vm._source.Items, columns);
        if (elsewhere is not null) source.Extension = Extension(here, session.Config.Id, elsewhere, openTable, status, objects);
        vm.Use(source);
        vm.Rank();
        if (objects is not null && session.Routines is null && session.Driver.HasRoutines && objects.LoadRoutines is { } load)
            vm.Loading = AppendRoutinesAsync(source, session, load, objects, here);
        return vm;
    }

    /// <summary>
    /// Objects of every connection that has a metadata cache, labelled <c>connection · schema.name</c>. Reads the
    /// cache only; confirming a result calls <paramref name="connect"/> (which may return null after reporting its
    /// own failure) and then opens the object, unless it vanished from the database.
    /// </summary>
    public static QuickOpenViewModel ForAllConnections(
        MetadataCache cache, IEnumerable<ConnectionConfig> configs,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, TableRef, Task> openTable, StatusModel? status = null,
        QuickOpenObjects? objects = null, IReadOnlyList<ConnectionGroup>? groups = null)
    {
        var rows = CachedRows(cache, configs, groups, connect, openTable, status, objects);
        var notice = rows.Uncached.Count switch
        {
            0 => null,
            1 => "1 connection has no cache yet — open it once",
            var n => $"{n} connections have no cache yet — open them once",
        };
        var title = objects is null ? "Go to table in any connection" : "Go to table or object in any connection";
        var vm = new QuickOpenViewModel(title, rows.Items, status, notice);
        if (objects is null) return vm;

        vm.Use(new QuickOpenSource(rows.Items, rows.Columns));
        vm.Rank();
        return vm;
    }

    private static QuickOpenExtension Extension(
        QuickOpenScope here, Guid id, QuickOpenElsewhere elsewhere, Func<Session, TableRef, Task> openTable, StatusModel? status,
        QuickOpenObjects? objects)
    {
        var others = elsewhere.Configs.Where(c => c.Id != id).ToList();
        return new QuickOpenExtension(here, others.Select(c => QuickOpenScope.For(c, elsewhere.Groups)).ToList(), () =>
        {
            var rows = CachedRows(elsewhere.Cache, others, elsewhere.Groups, elsewhere.Connect, openTable, status, objects);
            _ = rows.Columns.Value; // listed here too, off the UI thread
            return rows;
        });
    }

    private static QuickOpenElsewhereRows CachedRows(
        MetadataCache cache, IEnumerable<ConnectionConfig> configs, IReadOnlyList<ConnectionGroup>? groups,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, TableRef, Task> openTable, StatusModel? status,
        QuickOpenObjects? objects)
    {
        var items = new List<QuickOpenItem>();
        var cached = new List<(ConnectionConfig Config, QuickOpenScope Scope, IReadOnlyList<CachedTable> Tables)>();
        var uncached = new List<QuickOpenScope>();
        foreach (var config in configs)
        {
            var scope = QuickOpenScope.For(config, groups);
            var tables = cache.Load(config.Id);
            var routines = objects is null ? [] : cache.LoadRoutines(config.Id);
            if (tables.Count == 0 && routines.Count == 0)
            {
                uncached.Add(scope);
                continue;
            }

            cached.Add((config, scope, tables));
            foreach (var table in tables)
            {
                var (cfg, tableRef) = (config, table.Ref);
                items.Add(TableItem(tableRef, table.IsView, $"{cfg.Name} · {tableRef}",
                        () => ConnectThenOpen(cfg, tableRef, connect, openTable, status), cfg.Name, scope.Badge)
                    with { Environment = cfg.Environment, Scope = scope });
            }

            foreach (var routine in routines)
            {
                var (cfg, wanted) = (config, routine);
                items.Add(RoutineItem(wanted, $"{cfg.Name} · {wanted.Ref}",
                        () => ConnectThenOpenRoutine(cfg, wanted, connect, objects!, status), cfg.Name, scope.Badge)
                    with { Environment = cfg.Environment, Scope = scope });
            }
        }

        var columns = new Lazy<QuickOpenColumn[]>(() => objects is null ? [] : cached.SelectMany(x => x.Tables.SelectMany(t =>
        {
            var owner = new QuickOpenColumnOwner(t.Ref.ToString(), x.Scope.Badge, x.Config.Name,
                column => ConnectThenOpen(x.Config, t.Ref, connect, (s, table) => objects.OpenColumn(s, table, column), status, column))
                { Environment = x.Config.Environment, Scope = x.Scope };
            return t.Columns.Select(c => new QuickOpenColumn(owner, c));
        })).ToArray());
        return new QuickOpenElsewhereRows([.. items], columns, uncached);
    }

    private static QuickOpenItem TableItem(
        TableRef table, bool isView, string label, Func<Task> open, string group, string? context) =>
        new(label, isView ? "view" : null, open)
        {
            Group = group,
            Title = context is null ? null : table.ToString(),
            Context = context,
            Name = table.Name,
            Icon = isView ? "IconView" : "IconTable",
            Type = isView ? QuickOpenTypes.Views : QuickOpenTypes.Tables,
        };

    private static QuickOpenItem RoutineItem(
        RoutineInfo routine, string label, Func<Task> open, string group, string? context)
    {
        var procedure = routine.Kind == RoutineKind.Procedure;
        var kind = procedure ? "procedure" : "function";
        return new QuickOpenItem(label, routine.Arguments is null ? kind : $"{kind} ({routine.Arguments})", open)
        {
            Group = group,
            Title = context is null ? null : routine.Ref.ToString(),
            Context = context,
            Name = routine.Ref.Name,
            Icon = procedure ? "IconProcedure" : "IconFunction",
            Type = procedure ? QuickOpenTypes.Procedures : QuickOpenTypes.Functions,
        };
    }

    private static IEnumerable<QuickOpenItem> RoutineItems(
        Session session, IEnumerable<RoutineInfo> routines, QuickOpenObjects objects, QuickOpenScope scope) =>
        routines.Select(r => RoutineItem(r, r.Ref.ToString(), () => objects.OpenRoutine(session, r), session.Config.Name, null)
            with { Scope = scope });

    private static async Task AppendRoutinesAsync(
        QuickOpenSource source, Session session, Func<Session, Task<IReadOnlyList<RoutineInfo>>> load, QuickOpenObjects objects,
        QuickOpenScope scope)
    {
        try
        {
            source.Append(RoutineItems(session, await load(session), objects, scope));
        }
        catch (Exception)
        {
            // The search goes on without them; the navigator reports a read that fails when the connection opens.
        }
    }

    private static async Task ConnectThenOpen(
        ConnectionConfig config, TableRef table,
        Func<ConnectionConfig, Task<Session?>> connect, Func<Session, TableRef, Task> openTable, StatusModel? status,
        string? column = null)
    {
        var session = await connect(config);
        if (session is null) return; // the connect step already told the user why

        // The cache can be older than the database.
        var live = session.Schema.FirstOrDefault(t => t.Ref == table);
        if (live is null)
        {
            status?.Error($"Table {table} no longer exists in {config.Name} (the schema changed since it was cached).");
            return;
        }

        if (column is not null && !live.Columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase)))
        {
            status?.Error($"Column {table}.{column} no longer exists in {config.Name} (the schema changed since it was cached).");
            return;
        }

        await openTable(session, table);
    }

    // Matched on the live list: a cached identifier (PostgreSQL oid) changes when the routine is dropped and created again.
    private static async Task ConnectThenOpenRoutine(
        ConnectionConfig config, RoutineInfo wanted,
        Func<ConnectionConfig, Task<Session?>> connect, QuickOpenObjects objects, StatusModel? status)
    {
        var session = await connect(config);
        if (session is null) return;

        var routines = await session.LoadRoutinesAsync(CancellationToken.None);
        var live = routines.FirstOrDefault(r => r.Ref == wanted.Ref && r.Kind == wanted.Kind && r.Arguments == wanted.Arguments);
        if (live is null)
        {
            status?.Error($"{wanted.Ref} no longer exists in {config.Name} (the database changed since it was cached).");
            return;
        }

        await objects.OpenRoutine(session, live);
    }
}
