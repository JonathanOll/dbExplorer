using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests;

internal sealed class FakeEditDialogs : IEditDialogs
{
    public SaveChoice Choice { get; set; } = SaveChoice.Cancel;
    public bool PreviewResult { get; set; } = true;
    public string[]? KeyResult { get; set; }
    public int AskCount { get; private set; }
    public string? LastAskMessage { get; private set; }
    public List<IReadOnlyList<string>> Previews { get; } = [];
    public VirtualKeyDialogViewModel? LastKeyDialog { get; private set; }
    public bool ThrowOnAsk { get; set; }

    /// <summary>When set, AskSaveDiscardAsync completes only when this source is completed (to test the async close path).</summary>
    public TaskCompletionSource? AskGate { get; set; }

    /// <summary>Answers given first, one per question; <see cref="Choice"/> answers once this is empty.</summary>
    public Queue<SaveChoice> Choices { get; } = new();

    /// <summary>Runs when a question is asked, before it is answered (to look at the state of the app at that moment).</summary>
    public Action? OnAsk { get; set; }

    public async Task<SaveChoice> AskSaveDiscardAsync(string title, string message)
    {
        AskCount++;
        LastAskMessage = message;
        OnAsk?.Invoke();
        if (ThrowOnAsk) throw new InvalidOperationException("dialog failed");
        if (AskGate is { } gate) await gate.Task;
        return Choices.Count > 0 ? Choices.Dequeue() : Choice;
    }

    public List<string> PreviewTargets { get; } = [];

    public Task<bool> ShowSqlPreviewAsync(IReadOnlyList<string> statements, string target)
    {
        PreviewTargets.Add(target);
        Previews.Add(statements);
        return Task.FromResult(PreviewResult);
    }

    public Task<string[]?> ShowVirtualKeyDialogAsync(VirtualKeyDialogViewModel viewModel)
    {
        LastKeyDialog = viewModel;
        return Task.FromResult(KeyResult);
    }

    /// <summary>Plays the user of the "Save script" dialog; its answer is the result (true = Save or another location, false = Cancel).</summary>
    public Func<SaveScriptViewModel, bool>? OnSaveScript { get; set; }

    public Task<bool> ShowSaveScriptAsync(SaveScriptViewModel viewModel) => Task.FromResult(OnSaveScript?.Invoke(viewModel) ?? false);
}

public class EditingViewModelTests : IDisposable
{
    private readonly TableTabFixture _t;
    private readonly FakeEditDialogs _dialogs = new();
    private AppConfig _config = AppConfig.Empty();
    private ConnectionTreeViewModel? _tree;

    public EditingViewModelTests() => _t = new TableTabFixture();

    public void Dispose() => _t.Dispose();

    /// <summary>A tab wired like the shell wires it: live config and virtual-key persistence of a real tree view model.</summary>
    private async Task<TableTabViewModel> Open(string table, bool dialogs = true, bool persistence = true, TableTabFixture? fixture = null)
    {
        var f = fixture ?? _t;
        _config = new AppConfig([], [f.Config], new());
        _tree = persistence
            ? f.Fx.CreateTree(_config)
            : new ConnectionTreeViewModel(f.Fx.Store, _config, f.Fx.Sessions, f.Fx.Status, f.Fx.Dialogs, f.Fx.DBeaverDir, persistenceEnabled: false);
        var tree = _tree;
        var session = await f.OpenSession();
        var hooks = new TableTabHooks(
            ct => f.Sessions.GetOrOpenAsync(f.Config, ct).ContinueWith(t => (Session?)t.Result, ct),
            (_, _, _) => Task.CompletedTask,
            dialogs ? _dialogs : null,
            () => tree.Config,
            tree.SetVirtualKey);
        var tab = new TableTabViewModel(session, new TableRef(null, table), hooks);
        await tab.LoadAsync();
        return tab;
    }

    private static int Col(TableTabViewModel tab, string name) => tab.FindColumn(name)!.Value;

    private static object? Cell(TableTabViewModel tab, int row, string name) => tab.Rows[row].Values[Col(tab, name)];

    // ---- staging ----

    [Fact]
    public async Task SetCell_MarksCellDirty_AndHasPendingEdits()
    {
        var tab = await Open("customers");
        Assert.True(tab.CanEdit);
        Assert.False(tab.HasPendingEdits);

        Assert.True(tab.SetCell(0, Col(tab, "name"), "Alicia"));

        Assert.True(tab.HasPendingEdits);
        Assert.True(tab.Rows[0].IsDirty(Col(tab, "name")));
        Assert.False(tab.Rows[0].IsDirty(Col(tab, "id")));
        Assert.Equal("1 pending change", tab.PendingText);
        Assert.Equal("Alice", tab.Rows[0].Original[Col(tab, "name")]);
    }

    [Fact]
    public async Task SetCell_BackToOriginal_ClearsDirty()
    {
        var tab = await Open("customers");
        var name = Col(tab, "name");
        tab.SetCell(0, name, "Alicia");
        tab.SetCell(0, name, "Alice");

        Assert.False(tab.Rows[0].IsDirty(name));
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task SetCell_TypingTheLoadedValueBack_IsNotAChange()
    {
        var tab = await Open("orders");
        var created = Col(tab, "created");
        var paid = Col(tab, "paid");
        var total = Col(tab, "total");
        Assert.Equal(typeof(DateTime), tab.Columns[created].ClrType);   // read back as text by SQLite: the runtime type differs from the column type

        // what the editor shows for the loaded values, typed back unchanged
        Assert.True(tab.SetCell(0, created, TableTabViewModel.EditText(tab.Rows[0].Original[created])));
        Assert.True(tab.SetCell(0, paid, TableTabViewModel.EditText(tab.Rows[0].Original[paid])));
        Assert.True(tab.SetCell(0, total, TableTabViewModel.EditText(tab.Rows[0].Original[total])));

        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task SetCell_TextToIntColumn_InvalidValue_Rejected()
    {
        var tab = await Open("orders");
        var customer = Col(tab, "customer_id");
        var before = Cell(tab, 0, "customer_id");

        Assert.False(tab.SetCell(0, customer, "abc"));

        Assert.Equal(before, Cell(tab, 0, "customer_id"));
        Assert.False(tab.HasPendingEdits);
        Assert.Contains("customer_id", tab.CellError);
        Assert.Contains("abc", tab.CellError);
    }

    [Fact]
    public async Task EmptyStringVsNull_InCellEditor()
    {
        var tab = await Open("orders");
        var note = Col(tab, "note");
        var total = Col(tab, "total");

        // text column: empty text is the empty string, NULL is a separate explicit gesture
        Assert.True(tab.SetCell(0, note, ""));
        Assert.Equal("", Cell(tab, 0, "note"));
        Assert.True(tab.SetCell(0, note, null));
        Assert.Null(Cell(tab, 0, "note"));

        // numeric column: empty text is refused (with a hint), NULL clears it
        Assert.False(tab.SetCell(0, total, ""));
        Assert.Contains("Set NULL", tab.CellError);
        Assert.Equal(12.5, Cell(tab, 0, "total"));
        Assert.True(tab.SetCell(0, total, null));
        Assert.Null(Cell(tab, 0, "total"));
        Assert.Null(tab.CellError);
    }

    [Fact]
    public async Task SetCell_NullInNotNullColumn_Rejected()
    {
        var tab = await Open("customers");
        Assert.False(tab.SetCell(0, Col(tab, "name"), null));
        Assert.Contains("NULL", tab.CellError);
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task SetCell_BinaryColumnAsText_Rejected()
    {
        var tab = await Open("orders");
        Assert.False(tab.SetCell(0, Col(tab, "payload"), "0x01"));
        Assert.True(tab.SetCell(0, Col(tab, "payload"), null));   // NULL is fine
    }

    [Fact]
    public async Task EditText_IsTheCompleteValue_NotTheShortenedCellText()
    {
        var longText = new string('x', 500) + "\nsecond line";
        Assert.Equal(longText, TableTabViewModel.EditText(longText));
        Assert.Equal("", TableTabViewModel.EditText(null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Revert_RestoresOriginals()
    {
        var tab = await Open("customers");
        var name = Col(tab, "name");
        tab.SetCell(0, name, "Changed");
        tab.DeleteRow(1);
        var added = tab.AddRow();
        tab.SetCell(added, name, "Newbie");
        Assert.Equal(5, tab.Rows.Count);

        tab.Revert();

        Assert.False(tab.HasPendingEdits);
        Assert.Equal(4, tab.Rows.Count);
        Assert.Equal("Alice", Cell(tab, 0, "name"));
        Assert.False(tab.Rows[1].IsDeleted);
        Assert.All(tab.Rows, r => Assert.False(r.HasChanges));
    }

    [Fact]
    public async Task DeleteRow_OfNewRow_JustDropsIt_AndUndeleteRestores()
    {
        var tab = await Open("customers");
        var added = tab.AddRow();
        Assert.True(tab.HasPendingEdits);
        tab.DeleteRow(added);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal(4, tab.Rows.Count);

        tab.DeleteRow(0);
        Assert.True(tab.Rows[0].IsDeleted);
        Assert.False(tab.SetCell(0, Col(tab, "name"), "x"));   // a deleted row is not editable
        tab.UndeleteRow(0);
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task RowChanged_IsRaised_ForTheGridToRepaint()
    {
        var tab = await Open("customers");
        var changed = new List<RowViewModel>();
        tab.RowChanged += changed.Add;

        tab.SetCell(0, Col(tab, "name"), "Z");
        tab.DeleteRow(1);
        tab.Revert();

        Assert.Equal(4, changed.Count);   // set, delete, then one repaint per reverted row (2)
    }

    // ---- gating: read-only connection, views, keys ----

    [Fact]
    public async Task ReadOnlyConnection_CanEditFalse_AndEveryEditPathRefuses()
    {
        using var ro = new TableTabFixture(readOnly: true);
        var tab = await Open("customers", fixture: ro);

        Assert.False(tab.CanEdit);
        Assert.Null(tab.KeyColumns);
        Assert.Equal("read-only", tab.EditBadge);
        Assert.False(tab.CanDefineKey);
        Assert.False(tab.SetCell(0, Col(tab, "name"), "x"));
        Assert.Equal(-1, tab.AddRow());
        tab.DeleteRow(0);

        Assert.False(tab.HasPendingEdits);
        Assert.Equal(4, tab.Rows.Count);
        Assert.False(tab.Rows[0].IsDeleted);
        Assert.Empty(tab.BuildPreview());
        // a virtual key cannot re-enable editing of a read-only connection
        tab.SetVirtualKey(["id"]);
        Assert.False(tab.CanEdit);
    }

    [Fact]
    public async Task View_IsNotEditable_AndInsertIsRefused()
    {
        var tab = await Open("v_big");
        Assert.True(tab.IsView);
        Assert.False(tab.CanEdit);
        Assert.Equal("view (read-only)", tab.EditBadge);
        Assert.Equal(-1, tab.AddRow());
        Assert.False(tab.SetCell(0, 1, "x"));
        tab.SetVirtualKey(["id"]);
        Assert.False(tab.CanEdit);
    }

    [Fact]
    public async Task NoKey_CanEditFalse_UntilVirtualKeySet()
    {
        var tab = await Open("nopk");
        Assert.False(tab.CanEdit);
        Assert.True(tab.CanDefineKey);
        Assert.Equal("no primary key: insert only", tab.EditBadge);
        Assert.True(tab.CanInsert);       // an INSERT needs no key (ruling changed after the "+ Row" report): only UPDATE/DELETE are gated
        Assert.False(tab.SetCell(0, Col(tab, "y"), "x"));

        tab.SetVirtualKey(["x"]);

        Assert.True(tab.CanEdit);
        Assert.Equal(["x"], tab.KeyColumns);
        Assert.Null(tab.EditBadge);
    }

    [Fact]
    public async Task SetVirtualKey_PersistsToConfig()
    {
        var tab = await Open("nopk");
        tab.SetVirtualKey(["x", "y"]);

        Assert.Equal(["x", "y"], _tree!.Config.VirtualKeys[_t.Config.Id]["nopk"]);
        var reloaded = _t.Fx.Store.Load();
        Assert.Equal(["x", "y"], reloaded.VirtualKeys[_t.Config.Id]["nopk"]);
        Assert.Contains("saved", tab.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session", tab.Notice!);
    }

    [Fact]
    public async Task SetVirtualKey_AllColumns_StoresStar()
    {
        var tab = await Open("nopk");
        tab.SetVirtualKey(["*"]);

        Assert.Equal(["*"], _tree!.Config.VirtualKeys[_t.Config.Id]["nopk"]);
        Assert.Equal(["x", "y"], tab.KeyColumns);
    }

    [Fact]
    public async Task SetVirtualKey_WhenConfigCannotBeSaved_AppliesForThisSessionAndTellsTheUser()
    {
        var tab = await Open("nopk", persistence: false);
        tab.SetVirtualKey(["x"]);

        Assert.True(tab.CanEdit);
        Assert.Contains("session only", tab.Notice);
        Assert.False(File.Exists(_t.Fx.ConfigPath));   // nothing written
    }

    [Fact]
    public async Task SetVirtualKey_UnknownColumn_IsRefused()
    {
        var tab = await Open("nopk");
        tab.SetVirtualKey(["nope"]);
        Assert.False(tab.CanEdit);
        Assert.NotNull(tab.ErrorMessage);
    }

    [Fact]
    public async Task DefineKey_UsesTheDialogResult_AndCancelChangesNothing()
    {
        var tab = await Open("nopk");

        _dialogs.KeyResult = null;
        await tab.DefineKeyAsync();
        Assert.False(tab.CanEdit);
        Assert.NotNull(_dialogs.LastKeyDialog);

        _dialogs.KeyResult = ["x"];
        await tab.DefineKeyAsync();
        Assert.True(tab.CanEdit);
    }

    [Fact]
    public async Task ReadOnlyToggledMidEdit_SaveIsRefused()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");

        var session = await _t.OpenSession();
        session.UpdateConfig(session.Config with { ReadOnly = true });   // what the tree does on "toggle read-only"
        await tab.SaveAsync();

        Assert.Equal("Alice", await Task.FromResult(ScalarText("SELECT name FROM customers WHERE id = 1")));
        Assert.True(tab.HasPendingEdits);
        Assert.Contains("read-only", tab.ErrorMessage);
        Assert.False(tab.CanEdit);
        Assert.Equal("read-only", tab.EditBadge);
    }

    // ---- preview and save ----

    [Fact]
    public async Task BuildPreview_ListsStatements_DeletesThenUpdatesThenInserts_AndNeverExecutes()
    {
        var tab = await Open("customers");
        _t.Log.Clear();
        tab.SetCell(0, Col(tab, "name"), "O'Brien");
        tab.DeleteRow(3);
        var added = tab.AddRow();
        tab.SetCell(added, Col(tab, "id"), "50");
        tab.SetCell(added, Col(tab, "name"), "Eve");

        var preview = tab.BuildPreview();

        Assert.Equal(3, preview.Count);
        Assert.StartsWith("DELETE FROM", preview[0].PreviewSql);
        Assert.Contains("WHERE \"id\" = 4", preview[0].PreviewSql);
        Assert.StartsWith("UPDATE", preview[1].PreviewSql);
        Assert.Contains("'O''Brien'", preview[1].PreviewSql);
        Assert.StartsWith("INSERT INTO", preview[2].PreviewSql);
        Assert.True(preview[0].ExpectsSingleRow && preview[1].ExpectsSingleRow);
        Assert.Empty(_t.Log.Items);   // building a preview touches nothing
        Assert.Equal(4, _t.Scalar("SELECT COUNT(*) FROM customers"));
    }

    [Fact]
    public async Task Save_AppliesAndReloadsPage()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");
        tab.SetCell(1, Col(tab, "boss_id"), null);

        await tab.SaveAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal("Alicia", ScalarText("SELECT name FROM customers WHERE id = 1"));
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM customers WHERE id = 2 AND boss_id IS NULL"));
        Assert.Equal("Alicia", Cell(tab, 0, "name"));   // page reloaded from the database
        Assert.All(tab.Rows, r => Assert.False(r.HasChanges));
        Assert.Contains("Saved", tab.Notice);
    }

    [Fact]
    public async Task Save_InsertAndDelete_EndToEnd()
    {
        var tab = await Open("customers");
        tab.DeleteRow(3);                      // Dave
        var added = tab.AddRow();
        tab.SetCell(added, Col(tab, "id"), "5");
        tab.SetCell(added, Col(tab, "name"), "Eve");

        await tab.SaveAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM customers WHERE id = 4"));
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM customers WHERE id = 5 AND name = 'Eve'"));
        Assert.Equal(4, tab.Rows.Count);
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task Save_EmptyStringAndNull_AreWrittenAsDifferentValues()
    {
        var tab = await Open("orders");
        tab.SetCell(1, Col(tab, "note"), "");      // was NULL
        tab.SetCell(0, Col(tab, "note"), null);    // was 'first'
        await tab.SaveAsync();

        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM orders WHERE id = 11 AND note = ''"));
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM orders WHERE id = 10 AND note IS NULL"));
    }

    [Fact]
    public async Task Save_TypedValues_AreBoundWithTheColumnType()
    {
        var tab = await Open("orders");
        tab.SetCell(0, Col(tab, "total"), "7.25");
        tab.SetCell(0, Col(tab, "customer_id"), "2");
        await tab.SaveAsync();

        Assert.Null(tab.ErrorMessage);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM orders WHERE id = 10 AND total = 7.25 AND customer_id = 2"));
    }

    [Fact]
    public async Task Save_StaleRow_ShowsConflictAndKeepsEdits()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");
        tab.SetCell(3, Col(tab, "name"), "David");
        Exec("DELETE FROM customers WHERE id = 4");   // someone else deletes Dave meanwhile

        await tab.SaveAsync();

        Assert.Contains("no longer exists", tab.ErrorMessage);
        Assert.Contains("nothing was saved", tab.ErrorMessage);
        Assert.True(tab.NeedsReload);                       // Reload is offered
        Assert.True(tab.HasPendingEdits);                   // the user's edits are kept
        Assert.Equal("Alicia", Cell(tab, 0, "name"));
        Assert.Equal("Alice", ScalarText("SELECT name FROM customers WHERE id = 1"));   // whole batch rolled back
    }

    [Fact]
    public async Task Save_AmbiguousKey_ShowsMessageKeepsEditsAndRollsBack()
    {
        Exec("INSERT INTO nopk VALUES (1, 'a'), (1, 'b'), (2, 'c')");
        var tab = await Open("nopk");
        tab.SetVirtualKey(["x"]);
        var rowOfC = tab.Rows.ToList().FindIndex(r => (string?)r.Values[Col(tab, "y")] == "c");
        tab.SetCell(rowOfC, Col(tab, "y"), "c2");
        tab.SetCell(0, Col(tab, "y"), "changed");   // x = 1: matches two rows

        await tab.SaveAsync();

        Assert.Contains("matches 2 rows", tab.ErrorMessage);
        Assert.Contains("more specific key", tab.ErrorMessage);
        Assert.True(tab.HasPendingEdits);
        Assert.True(tab.NeedsReload);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM nopk WHERE y = 'c'"));       // the first update was rolled back too
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM nopk WHERE y IN ('changed', 'c2')"));
    }

    [Fact]
    public async Task Save_DatabaseError_RollsBackAndKeepsEdits()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");
        tab.SetCell(1, Col(tab, "id"), "3");   // primary key clash with Carol

        await tab.SaveAsync();

        Assert.Contains("Nothing was saved", tab.ErrorMessage);
        Assert.True(tab.HasPendingEdits);
        Assert.Equal("Alice", ScalarText("SELECT name FROM customers WHERE id = 1"));
    }

    [Fact]
    public async Task Save_WithSessionDisposed_ShowsConnectionClosed_KeepsEdits_AndReloadCanStillSave()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");
        _t.Sessions.Close(_t.Config.Id);   // Disconnect / config edit disposes the session

        await tab.SaveAsync();

        Assert.Equal(TableTabViewModel.ConnectionClosedMessage, tab.ErrorMessage);
        Assert.True(tab.NeedsReload);
        Assert.True(tab.HasPendingEdits);

        // Reload reconnects; with pending edits the user is asked, and Save goes through the preview
        _dialogs.Choice = SaveChoice.Save;
        await tab.ReloadAsync();

        Assert.Equal(1, _dialogs.AskCount);
        Assert.Single(_dialogs.Previews);
        Assert.Null(tab.ErrorMessage);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal("Alicia", ScalarText("SELECT name FROM customers WHERE id = 1"));
    }

    [Fact]
    public async Task ReviewAndSave_ShowsPreviewFirst_AndCancelWritesNothing()
    {
        var tab = await Open("customers");
        tab.SetCell(0, Col(tab, "name"), "Alicia");

        _dialogs.PreviewResult = false;
        await tab.ReviewAndSaveAsync();
        Assert.Single(_dialogs.Previews);
        Assert.Single(_dialogs.Previews[0]);
        Assert.StartsWith("UPDATE", _dialogs.Previews[0][0]);
        Assert.Equal("Alice", ScalarText("SELECT name FROM customers WHERE id = 1"));
        Assert.True(tab.HasPendingEdits);

        _dialogs.PreviewResult = true;
        await tab.ReviewAndSaveAsync();
        Assert.Equal("Alicia", ScalarText("SELECT name FROM customers WHERE id = 1"));
        Assert.False(tab.HasPendingEdits);
    }

    [Fact]
    public async Task ReviewAndSave_WithoutDialogs_WritesNothing()
    {
        var tab = await Open("customers", dialogs: false);
        tab.SetCell(0, Col(tab, "name"), "Alicia");
        await tab.ReviewAndSaveAsync();
        Assert.Equal("Alice", ScalarText("SELECT name FROM customers WHERE id = 1"));
        Assert.NotNull(tab.ErrorMessage);
    }

    [Fact]
    public async Task Save_LastRowOfLastPageDeleted_GoesBackAPage()
    {
        var tab = await Open("big");
        await tab.NextPage();   // rows 201-400
        await tab.NextPage();   // rows 401-500
        Assert.Equal(2, tab.PageIndex);
        for (var i = 0; i < tab.Rows.Count; i++) tab.DeleteRow(i);

        await tab.SaveAsync();

        Assert.Equal(1, tab.PageIndex);
        Assert.NotEmpty(tab.Rows);
        Assert.Null(tab.ErrorMessage);
    }

    // ---- leaving with pending edits ----

    [Fact]
    public async Task Navigating_WithPendingEdits_Prompts()
    {
        var tab = await Open("big");
        tab.SetCell(0, Col(tab, "label"), "edited");

        // Cancel: stay put, keep the edit
        _dialogs.Choice = SaveChoice.Cancel;
        await tab.NextPage();
        Assert.Equal(1, _dialogs.AskCount);
        Assert.Equal(0, tab.PageIndex);
        Assert.True(tab.HasPendingEdits);

        // every entry point asks
        tab.ColumnFilters["label"] = "row 1";
        await tab.ApplyFiltersAsync();
        await tab.ToggleSortAsync("label");
        await tab.RefreshAsync();
        await tab.FilterByCellAsync(0, "id");
        Assert.Equal(5, _dialogs.AskCount);
        Assert.Equal(0, tab.PageIndex);
        Assert.Null(tab.Sort);
        Assert.True(tab.HasPendingEdits);
        Assert.False(tab.ColumnFilters.ContainsKey("id"));   // a cancelled cell filter leaves the filter row alone

        // Discard: edits dropped, navigation happens
        _dialogs.Choice = SaveChoice.Discard;
        await tab.NextPage();
        Assert.Equal(1, tab.PageIndex);
        Assert.False(tab.HasPendingEdits);
        Assert.Equal("label", Assert.IsType<string>(tab.Columns[1].Name));
        Assert.Equal(0, _t.Scalar("SELECT COUNT(*) FROM big WHERE label = 'edited'"));
    }

    [Fact]
    public async Task Navigating_WithPendingEdits_Save_GoesThroughPreviewThenNavigates()
    {
        var tab = await Open("big");
        tab.SetCell(0, Col(tab, "label"), "edited");
        _dialogs.Choice = SaveChoice.Save;

        await tab.NextPage();

        Assert.Single(_dialogs.Previews);
        Assert.Equal(1, tab.PageIndex);
        Assert.Equal(1, _t.Scalar("SELECT COUNT(*) FROM big WHERE label = 'edited'"));
    }

    [Fact]
    public async Task Navigating_WithPendingEdits_SaveThenPreviewCancelled_StaysOnPage()
    {
        var tab = await Open("big");
        tab.SetCell(0, Col(tab, "label"), "edited");
        _dialogs.Choice = SaveChoice.Save;
        _dialogs.PreviewResult = false;

        await tab.NextPage();

        Assert.Equal(0, tab.PageIndex);
        Assert.True(tab.HasPendingEdits);
    }

    [Fact]
    public async Task Navigating_WithPendingEdits_WithoutDialogs_IsRefused()
    {
        var tab = await Open("big", dialogs: false);
        tab.SetCell(0, Col(tab, "label"), "edited");
        await tab.NextPage();
        Assert.Equal(0, tab.PageIndex);
        Assert.True(tab.HasPendingEdits);
    }

    [Fact]
    public async Task NoPendingEdits_NavigationDoesNotPrompt()
    {
        var tab = await Open("big");
        await tab.NextPage();
        Assert.Equal(1, tab.PageIndex);
        Assert.Equal(0, _dialogs.AskCount);
    }

    [Fact]
    public async Task ClosingTheTab_WithPendingEdits_Prompts()
    {
        var tab = await Open("customers");
        Assert.True(await tab.ConfirmCloseAsync());
        Assert.Equal(0, _dialogs.AskCount);

        tab.SetCell(0, Col(tab, "name"), "Alicia");
        _dialogs.Choice = SaveChoice.Cancel;
        Assert.False(await tab.ConfirmCloseAsync());
        _dialogs.Choice = SaveChoice.Discard;
        Assert.True(await tab.ConfirmCloseAsync());
    }

    [Fact]
    public async Task MainViewModel_CloseTab_AsksAboutPendingEdits()
    {
        var tab = await Open("customers");
        var main = new MainViewModel(_tree!, _t.Fx.Status);
        main.Tabs.Add(tab);
        main.ActiveTab = tab;
        tab.SetCell(0, Col(tab, "name"), "Alicia");

        _dialogs.Choice = SaveChoice.Cancel;
        await main.CloseTabCommand.ExecuteAsync(tab);
        Assert.Single(main.Tabs);

        _dialogs.Choice = SaveChoice.Discard;
        await main.CloseTabCommand.ExecuteAsync(tab);
        Assert.Empty(main.Tabs);
    }

    // ---- virtual key dialog ----

    private static TableInfo Table(params (string Name, string Type)[] columns) =>
        new(new TableRef("dbo", "t"), false,
            columns.Select((c, i) => new ColumnInfo(c.Name, c.Type, typeof(string), true, false, i)).ToList(), [], []);

    [Fact]
    public void KeyDialog_AllColumns_IsStar_WhenEveryColumnIsComparable()
    {
        var vm = new VirtualKeyDialogViewModel(Table(("a", "int"), ("b", "nvarchar(50)")), DbKind.SqlServer, null) { UseAllColumns = true };
        Assert.Equal(["*"], vm.Result);
        Assert.Contains("several rows", VirtualKeyDialogViewModel.Warning);
    }

    [Fact]
    public void KeyDialog_SqlServer_ExcludesTextNtextImageXml_FromAllColumns()
    {
        var vm = new VirtualKeyDialogViewModel(
            Table(("a", "int"), ("t", "text"), ("n", "ntext"), ("i", "image"), ("x", "xml"), ("m", "nvarchar(max)")), DbKind.SqlServer, null)
        { UseAllColumns = true };

        Assert.Equal(["a", "m"], vm.Result);   // explicit list, never "*" (it would include the unusable ones)
        Assert.True(vm.HasExcludedColumns);
        Assert.Contains("t, n, i, x", vm.ExcludedNote);
        Assert.All(vm.Columns.Where(c => c.Name is "t" or "n" or "i" or "x"), c => Assert.False(c.IsKeyable));
    }

    [Fact]
    public void KeyDialog_SelectedUnusableColumn_IsIgnored_AndNothingSelectedIsAnError()
    {
        var vm = new VirtualKeyDialogViewModel(Table(("a", "int"), ("t", "text")), DbKind.SqlServer, null);
        Assert.Null(vm.Result);
        Assert.NotNull(vm.Error);

        vm.Columns[1].IsSelected = true;   // text: cannot be a key
        Assert.Null(vm.Result);

        vm.Columns[0].IsSelected = true;
        Assert.Equal(["a"], vm.Result);
    }

    [Fact]
    public void KeyDialog_StartsFromTheCurrentKey()
    {
        var vm = new VirtualKeyDialogViewModel(Table(("a", "int"), ("b", "int")), DbKind.Sqlite, ["B"]);
        Assert.False(vm.UseAllColumns);
        Assert.False(vm.Columns[0].IsSelected);
        Assert.True(vm.Columns[1].IsSelected);
        Assert.True(new VirtualKeyDialogViewModel(Table(("a", "int")), DbKind.Sqlite, ["*"]).UseAllColumns);
    }

    [Fact]
    public async Task SetVirtualKey_StarOnSqlServerLikeTable_StoresExplicitUsableColumns()
    {
        // The tab's driver is SQLite (all columns comparable): "*" stays "*". The SQL Server rule is covered by the dialog tests above.
        var tab = await Open("nopk");
        tab.SetVirtualKey(["*"]);
        Assert.Equal(["*"], _tree!.Config.VirtualKeys[_t.Config.Id]["nopk"]);
    }

    // ---- helpers ----

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? ScalarText(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_t.DbFile};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }
}
