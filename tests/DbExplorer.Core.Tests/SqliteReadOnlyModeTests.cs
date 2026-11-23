using DbExplorer.Core.Drivers;
using DbExplorer.Core.Model;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

public class SqliteReadOnlyModeTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "dbx-ro-" + Guid.NewGuid().ToString("N") + ".db");

    public SqliteReadOnlyModeTests()
    {
        using var c = new SqliteConnection($"Data Source={_file};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1)";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    private static ConnectionConfig Config(string file, bool readOnly) =>
        new(Guid.NewGuid(), "Lite", DbKind.Sqlite, null, null, null, file, null, null, false, false, readOnly);

    private static async Task<Exception?> TryWriteAsync(System.Data.Common.DbConnection c)
    {
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO t VALUES (2)";
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [Fact]
    public async Task ReadOnlyConfig_OpensTheFileReadOnly_SoTheDatabaseRefusesWrites()
    {
        await using var c = new SqliteDriver().CreateConnection(Config(_file, readOnly: true), null);

        var error = await TryWriteAsync(c);

        Assert.IsType<SqliteException>(error);
        Assert.Contains("readonly", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WritableConfig_StillWrites()
    {
        await using var c = new SqliteDriver().CreateConnection(Config(_file, readOnly: false), null);

        Assert.Null(await TryWriteAsync(c));
    }

    [Fact]
    public async Task ReadOnlyConfig_StillReadsAndLoadsTheSchema()
    {
        await using var c = new SqliteDriver().CreateConnection(Config(_file, readOnly: true), null);

        var schema = await new SqliteDriver().LoadSchemaAsync(c, CancellationToken.None);

        Assert.Equal("t", Assert.Single(schema).Ref.Name);
    }

    [Fact]
    public async Task ReadOnlyConfig_OpensAWalDatabase_WhoseSideFilesAreGone()
    {
        var wal = Path.Combine(Path.GetTempPath(), "dbx-ro-wal-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={wal};Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (id INTEGER PRIMARY KEY); INSERT INTO t VALUES (1)";
                cmd.ExecuteNonQuery();
            }   // the last connection closed: the -wal and -shm files are checkpointed away
            SqliteConnection.ClearAllPools();
            Assert.False(File.Exists(wal + "-wal"));

            await using var ro = new SqliteDriver().CreateConnection(Config(wal, readOnly: true), null);
            await ro.OpenAsync();
            await using var read = ro.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM t";

            Assert.Equal(1L, await read.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { wal, wal + "-wal", wal + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task ReadOnlyConfig_StillFailsOnAMissingFile()
    {
        await using var c = new SqliteDriver().CreateConnection(Config(_file + ".missing", readOnly: true), null);

        await Assert.ThrowsAsync<SqliteException>(() => c.OpenAsync());
    }
}
