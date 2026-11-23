using DbExplorer.Core.Groups;

namespace DbExplorer.App.ViewModels;

/// <summary>A member that could not be loaded; the report says why instead of failing.</summary>
public sealed record UnreachableMember(string Database, string Error);

/// <summary>
/// One line of the report. <see cref="Severity"/> ("error", "warn" or "info") picks the colour of the chip, like the
/// banners: missing = error, a different type = warn, extra = info.
/// </summary>
public sealed record DiffFinding(string Label, string Severity, string Subject, string? Detail)
{
    public bool IsError => Severity == "error";
    public bool IsWarn => Severity == "warn";
    public bool IsInfo => Severity == "info";
}

/// <summary>What differs in one database compared to the rest of the group.</summary>
public sealed class MemberDiffViewModel
{
    public MemberDiffViewModel(SchemaDiff diff)
    {
        Database = diff.Database;
        var findings = new List<DiffFinding>();
        findings.AddRange(diff.MissingTables.Select(t => new DiffFinding("Missing table", "error", t, null)));
        findings.AddRange(diff.ExtraTables.Select(t => new DiffFinding("Extra table", "info", t, null)));
        findings.AddRange(diff.ColumnDiffs.Select(Describe));
        Findings = findings;
        Summary = findings.Count == 0 ? "identical to the others" : Plural(findings.Count, "difference");
    }

    public string Database { get; }
    public IReadOnlyList<DiffFinding> Findings { get; }
    public string Summary { get; }
    public bool IsClean => Findings.Count == 0;

    private static DiffFinding Describe(ColumnDiff d)
    {
        var isKind = d.Column == SchemaComparer.ObjectKindColumn;
        var subject = isKind ? d.Table : $"{d.Table}.{d.Column}";
        return d.Kind switch
        {
            "Missing" => new DiffFinding("Missing column", "error", subject, d.Expected is null ? null : $"elsewhere: {d.Expected}"),
            "Extra" => new DiffFinding("Extra column", "info", subject, d.Actual is null ? null : $"only here: {d.Actual}"),
            _ when isKind => new DiffFinding("Object kind", "warn", subject, $"{d.Actual} here, {d.Expected} elsewhere"),
            _ => new DiffFinding("Type differs", "warn", subject, $"{d.Actual} here, {d.Expected} elsewhere"),
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
    }

    public string GroupName { get; }
    public string Title => $"Schema differences · {GroupName}";

    /// <summary>One entry per database that was loaded, in group order.</summary>
    public IReadOnlyList<MemberDiffViewModel> Members { get; }

    public IReadOnlyList<UnreachableMember> Unreachable { get; }
    public bool HasUnreachable => Unreachable.Count > 0;

    /// <summary>At least two databases were loaded, so there is something to compare.</summary>
    public bool CanCompare => Members.Count >= 2;

    public bool HasDifferences => Members.Any(m => !m.IsClean);

    /// <summary>True when the databases were compared and match: the green "all good" banner.</summary>
    public bool IsIdentical => CanCompare && !HasDifferences;

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
}
