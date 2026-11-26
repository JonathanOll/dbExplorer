using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>One row of the palette. <see cref="Header"/> is the section caption drawn above the first row of a section.</summary>
public sealed record PaletteRow(AppCommand Command, IReadOnlyList<int> Matches, string? Header = null)
{
    public string Title => Command.Title;
    public string Category => Command.Category;

    /// <summary>False on the choices of a step: they all share the category of the command.</summary>
    public bool ShowCategory { get; init; } = true;
    public string Icon => Command.Icon;
    public string? Shortcut => Command.Shortcut;
    public bool HasShortcut => Command.Shortcut is not null;
    public bool IsEnabled => Command.IsEnabled;
    public bool HasChoices => Command.HasChoices;
    public bool HasHeader => Header is not null;

    /// <summary>The dim text after the title: why it cannot run, else its detail.</summary>
    public string? Note => Command.DisabledReason ?? Command.Detail;

    public bool HasNote => Note is not null;

    public string AutomationName => string.Join(", ", new[] { Title, Category, Note, Shortcut }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// State of the command palette: a query, the ranked commands and a selection. A command with choices opens a second step in
/// place (Backspace on an empty query goes back). With no query the list reads: recently used, then what can be done here
/// (the categories of the current context first), then what cannot, with the reason. Never throws: a failing command is
/// reported in the status bar.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    public const string RecentHeader = "Recently used";
    public const string AvailableHeader = "Commands";
    public const string UnavailableHeader = "Not available here";

    /// <summary>Recent commands shown on top of the unfiltered list.</summary>
    public const int RecentShown = 5;

    private const int RecentBonus = 30;

    /// <summary>One level of the palette: its commands, and for a step the command that opened it.</summary>
    private sealed record Step(IReadOnlyList<AppCommand> Commands, AppCommand? Parent, string Query = "", int Selected = -1);

    private readonly List<Step> _back = [];
    private readonly IReadOnlyList<string> _recent;
    private readonly IReadOnlyList<string> _context;
    private readonly Action<string>? _used;
    private readonly StatusModel? _status;
    private Step _step;
    private bool _closed;
    private string _query = "";
    private IReadOnlyList<PaletteRow> _rows = [];
    private int _selectedIndex = -1;

    /// <param name="recent">Ids of the commands used last, newest first.</param>
    /// <param name="contextCategories">Categories that fit the current tab: listed first.</param>
    /// <param name="used">Told the id of each command that runs.</param>
    public CommandPaletteViewModel(IReadOnlyList<AppCommand> commands, IReadOnlyList<string>? recent = null,
        IReadOnlyList<string>? contextCategories = null, Action<string>? used = null, StatusModel? status = null)
    {
        _step = new Step(commands, null);
        _recent = recent?.ToList() ?? [];
        _context = contextCategories ?? [];
        _used = used;
        _status = status;
        Rank();
    }

    /// <summary>Raised when the palette must go away.</summary>
    public event Action? CloseRequested;

    /// <summary>Awaited between closing and running, so the window layer can give the focus back first.</summary>
    public Func<Task>? BeforeRun { get; set; }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? "")) Rank();
        }
    }

    public IReadOnlyList<PaletteRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value)) OnPropertyChanged(nameof(ConfirmHint));
        }
    }

    /// <summary>The title of the step being chosen ("Connect to"); null on the first level.</summary>
    public string? StepTitle => _step.Parent?.Title.TrimEnd('…', ' ');

    public bool InStep => _step.Parent is not null;

    public string Watermark => InStep ? "Choose…" : "Type a command…";

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => Query.Trim().Length > 0 ? $"No command matches ‘{Query.Trim()}’" : "Nothing to choose from.";

    public string CountText => !InStep && Query.Trim().Length == 0 ? $"{Rows.Count} commands"
        : Rows.Count == 1 ? "1 result" : $"{Rows.Count} results";

    public string ConfirmHint => SelectedRow is { HasChoices: true } ? "choose" : "run";

    public PaletteRow? SelectedRow => SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    public void MoveDown() => Wrap(1);

    public void MoveUp() => Wrap(-1);

    /// <summary>PageUp / PageDown: moves the selection and stops at the ends.</summary>
    public void MoveBy(int delta)
    {
        if (Rows.Count > 0) SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Rows.Count - 1);
    }

    public void MoveFirst() => MoveBy(-Rows.Count);

    public void MoveLast() => MoveBy(Rows.Count);

    private void Wrap(int delta)
    {
        if (Rows.Count > 0) SelectedIndex = ((SelectedIndex + delta) % Rows.Count + Rows.Count) % Rows.Count;
    }

    public Task ConfirmAsync() => SelectedRow is { } row ? ConfirmAsync(row) : Task.CompletedTask;

    /// <summary>Opens the second step of a command, or closes the palette and runs it. A greyed command does nothing.</summary>
    public async Task ConfirmAsync(PaletteRow row)
    {
        if (_closed || !row.IsEnabled) return;
        var command = row.Command;
        try
        {
            if (command.Choices is { } choices)
            {
                var next = new Step(choices(), command);
                _back.Add(_step with { Query = Query, Selected = SelectedIndex });
                Show(next);
                return;
            }

            // "Recently used" remembers the command of the first level, not the choice made in its steps.
            var root = _back.Count == 0 ? command : _back.Count > 1 ? _back[1].Parent! : _step.Parent!;
            Close();
            _used?.Invoke(root.Id);
            if (BeforeRun is { } wait) await wait();
            if (command.Run is { } run) await run();
        }
        catch (OperationCanceledException)
        {
            _status?.Info($"{command.Title.TrimEnd('…')} cancelled.");
        }
        catch (Exception e)
        {
            _status?.Error($"{command.Title.TrimEnd('…')} failed: {ErrorText.Describe(e)}");
        }
    }

    /// <summary>Back to the previous step, with what was typed and selected there. False on the first level.</summary>
    public bool Back()
    {
        if (_back.Count == 0) return false;
        var previous = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Show(previous);
        if (previous.Selected >= 0 && previous.Selected < Rows.Count) SelectedIndex = previous.Selected;
        return true;
    }

    /// <summary>Esc, a click outside: closes without running anything.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        CloseRequested?.Invoke();
    }

    private void Show(Step step)
    {
        _step = step;
        // The field is set directly: the list is ranked once, for the new step and its query together.
        _query = step.Query;
        OnPropertyChanged(nameof(Query));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(InStep));
        OnPropertyChanged(nameof(Watermark));
        Rank();
    }

    private void Rank()
    {
        var query = Query.Trim();
        var rows = query.Length == 0 ? Unfiltered() : Matching(query);
        _selectedIndex = -1;   // announced again below, after the list: the same index in a new list is a change for the view
        Rows = rows;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
        var first = 0;
        while (first < rows.Count && !rows[first].IsEnabled) first++;
        SelectedIndex = rows.Count == 0 ? -1 : first < rows.Count ? first : 0;
    }

    private List<PaletteRow> Unfiltered()
    {
        var commands = _step.Commands;
        if (InStep) return commands.Select(c => new PaletteRow(c, []) { ShowCategory = false }).ToList();   // choices keep the order they were given in

        var recent = _recent.Select(id => commands.FirstOrDefault(c => c.Id == id))
            .OfType<AppCommand>().Where(c => c.IsEnabled).Take(RecentShown).ToList();
        var others = commands.Except(recent).ToList();
        int ContextRank(AppCommand c)
        {
            for (var i = 0; i < _context.Count; i++)
                if (_context[i] == c.Category) return i;
            return _context.Count;
        }

        var rows = new List<PaletteRow>(commands.Count);
        void Section(string header, IEnumerable<AppCommand> section)
        {
            var first = true;
            foreach (var c in section)
            {
                rows.Add(new PaletteRow(c, [], first ? header : null));
                first = false;
            }
        }
        Section(RecentHeader, recent);
        Section(AvailableHeader, others.Where(c => c.IsEnabled).OrderBy(ContextRank));   // stable: registration order otherwise
        Section(UnavailableHeader, others.Where(c => !c.IsEnabled).OrderBy(ContextRank));
        return rows;
    }

    private List<PaletteRow> Matching(string query)
    {
        var scored = new List<(int Score, int Index, AppCommand Command)>();
        for (var i = 0; i < _step.Commands.Count; i++)
        {
            var command = _step.Commands[i];
            if (CommandMatcher.Score(query, command) is not { } score) continue;
            if (!InStep)
            {
                var used = IndexOfRecent(command.Id);
                if (used >= 0) score += Math.Max(1, RecentBonus - 3 * used);
                if (_context.Contains(command.Category)) score += 1;
            }
            scored.Add((score, i, command));
        }

        // A command whose title holds the typed text comes first even when greyed (the reason is what was looked for);
        // otherwise what can run comes before what cannot, whatever the letters scattered over its title score.
        return scored.OrderByDescending(s => s.Command.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => s.Command.IsEnabled).ThenByDescending(s => s.Score).ThenBy(s => s.Index)
            .Select(s => new PaletteRow(s.Command, CommandMatcher.Positions(s.Command.Title, query)) { ShowCategory = !InStep })
            .ToList();
    }

    private int IndexOfRecent(string id)
    {
        for (var i = 0; i < _recent.Count; i++)
            if (_recent[i] == id) return i;
        return -1;
    }
}
