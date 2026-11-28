using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Claude.Assist;

public enum ClaudeTask { ExplainError, OptimiseQuery, GenerateSql, AnalysePlan, ReviewUpgrade }

/// <summary>A script of an upgrade, as read from the dbupdate folder.</summary>
public sealed record ClaudeScript(string Name, string Text);

/// <summary>What a question is about, before anything is hidden.</summary>
public sealed record ClaudeQuestion(ClaudeTask Task, DbKind Kind)
{
    public string? Sql { get; init; }
    public string? Error { get; init; }

    /// <summary>What the user asks for, in their words (sent as typed).</summary>
    public string? Description { get; init; }

    public IReadOnlyList<(PlanStatement Statement, PlanMode Mode)> Plans { get; init; } = [];
    public IReadOnlyList<StatsItem> Statistics { get; init; } = [];
    public IReadOnlyList<TableInfo> Schema { get; init; } = [];
    public IReadOnlyList<ClaudeScript> Scripts { get; init; } = [];
}

/// <summary>
/// Exactly what is sent for one question: <see cref="Prompt"/> goes to the standard input of claude, after the system
/// prompt <see cref="ClaudeHeadless.SystemPrompt"/>. On a schema-only connection no literal, comment, plan predicate or
/// unrecognised error text is in it, and rows are never part of any question.
/// </summary>
public sealed record ClaudePayload(ClaudeTask Task, ClaudeAccess Access, string Prompt, IReadOnlyList<string> Contents, ClaudeRedactor? Redactor)
{
    public const int MaxLength = 150_000;

    /// <summary>The fence marker of the one block that may replace the user's SQL.</summary>
    public const string Replacement = "sql replacement";

    private const int SchemaBudget = 60_000;

    /// <param name="nonce">Ends the data tags, so that no data can close them; random unless a test gives one.</param>
    /// <exception cref="ClaudeRefusedException">Nothing may be sent, or the question is too large.</exception>
    public static ClaudePayload Build(ClaudeQuestion question, ClaudeAccess access, string? nonce = null)
    {
        if (access is not (ClaudeAccess.Schema or ClaudeAccess.SchemaAndData))
            throw new ClaudeRefusedException("This connection is not allowed for Claude Code.");
        nonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var values = access == ClaudeAccess.SchemaAndData;
        var redactor = values ? null : new ClaudeRedactor(question.Kind,
            question.Schema.SelectMany(t => t.Columns.Select(c => c.Name).Append(t.Ref.Name).Append(t.Ref.Schema ?? "")));
        string Sql(string text) => redactor?.Redact(text) ?? text;

        var prompt = new StringBuilder();
        var contents = new List<string>();
        void Data(string name, string text, string what)
        {
            prompt.Append('\n').Append($"<data-{nonce} name=\"{name}\">\n").Append(text.TrimEnd()).Append('\n').Append($"</data-{nonce}>\n");
            if (!contents.Contains(what)) contents.Add(what);
        }

        prompt.Append("Database engine: ").Append(Engine(question.Kind)).Append('\n').Append(Instructions(question.Task)).Append('\n');
        var hidden = values ? "" : ", literals and comments hidden";

        var sent = question.Sql is { } source ? Sql(source) : "";
        if (question.Task == ClaudeTask.GenerateSql)
            Data("request", question.Description ?? "", "Your description, as typed");
        if (sent.Length > 0) Data("sql", sent, "The SQL" + hidden);
        if (question.Error is { } error)
            Data("error", values ? error : ClaudeErrorFilter.Filter(error, sent),
                values ? "The error message" : "The recognised parts of the error message, values hidden");
        foreach (var (statement, mode) in question.Plans)
            Data("plan", ClaudePlanSummary.Write(statement, mode, redactor),
                values ? "The execution plan, with its predicates" : "The operators, rows and costs of the execution plan, without predicates or values");
        if (question.Statistics.Count > 0 && Statistics(question.Statistics) is { Length: > 0 } statistics)
            Data("statistics", statistics, "STATISTICS IO and TIME counters per table");
        foreach (var script in question.Scripts)
            Data("script " + script.Name.Replace('"', ' '), Sql(script.Text), $"{question.Scripts.Count} upgrade script(s){hidden}");

        var about = $"{question.Sql} {question.Description}";
        var (schema, listed) = SchemaText(question, about);
        if (schema.Length > 0)
            Data("schema", schema, $"Names, columns, types, keys and indexes of {listed} table(s) and view(s)");

        if (prompt.Length > MaxLength)
            throw new ClaudeRefusedException(string.Create(CultureInfo.InvariantCulture,
                $"This is too large to send ({prompt.Length:N0} characters, the limit is {MaxLength:N0}). Select less."));
        contents.Add("No rows of any table");
        return new ClaudePayload(question.Task, access, prompt.ToString(), contents, redactor);
    }

    private static string Engine(DbKind kind) => kind switch
    {
        DbKind.SqlServer => "Microsoft SQL Server (T-SQL)",
        DbKind.Postgres => "PostgreSQL",
        DbKind.MySql => "MySQL or MariaDB",
        _ => "SQLite",
    };

    private static string Instructions(ClaudeTask task) => task switch
    {
        ClaudeTask.ExplainError =>
            "Explain why this statement failed and how to fix it. If a change of the SQL fixes it, give the whole corrected "
            + $"text, ready to replace the SQL given, in one fenced block opened with ```{Replacement}",
        ClaudeTask.OptimiseQuery =>
            "Optimise this SQL without changing what it returns. Explain the changes. If you rewrite it, give the whole new "
            + $"text, ready to replace the SQL given, in one fenced block opened with ```{Replacement}. Put index suggestions "
            + "as DDL in separate fenced blocks opened with ```sql: they are only shown to the user.",
        ClaudeTask.GenerateSql =>
            "Write the SQL for the request, using only the objects of the schema. Give it in one fenced block opened with "
            + $"```{Replacement}, then say in a few lines what it does and what you assumed.",
        ClaudeTask.AnalysePlan =>
            "Analyse this execution plan. Explain the costly operators and why they cost that much, then suggest indexes as "
            + "DDL in fenced ```sql blocks, with their trade-offs. Nothing you write is run.",
        _ =>
            "Review these database upgrade scripts before they run. Report, script by script: the locks they take and who "
            + "they block, operations likely to be long on big tables, statements that fail or do damage when run a second "
            + "time, and data loss risks. Order by severity. Say when a script has nothing to report.",
    };

    private static string Statistics(IReadOnlyList<StatsItem> items)
    {
        var text = new StringBuilder();
        // Counters only: any other server message may hold printed values.
        foreach (var item in items)
        {
            if (item is IoStats io)
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{io.Table}: scans {io.ScanCount}, logical reads {io.LogicalReads}, physical reads {io.PhysicalReads}, read-ahead {io.ReadAheadReads}, lob logical reads {io.LobLogicalReads}");
            else if (item is TimeStats time)
                text.AppendLine(CultureInfo.InvariantCulture, $"{time.Phase}: CPU {time.CpuMs} ms, elapsed {time.ElapsedMs} ms");
        }
        return text.ToString();
    }

    // The tables the question names; for a request in words that names none, every table. Full descriptions while they fit,
    // then names only.
    private static (string Text, int Count) SchemaText(ClaudeQuestion question, string about)
    {
        if (question.Schema.Count == 0) return ("", 0);
        var words = new HashSet<string>(about.Split(about.Where(c => !char.IsLetterOrDigit(c) && c != '_').Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        if (question.Scripts.Count > 0)
            foreach (var script in question.Scripts)
                words.UnionWith(script.Text.Split([' ', '\t', '\r', '\n', '.', '(', ')', ',', ';', '[', ']', '"', '`'], StringSplitOptions.RemoveEmptyEntries));

        bool Named(TableInfo t) => words.Contains(t.Ref.Name) || words.Contains(t.Ref.Name + "s")
                                   || t.Ref.Name.EndsWith('s') && words.Contains(t.Ref.Name[..^1]);
        var named = question.Schema.Where(Named).ToList();
        var generate = question.Task == ClaudeTask.GenerateSql;
        var described = generate ? named.Concat(question.Schema.Except(named)).ToList() : named;

        var text = new StringBuilder();
        var count = 0;
        foreach (var table in described)
        {
            var one = Describe(table);
            if (text.Length + one.Length > SchemaBudget) break;
            text.Append(one);
            count++;
        }
        if (generate && count < described.Count)
        {
            text.AppendLine("Other tables (names only):");
            foreach (var table in described.Skip(count))
            {
                if (text.Length > SchemaBudget + 20_000) { text.AppendLine("(more not listed)"); break; }
                text.AppendLine(table.Ref.ToString());
                count++;
            }
        }
        return (text.ToString(), count);
    }

    private static string Describe(TableInfo table)
    {
        var text = new StringBuilder();
        text.Append(table.IsView ? "view " : "table ").Append(table.Ref).Append('\n');
        foreach (var c in table.Columns)
            text.Append("  ").Append(c.Name).Append(' ').Append(c.DataType).Append(c.IsPrimaryKey ? " primary key" : "")
                .Append(c.Nullable ? " null" : " not null").Append('\n');
        foreach (var f in table.ForeignKeys)
            text.Append("  foreign key (").AppendJoin(", ", f.Columns).Append(") references ").Append(f.RefTable)
                .Append(" (").AppendJoin(", ", f.RefColumns).Append(")\n");
        foreach (var i in table.Indexes)
            text.Append("  ").Append(i.Unique ? "unique index " : "index ").Append(i.Name).Append(" (").AppendJoin(", ", i.Columns).Append(")\n");
        return text.ToString();
    }
}
