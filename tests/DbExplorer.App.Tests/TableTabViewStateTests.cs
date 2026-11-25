using DbExplorer.App.Views;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;

namespace DbExplorer.App.Tests;

/// <summary>
/// What the table tab view derives from the tab: pending breakdown, filter chips and their draft state, loading / empty / stale
/// states, busy flag, and the small view-model additions of the grid (sort direction, exclude by cell, column flags).
/// </summary>
public class TableTabViewStateTests : IDisposable
{
    private readonly TableTabFixture _t = new();

    public void Dispose() => _t.Dispose();

    // ---- pending changes ----

    [Fact]
    public async Task PendingBreakdown_CountsEditedAddedAndDeletedRows()
    {
        var tab = await _t.OpenTab("customers");
        Assert.Equal("", tab.PendingSummary);

        tab.SetCell(0, tab.FindColumn("name")!.Value, "Alicia");
        tab.SetCell(1, tab.FindColumn("name")!.Value, "Bobby");
        tab.DeleteRow(2);
        tab.AddRow();

        Assert.Equal(2, tab.PendingEditedCount);
        Assert.Equal(1, tab.PendingDeletedCount);
        Assert.Equal(1, tab.PendingAddedCount);
        Assert.Equal("2 edited · 1 added · 1 deleted", tab.PendingSummary);
        Assert.Equal("4 pending changes", tab.PendingText);

        tab.Revert();
        Assert.Equal("", tab.PendingSummary);
        Assert.Equal(0, tab.PendingAddedCount);
    }

    [Fact]
    public async Task IsBusy_FollowsSaving_AndTheSaveButtonSaysSo()
    {
        var tab = await _t.OpenTab("customers");
        Assert.False(tab.IsBusy);
        Assert.Equal("Review and save…", tab.SaveButtonText);

        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        tab.IsSaving = true;

        Assert.True(tab.IsBusy);
        Assert.Equal("Saving…", tab.SaveButtonText);
        Assert.Contains(nameof(TableTabViewModel.IsBusy), changed);
        tab.IsSaving = false;
        Assert.False(tab.IsBusy);
    }

    // ---- filters ----

    [Fact]
    public async Task FilterChips_ShowTheAppliedFilters_AndTheirDraftState()
    {
        var tab = await _t.OpenTab("customers");
        Assert.False(tab.HasActiveFilters);
        Assert.False(tab.IsFilterDirty);
        Assert.Equal("Filters", tab.FiltersLabel);

        tab.SetFilter("name", "a");
        tab.RawWhere = "id > 1";
        Assert.True(tab.IsFilterDirty);   // typed, not applied
        Assert.Equal("Not applied yet", tab.NotAppliedText);
        Assert.Empty(tab.FilterChips);
        Assert.Equal("", tab.GetAppliedFilter("name"));

        await tab.ApplyFiltersAsync();

        Assert.False(tab.IsFilterDirty);
        Assert.Equal("", tab.NotAppliedText);
        Assert.True(tab.HasActiveFilters);
        Assert.Equal(["name a", "WHERE id > 1"], tab.FilterChips.Select(c => c.Label));
        Assert.Equal("Filters (2)", tab.FiltersLabel);
        Assert.Equal("a", tab.GetAppliedFilter("NAME"));

        tab.SetFilter("name", "ab");
        Assert.True(tab.IsFilterDirty);
        Assert.Equal("a", tab.GetAppliedFilter("name"));   // the rows are still those of the applied text
    }

    [Fact]
    public async Task ClearFilter_RemovesOnlyThatColumn_ClearFilters_RemovesEverything()
    {
        var tab = await _t.OpenTab("customers");
        tab.SetFilter("name", "a");
        tab.SetFilter("id", ">1");
        tab.RawWhere = "id < 4";
        await tab.ApplyFiltersAsync();
        Assert.Equal(3, tab.FilterChips.Count);

        await tab.FilterChips.Single(c => c.Column == "name").RemoveCommand.ExecuteAsync(null);

        Assert.Equal(["id >1", "WHERE id < 4"], tab.FilterChips.Select(c => c.Label));
        Assert.False(tab.ColumnFilters.ContainsKey("name"));

        await tab.ClearFiltersCommand.ExecuteAsync(null);

        Assert.False(tab.HasActiveFilters);
        Assert.Null(tab.RawWhere);
        Assert.Empty(tab.ColumnFilters);
        Assert.Equal(4, tab.Rows.Count);
    }

    [Fact]
    public async Task ClearWhere_KeepsTheColumnFilters()
    {
        var tab = await _t.OpenTab("customers");
        tab.SetFilter("name", "a");
        tab.RawWhere = "id > 1";
        await tab.ApplyFiltersAsync();

        await tab.FilterChips.Single(c => c.IsWhere).RemoveCommand.ExecuteAsync(null);

        Assert.Null(tab.RawWhere);
        Assert.Equal(["name a"], tab.FilterChips.Select(c => c.Label));
    }

    [Fact]
    public async Task ExcludeByCell_FiltersTheValueOut_AndNullBecomesNotNull()
    {
        var tab = await _t.OpenTab("customers");
        var name = tab.FindColumn("name")!.Value;
        Assert.Equal("Alice", tab.Rows[0].Values[name]);

        await tab.ExcludeByCellAsync(0, "name");

        Assert.Equal("!=Alice", tab.ColumnFilters["name"]);
        Assert.DoesNotContain(tab.Rows, r => (string?)r.Values[name] == "Alice");
        Assert.Equal(3, tab.Rows.Count);

        var boss = tab.FindColumn("boss_id")!.Value;
        await tab.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Null(tab.Rows[0].Values[boss]);   // Alice has no boss
        await tab.ExcludeByCellAsync(0, "boss_id");
        Assert.Equal("!null", tab.ColumnFilters["boss_id"]);
        Assert.All(tab.Rows, r => Assert.NotNull(r.Values[boss]));
    }

    [Fact]
    public void ExcludeText_IsTheOppositeOfFilterText()
    {
        Assert.Equal("!null", CellFormatter.ExcludeText(null));
        Assert.Equal("!=42", CellFormatter.ExcludeText(42));
        Assert.Equal("!=x y", CellFormatter.ExcludeText("x y"));
        Assert.Throws<FilterException>(() => CellFormatter.ExcludeText(new byte[] { 1 }));
    }

    // ---- sort ----

    [Fact]
    public async Task SortAsync_SetsTheDirectionAsked_NotTheNextStepOfTheCycle()
    {
        var tab = await _t.OpenTab("customers");

        await tab.SortAsync("name", true);
        Assert.Equal(new SortSpec("name", true), tab.Sort);
        Assert.Equal("Dave", tab.Rows[0].Values[tab.FindColumn("name")!.Value]);

        await tab.SortAsync("name", false);
        Assert.Equal(new SortSpec("name", false), tab.Sort);

        await tab.SortAsync("name", null);
        Assert.Null(tab.Sort);
    }

    // ---- grid states ----

    [Fact]
    public async Task EmptyStates_TellNoMatchesFromAnEmptyTable()
    {
        var tab = await _t.OpenTab("customers");
        Assert.False(tab.ShowNoMatches);
        Assert.False(tab.ShowEmptyTable);

        tab.SetFilter("name", "=zzz");
        await tab.ApplyFiltersAsync();
        Assert.Empty(tab.Rows);
        Assert.True(tab.ShowNoMatches);
        Assert.False(tab.ShowEmptyTable);

        var empty = await _t.OpenTab("nopk");
        Assert.True(empty.ShowEmptyTable);
        Assert.False(empty.ShowNoMatches);
        empty.AddRow();   // a new row fills the grid at once
        Assert.False(empty.ShowEmptyTable);
        empty.Revert();
        Assert.True(empty.ShowEmptyTable);
    }

    [Fact]
    public async Task ARestoredTab_ShowsTheInitialLoadState_UntilItIsLoaded()
    {
        var session = await _t.OpenSession();
        var tab = new TableTabViewModel(session.Config, new TableRef(null, "customers"), _t.Hooks);
        Assert.True(tab.ShowInitialLoad);
        Assert.False(tab.ShowEmptyTable);   // nothing has been loaded: it is not "empty" yet

        await tab.EnsureLoadedAsync();

        Assert.False(tab.ShowInitialLoad);
        Assert.False(tab.ShowEmptyTable);
        Assert.Equal(4, tab.Rows.Count);
    }

    [Fact]
    public async Task AFailedReload_KeepsTheRows_AndFlagsThemAsPossiblyOutOfDate()
    {
        var tab = await _t.OpenTab("customers");
        Assert.False(tab.ShowStaleRows);

        tab.RawWhere = "nonexistent_column = 1";
        await tab.ApplyFiltersAsync();

        Assert.NotNull(tab.ErrorMessage);
        Assert.Equal(4, tab.Rows.Count);
        Assert.True(tab.ShowStaleRows);
        Assert.False(tab.ShowLoadFailed);   // there are rows to show

        tab.RawWhere = null;
        await tab.ApplyFiltersAsync();
        Assert.False(tab.ShowStaleRows);
        Assert.Null(tab.ErrorMessage);
    }

    [Fact]
    public async Task ALastPage_KnowsItsTotal_WithoutCounting()
    {
        var customers = await _t.OpenTab("customers");
        Assert.False(customers.HasNextPage);
        Assert.Equal(4, customers.TotalCount);
        Assert.Equal("Rows 1–4 of 4", customers.RangeText);

        var big = await _t.OpenTab("big");
        Assert.True(big.HasNextPage);
        Assert.Null(big.TotalCount);   // only a count would tell
    }

    [Fact]
    public async Task EditBadgeTip_ExplainsTheBadge()
    {
        var readOnly = new TableTabFixture(readOnly: true);
        try
        {
            var tab = await readOnly.OpenTab("customers");
            Assert.Equal("read-only", tab.EditBadge);
            Assert.Contains("read-only", tab.EditBadgeTip);
        }
        finally
        {
            readOnly.Dispose();
        }

        var keyless = await _t.OpenTab("nopk");
        Assert.Contains("Define key", keyless.EditBadgeTip);
        var plain = await _t.OpenTab("customers");
        Assert.Null(plain.EditBadgeTip);
    }

    // ---- structure sub-tab ----

    [Fact]
    public async Task Structure_HasTitlesWithCounts_AndJumpsToAColumn()
    {
        var jumped = new List<string>();
        var session = await _t.OpenSession();
        var info = session.Schema.Single(t => t.Ref.Name == "customers");
        var structure = new StructureViewModel(info, _ => Task.CompletedTask, jumped.Add);

        Assert.Equal("COLUMNS (3)", structure.ColumnsTitle);
        Assert.Equal("FOREIGN KEYS (1)", structure.ForeignKeysTitle);
        Assert.True(structure.HasForeignKeys);
        Assert.Equal($"INDEXES ({info.Indexes.Count})", structure.IndexesTitle);

        var name = structure.Columns.Single(c => c.Name == "name");
        Assert.True(name.IsRequired);
        Assert.False(structure.Columns.Single(c => c.Name == "boss_id").IsRequired);
        name.Jump();
        Assert.Equal(["name"], jumped);

        var empty = StructureViewModel.Empty;
        Assert.False(empty.HasColumns);
        Assert.False(empty.HasForeignKeys);
        Assert.False(empty.HasIndexes);
    }

    [Fact]
    public async Task ATabsStructure_JumpsToTheColumnInTheDataGrid()
    {
        var tab = await _t.OpenTab("customers");
        var requested = new List<int>();
        tab.FocusColumnRequested += requested.Add;

        tab.Structure.Columns.Single(c => c.Name == "name").Jump();

        Assert.Equal([tab.FindColumn("name")!.Value], requested);
    }

    // ---- column flags and widths ----

    [Fact]
    public void ResultColumn_KnowsItsKind()
    {
        Assert.True(new ResultColumn("n", "INTEGER", typeof(int)).IsNumeric);
        Assert.True(new ResultColumn("n", "REAL", typeof(double?)).IsNumeric);
        Assert.False(new ResultColumn("s", "TEXT", typeof(string)).IsNumeric);
        Assert.True(new ResultColumn("b", "BOOLEAN", typeof(bool)).IsBoolean);
        Assert.True(new ResultColumn("d", "DATETIME", typeof(DateTime)).IsTemporal);
        Assert.True(new ResultColumn("d", "DATE", typeof(string)).IsTemporal);   // SQLite returns dates as text: the name tells
        Assert.False(new ResultColumn("x", "TEXT", typeof(string)).IsTemporal);
        Assert.True(new ResultColumn("g", "uuid", typeof(Guid)).IsGuid);
    }

    [Fact]
    public void DefaultWidth_FollowsTheType_NeverCutsADate()
    {
        var date = ResultGrid.DefaultWidth(new ResultColumn("created", "DATETIME", typeof(DateTime)));
        var flag = ResultGrid.DefaultWidth(new ResultColumn("ok", "BOOLEAN", typeof(bool)));
        var guid = ResultGrid.DefaultWidth(new ResultColumn("id", "uuid", typeof(Guid)));
        var number = ResultGrid.DefaultWidth(new ResultColumn("qty", "INTEGER", typeof(int)));

        Assert.True(date >= 170, "a yyyy-MM-dd HH:mm:ss value needs about 160 px");
        Assert.True(guid >= 280, "a GUID is 36 characters");
        Assert.True(flag < number && number < date);
        Assert.Equal(120, ResultGrid.DefaultWidth(new ResultColumn("id")));   // a result set without types: from the name alone
    }

    // ---- dialogs ----

    [Theory]
    [InlineData("UPDATE \"t\" SET \"a\" = 1;", "UPDATE")]
    [InlineData("  delete from t;", "DELETE")]
    [InlineData("INSERT INTO t VALUES (1);", "INSERT")]
    [InlineData("-- comment", "SQL")]
    [InlineData("", "SQL")]
    public void SqlPreview_NamesTheKindOfEachStatement(string sql, string kind)
    {
        var item = new StatementItem(sql);
        Assert.Equal(kind, item.Kind);
        Assert.Equal(kind == "DELETE", item.IsDelete);
        Assert.Equal(kind == "UPDATE", item.IsUpdate);
        Assert.Equal(kind == "INSERT", item.IsInsert);
    }

    [Fact]
    public async Task VirtualKeyDialog_MarksTheColumnsOfAUniqueIndex()
    {
        var session = await _t.OpenSession();
        var info = session.Schema.Single(t => t.Ref.Name == "nopk");
        var withIndex = info with { Indexes = [new IndexInfo("ux_nopk_x", true, ["x"]), new IndexInfo("ix_nopk_y", false, ["y"])] };

        var viewModel = new VirtualKeyDialogViewModel(withIndex, DbKind.Sqlite, null);

        Assert.True(viewModel.HasUniqueIndexColumns);
        Assert.True(viewModel.Columns.Single(c => c.Name == "x").InUniqueIndex);
        Assert.False(viewModel.Columns.Single(c => c.Name == "y").InUniqueIndex);   // a plain index says nothing about uniqueness
        Assert.False(new VirtualKeyDialogViewModel(info, DbKind.Sqlite, null).HasUniqueIndexColumns);
    }
}
