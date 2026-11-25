using DbExplorer.App.ViewModels;
using DbExplorer.Core.Config;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;

namespace DbExplorer.App.Tests;

/// <summary>The view-model behaviour behind the restyled group editor and schema-difference dialogs.</summary>
public class DialogViewModelTests
{
    private static ConnectionConfig Conn(string name, DbKind kind = DbKind.Postgres, string? folder = null, string db = "db") =>
        TreeFixture.Conn(name, folder, db, kind);

    private static AppConfig Config(IEnumerable<ConnectionConfig> connections, params ConnectionGroup[] groups) =>
        new(new(), connections.ToList(), new()) { Groups = groups.ToList() };

    private static ConnectionGroup Group(string name, params ConnectionConfig[] members) =>
        new(Guid.NewGuid(), name, members.Select(m => m.Id).ToList());

    // ---- group editor ----

    [Fact]
    public void GroupEditor_NameClash_ShowsAsTyped_EmptyNameOnlyAfterOk()
    {
        var a = Conn("A");
        var vm = new GroupEditorViewModel(Config([a], Group("Billing", a)), null);

        vm.Name = "";
        Assert.False(vm.HasNameError);

        vm.Name = "billing";
        Assert.Equal("A group named \"billing\" already exists.", vm.NameError);
        Assert.True(vm.HasNameError);

        vm.Name = "";
        Assert.False(vm.TryConfirm());
        Assert.Equal("The group name cannot be empty.", vm.NameError);
        Assert.False(vm.ShowErrorBand);   // the line under the name box says it; the band is for the members

        vm.Name = "Fresh";
        Assert.Null(vm.NameError);
    }

    [Fact]
    public void GroupEditor_EditingAGroup_DoesNotClashWithItself()
    {
        var a = Conn("A");
        var existing = Group("Billing", a);
        var vm = new GroupEditorViewModel(Config([a], existing), existing);

        vm.Name = "billing";

        Assert.Null(vm.NameError);
    }

    [Fact]
    public void GroupEditor_MemberProblem_ShowsInTheBand_NotUnderTheName()
    {
        var lite = Conn("Lite", DbKind.Sqlite);
        var pg = Conn("PG");
        var vm = new GroupEditorViewModel(Config([lite, pg]), null) { Name = "Mixed" };
        vm.Selected.Add(lite);
        vm.Selected.Add(pg);   // not reachable through the lists, but the rule is enforced

        Assert.False(vm.TryConfirm());

        Assert.True(vm.ShowErrorBand);
        Assert.False(vm.HasNameError);
    }

    [Fact]
    public void GroupEditor_Filter_NarrowsTheCandidates_ByNameFolderHostOrDatabase()
    {
        var shop = Conn("Shop", folder: "Production", db: "shop_prod");
        var notes = Conn("Notes", folder: "Staging", db: "notes");
        var vm = new GroupEditorViewModel(Config([shop, notes]), null);
        Assert.Equal(2, vm.Candidates.Count);

        vm.CandidateFilter = "prod";   // the folder of one, the database of the same one
        Assert.Equal([shop], vm.Candidates);

        vm.CandidateFilter = "NOTES";
        Assert.Equal([notes], vm.Candidates);

        vm.CandidateFilter = "localhost";   // the host both share
        Assert.Equal(2, vm.Candidates.Count);

        vm.CandidateFilter = "";
        Assert.Equal(2, vm.Candidates.Count);
    }

    [Fact]
    public void GroupEditor_HiddenCount_SaysHowManyOtherEngineConnectionsAreNotOffered()
    {
        var pg = Conn("PG");
        var lite1 = Conn("Lite1", DbKind.Sqlite);
        var lite2 = Conn("Lite2", DbKind.Sqlite);
        var vm = new GroupEditorViewModel(Config([pg, lite1, lite2]), null);
        Assert.Equal(0, vm.HiddenCount);
        Assert.Null(vm.HiddenText);

        vm.Add(pg);

        Assert.Equal(2, vm.HiddenCount);
        Assert.Equal("2 connections of another engine not shown.", vm.HiddenText);
        Assert.Equal("PostgreSQL only", vm.EngineOnlyText);
    }

    [Fact]
    public void GroupEditor_AddRange_AddsSeveralInOrder_SkippingTheOtherEngine_AndRemoveRangeTakesThemOut()
    {
        var a = Conn("A");
        var b = Conn("B");
        var lite = Conn("Lite", DbKind.Sqlite);
        var vm = new GroupEditorViewModel(Config([a, b, lite]), null);

        vm.AddRange([b, a, lite]);

        Assert.Equal([a, b], vm.Selected.OrderBy(c => c.Name));   // the first one fixed the engine, Lite was refused
        Assert.DoesNotContain(lite, vm.Selected);
        Assert.Equal("MEMBERS (2)", vm.MembersTitle);

        vm.RemoveRange([a, b]);

        Assert.Empty(vm.Selected);
        Assert.True(vm.IsMembersEmpty);
    }

    [Fact]
    public void GroupEditor_MoveTo_PlacesAMemberAtTheDroppedPosition()
    {
        var a = Conn("A");
        var b = Conn("B");
        var c = Conn("C");
        var vm = new GroupEditorViewModel(Config([a, b, c]), null);
        vm.AddRange([a, b, c]);

        vm.MoveTo(c, 0);
        Assert.Equal([c, a, b], vm.Selected);
        vm.MoveTo(c, 99);   // clamped to the end
        Assert.Equal([a, b, c], vm.Selected);
    }

    [Fact]
    public void GroupEditor_MembersHint_GuidesUntilTwoMembers()
    {
        var a = Conn("A");
        var b = Conn("B");
        var vm = new GroupEditorViewModel(Config([a, b]), null);

        Assert.True(vm.IsMembersEmpty);
        Assert.Contains("first one sets the engine", vm.MembersHint);

        vm.Add(a);
        Assert.True(vm.NeedsMoreMembers);
        Assert.False(vm.IsMembersEmpty);

        vm.Add(b);
        Assert.Null(vm.MembersHint);
        Assert.False(vm.NeedsMoreMembers);
        Assert.Equal("Create group", vm.OkText);
    }

    // ---- schema differences ----

    [Fact]
    public void SchemaDiff_GroupsFindingsByTable_AndCountsBySeverity()
    {
        var a = new SchemaDiff("A", ["logs"], [], [new ColumnDiff("users", "email", "Missing", "TEXT", null), new ColumnDiff("users", "age", "Type", "INTEGER", "TEXT")]);
        var b = new SchemaDiff("B", [], ["logs"], [new ColumnDiff("users", "email", "Extra", null, "TEXT")]);
        var vm = new SchemaDiffViewModel("G", [a, b], []);

        var tables = vm.Members[0].Tables;
        Assert.Equal(["logs", "users"], tables.Select(t => t.Table));
        Assert.Equal(["Missing column", "Type differs"], tables[1].Findings.Select(f => f.Label).Order());
        Assert.Equal("age", tables[1].Findings.First(f => f.Label == "Type differs").Column);

        Assert.Equal(2, vm.MissingCount);   // table logs + column email
        Assert.Equal(2, vm.ExtraCount);     // table logs + column email
        Assert.Equal(1, vm.DifferentCount);
        Assert.Equal("1 type difference", vm.DifferentText);
    }

    [Fact]
    public void SchemaDiff_CleanDatabases_FoldIntoOneLine_AndAreNotCards()
    {
        var differs = new SchemaDiff("A", ["t"], [], []);
        var clean1 = new SchemaDiff("B", [], [], []);
        var clean2 = new SchemaDiff("C", [], [], []);
        var vm = new SchemaDiffViewModel("G", [clean1, differs, clean2], []);

        Assert.Equal(["A"], vm.DifferingMembers.Select(m => m.Database));
        Assert.Equal("Identical to the others: B, C", vm.IdenticalText);
        Assert.Equal(["B", "A", "C"], vm.Members.Select(m => m.Database));   // the report keeps the group order
        Assert.True(vm.ShowCards);
        Assert.False(vm.IsIdentical);
    }

    [Fact]
    public void SchemaDiff_ALongReport_StartsWithOnlyTheFirstCardOpen()
    {
        var diffs = Enumerable.Range(1, 5).Select(i => new SchemaDiff("DB" + i, ["t" + i], [], [])).ToList();
        var vm = new SchemaDiffViewModel("G", diffs, []);

        Assert.True(vm.DifferingMembers[0].IsExpanded);
        Assert.All(vm.DifferingMembers.Skip(1), m => Assert.False(m.IsExpanded));

        var few = new SchemaDiffViewModel("G", diffs.Take(3).ToList(), []);
        Assert.All(few.DifferingMembers, m => Assert.True(m.IsExpanded));
    }

    [Fact]
    public void SchemaDiff_NothingToCompare_IsAnEmptyReport_AndReportTextStillSaysWhy()
    {
        var vm = new SchemaDiffViewModel("G", [new SchemaDiff("A", [], [], [])], [new UnreachableMember("B", "boom")]);

        Assert.True(vm.IsEmptyReport);
        Assert.False(vm.ShowCards);
        Assert.Contains("Not loaded: B - boom", vm.ReportText);
        Assert.Contains("nothing to compare", vm.ReportText);
    }

    [Fact]
    public void SchemaDiff_ReportText_ListsEachDifferingDatabaseTableByTable()
    {
        var a = new SchemaDiff("A", ["logs"], [], [new ColumnDiff("users", "email", "Missing", "TEXT", null)]);
        var vm = new SchemaDiffViewModel("G", [a, new SchemaDiff("B", [], [], [])], []);

        var text = vm.ReportText;

        Assert.Contains("A (2 differences)", text);
        Assert.Contains("Missing table: logs", text);
        Assert.Contains("Missing column: users.email  in the others: TEXT", text);
        Assert.Contains("Identical to the others: B", text);
    }
}
