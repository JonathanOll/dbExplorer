using DbExplorer.App.ViewModels;
using Microsoft.Data.Sqlite;

namespace DbExplorer.App.Tests.Upgrade;

/// <summary>
/// The read-only guarantee of the group run, from the app side: whatever an upgrade script looks like, the existing
/// "Script on group" command refuses it before any member is resolved or connected (the upgrade has its own runner).
/// </summary>
public class UpgradeGroupRunIsolationTests : IDisposable
{
    private readonly GroupSwitchFixture _g = new();

    public void Dispose() => _g.Dispose();

    private static long Count(string file, string sql)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("IF NOT EXISTS (SELECT 1 FROM big) BEGIN CREATE NONCLUSTERED INDEX IX_big_label ON big(label) END")]
    [InlineData("CREATE INDEX IX_big_label ON big(label)")]
    [InlineData("ALTER TABLE big ADD c int")]
    [InlineData("UPDATE big SET label = 'x'")]
    public async Task GroupRun_ThroughRunScriptOnGroupAsync_StillRefusesEveryUpgradeStyleWrite(string script)
    {
        var tab = await _g.NewScript(_g.A);
        tab.ActiveGroup = _g.Items;
        var members = new[] { _g.A, _g.B, _g.Prod };
        var resolvedBefore = members.Select(m => _g.ResolveCount(m.Id)).ToArray();

        tab.Text = script;
        tab.CaretOffset = 0;
        await tab.RunScriptOnGroupAsync();

        var run = Assert.IsType<GroupRunViewModel>(tab.GroupRun);
        Assert.Equal(GroupRunViewModel.ReadOnlyNotice, run.Notice);
        Assert.Empty(run.Members);                       // no member ran
        Assert.Empty(tab.GroupConnectionsOpened);        // nothing was connected
        Assert.Equal(resolvedBefore, members.Select(m => _g.ResolveCount(m.Id)).ToArray());   // nobody was even asked for
        foreach (var m in members)                        // the files are untouched
        {
            Assert.Equal(0, Count(m.Database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'IX_big_label'"));
            Assert.Equal(0, Count(m.Database, "SELECT COUNT(*) FROM big WHERE label = 'x'"));
            Assert.Equal(2, Count(m.Database, "SELECT COUNT(*) FROM pragma_table_info('big')"));
        }
    }
}
