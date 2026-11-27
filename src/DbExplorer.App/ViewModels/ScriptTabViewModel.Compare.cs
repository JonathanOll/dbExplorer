using DbExplorer.Core.Comparing;

namespace DbExplorer.App.ViewModels;

public sealed partial class ScriptTabViewModel : ICompareHost
{
    /// <summary>"Select for compare" of the result grids; the shell sets it.</summary>
    public CompareService? Compare { get; set; }

    public CompareSnapshot CreateCompareSnapshot(IReadOnlyList<ResultColumn> columns, IReadOnlyList<object?[]> rows)
    {
        var script = FilePath is { } path ? Path.GetFileNameWithoutExtension(path) : BaseName;
        var members = GroupRun?.Tabs.Select(t => t.Member).OfType<StatementResultViewModel>() ?? [];
        string name = script, connection = Connection.Name;
        var truncated = false;
        if (Results.Concat(members).SelectMany(r => r.Sets.Select(s => (Result: r, Set: s)))
                .FirstOrDefault(p => ReferenceEquals(p.Set.Columns, columns)) is { Set: { } set, Result: { } result })
        {
            truncated = set.Truncated;
            var inGroup = !Results.Contains(result);
            // A group member's result is titled with its database; the others with the number of the statement.
            name = inGroup ? $"{script} · {result.Header}" : $"{script} · {result.Index}{(result.HasSeveralSets ? "." + set.Index : "")}";
            if (!inGroup && set.Source is { } source)
                connection = GroupHooks?.GetConfig().Connections.FirstOrDefault(c => c.Id == source.ConnectionId)?.Name ?? connection;
        }
        else if (GroupRun is { } run && ReferenceEquals(run.AllColumns, columns))
        {
            name = $"{script} · group";
            truncated = run.All?.Truncated == true;
        }

        var data = new ComparedData(
            columns.Select(c => new ComparedColumn(c.Name, c.TypeName, c.ClrType, c.IsPrimaryKey)).ToList(), rows.ToArray());
        return new CompareSnapshot(name, $"{name} ({connection})", Connection, data,
            truncated ? $"the result stopped at the row limit ({data.Rows.Count:N0} rows)." : null);
    }
}
