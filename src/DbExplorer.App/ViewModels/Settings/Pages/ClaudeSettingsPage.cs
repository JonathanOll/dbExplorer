using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>What the Claude Code page reads and stores.</summary>
/// <param name="Set">Stores the choices (applied at once); false when they could not be saved.</param>
/// <param name="AddCommand">The command that adds the server to the user's own Claude Code sessions.</param>
public sealed record ClaudeSettingsAccess(
    Func<ClaudeSettings> Get,
    Func<IReadOnlyList<ConnectionConfig>> Connections,
    Func<ClaudeSettings, bool> Set,
    ClaudeActivityLog Log,
    string AddCommand);

/// <summary>One option of the access list of a connection.</summary>
public sealed record ClaudeAccessOption(ClaudeAccess Access, string Label);

/// <summary>A connection and what Claude Code may do on it. A production connection cannot be allowed.</summary>
public sealed class ClaudeConnectionRow(ClaudeSettingsPage page, ConnectionConfig connection) : ObservableObject
{
    public static IReadOnlyList<ClaudeAccessOption> Choices { get; } =
    [
        new(ClaudeAccess.None, "Not allowed"),
        new(ClaudeAccess.Schema, "Schema only"),
        new(ClaudeAccess.SchemaAndData, "Schema and data"),
    ];

    public IReadOnlyList<ClaudeAccessOption> Options => Choices;
    public string Name => connection.Name;
    public DbEnvironment Environment => connection.Environment;

    public string Detail => connection.IsProduction ? $"{connection.Kind} · production connections are never available"
        : ClaudePolicy.CannotConnectUnattended(connection) is not null ? $"{connection.Kind} · interactive sign-in: Claude Code cannot connect"
        : connection.Kind.ToString();

    public bool CanChoose => page.IsEnabled && !connection.IsProduction;

    /// <summary>Why the list is disabled; null when it is not.</summary>
    public string? DisabledReason => connection.IsProduction ? "Production connections are never available to Claude Code."
        : page.IsEnabled ? null : "Turn the integration on first.";

    public string AccessName => $"Claude Code access to {connection.Name}";

    public ClaudeAccessOption Selected
    {
        get => Choices.First(c => c.Access == (connection.IsProduction ? ClaudeAccess.None : page.AccessOf(connection.Id)));
        set
        {
            if (value is null || !CanChoose || value.Access == page.AccessOf(connection.Id)) return;
            page.Allow(connection.Id, value.Access);
            OnPropertyChanged();
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(DisabledReason));
    }
}

/// <summary>One line of the activity list.</summary>
public sealed record ClaudeActivityRow(ClaudeActivity Entry)
{
    public string Time => Entry.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Tool => Entry.Tool;
    public string Connection => Entry.Connection ?? "";
    public bool Failed => Entry.Outcome != "ok";
    public string Outcome => Entry.Outcome;
    public string Detail => (Entry.Detail ?? "").ReplaceLineEndings(" ");

    public bool HasDetail => Detail.Length > 0;

    public string Tip => string.Join("\n", new[] { Entry.Detail, Entry.Message }.Where(t => !string.IsNullOrEmpty(t)));
}

/// <summary>
/// The Claude Code integration: the master switch (off by default), the connections it may use, each with schema only or
/// schema and data, and the log of what it asked.
/// </summary>
public sealed partial class ClaudeSettingsPage : SettingsPageViewModel
{
    internal const int ShownEntries = 100;
    private const string Keywords = "claude code mcp ai assistant anthropic integration access activity log privacy";

    private readonly SettingsContext _ctx;
    private readonly ClaudeSettingsAccess _access;

    public ClaudeSettingsPage(SettingsContext ctx, ClaudeSettingsAccess access)
        : base("claude", "Claude Code", "IconConsole",
            "Lets Claude Code explore the databases you choose, and propose SQL in dbExplorer. It can never write.")
    {
        _ctx = ctx;
        _access = access;
        Connections = access.Connections().Select(c => new ClaudeConnectionRow(this, c)).ToList();
        LoadActivity();
    }

    public override bool IsGeneric => false;
    public override bool CanReset => true;

    public string PrivacyNotice =>
        "What Claude Code reads is sent to Anthropic. Schema only shares table, column and object names and the source of views " +
        "and routines (which can hold sensitive text). Schema and data also sends the rows of the queries it runs, and the error " +
        "messages of your script tabs. Access is limited to what the database login can read: use a login with only the rights you " +
        "want to share.";

    public bool IsEnabled
    {
        get => _access.Get().Enabled;
        set
        {
            if (value == IsEnabled) return;
            _access.Set(_access.Get() with { Enabled = value });
            Refresh();
        }
    }

    public IReadOnlyList<ClaudeConnectionRow> Connections { get; }
    public bool HasNoConnections => Connections.Count == 0;

    public string AddCommand => _access.AddCommand;

    [ObservableProperty] private IReadOnlyList<ClaudeActivityRow> _activity = [];
    [ObservableProperty] private bool _hasNoActivity = true;

    internal ClaudeAccess AccessOf(Guid id) => _access.Get().Of(id);

    internal void Allow(Guid id, ClaudeAccess level)
    {
        var current = _access.Get();
        var map = new Dictionary<Guid, ClaudeAccess>(current.Connections);
        if (level == ClaudeAccess.None) map.Remove(id);
        else map[id] = level;
        _access.Set(current with { Connections = map });
    }

    [RelayCommand]
    private void LoadActivity()
    {
        try { Activity = _access.Log.ReadLatest(ShownEntries).Select(e => new ClaudeActivityRow(e)).ToList(); }
        catch (Exception) { Activity = []; }   // locked or unreadable: Refresh tries again
        HasNoActivity = Activity.Count == 0;
    }

    [RelayCommand]
    private void ClearActivity()
    {
        try { _access.Log.Clear(); }
        catch (Exception) { /* locked: the reload shows what is left */ }
        LoadActivity();
    }

    [RelayCommand]
    private Task CopyAddCommandAsync() => _ctx.CopyText?.Invoke(AddCommand) ?? Task.CompletedTask;

    public override bool MatchesQuery(string query) =>
        string.IsNullOrWhiteSpace(query) || Keywords.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"Reset to defaults": off, no connection allowed.</summary>
    public override void ResetToDefaults()
    {
        _access.Set(new ClaudeSettings());
        Refresh();
    }

    public override void Refresh()
    {
        OnPropertyChanged(nameof(IsEnabled));
        foreach (var row in Connections ?? []) row.Refresh();
    }
}
