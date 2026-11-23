using DbExplorer.Core.Model;

namespace DbExplorer.Core.Groups;

/// <summary>The schema of one group member (one database).</summary>
public record SchemaMember(string Database, IReadOnlyList<TableInfo> Schema);

/// <summary>One column-level difference. <see cref="Kind"/> is "Missing", "Extra" or "TypeDiffers".</summary>
public record ColumnDiff(string Table, string Column, string Kind, string? Expected, string? Actual);

/// <summary>All differences of one member against the union of the group.</summary>
public record SchemaDiff(
    string Database,
    IReadOnlyList<string> MissingTables,
    IReadOnlyList<string> ExtraTables,
    IReadOnlyList<ColumnDiff> ColumnDiffs);

/// <summary>Pure comparison of the schemas of several databases. The reference is the union of the members.</summary>
public static class SchemaComparer
{
    public const string ObjectKindColumn = "(object kind)";

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<SchemaDiff> Compare(IReadOnlyList<SchemaMember> members)
    {
        // Per member: table key -> table (first wins on duplicates).
        var perMember = members
            .Select(m => m.Schema
                .GroupBy(t => t.Ref.ToString(), Cmp)
                .ToDictionary(g => g.Key, g => g.First(), Cmp))
            .ToList();

        // Union of table names (first-seen spelling), sorted for stable output.
        var tableNames = new Dictionary<string, string>(Cmp);
        foreach (var tables in perMember)
            foreach (var (key, t) in tables)
                tableNames.TryAdd(key, t.Ref.ToString());

        var result = new List<SchemaDiff>();
        for (var i = 0; i < members.Count; i++)
        {
            if (members.Count < 2)
            {
                result.Add(new SchemaDiff(members[i].Database, [], [], []));
                continue;
            }
            var missing = new List<string>();
            var extra = new List<string>();
            var columnDiffs = new List<ColumnDiff>();

            foreach (var key in tableNames.Keys.OrderBy(k => k, Cmp))
            {
                var holders = Enumerable.Range(0, members.Count).Where(j => perMember[j].ContainsKey(key)).ToList();
                var has = perMember[i].TryGetValue(key, out var mine);
                if (!has)
                {
                    missing.Add(tableNames[key]);
                    continue;
                }
                if (holders.Count == 1)
                {
                    extra.Add(mine!.Ref.ToString());
                    continue;
                }
                CompareTable(mine!, key, i, holders, perMember, columnDiffs);
            }

            result.Add(new SchemaDiff(members[i].Database, missing, extra, columnDiffs));
        }
        return result;
    }

    private static void CompareTable(
        TableInfo mine, string tableKey, int self, List<int> holders,
        List<Dictionary<string, TableInfo>> perMember, List<ColumnDiff> output)
    {
        var tableName = mine.Ref.ToString();
        var tables = holders.Select(j => perMember[j][tableKey]).ToList();

        // View vs table.
        var kinds = tables.Select(t => t.IsView ? "view" : "table").ToList();
        var commonKind = MostCommon(kinds);
        var myKind = mine.IsView ? "view" : "table";
        if (!Cmp.Equals(myKind, commonKind))
            output.Add(new ColumnDiff(tableName, ObjectKindColumn, "TypeDiffers", commonKind, myKind));

        // Columns: per holder, name -> column (first wins on duplicates).
        var columnSets = tables
            .Select(t => t.Columns.GroupBy(c => c.Name, Cmp).ToDictionary(g => g.Key, g => g.First(), Cmp))
            .ToList();
        var mineColumns = columnSets[holders.IndexOf(self)];

        var columnNames = new Dictionary<string, string>(Cmp);
        foreach (var set in columnSets)
            foreach (var (key, c) in set)
                columnNames.TryAdd(key, c.Name);

        foreach (var key in columnNames.Keys.OrderBy(k => k, Cmp))
        {
            var holderTypes = columnSets.Where(s => s.ContainsKey(key)).Select(s => s[key].DataType).ToList();
            var common = MostCommon(holderTypes);

            if (!mineColumns.TryGetValue(key, out var col))
            {
                output.Add(new ColumnDiff(tableName, columnNames[key], "Missing", common, null));
            }
            else if (holderTypes.Count == 1)
            {
                output.Add(new ColumnDiff(tableName, col.Name, "Extra", null, col.DataType));
            }
            else if (!Cmp.Equals(col.DataType, common))
            {
                output.Add(new ColumnDiff(tableName, col.Name, "TypeDiffers", common, col.DataType));
            }
        }
    }

    /// <summary>Most frequent value (case-insensitive); ties go to the earliest in the list. Returns the first spelling.</summary>
    private static string MostCommon(IReadOnlyList<string> values)
    {
        string best = values[0];
        var bestCount = 0;
        foreach (var v in values)
        {
            var count = values.Count(x => Cmp.Equals(x, v));
            if (count > bestCount) { best = v; bestCount = count; }
        }
        return best;
    }
}
