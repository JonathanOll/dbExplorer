using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Groups;

namespace DbExplorer.App.ViewModels;

/// <summary>A member that could not be loaded; the report says why instead of failing.</summary>
public sealed record UnreachableMember(string Database, string Error);

/// <summary>
/// One line of the report. <see cref="Severity"/> ("error", "warn" or "info") picks the colour of the chip, like the
/// banners: missing = error, a different type = warn, extra = info. <see cref="Table"/> and <see cref="Column"/> place it in the
/// table-by-table view (<see cref="Column"/> is null for a whole table).
/// </summary>
public sealed record DiffFinding(string Label, string Severity, string Subject, string? Detail, string Table = "", string? Column = null)
{
    public bool IsError => Severity == "error";
    public bool IsWarn => Severity == "warn";
    public bool IsInfo => Severity == "info";

    /// <summary>The column name, or an empty text for a finding about a whole table.</summary>
    public string ColumnText => Column ?? "";

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>The findings of one table in one database: the table name once, then its lines.</summary>
public sealed record TableDiffGroup(string Table, IReadOnlyList<DiffFinding> Findings);

/// <summary>What differs in one database compared to the rest of the group.</summary>
public sealed partial class MemberDiffViewModel : ObservableObject
{
    public MemberDiffViewModel(SchemaDiff diff)
    {
        Database = diff.Database;
        var findings = new List<DiffFinding>();
        findings.AddRange(diff.MissingTables.Select(t => new DiffFinding("Missing table", "error", t, null, t)));
        findings.AddRange(diff.ExtraTables.Select(t => new DiffFinding("Extra table", "info", t, null, t)));
        findings.AddRange(diff.ColumnDiffs.Select(Describe));
        Findings = findings;
        Tables = [.. findings.GroupBy(f => f.Table, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TableDiffGroup(g.Key, [.. g.OrderBy(f => f.Column is null ? 0 : 1).ThenBy(f => f.Column, StringComparer.OrdinalIgnoreCase)]))];
        Summary = findings.Count == 0 ? "identical to the others" : Plural(findings.Count, "difference");
        _isExpanded = true;
    }

    public string Database { get; }
    public IReadOnlyList<DiffFinding> Findings { get; }

    /// <summary>The same findings, grouped by table and sorted: what the card lists.</summary>
    public IReadOnlyList<TableDiffGroup> Tables { get; }

    public string Summary { get; }
    public bool IsClean => Findings.Count == 0;

    /// <summary>The card shows its table list (a long report starts with all but the first cards folded).</summary>
    [ObservableProperty]
    private bool _isExpanded;

    private static DiffFinding Describe(ColumnDiff d)
    {
        var isKind = d.Column == SchemaComparer.ObjectKindColumn;
        var subject = isKind ? d.Table : $"{d.Table}.{d.Column}";
        var column = isKind ? "(object kind)" : d.Column;
        return d.Kind switch
        {
            "Missing" => new DiffFinding("Missing column", "error", subject, d.Expected is null ? null : $"in the others: {d.Expected}", d.Table, column),
            "Extra" => new DiffFinding("Extra column", "info", subject, d.Actual is null ? null : $"only here: {d.Actual}", d.Table, column),
            _ when isKind => new DiffFinding("Object kind", "warn", subject, $"{d.Actual} here, {d.Expected} elsewhere", d.Table, column),
            _ => new DiffFinding("Type differs", "warn", subject, $"{d.Actual} here, {d.Expected} elsewhere", d.Table, column),
        };
    }

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>The schema-difference report of a group, shown by <see cref="IDialogService.ShowSchemaDiffAsync"/>.</summary>
public sealed class SchemaDiffViewModel
{
    public SchemaDiffViewModel(string groupName, IReadOnlyList<SchemaDiff> diffs, IReadOnlyList<UnreachableMember> unreachable)
    {
        GroupName = groupName;
        Members = diffs.Select(d => new MemberDiffViewModel(d)).ToList();
        Unreachable = unreachable;

        // A long report opens with the first card unfolded and the rest folded: the overview first, the detail on demand.
        DifferingMembers = [.. Members.Where(m => !m.IsClean)];
        for (var i = 0; i < DifferingMembers.Count; i++) DifferingMembers[i].IsExpanded = DifferingMembers.Count <= 3 || i == 0;

        var all = Members.SelectMany(m => m.Findings).ToList();
        MissingCount = all.Count(f => f.IsError);
        ExtraCount = all.Count(f => f.IsInfo);
        DifferentCount = all.Count(f => f.IsWarn);
    }

    public string GroupName { get; }
    public string Title => $"Schema differences · {GroupName}";

    /// <summary>One entry per database that was loaded, in group order.</summary>
    public IReadOnlyList<MemberDiffViewModel> Members { get; }

    /// <summary>The databases that differ from the rest, in group order (the report lists these as cards).</summary>
    public IReadOnlyList<MemberDiffViewModel> DifferingMembers { get; }

    /// <summary>The databases that match the rest, folded into one line: <c>Identical to the others: A, B</c>; null when none.</summary>
    public string? IdenticalText
    {
        get
        {
            var clean = Members.Where(m => m.IsClean).Select(m => m.Database).ToList();
            return CanCompare && clean.Count > 0 ? $"Identical to the others: {string.Join(", ", clean)}" : null;
        }
    }

    public IReadOnlyList<UnreachableMember> Unreachable { get; }
    public bool HasUnreachable => Unreachable.Count > 0;

    /// <summary>At least two databases were loaded, so there is something to compare.</summary>
    public bool CanCompare => Members.Count >= 2;

    /// <summary>Not enough databases loaded: the report shows why instead of an empty list.</summary>
    public bool IsEmptyReport => !CanCompare;

    public bool HasDifferences => Members.Any(m => !m.IsClean);

    /// <summary>True when the databases were compared and match: the green "all good" banner.</summary>
    public bool IsIdentical => CanCompare && !HasDifferences;

    /// <summary>The cards and the folded line show: there was something to compare and something differs.</summary>
    public bool ShowCards => CanCompare && HasDifferences;

    public int MissingCount { get; }
    public int ExtraCount { get; }
    public int DifferentCount { get; }

    public string MissingText => $"{MissingCount} missing";
    public string ExtraText => $"{ExtraCount} extra";
    public string DifferentText => $"{DifferentCount} type {(DifferentCount == 1 ? "difference" : "differences")}";

    public string Summary
    {
        get
        {
            if (!CanCompare) return "Fewer than two databases could be loaded: nothing to compare.";
            var differing = Members.Count(m => !m.IsClean);
            return differing == 0
                ? $"The {Members.Count} databases have the same tables and columns."
                : $"{differing} of {Members.Count} databases differ from the rest of the group.";
        }
    }

    /// <summary>The report as plain text (what "Copy report" puts on the clipboard).</summary>
    public string ReportText
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(Title).AppendLine(Summary);
            foreach (var member in DifferingMembers)
            {
                text.AppendLine().AppendLine($"{member.Database} ({member.Summary})");
                foreach (var table in member.Tables)
                    foreach (var f in table.Findings)
                        text.AppendLine($"  {f.Label}: {f.Subject}{(f.HasDetail ? "  " + f.Detail : "")}");
            }
            if (IdenticalText is { } identical) text.AppendLine().AppendLine(identical);
            foreach (var u in Unreachable) text.AppendLine().AppendLine($"Not loaded: {u.Database} - {u.Error}");
            return text.ToString().TrimEnd();
        }
    }
}
