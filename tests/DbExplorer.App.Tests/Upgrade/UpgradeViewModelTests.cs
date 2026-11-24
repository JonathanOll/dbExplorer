using DbExplorer.App.ViewModels;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Sessions;
using DbExplorer.Core.Upgrade;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>
/// The dbupdate tree (<see cref="TempDbUpdateTree.Standard"/> with scripts that write to table <c>u(a INTEGER)</c>) and SQLite
/// databases to play it on. Sessions come from a delegate that counts its calls and the threads they were made on.
/// Sprint 143 = v0860 (8 scripts, one of them Production2) + v0861 + v0862 (one <c>SELECT 1</c> each).
/// Expected rows in <c>u</c> after a run with the default ticks: 2, 3, 10, 37, 101, 102, 4 (in this order).
/// </summary>
internal sealed class UpgradeFixture : IDisposable
{
    public const string V0860 = "v0860-sprint 143";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-upgrade-vm-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<Guid, ConnectionConfig> _configs = [];
    private readonly List<int> _threads = [];
    private int _resolved;

    public UpgradeFixture()
    {
        Directory.CreateDirectory(_dir);
        Tree = TempDbUpdateTree.Standard();
        Tree.Add(V0860, "adj - 2 - x.sql", "INSERT INTO u VALUES (2)");
        Tree.Add(V0860, "adj - 003 - Add covering index on Suppliers AccountId Deleted.sql",
            "CREATE INDEX ix_u_a ON u(a)\nGO\nINSERT INTO u VALUES (3)");
        Tree.Add(V0860, "adj - 10 - x.sql", "INSERT INTO u VALUES (10)");
        Tree.Add(V0860, "adj - 037 - x.sql", "INSERT INTO u VALUES (37)");
        Tree.Add(V0860, "fbo - 001 - A.sql", "INSERT INTO u VALUES (101)");
        Tree.Add(V0860, "fbo - 001 - B.sql", "INSERT INTO u VALUES (102)");
        Tree.Add(V0860, "mch - 003 - Production notes.sql", "INSERT INTO u VALUES (4)");
        Tree.Add(V0860, "mch - 010 - Disable users - Production2.sql", "INSERT INTO u VALUES (99)");
    }

    public TempDbUpdateTree Tree { get; }

    public int ResolveCount => Volatile.Read(ref _resolved);

    public IReadOnlyList<int> ResolveThreads
    {
        get { lock (_threads) return [.. _threads]; }
    }

    /// <summary>Replaces the session resolution (e.g. one that never completes, or a session that became read-only).</summary>
    public Func<Guid, CancellationToken, Task<Session?>>? Override { get; set; }

    public DbUpdateCatalog Catalog() => DbUpdateCatalog.Scan(Tree.Root);

    /// <summary>A SQLite file holding <c>u(a INTEGER)</c> (not when <paramref name="withTable"/> is false: every script then fails on it).</summary>
    public ConnectionConfig Db(string name, bool withTable = true, bool readOnly = false)
    {
        var file = Path.Combine(_dir, name + "-" + Guid.NewGuid().ToString("N") + ".db");
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = withTable ? "CREATE TABLE u (a INTEGER)" : "CREATE TABLE other (a INTEGER)";
            cmd.ExecuteNonQuery();
        }
        var config = new ConnectionConfig(Guid.NewGuid(), name, DbKind.Sqlite, null, null, null, file, null, null, false, false, readOnly);
        _configs[config.Id] = config;
        return config;
    }

    public static UpgradeTarget One(ConnectionConfig db) => new($"This database ({db.Name})", [db], null);

    public static UpgradeTarget Group(string name, params ConnectionConfig[] members) =>
        new($"Group {name} ({members.Length} databases)", members, new ConnectionGroup(Guid.NewGuid(), name, [.. members.Select(m => m.Id)]));

    public Task<Session?> Resolve(Guid id, CancellationToken ct)
    {
        Interlocked.Increment(ref _resolved);
        lock (_threads) _threads.Add(Environment.CurrentManagedThreadId);
        if (Override is { } o) return o(id, ct);
        return Task.FromResult<Session?>(SessionOf(_configs[id]));
    }

    public static Session SessionOf(ConnectionConfig config) => new(config, new SqliteDriver(), [], null);

    public UpgradeViewModel Vm(IReadOnlyList<UpgradeTarget> targets, bool requireSqlServer = false, Func<string, Task>? copy = null) =>
        new(Catalog(), targets, Resolve, copy, requireSqlServer, open => new UpgradeRunner(open, requireSqlServer));

    public static List<long> Rows(ConnectionConfig config)
    {
        using var c = new SqliteConnection($"Data Source={config.Database};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT a FROM u ORDER BY rowid";
        using var r = cmd.ExecuteReader();
        var rows = new List<long>();
        while (r.Read()) rows.Add(r.GetInt64(0));
        return rows;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Tree.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class UpgradeViewModelTests : IDisposable
{
    private static readonly long[] Expected143 = [2, 3, 10, 37, 101, 102, 4];
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    private readonly UpgradeFixture _f = new();
    private readonly TimeSpan _grace = UpgradeViewModel.CancelGrace;

    public void Dispose()
    {
        UpgradeViewModel.CancelGrace = _grace;
        _f.Dispose();
    }

    private static async Task Load(UpgradeViewModel vm, string sprint)
    {
        vm.SprintText = sprint;
        await vm.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Loads sprint 143 and asks for the run: the view model is then in Confirm.</summary>
    private static async Task<UpgradeViewModel> InConfirm(UpgradeFixture f, UpgradeTarget target, params UpgradeTarget[] more)
    {
        var vm = f.Vm([target, .. more]);
        await Load(vm, "143");
        vm.RequestRunCommand.Execute(null);
        Assert.Equal(UpgradePhase.Confirm, vm.Phase);
        return vm;
    }

    private static async Task Finish(Task run) => await run.WaitAsync(Bound);

    // ---- Load: input that is not a sprint (Review Focus 1) ----

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("99999999999")]
    [InlineData("1.5")]
    public async Task Load_InvalidSprintText_GivesAMessage_NoThrow_NothingResolved(string text)
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, text);

        Assert.Equal("Enter a sprint number, for example 143.", vm.Message);
        Assert.Equal(UpgradePhase.Setup, vm.Phase);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task Load_SprintTextWithSpaces_IsTrimmed()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, " 143 ");

        Assert.Null(vm.Message);
        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal(10, vm.Rows.Count);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task Load_UnknownSprint_NamesTheNearestSprints()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "999");

        Assert.Equal("Sprint 999 was not found. Nearest sprints: 129, 143.", vm.Message);
        Assert.Equal(UpgradePhase.Setup, vm.Phase);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task Load_NoSprintFolderAtAll_SaysSo()
    {
        using var empty = new TempDbUpdateTree();
        empty.Add("v0001-hotfix", "a - 001 - x.sql");
        var vm = new UpgradeViewModel(DbUpdateCatalog.Scan(empty.Root), [UpgradeFixture.One(_f.Db("A"))], _f.Resolve);

        await Load(vm, "143");

        Assert.Equal("No sprint folders found.", vm.Message);
    }

    [Fact]
    public async Task Load_SprintWhoseFoldersHoldNoScript_SaysSo()
    {
        _f.Tree.AddDir("v0900-sprint 150");
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "150");

        Assert.Equal("Sprint 150 has no scripts.", vm.Message);
        Assert.Equal(UpgradePhase.Setup, vm.Phase);
        Assert.Empty(vm.Rows);
        Assert.Equal(0, _f.ResolveCount);
    }

    // ---- Load: the preview ----

    [Fact]
    public async Task Load_Sprint143_RowsInOrder_ProductionUnchecked_WarningsCounted()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "143");

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal("143", vm.ConfirmToken);
        Assert.Equal(
        [
            "adj - 2 - x.sql", "adj - 003 - Add covering index on Suppliers AccountId Deleted.sql", "adj - 10 - x.sql",
            "adj - 037 - x.sql", "fbo - 001 - A.sql", "fbo - 001 - B.sql", "mch - 003 - Production notes.sql",
            "mch - 010 - Disable users - Production2.sql", "abc - 001 - x.sql", "abc - 001 - x.sql",
        ], vm.Rows.Select(r => r.Name));
        Assert.Equal([UpgradeFixture.V0860, UpgradeFixture.V0860, UpgradeFixture.V0860, UpgradeFixture.V0860, UpgradeFixture.V0860,
            UpgradeFixture.V0860, UpgradeFixture.V0860, UpgradeFixture.V0860, "v0861-sprint 143", "v0862-sprint 143"],
            vm.Rows.Select(r => r.Folder));

        var production = vm.Rows.Single(r => r.IsProduction);
        Assert.Equal("Production2", production.ProductionTag);
        Assert.False(production.IsSelected);
        Assert.True(production.CanSelect);
        Assert.Equal(9, vm.Rows.Count(r => r.IsSelected));   // "Production notes" is not a Production<n> script

        var index = vm.Rows[1];
        Assert.Equal("1 warning: 1 index", vm.WarningSummary);
        Assert.Single(index.Warnings);
        Assert.Contains("Creates index ix_u_a", index.WarningText);
        Assert.Equal(["v0860-sprint 143/adj - 003 - Add covering index on Suppliers AccountId Deleted.sql: Index - Creates index ix_u_a (line 1)"],
            vm.SelectedWarnings.Select(w => w.Text));

        index.IsSelected = false;
        Assert.Equal("", vm.WarningSummary);
        Assert.Empty(vm.SelectedWarnings);
        index.IsSelected = true;
        Assert.Equal("1 warning: 1 index", vm.WarningSummary);
    }

    [Fact]
    public async Task WarningSummary_CountsEveryKind()
    {
        _f.Tree.Add(UpgradeFixture.V0860, "adj - 2 - x.sql", "UPDATE u SET a = 1");
        _f.Tree.Add(UpgradeFixture.V0860, "adj - 10 - x.sql", "WHILE 1 = 0 BEGIN SELECT 1 END");
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "143");

        Assert.Equal("3 warnings: 1 index, 2 long-running", vm.WarningSummary);
        Assert.Equal(3, vm.SelectedWarnings.Count);
    }

    [Fact]
    public async Task Load_ByFolder_UsesOnlyThatFolder()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        vm.SelectedFolder = vm.FolderChoices.Single(c => c.Label.StartsWith("v0861"));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal("v0861", vm.ConfirmToken);
        Assert.Equal(["abc - 001 - x.sql"], vm.Rows.Select(r => r.Name));
        Assert.Equal(["v0861-sprint 143"], vm.Rows.Select(r => r.Folder));
    }

    [Fact]
    public async Task Load_FolderWithoutSprint_SelectableByFolderOnly()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        vm.SelectedFolder = vm.FolderChoices.Single(c => c.Label.StartsWith("V0121"));

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("V0121", vm.ConfirmToken);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public void FolderChoices_NewestFirst_WithScriptCount()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        Assert.Equal("v0862-sprint 143 (1 script)", vm.FolderChoices[0].Label);
        Assert.Equal("v0861-sprint 143 (1 script)", vm.FolderChoices[1].Label);
        Assert.Equal("v0860-sprint 143 (8 scripts)", vm.FolderChoices[2].Label);
        Assert.Equal("v0002-sprint 23 (1 script)", vm.FolderChoices[^1].Label);
    }

    [Fact]
    public void SprintText_AndFolderChoice_ClearEachOther()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        vm.SelectedFolder = vm.FolderChoices[0];
        Assert.Equal("", vm.SprintText);

        vm.SprintText = "143";
        Assert.Null(vm.SelectedFolder);
        Assert.Equal("143", vm.SprintText);

        vm.SelectedFolder = vm.FolderChoices[0];
        Assert.Equal("", vm.SprintText);
        Assert.NotNull(vm.SelectedFolder);
    }

    [Fact]
    public async Task Load_ShowsIgnoredFilesNote()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "143");

        Assert.Equal(
            "Ignored (not a .sql file): v0860-sprint 143/fbo - 001 - Delete Obsole Sync Triggers, v0860-sprint 143/notes.txt",
            vm.IgnoredNote);

        await Load(vm, "128");   // nothing ignored there
        Assert.Null(vm.IgnoredNote);
    }

    [Fact]
    public async Task Load_ScriptWithLoadError_DisablesRowAndBlocksRun()
    {
        _f.Tree.Add(UpgradeFixture.V0860, "adj - 10 - x.sql", "INSERT INTO u VALUES (1)\nGO 3");
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await Load(vm, "143");

        var bad = vm.Rows.Single(r => r.LoadError is not null);
        Assert.Equal("adj - 10 - x.sql", bad.Name);
        Assert.Contains("GO n", bad.LoadError);
        Assert.False(bad.CanSelect);
        Assert.False(bad.IsSelected);
        Assert.False(vm.RequestRunCommand.CanExecute(null));

        vm.RequestRunCommand.Execute(null);   // even if it is invoked anyway
        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Contains("cannot be played", vm.Message);
        Assert.Equal(0, _f.ResolveCount);
    }

    // ---- RequestRun: refusals happen before anyone is contacted ----

    [Fact]
    public async Task RequestRun_ReadOnlyTarget_RefusedBeforeAnySessionIsResolved()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("Prod", readOnly: true))]);
        await Load(vm, "143");

        vm.RequestRunCommand.Execute(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Contains("Read-only connections cannot be upgraded: Prod", vm.Message);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task RequestRun_GroupWithOneReadOnlyMember_RefusesTheWholeRun()
    {
        var vm = _f.Vm([UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B", readOnly: true), _f.Db("C"))]);
        await Load(vm, "143");

        vm.RequestRunCommand.Execute(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Contains("Read-only connections cannot be upgraded: B.", vm.Message);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task RequestRun_RequireSqlServer_RefusesAnotherEngine()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("Lite"))], requireSqlServer: true);
        await Load(vm, "143");

        vm.RequestRunCommand.Execute(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Contains("only SQL Server databases can be upgraded (not: Lite (Sqlite))", vm.Message);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task RequestRun_NothingSelected_SaysSo()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        await Load(vm, "143");
        foreach (var row in vm.Rows) row.IsSelected = false;

        Assert.False(vm.RequestRunCommand.CanExecute(null));
        vm.RequestRunCommand.Execute(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal("Tick at least one script to run.", vm.Message);
    }

    [Fact]
    public void RequestRun_BeforeAnyLoad_DoesNothing()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        vm.RequestRunCommand.Execute(null);

        Assert.Equal(UpgradePhase.Setup, vm.Phase);
        Assert.Equal(0, _f.ResolveCount);
    }

    // ---- Confirm ----

    [Fact]
    public async Task Confirm_SingleDatabase_NoTyping()
    {
        var vm = await InConfirm(_f, UpgradeFixture.One(_f.Db("A")));

        Assert.False(vm.NeedsTypedConfirmation);
        Assert.True(vm.ConfirmRunCommand.CanExecute(null));
        Assert.Equal("Upgrade 9 scripts on:", vm.ConfirmHeading);
        Assert.Equal(["A"], vm.ConfirmDatabases);
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task Confirm_Group_RequiresTypedSprint()
    {
        var group = UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B"), _f.Db("C"));
        var vm = await InConfirm(_f, group);

        Assert.True(vm.NeedsTypedConfirmation);
        Assert.Equal(["A", "B", "C"], vm.ConfirmDatabases);
        Assert.False(vm.ConfirmRunCommand.CanExecute(null));

        vm.ConfirmInput = "142";
        Assert.False(vm.ConfirmRunCommand.CanExecute(null));

        vm.ConfirmInput = " 143 ";
        Assert.True(vm.ConfirmRunCommand.CanExecute(null));
        Assert.Equal(0, _f.ResolveCount);
    }

    [Fact]
    public async Task Confirm_ByFolder_AsksForTheFolderCode()
    {
        var vm = _f.Vm([UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B"))]);
        vm.SelectedTarget = vm.Targets[0];
        vm.SelectedFolder = vm.FolderChoices.Single(c => c.Label.StartsWith("v0861"));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.RequestRunCommand.Execute(null);

        vm.ConfirmInput = "143";
        Assert.False(vm.ConfirmRunCommand.CanExecute(null));
        vm.ConfirmInput = "v0861";
        Assert.True(vm.ConfirmRunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Confirm_ListsTheProductionScriptsTicked_AndEveryWarning()
    {
        var vm = await InConfirm(_f, UpgradeFixture.One(_f.Db("A")));
        Assert.Equal(0, vm.ProductionSelectedCount);
        Assert.Single(vm.SelectedWarnings);

        vm.BackCommand.Execute(null);
        vm.Rows.Single(r => r.IsProduction).IsSelected = true;
        vm.RequestRunCommand.Execute(null);

        Assert.Equal(1, vm.ProductionSelectedCount);
        Assert.Equal("Upgrade 10 scripts on:", vm.ConfirmHeading);
    }

    [Fact]
    public async Task Back_ReturnsToPreview_KeepingTheTicks()
    {
        var vm = await InConfirm(_f, UpgradeFixture.One(_f.Db("A")));

        vm.BackCommand.Execute(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal(9, vm.Rows.Count(r => r.IsSelected));
    }

    [Fact]
    public async Task ConfirmRun_OutsideConfirm_DoesNothing()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);
        await Load(vm, "143");   // Preview, not Confirm

        await vm.ConfirmRunCommand.ExecuteAsync(null);

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Equal(0, _f.ResolveCount);
    }

    // ---- Running ----

    [Fact]
    public async Task Run_Group_PlaysSelectedScriptsOnEveryDatabase()
    {
        var a = _f.Db("A");
        var b = _f.Db("B");
        var c = _f.Db("C");
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, b, c));
        vm.ConfirmInput = "143";

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.Equal(["A", "B", "C"], vm.Members.Select(m => m.Name));
        Assert.All(vm.Members, m => Assert.Equal(MemberState.Done, m.State));
        Assert.All(new[] { a, b, c }, db => Assert.Equal(Expected143, UpgradeFixture.Rows(db)));   // 99 (Production2, unticked) never ran
        Assert.Equal("3 of 3 databases done.", vm.Message);
        Assert.Contains("== A: Done (9 of 9 scripts", vm.Log);
        Assert.Contains("== B: Done", vm.Log);
        Assert.Contains("== C: Done", vm.Log);
        Assert.Contains("[OK] v0860-sprint 143/adj - 2 - x.sql", vm.Log);
        Assert.DoesNotContain("Production2", vm.Log);
        Assert.StartsWith("Sprint 143", vm.Log);
        Assert.True(vm.CanClose);
        Assert.StartsWith("done - 9 scripts - ", vm.Members[0].StatusText);
        Assert.EndsWith(" s", vm.Members[0].StatusText);
    }

    [Fact]
    public async Task Run_TickedProductionScript_Runs()
    {
        var a = _f.Db("A");
        var vm = _f.Vm([UpgradeFixture.One(a)]);
        await Load(vm, "143");
        vm.Rows.Single(r => r.IsProduction).IsSelected = true;
        vm.RequestRunCommand.Execute(null);

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal([2, 3, 10, 37, 101, 102, 4, 99], UpgradeFixture.Rows(a));
    }

    [Fact]
    public async Task Run_OneDatabaseFails_OthersFinish_SummaryNamesIt()
    {
        var a = _f.Db("A");
        var broken = _f.Db("Broken", withTable: false);
        var c = _f.Db("C");
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, broken, c));
        vm.ConfirmInput = "143";

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.Equal(MemberState.Done, vm.Members[0].State);
        Assert.Equal(MemberState.Failed, vm.Members[1].State);
        Assert.Equal(MemberState.Done, vm.Members[2].State);
        Assert.Equal("2 of 3 databases done, 1 failed.", vm.Message);
        Assert.StartsWith("failed at v0860-sprint 143/adj - 2 - x.sql: batch 1 of 1:", vm.Members[1].StatusText);
        Assert.Equal(Expected143, UpgradeFixture.Rows(a));
        Assert.Equal(Expected143, UpgradeFixture.Rows(c));
        Assert.Contains("== Broken: Failed", vm.Log);
        Assert.Contains("[FAILED] v0860-sprint 143/adj - 2 - x.sql : batch 1 of 1", vm.Log);
        Assert.Contains("[SKIPPED]", vm.Log);
    }

    [Fact]
    public async Task Run_FactoryRecheck_SessionBecameReadOnly_FailsThatMemberOnly()
    {
        var a = _f.Db("A");
        var b = _f.Db("B");   // the snapshot says writable...
        _f.Override = (id, _) => Task.FromResult<Session?>(
            UpgradeFixture.SessionOf(id == b.Id ? b with { ReadOnly = true } : a));   // ...the live session says read-only
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, b));
        vm.ConfirmInput = "143";

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(MemberState.Done, vm.Members[0].State);
        Assert.Equal(MemberState.Failed, vm.Members[1].State);
        Assert.Contains("B is read-only: not run.", vm.Members[1].StatusText);
        Assert.Empty(UpgradeFixture.Rows(b));
        Assert.Equal(Expected143, UpgradeFixture.Rows(a));
        Assert.Equal("1 of 2 databases done, 1 failed.", vm.Message);
    }

    [Fact]
    public async Task Run_SessionThatNoLongerExists_FailsThatMember()
    {
        var a = _f.Db("A");
        var b = _f.Db("B");
        _f.Override = (id, _) => Task.FromResult<Session?>(id == b.Id ? null : UpgradeFixture.SessionOf(a));
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, b));
        vm.ConfirmInput = "143";

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(MemberState.Failed, vm.Members[1].State);
        Assert.Contains("B no longer exists.", vm.Members[1].StatusText);
    }

    [Fact]
    public async Task Run_ResolvesStartOnTheCallingThread_OneEach()
    {
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", _f.Db("A"), _f.Db("B"), _f.Db("C")));
        vm.ConfirmInput = "143";
        var resolvedBefore = _f.ResolveCount;
        var thread = Environment.CurrentManagedThreadId;   // the thread that calls the command (an await may move the test afterwards)

        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        var resolvedByTheCall = _f.ResolveCount;           // all of them started before the call returned its task
        await Finish(run);

        Assert.Equal(0, resolvedBefore);
        Assert.Equal(3, resolvedByTheCall);
        Assert.Equal(3, _f.ResolveCount);
        Assert.All(_f.ResolveThreads, t => Assert.Equal(thread, t));
    }

    [Fact]
    public async Task Run_MembersWithTheSameName_GetUniqueNames()
    {
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", _f.Db("Same"), _f.Db("Same")));
        vm.ConfirmInput = "143";

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(["Same", "Same (2)"], vm.Members.Select(m => m.Name));
    }

    [Fact]
    public async Task CancelAll_WhileConnecting_EndsCancelled_AndReleasesEverything()
    {
        UpgradeViewModel.CancelGrace = TimeSpan.FromMilliseconds(100);
        _f.Override = (_, _) => new TaskCompletionSource<Session?>().Task;   // never completes
        var a = _f.Db("A");
        var b = _f.Db("B");
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, b));
        vm.ConfirmInput = "143";

        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.Equal(UpgradePhase.Running, vm.Phase);
        Assert.False(vm.CanClose);
        Assert.True(vm.CancelAllCommand.CanExecute(null));
        vm.CancelAllCommand.Execute(null);
        await Finish(run);

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.True(vm.CanClose);
        Assert.All(vm.Members, m =>
        {
            Assert.Equal(MemberState.Cancelled, m.State);
            Assert.Equal("cancelled", m.StatusText);
        });
        Assert.Empty(UpgradeFixture.Rows(a));
        Assert.Empty(UpgradeFixture.Rows(b));
        Assert.Equal("0 of 2 databases done, 2 cancelled.", vm.Message);
    }

    [Fact]
    public async Task CancelMember_OnlyThatMember()
    {
        var never = new TaskCompletionSource<Session?>();
        var a = _f.Db("A");
        var b = _f.Db("B");
        var c = _f.Db("C");
        _f.Override = (id, _) => id == b.Id ? never.Task : Task.FromResult<Session?>(UpgradeFixture.SessionOf(id == a.Id ? a : c));
        var vm = await InConfirm(_f, UpgradeFixture.Group("G", a, b, c));
        vm.ConfirmInput = "143";

        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        var middle = vm.Members[1];
        Assert.True(middle.CanCancel);
        middle.CancelCommand.Execute(null);
        Assert.Equal("cancelled", middle.StatusText);   // at once, before the runner reports
        await Finish(run);

        Assert.Equal(UpgradePhase.Finished, vm.Phase);
        Assert.Equal([MemberState.Done, MemberState.Cancelled, MemberState.Done], vm.Members.Select(m => m.State));
        Assert.Equal(Expected143, UpgradeFixture.Rows(a));
        Assert.Empty(UpgradeFixture.Rows(b));
        Assert.Equal(Expected143, UpgradeFixture.Rows(c));
        Assert.Equal("2 of 3 databases done, 1 cancelled.", vm.Message);
    }

    [Fact]
    public async Task CanClose_FalseWhileRunning_TrueOtherwise()
    {
        var gate = new TaskCompletionSource();
        var a = _f.Db("A");
        _f.Override = async (_, _) => { await gate.Task; return UpgradeFixture.SessionOf(a); };
        var vm = _f.Vm([UpgradeFixture.One(a)]);
        Assert.True(vm.CanClose);
        await Load(vm, "143");
        Assert.True(vm.CanClose);
        vm.RequestRunCommand.Execute(null);
        Assert.True(vm.CanClose);

        var run = vm.ConfirmRunCommand.ExecuteAsync(null);
        Assert.Equal(UpgradePhase.Running, vm.Phase);
        Assert.False(vm.CanClose);
        Assert.Equal("waiting", vm.Members[0].StatusText);

        gate.SetResult();
        await Finish(run);
        Assert.True(vm.CanClose);
        Assert.Equal(UpgradePhase.Finished, vm.Phase);
    }

    [Fact]
    public async Task Run_FinalState_CarriesTheLastScriptReached()
    {
        var vm = await InConfirm(_f, UpgradeFixture.One(_f.Db("A")));

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(9, vm.Members[0].ScriptCount);
        Assert.Equal(9, vm.Members[0].ScriptIndex);
        Assert.Equal(MemberState.Done, vm.Members[0].State);
        Assert.False(vm.Members[0].CanCancel);
    }

    [Fact]
    public async Task Run_RefusedByTheRunner_GoesBackToPreviewWithTheMessage_NothingContacted()
    {
        // The runner repeats the checks itself: a member that became read-only between the confirmation request and the run
        // is refused there (provoked here through the list the target holds), before any session is resolved or database opened.
        var a = _f.Db("A");
        var members = new List<ConnectionConfig> { a };
        var vm = await InConfirm(_f, new UpgradeTarget("This database (A)", members, null));
        members[0] = a with { ReadOnly = true };

        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        Assert.Equal(UpgradePhase.Preview, vm.Phase);
        Assert.Contains("Read-only connections cannot be upgraded: A", vm.Message);
        Assert.Empty(UpgradeFixture.Rows(a));
        Assert.Equal(0, _f.ResolveCount);
        Assert.True(vm.CanClose);
    }

    [Fact]
    public async Task CopyLog_CallsTheDelegateWithTheLog()
    {
        var copied = new List<string>();
        var a = _f.Db("A");
        var vm = _f.Vm([UpgradeFixture.One(a)], copy: text => { copied.Add(text); return Task.CompletedTask; });
        await Load(vm, "143");
        vm.RequestRunCommand.Execute(null);
        await Finish(vm.ConfirmRunCommand.ExecuteAsync(null));

        await vm.CopyLogCommand.ExecuteAsync(null);

        Assert.Equal([vm.Log], copied);
        Assert.NotEmpty(vm.Log);
    }

    [Fact]
    public async Task CopyLog_WithoutADelegate_DoesNothing()
    {
        var vm = _f.Vm([UpgradeFixture.One(_f.Db("A"))]);

        await vm.CopyLogCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Log);
    }

    [Fact]
    public void Targets_AreExposed_FirstOneSelected()
    {
        var a = _f.Db("A");
        var one = UpgradeFixture.One(a);
        var group = UpgradeFixture.Group("G", a, _f.Db("B"));
        var vm = _f.Vm([one, group]);

        Assert.Equal([one, group], vm.Targets);
        Assert.Same(one, vm.SelectedTarget);
    }
}
