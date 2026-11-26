using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;

namespace DbExplorer.App.ViewModels;

/// <summary>Views, procedures and functions of a connected database, and opening their source text.</summary>
public partial class ConnectionTreeViewModel
{
    /// <summary>
    /// Shows a definition (session, object name, source text) in a new script tab; wired to
    /// <see cref="MainViewModel.OpenDefinition"/>. Nothing is executed.
    /// </summary>
    public Action<Session, string, string>? DefinitionOpener { get; set; }

    /// <summary>
    /// What sits under a schema (under the connection for SQLite): the Views group, the Procedures and Functions groups of
    /// engines that have routines, then the tables. Only table nodes are created here.
    /// </summary>
    private static IReadOnlyList<TreeNodeViewModel> SchemaChildren(
        ConnectionNodeViewModel node, Session session, string schema, IReadOnlyList<TableInfo> objects)
    {
        var result = new List<TreeNodeViewModel>();
        var views = objects.Where(t => t.IsView).ToList();
        if (views.Count > 0) result.Add(new ViewGroupNodeViewModel(views.Count, () => TableNodes(session, views)));
        if (session.Driver.HasRoutines)
        {
            foreach (var kind in new[] { ObjectGroupKind.Procedures, ObjectGroupKind.Functions })
            {
                var group = new RoutineGroupNodeViewModel(session, kind, schema, node.Tree?._status);
                group.Loaded += () =>
                {
                    if (node.Tree is { HasFilter: true } tree) tree.ApplyFilter();
                };
                result.Add(group);
            }
        }
        result.AddRange(TableNodes(session, objects.Where(t => !t.IsView)));
        return result;
    }

    private static SchemaNodeViewModel SchemaNode(
        ConnectionNodeViewModel node, Session session, string schema, IReadOnlyList<TableInfo> objects) =>
        new(schema, objects.Count(t => !t.IsView), () => SchemaChildren(node, session, schema, objects));

    /// <summary>
    /// Runs after the tables are shown, in the background: reads the procedures and functions once (they feed the quick open
    /// and its cache) and adds the schemas that hold only routines, which the table list does not know. Never throws.
    /// </summary>
    public async Task LoadRoutinesInBackgroundAsync(ConnectionNodeViewModel node, Session session)
    {
        if (!session.Driver.HasRoutines) return;
        var children = node.Children;
        try
        {
            var routines = await _sessions.LoadRoutinesAsync(session, CancellationToken.None);
            if (!ReferenceEquals(node.Children, children)) return;   // disconnected or refreshed meanwhile
            foreach (var schema in routines.Select(r => r.Ref.Schema ?? "").Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var shown = children.OfType<SchemaNodeViewModel>().ToList();
                if (shown.Any(s => string.Equals(s.Title, schema, StringComparison.OrdinalIgnoreCase))) continue;
                var index = shown.Count(s => string.Compare(s.Title, schema, StringComparison.OrdinalIgnoreCase) < 0);
                children.Insert(index, SchemaNode(node, session, schema, []));
            }
            if (HasFilter) ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // the connection was closed
        }
        catch (ObjectDisposedException)
        {
            // the connection was closed before the read started
        }
        catch (Exception e)
        {
            if (ReferenceEquals(node.Children, children))
                _status.Error($"Cannot list the procedures and functions of {node.Config.Name}: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>The routines of an open session (read once, written to the metadata cache).</summary>
    public Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(Session session) =>
        _sessions.LoadRoutinesAsync(session, CancellationToken.None);

    /// <summary>Remembers the object kinds switched off in the quick open.</summary>
    public bool SetQuickOpenHidden(QuickOpenTypes hidden)
    {
        Config.QuickOpenHidden = hidden;
        return SaveSetting();
    }

    /// <summary>
    /// Reads the source of a view or routine node (off the UI thread, cancellable from the status bar) and opens it in a
    /// script tab. Failures, including a definition the database does not give, end up in the status bar.
    /// </summary>
    [RelayCommand]
    public Task OpenDefinitionAsync(TreeNodeViewModel? node) => node switch
    {
        TableNodeViewModel { IsView: true } view =>
            OpenDefinitionAsync(view.Session, view.Table.Ref, ct => view.Session.LoadViewDefinitionAsync(view.Table.Ref, ct)),
        RoutineNodeViewModel routine =>
            OpenDefinitionAsync(routine.Session, routine.Routine.Ref, ct => routine.Session.LoadRoutineDefinitionAsync(routine.Routine, ct)),
        _ => Task.CompletedTask,
    };

    /// <summary>As above for an object known by name (quick open).</summary>
    public Task OpenRoutineDefinitionAsync(Session session, RoutineInfo routine) =>
        OpenDefinitionAsync(session, routine.Ref, ct => session.LoadRoutineDefinitionAsync(routine, ct));

    private async Task OpenDefinitionAsync(Session session, TableRef name, Func<CancellationToken, Task<string>> read)
    {
        if (DefinitionOpener is null) return;
        using var cts = new CancellationTokenSource();
        using var busy = _status.Busy($"Reading the definition of {name}…", () => cts.Cancel());
        try
        {
            var text = await read(cts.Token);
            DefinitionOpener(session, name.ToString(), text);
        }
        catch (OperationCanceledException)
        {
            _status.Info($"Reading the definition of {name} cancelled.");
        }
        catch (DefinitionUnavailableException e)
        {
            _status.Error(e.Message);
        }
        catch (Exception e)
        {
            _status.Error($"Cannot read the definition of {name}: {ErrorText.Describe(e)}");
        }
    }
}
