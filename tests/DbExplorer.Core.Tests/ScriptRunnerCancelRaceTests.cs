using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using Microsoft.Data.Sqlite;

namespace DbExplorer.Core.Tests;

/// <summary>
/// Cancelling a script at the very moment its statement starts must still stop it. sqlite3_interrupt is a no-op when no statement
/// is running yet, so a cancel landing between "the command is prepared" and "sqlite3_step starts" used to be lost: the endless
/// statement then ran for ever in the background, holding the database file (a script tab closed right after a run was started
/// leaked a busy thread and a locked file).
/// </summary>
public class ScriptRunnerCancelRaceTests
{
    // Never returns a row: only an interrupt can end it.
    private const string Endless = "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c) SELECT n FROM c WHERE n < 0";

    [Fact]
    public async Task CancellingAtAnyMomentOfTheStart_AlwaysStopsTheStatement()
    {
        const int rounds = 200;
        var dir = Path.Combine(Path.GetTempPath(), "dbx-cancel-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var stmts = ScriptSplitter.Split(Endless, DbKind.Sqlite);
            var hung = 0;
            await Parallel.ForEachAsync(Enumerable.Range(0, rounds), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
            {
                await using var conn = new SqliteConnection($"Data Source={Path.Combine(dir, i + ".db")};Pooling=False");
                await conn.OpenAsync();
                using var cts = new CancellationTokenSource();
                var run = Task.Run(async () =>
                {
                    await foreach (var _ in ScriptRunner.RunAsync(conn, stmts, new ScriptOptions(), cts.Token)) { }
                });
                // Spread the cancel over the whole start-up of the statement (0 .. ~2 ms).
                var spins = Random.Shared.Next(0, 20_000);
                for (var s = 0; s < spins; s++) Thread.SpinWait(1);
                cts.Cancel();
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5))) != run) Interlocked.Increment(ref hung);
            });

            Assert.Equal(0, hung);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { /* a hung statement (the failure) still holds a file */ }
        }
    }
}
