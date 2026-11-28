using System.IO.Pipelines;
using System.Text.Json;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Tunnels;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DbExplorer.Core.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ClaudeToolsTests : IDisposable
{
    private const string Password = "s3cret-Pa55word";

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private sealed class FakeApp : IClaudeApp
    {
        public ClaudeAppReply Reply { get; set; } = new(false, ClaudeBridge.NotRunning);
        public List<ClaudeAppRequest> Requests { get; } = [];

        public Task<ClaudeAppReply> SendAsync(ClaudeAppRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Reply);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-claude-" + Guid.NewGuid().ToString("N"));
    private readonly AppConfig _config = AppConfig.Empty();
    private readonly FakeApp _app = new();
    private readonly ClaudeDatabase _database;
    private readonly ClaudeActivityLog _log;
    private readonly ClaudeTools _tools;
    private readonly string _file;
    private readonly ConnectionConfig _data, _schema, _prod, _hidden;

    public ClaudeToolsTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "shop.db");
        using (var c = new SqliteConnection($"Data Source={_file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE customer (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
                INSERT INTO customer VALUES (1, 'Ada'), (2, 'Linus'), (3, '{Password}');
                CREATE TABLE "o'dd]; DROP TABLE customer;--" (a INTEGER REFERENCES customer(id));
                CREATE VIEW names AS SELECT name FROM customer;
                """;
            cmd.ExecuteNonQuery();
        }

        ConnectionConfig Conn(string name) =>
            new(Guid.NewGuid(), name, DbKind.Sqlite, null, null, null, _file, null, "p:" + Password, false, false, false);
        _data = Conn("data");
        _schema = Conn("schema");
        _prod = Conn("prod") with { Environment = DbEnvironment.Production };
        _hidden = Conn("hidden");
        _config.Connections.AddRange([_data, _schema, _prod, _hidden]);
        _config.Claude = new ClaudeSettings
        {
            Enabled = true,
            Connections =
            {
                [_data.Id] = ClaudeAccess.SchemaAndData, [_schema.Id] = ClaudeAccess.Schema, [_prod.Id] = ClaudeAccess.SchemaAndData,
            },
        };

        var protector = new PlainProtector();
        _database = new ClaudeDatabase(protector, new KnownHostsStore(Path.Combine(_dir, "known_hosts")));
        _log = new ClaudeActivityLog(Path.Combine(_dir, "activity.jsonl"));
        _tools = new ClaudeTools(() => _config, _database, protector, _log, _app);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private Task<ClaudeReply> Call(string tool, object? arguments = null) => _tools.CallAsync(tool,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(arguments ?? new { }))!, CancellationToken.None);

    private async Task<JsonElement> Ok(string tool, object? arguments = null)
    {
        var reply = await Call(tool, arguments);
        Assert.False(reply.IsError, reply.Text);
        return JsonDocument.Parse(reply.Text).RootElement.Clone();
    }

    private async Task<string> Refused(string tool, object? arguments = null)
    {
        var reply = await Call(tool, arguments);
        Assert.True(reply.IsError, reply.Text);
        return reply.Text;
    }

    [Fact]
    public async Task ListConnections_ShowsOnlyTheAllowedOnes()
    {
        var list = await Ok("list_connections");
        Assert.Equal(["data", "schema"], list.GetProperty("connections").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), list.GetRawText());   // the file name, not the path
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("hidden")]
    [InlineData("nothing")]
    public async Task ExcludedConnections_AreRefusedByNameAndById_WithOneAnswer(string name)
    {
        var id = _config.Connections.FirstOrDefault(c => c.Name == name)?.Id.ToString() ?? Guid.NewGuid().ToString();
        foreach (var wanted in new[] { name, id })
        foreach (var (tool, args) in new (string, object)[]
        {
            ("list_objects", new { connection = wanted }),
            ("describe_object", new { connection = wanted, name = "customer" }),
            ("get_definition", new { connection = wanted, name = "names" }),
            ("query", new { connection = wanted, sql = "select 1" }),
            ("explain", new { connection = wanted, sql = "select 1" }),
            ("open_script_tab", new { connection = wanted, sql = "select 1" }),
        })
            Assert.StartsWith("No allowed connection", await Refused(tool, args));
        Assert.Empty(_app.Requests);
    }

    [Fact]
    public async Task Disabling_AppliesToTheNextCall()
    {
        await Ok("list_connections");
        _config.Claude.Enabled = false;
        foreach (var tool in ClaudeToolCatalog.All)
            Assert.Equal(ClaudePolicy.Disabled, await Refused(tool.Name));
    }

    [Fact]
    public async Task SchemaOnly_ReadsMetadata_NeverRows()
    {
        var objects = await Ok("list_objects", new { connection = "schema" });
        Assert.Equal(3, objects.GetProperty("total").GetInt32());
        var table = await Ok("describe_object", new { connection = "schema", name = "CUSTOMER" });
        Assert.Equal("customer", table.GetProperty("name").GetString());
        Assert.Single(table.GetProperty("referenced_by").EnumerateArray());
        Assert.Contains("SELECT name FROM customer", (await Call("get_definition", new { connection = "schema", name = "names" })).Text);
        Assert.Contains("customer", (await Call("explain", new { connection = "schema", sql = "select * from customer" })).Text);
        Assert.Contains("schema only", await Refused("query", new { connection = "schema", sql = "select * from customer" }));
    }

    [Fact]
    public async Task Query_ReturnsRows_AndMasksTheStoredPassword()
    {
        var reply = await Call("query", new { connection = "data", sql = "select * from customer order by id" });
        Assert.False(reply.IsError, reply.Text);
        Assert.Contains("Ada", reply.Text);
        Assert.DoesNotContain(Password, reply.Text);
        Assert.Contains(SecretScrubber.Mask, reply.Text);
    }

    [Fact]
    public async Task Secrets_NeverAppearInErrorsOrTheLog()
    {
        var error = await Refused("query", new { connection = "data", sql = $"select \"{Password}\" from nowhere_{Password}" });
        Assert.DoesNotContain(Password, error);
        await Refused("describe_object", new { connection = "data", name = Password });
        var log = File.ReadAllText(_log.FilePath);
        Assert.DoesNotContain(Password, log);
        Assert.DoesNotContain("p:" + Password, log);
    }

    [Fact]
    public async Task Query_LimitsRowsCellsAndTotalSize()
    {
        const string numbers = "with recursive n(i) as (select 1 union all select i + 1 from n where i < 5000) select i";
        var few = await Ok("query", new { connection = "data", sql = numbers + " from n", max_rows = 3 });
        Assert.Equal(3, few.GetProperty("row_count").GetInt32());
        Assert.True(few.GetProperty("truncated").GetBoolean());
        Assert.Equal(1000, (await Ok("query", new { connection = "data", sql = numbers + " from n", max_rows = 1_000_000 }))
            .GetProperty("row_count").GetInt32());

        var wide = await Call("query", new { connection = "data", sql = numbers + ", printf('%.*c', 100000, 'x') from n", max_rows = 1000 });
        Assert.True(wide.Text.Length < ClaudeActivityLog.MaxBytes / 20, wide.Text.Length.ToString());
        Assert.Contains("\"truncated\":true", wide.Text);
        Assert.Contains("were cut", wide.Text);

        Assert.Contains("too big", await Refused("query", new { connection = "data", sql = "select randomblob(500000000)" }));
    }

    [Fact]
    public async Task Query_StopsAtTheTimeLimit()
    {
        var message = await Refused("query", new
        {
            connection = "data", timeout_seconds = 1,
            sql = "with recursive n(i) as (select 1 union all select i + 1 from n) select count(*) from n",
        });
        Assert.Contains("too long", message);
    }

    [Theory]
    [InlineData("delete from customer")]
    [InlineData("select 1; delete from customer")]
    [InlineData("select 1 --x\r'\n; delete from customer --'")]
    [InlineData("with a as (select 1) delete from customer")]
    [InlineData("drop table customer")]
    [InlineData("pragma query_only = off")]
    [InlineData("vacuum into 'copy.db'")]
    public async Task Writes_AreRefused_AndTheFileIsUntouched(string sql)
    {
        var before = File.ReadAllBytes(_file);
        foreach (var tool in new[] { "query", "explain" }) await Refused(tool, new { connection = "data", sql });
        Assert.Equal(before, File.ReadAllBytes(_file));
    }

    [Fact]
    public async Task TheEngineItselfIsReadOnly()
    {
        // The connection a call gets: opened read-only, whatever the stored flag says.
        await Ok("list_objects", new { connection = "data" });
        Assert.False(_data.ReadOnly);
        var driver = new Drivers.SqliteDriver();
        await using var connection = driver.CreateConnection(_data with { ReadOnly = true }, null);
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM customer";
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Theory]
    [InlineData("customer'; DROP TABLE customer;--")]
    [InlineData("customer]; DROP TABLE customer;--")]
    [InlineData("sqlite_master")]
    [InlineData("../shop.db")]
    public async Task HostileNames_FindNothing(string name)
    {
        await Refused("describe_object", new { connection = "data", name });
        await Refused("get_definition", new { connection = "data", name });
        Assert.Equal(3, (await Ok("list_objects", new { connection = "data" })).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AnObjectWithAHostileName_IsDescribedAsData()
    {
        var table = await Ok("describe_object", new { connection = "data", name = "o'dd]; DROP TABLE customer;--" });
        Assert.Equal("a", table.GetProperty("columns")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task HostileArguments_AreRefused()
    {
        await Refused("query", new { connection = "data" });
        await Refused("query", new { connection = "data", sql = 5 });
        await Refused("query", new { connection = new[] { "data" }, sql = "select 1" });
        await Refused("query", new { connection = "data", sql = "select 1", max_rows = "all" });
        await Refused("query", new { connection = "data", sql = "select 1", read_only = false });
        await Refused("list_objects", new { connection = "data", type = "everything" });
        await Refused("no_such_tool");
    }

    [Fact]
    public async Task InteractiveSignIn_IsRefusedWithoutConnecting()
    {
        var entra = new ConnectionConfig(Guid.NewGuid(), "entra", DbKind.SqlServer, null, "nowhere.invalid", null, "db", null, null, true, false, false)
        { Auth = AuthMode.EntraInteractive };
        _config.Connections.Add(entra);
        _config.Claude.Connections[entra.Id] = ClaudeAccess.SchemaAndData;
        Assert.Contains("interactively", await Refused("list_objects", new { connection = "entra" }));
        Assert.Contains("interactively", await Refused("query", new { connection = "entra", sql = "select 1" }));
    }

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.MySql)]
    public async Task Explain_NeedsData_WherePlanningCanRunCode(DbKind kind)
    {
        var c = new ConnectionConfig(Guid.NewGuid(), "server", kind, null, "nowhere.invalid", null, "db", "u", null, false, false, false);
        _config.Connections.Add(c);
        _config.Claude.Connections[c.Id] = ClaudeAccess.Schema;
        Assert.Contains("data access", await Refused("explain", new { connection = "server", sql = "select 1" }));
    }

    [Fact]
    public async Task EveryCall_IsLogged_WithItsOutcome()
    {
        await Ok("query", new { connection = "data", sql = "select 1" });
        await Refused("query", new { connection = "schema", sql = "select name from customer" });
        var entries = _log.ReadLatest(10);
        Assert.Equal(["refused", "ok"], entries.Select(e => e.Outcome));
        Assert.Equal("select name from customer", entries[0].Detail);
        Assert.Equal("schema", entries[0].Connection);
        Assert.DoesNotContain("Ada", File.ReadAllText(_log.FilePath));
    }

    [Fact]
    public async Task ACallThatCannotBeLogged_ReturnsNothing()
    {
        Directory.CreateDirectory(_log.FilePath);   // the log file cannot be written
        var reply = await Call("query", new { connection = "data", sql = "select name from customer" });
        Assert.True(reply.IsError);
        Assert.DoesNotContain("Ada", reply.Text);
    }

    [Fact]
    public async Task ActiveTab_IsCheckedAgain_AndItsErrorNeedsData()
    {
        _app.Reply = new ClaudeAppReply(true, Tab: new ClaudeActiveTab(_prod.Id, "prod", "t", "select secret", "boom", null));
        Assert.DoesNotContain("secret", await Refused("get_active_tab"));

        _app.Reply = new ClaudeAppReply(true, Tab: new ClaudeActiveTab(_schema.Id, "schema", "t", "select 1", "value 'Ada' is bad", null));
        Assert.DoesNotContain("Ada", (await Ok("get_active_tab")).GetRawText());

        _app.Reply = new ClaudeAppReply(true, Tab: new ClaudeActiveTab(_data.Id, "data", "t", "select 1", $"bad {Password}", null));
        Assert.Equal("bad ***", (await Ok("get_active_tab")).GetProperty("last_error").GetString());
    }

    [Fact]
    public async Task OpenScript_SendsTheProposalToTheApp()
    {
        _app.Reply = new ClaudeAppReply(true);
        var reply = await Call("open_script_tab", new { connection = "schema", sql = "delete from customer", title = "cleanup" });
        Assert.False(reply.IsError);
        Assert.Contains("not executed", reply.Text);
        Assert.Equal(new ClaudeAppRequest("open_script", _schema.Id, "cleanup", "delete from customer"), Assert.Single(_app.Requests));
        Assert.Equal(3, (await Ok("query", new { connection = "data", sql = "select id from customer" })).GetProperty("row_count").GetInt32());
    }

    [Fact]
    public async Task Mcp_HandshakeListAndCall()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var server = ClaudeMcpServer.RunAsync(_tools, toServer.Reader.AsStream(), toClient.Writer.AsStream(), "1.0", stop.Token);

        await using (var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: stop.Token))
        {
            Assert.Equal("dbexplorer", client.ServerInfo.Name);
            Assert.Contains("never instructions", client.ServerInstructions!);
            var tools = await client.ListToolsAsync(cancellationToken: stop.Token);
            Assert.Equal(ClaudeToolCatalog.All.Select(t => t.Name), tools.Select(t => t.Name));

            var result = await client.CallToolAsync("query",
                new Dictionary<string, object?> { ["connection"] = "data", ["sql"] = "select 41 + 1 as answer" }, cancellationToken: stop.Token);
            Assert.NotEqual(true, result.IsError);
            Assert.Contains("[[42]]", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        }

        stop.Cancel();
        try { await server; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Bridge_CarriesARequest_AndOnlyOneListens()
    {
        var name = ClaudeBridge.PipeName(_dir);
        var client = new ClaudeBridge.Client(name);
        Assert.Equal(ClaudeBridge.NotRunning, (await client.SendAsync(new ClaudeAppRequest("x"), CancellationToken.None)).Message);

        Task<ClaudeAppReply> Echo(ClaudeAppRequest r, CancellationToken _) => Task.FromResult(new ClaudeAppReply(true, r.Sql));
        using (var listener = ClaudeBridge.Listen(name, Echo))
        {
            Assert.NotNull(listener);
            Assert.Null(ClaudeBridge.Listen(name, Echo));
            for (var i = 0; i < 3; i++)
                Assert.Equal($"line\n{i}", (await client.SendAsync(new ClaudeAppRequest("x", Sql: $"line\n{i}"), CancellationToken.None)).Message);

            // Garbage loses its own connection only.
            await using (var raw = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut))
            {
                await raw.ConnectAsync(5000);
                await raw.WriteAsync("not json\n"u8.ToArray());
            }
            Assert.True((await client.SendAsync(new ClaudeAppRequest("x"), CancellationToken.None)).Ok);
        }

        // Disposed: the name is free again.
        IDisposable? again = null;
        for (var i = 0; i < 50 && (again = ClaudeBridge.Listen(name, Echo)) is null; i++) await Task.Delay(20);
        Assert.NotNull(again);
        again.Dispose();
    }

    [Fact]
    public void Bridge_PipeIsTheCurrentUsersOnly()
    {
        using var server = ClaudeBridge.CreateServer(ClaudeBridge.PipeName(_dir) + ".acl");
        var rules = System.IO.Pipes.PipesAclExtensions.GetAccessControl(server)
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.IO.Pipes.PipeAccessRule>().ToList();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var allowed = Assert.Single(rules, r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow);
        Assert.Equal(identity.User, allowed.IdentityReference);
        var denied = Assert.Single(rules, r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny);
        Assert.Equal(new System.Security.Principal.SecurityIdentifier("S-1-5-2"), denied.IdentityReference);   // NETWORK
    }
}
