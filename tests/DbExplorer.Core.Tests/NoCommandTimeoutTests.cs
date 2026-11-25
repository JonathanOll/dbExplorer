using DbExplorer.Core.Model;
using DbExplorer.Core.Querying;
using DbExplorer.Core.Scripting;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>User-facing statements have no automatic timeout (the providers default to 30 s), except SQLite where 0 would mean "no lock wait".</summary>
public class NoCommandTimeoutTests
{
    [Fact]
    public async Task ScriptRunner_StatementHasNoTimeout()
    {
        var fake = new FakeDbConnection(affected: 1);
        await foreach (var _ in ScriptRunner.RunAsync(fake, ScriptSplitter.Split("SELECT 1", DbKind.SqlServer), new ScriptOptions(Kind: DbKind.SqlServer), CancellationToken.None)) { }

        Assert.Equal(0, fake.Commands.Single(c => c.CommandText == "SELECT 1").CommandTimeout);
    }

    [Fact]
    public void Unbounded_SetsZero_ExceptOnSqlite()
    {
        using var fake = new FakeDbConnection(affected: 1);
        using var generic = fake.CreateCommand();
        CommandTimeouts.Unbounded(generic);
        Assert.Equal(0, generic.CommandTimeout);

        using var sqlite = new SqliteConnection("Data Source=:memory:");
        using var cmd = sqlite.CreateCommand();
        var before = cmd.CommandTimeout;
        CommandTimeouts.Unbounded(cmd);
        Assert.Equal(before, cmd.CommandTimeout);
        Assert.NotEqual(0, cmd.CommandTimeout);
    }
}
