using System.Data;
using System.Data.Common;
using DbExplorer.Core.Drivers;
using DbExplorer.Core.Groups;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Tests;

/// <summary>
/// A connection that behaves like a SQL Server one for statistics: it reports server messages while a command runs, refuses a
/// command without the pending transaction (as SqlClient does) and records the order of everything.
/// </summary>
internal sealed class MessagingConnection : DbConnection, IServerMessageSource
{
    private ConnectionState _state = ConnectionState.Open;
    private bool _transactionPending;

    public event Action<string>? MessageReceived;

    /// <summary>Command texts in execution order; "[tx]" marks a command that ran inside the read-only transaction.</summary>
    public List<string> Log { get; } = [];

    /// <summary>What the server says while a command text runs.</summary>
    public Func<string, string[]> Says { get; set; } = _ => [];

    /// <summary>A command text that fails (after its messages were sent).</summary>
    public string? FailOn { get; set; }

    public bool StatisticsOn { get; private set; }
    public int Handlers => MessageReceived?.GetInvocationList().Length ?? 0;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        _transactionPending = true;
        Log.Add("BEGIN TRANSACTION");
        return new Tx(this);
    }

    protected override DbCommand CreateDbCommand() => new Cmd(this);

    internal void Raise(string text) => MessageReceived?.Invoke(text);

    private sealed class Tx(MessagingConnection c) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => c;
        public override void Commit() => c._transactionPending = false;
        public override void Rollback()
        {
            c._transactionPending = false;
            c.Log.Add("ROLLBACK");
        }
    }

    private sealed class Cmd(MessagingConnection c) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = c;
        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParameterCollection();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        private void Begin()
        {
            // SqlClient: "ExecuteReader requires the command to have a transaction when the connection is in a pending transaction".
            if (c._transactionPending && DbTransaction is null)
                throw new InvalidOperationException("The command needs the pending transaction.");
            c.Log.Add((DbTransaction is null ? "" : "[tx] ") + CommandText);
        }

        public override int ExecuteNonQuery()
        {
            Begin();
            if (CommandText.StartsWith("SET STATISTICS", StringComparison.Ordinal)) c.StatisticsOn = CommandText.Contains(" ON;");
            return 0;
        }

        public override object? ExecuteScalar()
        {
            Begin();
            return 1;
        }

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            Begin();
            foreach (var message in c.Says(CommandText)) c.Raise(message);
            if (c.FailOn is { } fail && CommandText.Contains(fail, StringComparison.Ordinal)) throw new InvalidOperationException("boom");
            var table = new DataTable();
            table.Columns.Add("n", typeof(int));
            table.Rows.Add(1);
            return new DataTableReader(table);
        }
    }
}

internal sealed class MessagingDriver(DbKind kind, Func<MessagingConnection> create) : IDbDriver
{
    public DbKind Kind => kind;
    public string ParamPrefix => "@";
    public string Quote(string identifier) => identifier;
    public string QuoteTable(TableRef t) => t.Name;
    public string CastToText(string quotedColumn) => quotedColumn;
    public string EscapeLike(string text) => text;
    public string PageSql(string selectSql, string orderBy, int offset, int limit) => selectSql;
    public DbConnection CreateConnection(ConnectionConfig cfg, string? plainPassword) => create();
    public Task<IReadOnlyList<TableInfo>> LoadSchemaAsync(DbConnection c, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TableInfo>>([]);
}

public class StatisticsRunTests
{
    private const string IoLine =
        "Table 'Invoices'. Scan count 1, logical reads 5, physical reads 0, read-ahead reads 0, lob logical reads 0, lob physical reads 0, lob read-ahead reads 0.";
    private const string ExecLine = "SQL Server Execution Times:\n   CPU time = 15 ms, elapsed time = 20 ms.";

    private static ConnectionConfig Config(DbKind kind = DbKind.SqlServer, bool readOnly = false) =>
        new(Guid.NewGuid(), "c-" + Guid.NewGuid().ToString("N")[..4], kind, null, "h", null, "d", null, null, false, false, false) { ReadOnly = readOnly };

    private static (ScriptConnection Script, MessagingConnection Connection) Open(
        DbKind kind = DbKind.SqlServer, Func<string, string[]>? says = null)
    {
        var connection = new MessagingConnection { Says = says ?? (_ => []) };
        var session = new Session(Config(kind), new MessagingDriver(kind, () => connection), [], null);
        return (new ScriptConnection(session), connection);
    }

    private static async Task<List<StatementResult>> RunAsync(ScriptConnection script, string sql, ScriptOptions o, DbKind kind = DbKind.SqlServer)
    {
        var results = new List<StatementResult>();
        await foreach (var r in script.RunAsync(ScriptSplitter.Split(sql, kind), o, CancellationToken.None)) results.Add(r);
        return results;
    }

    [Fact]
    public async Task Statistics_SetBeforeTheStatements_AndUnsetAfter()
    {
        var (script, c) = Open(says: _ => [IoLine, ExecLine]);

        await RunAsync(script, "SELECT 1; SELECT 2", new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true));

        Assert.Equal(
            ["SET STATISTICS IO ON; SET STATISTICS TIME ON;", "SELECT 1", "SELECT 2", "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"],
            c.Log);
        Assert.False(c.StatisticsOn);
        Assert.Equal(0, c.Handlers);   // the connection is reused: no handler may stay behind
    }

    [Fact]
    public async Task Messages_AreAttributedToTheStatementThatProducedThem()
    {
        var (script, _) = Open(says: sql => sql == "SELECT 1" ? [IoLine, ExecLine] : sql == "SELECT 2" ? ["PRINT output"] : []);

        var results = await RunAsync(script, "SELECT 1; SELECT 2; SELECT 3", new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true));

        Assert.Equal([IoLine, ExecLine], results[0].Messages!.Select(m => m.Text).ToArray());
        Assert.Equal(["PRINT output"], results[1].Messages!.Select(m => m.Text).ToArray());
        Assert.Empty(results[2].Messages!);
        Assert.All(results.SelectMany(r => r.Messages!), m => Assert.True(m.At > DateTimeOffset.Now.AddMinutes(-1)));
    }

    [Fact]
    public async Task StatisticsOff_RunsExactlyAsBefore_NoSetCommands_NoCapture()
    {
        var (script, c) = Open(says: _ => [IoLine]);

        var results = await RunAsync(script, "SELECT 1", new ScriptOptions(Kind: DbKind.SqlServer));

        Assert.Equal(["SELECT 1"], c.Log);
        Assert.Null(results[0].Messages);
        Assert.Equal(0, c.Handlers);
    }

    [Theory]
    [InlineData(DbKind.Sqlite)]
    [InlineData(DbKind.Postgres)]
    public async Task OtherEngines_IgnoreTheFlag(DbKind kind)
    {
        var (script, c) = Open(kind, says: _ => [IoLine]);

        var results = await RunAsync(script, "SELECT 1", new ScriptOptions(Kind: kind, Statistics: true), kind);

        Assert.Equal(["SELECT 1"], c.Log);
        Assert.Null(results[0].Messages);
    }

    [Fact]
    public async Task ReadOnly_SetsStatisticsOutsideTheTransaction_AndStillRefusesWrites()
    {
        var (script, c) = Open(says: _ => [ExecLine]);

        var results = await RunAsync(script, "SELECT 1; DELETE FROM t",
            new ScriptOptions(ReadOnly: true, Kind: DbKind.SqlServer, Statistics: true));

        Assert.Null(results[0].Error);
        Assert.Contains("read-only", results[1].Error);   // the guard is unchanged by the session setting
        Assert.Equal(
            ["SET STATISTICS IO ON; SET STATISTICS TIME ON;", "BEGIN TRANSACTION", "[tx] SELECT 1", "ROLLBACK",
             "SET STATISTICS IO OFF; SET STATISTICS TIME OFF;"],
            c.Log);
        Assert.DoesNotContain(c.Log, l => l.Contains("DELETE"));
    }

    [Fact]
    public async Task FailedStatement_KeepsItsMessages_AndStatisticsAreStillUnset()
    {
        var (script, c) = Open(says: _ => [IoLine]);
        c.FailOn = "SELECT 2";

        var results = await RunAsync(script, "SELECT 1; SELECT 2; SELECT 3", new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true));

        Assert.Equal(2, results.Count);
        Assert.Contains("boom", results[1].Error);
        Assert.Equal([IoLine], results[1].Messages!.Select(m => m.Text).ToArray());
        Assert.False(c.StatisticsOn);
        Assert.Equal(0, c.Handlers);
    }

    [Fact]
    public async Task TwoRuns_DoNotDuplicateMessages_AndStatisticsCanBeSwitchedOffAgain()
    {
        var (script, c) = Open(says: _ => [IoLine]);

        await RunAsync(script, "SELECT 1", new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true));
        var second = await RunAsync(script, "SELECT 1", new ScriptOptions(Kind: DbKind.SqlServer, Statistics: true));
        var off = await RunAsync(script, "SELECT 1", new ScriptOptions(Kind: DbKind.SqlServer));

        Assert.Single(second[0].Messages!);
        Assert.Null(off[0].Messages);
        Assert.False(c.StatisticsOn);
    }

    [Fact]
    public async Task SessionOverload_AlsoSetsAndUnsetsStatistics_BeforeTheReadOnlyTransaction()
    {
        var connection = new MessagingConnection { Says = _ => [ExecLine] };
        var session = new Session(Config(readOnly: true), new MessagingDriver(DbKind.SqlServer, () => connection), [], null);
        var results = new List<StatementResult>();

        await foreach (var r in ScriptRunner.RunAsync(
                           session, ScriptSplitter.Split("SELECT 1", DbKind.SqlServer),
                           new ScriptOptions(ReadOnly: true, Kind: DbKind.SqlServer, Statistics: true), CancellationToken.None))
            results.Add(r);

        Assert.Single(results[0].Messages!);
        Assert.Equal("SET STATISTICS IO ON; SET STATISTICS TIME ON;", connection.Log[0]);
        Assert.Equal("BEGIN TRANSACTION", connection.Log[1]);
    }

    // ---- group runs ----

    [Fact]
    public async Task GroupRun_WithStatistics_ReturnsTheMessagesOfEveryMember()
    {
        var members = new[] { Config(), Config() };
        var runner = new GroupRunner((config, _) => Task.FromResult(new ScriptConnection(new Session(
            config, new MessagingDriver(DbKind.SqlServer, () => new MessagingConnection { Says = _ => [IoLine] }), [], null))));

        var results = await runner.RunAsync(
            members, ScriptSplitter.Split("SELECT 1", DbKind.SqlServer), new GroupRunOptions(Statistics: true), null, CancellationToken.None);

        Assert.All(results, r =>
        {
            Assert.Equal(MemberState.Done, r.State);
            Assert.Equal([IoLine], r.Statements[0].Messages!.Select(m => m.Text).ToArray());
        });
    }

    [Fact]
    public async Task GroupRun_WithStatistics_StillRefusesAWriteBeforeAnyMemberIsContacted()
    {
        var opened = 0;
        var runner = new GroupRunner((config, _) =>
        {
            opened++;
            return Task.FromResult(new ScriptConnection(new Session(config, new MessagingDriver(DbKind.SqlServer, () => new MessagingConnection()), [], null)));
        });

        await Assert.ThrowsAsync<GroupRunRefusedException>(() => runner.RunAsync(
            [Config()], ScriptSplitter.Split("DELETE FROM t", DbKind.SqlServer), new GroupRunOptions(Statistics: true), null, CancellationToken.None));

        Assert.Equal(0, opened);
    }
}
