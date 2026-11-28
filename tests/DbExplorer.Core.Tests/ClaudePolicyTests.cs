using DbExplorer.Core.Claude;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public sealed class ClaudePolicyTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    private static ConnectionConfig Conn(DbKind kind = DbKind.Sqlite) =>
        new(Guid.NewGuid(), "c", kind, null, null, null, "x.db", null, null, false, false, false);

    private static AppConfig With(ConnectionConfig c, ClaudeAccess access, bool enabled = true)
    {
        var config = AppConfig.Empty();
        config.Connections.Add(c);
        config.Claude = new ClaudeSettings { Enabled = enabled, Connections = { [c.Id] = access } };
        return config;
    }

    [Fact]
    public void Defaults_AllowNothing()
    {
        var config = AppConfig.Empty();
        config.Connections.Add(Conn());
        Assert.False(config.Claude.Enabled);
        Assert.Empty(ClaudePolicy.Allowed(config));
        config.Claude.Enabled = true;
        Assert.Empty(ClaudePolicy.Allowed(config));
    }

    [Fact]
    public void Disabled_RefusesAListedConnection()
    {
        var c = Conn();
        Assert.False(ClaudePolicy.For(With(c, ClaudeAccess.SchemaAndData, enabled: false), c).Allowed);
    }

    [Theory]
    [InlineData(ClaudeAccess.Schema, false)]
    [InlineData(ClaudeAccess.SchemaAndData, true)]
    public void Data_IsASeparateGrant(ClaudeAccess access, bool data)
    {
        var c = Conn();
        var grant = ClaudePolicy.For(With(c, access), c);
        Assert.True(grant.Allowed);
        Assert.Equal(data, grant.CanReadData);
    }

    [Fact]
    public void Production_IsRefused_WhateverIsStored()
    {
        var c = Conn() with { Environment = DbEnvironment.Production };
        var config = With(c, ClaudeAccess.SchemaAndData);
        Assert.False(ClaudePolicy.For(config, c).Allowed);
        Assert.Empty(ClaudePolicy.Allowed(config));
    }

    [Fact]
    public void InteractiveSignIn_CannotConnectUnattended()
    {
        Assert.NotNull(ClaudePolicy.CannotConnectUnattended(Conn(DbKind.SqlServer) with { Auth = AuthMode.EntraInteractive }));
        Assert.Null(ClaudePolicy.CannotConnectUnattended(Conn()));
    }

    [Fact]
    public void Settings_SurviveASaveAndLoad_AndOldFilesLoadOff()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dbx-claude-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(file, new PlainProtector());
            File.WriteAllText(file, """{"Folders":[],"Connections":[],"VirtualKeys":{}}""");
            Assert.False(store.Load().Claude.Enabled);
            File.WriteAllText(file, """{"Folders":[],"Connections":[],"VirtualKeys":{},"Claude":null}""");
            Assert.False(store.Load().Claude.Enabled);

            var c = Conn();
            store.Save(With(c, ClaudeAccess.Schema));
            var loaded = store.Load();
            Assert.True(loaded.Claude.Enabled);
            Assert.Equal(ClaudeAccess.Schema, loaded.Claude.Of(c.Id));

            // A number written by hand that names no level grants nothing.
            const string hand = """{"Folders":[],"Connections":[],"VirtualKeys":{},"Claude":{"Enabled":true,"Connections":{"ID":99}}}""";
            File.WriteAllText(file, hand.Replace("ID", c.Id.ToString()));
            Assert.Equal(ClaudeAccess.None, store.Load().Claude.Of(c.Id));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(DbKind.Sqlite, "select * from t")]
    [InlineData(DbKind.Sqlite, "  -- why\n select 1;")]
    [InlineData(DbKind.Postgres, "with a as (select 1) select * from a")]
    [InlineData(DbKind.SqlServer, "SELECT TOP 5 [open], [update] FROM t WITH (NOLOCK) ORDER BY 1 OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY")]
    [InlineData(DbKind.SqlServer, "SELECT CASE WHEN a = 1 THEN 'DROP' ELSE '' END FROM t")]
    [InlineData(DbKind.MySql, "(select 1) union (select 2)")]
    [InlineData(DbKind.MySql, "select sleep from t")]
    public void Sql_PlainReads_Pass(DbKind kind, string sql) => Assert.Null(ClaudeSqlPolicy.Refuse(sql, kind));

    [Theory]
    [InlineData(DbKind.Sqlite, "")]
    [InlineData(DbKind.Sqlite, "delete from t")]
    [InlineData(DbKind.Sqlite, "select 1; select 2")]
    [InlineData(DbKind.Sqlite, "select 1; drop table t")]
    [InlineData(DbKind.Sqlite, "select 1 --x\r'\n; delete from t --'")]
    [InlineData(DbKind.Sqlite, "select 1\0")]
    [InlineData(DbKind.Sqlite, "pragma journal_mode = delete")]
    [InlineData(DbKind.Sqlite, "pragma table_info(t)")]
    [InlineData(DbKind.Sqlite, "attach 'x' as y")]
    [InlineData(DbKind.Sqlite, "select load_extension('x')")]
    [InlineData(DbKind.Sqlite, "select \"load_extension\"('x')")]
    [InlineData(DbKind.Sqlite, "with a as (select 1) insert into t select * from a")]
    [InlineData(DbKind.Postgres, "explain analyze select 1")]
    [InlineData(DbKind.Postgres, "select pg_read_file('/etc/passwd')")]
    [InlineData(DbKind.Postgres, "select pg_catalog.\"pg_read_file\"('x')")]
    [InlineData(DbKind.Postgres, "select query_to_xml('select pg_terminate_backend(1)', true, true, '')")]
    [InlineData(DbKind.Postgres, "select pg_terminate_backend(1)")]
    [InlineData(DbKind.Postgres, "select pg_sleep(100)")]
    [InlineData(DbKind.Postgres, "select dblink_exec('x', 'y')")]
    [InlineData(DbKind.Postgres, "select set_config('transaction_read_only', 'off', true)")]
    [InlineData(DbKind.Postgres, "select 1; commit; delete from t")]
    [InlineData(DbKind.Postgres, "select @$$'$$; commit; delete from t; --'")]
    [InlineData(DbKind.Postgres, "select U&\"pg_read_f\\0069le\"('x')")]
    [InlineData(DbKind.Postgres, "select * from t for update")]
    [InlineData(DbKind.Postgres, "with d as (delete from t returning *) select * from d")]
    [InlineData(DbKind.Postgres, "select * into t2 from t")]
    [InlineData(DbKind.SqlServer, "select 1 commit delete from t")]
    [InlineData(DbKind.SqlServer, "select 1 rollback")]
    [InlineData(DbKind.SqlServer, "select 1 commit")]
    [InlineData(DbKind.SqlServer, "select 1 set showplan_xml off")]
    [InlineData(DbKind.SqlServer, "select 1 use master select 2")]
    [InlineData(DbKind.SqlServer, "select 1 waitfor delay '1:0:0'")]
    [InlineData(DbKind.SqlServer, "select 1 while 1=1 select 2")]
    [InlineData(DbKind.SqlServer, "select 1 revert")]
    [InlineData(DbKind.SqlServer, "select 1 exec xp_cmdshell 'dir'")]
    [InlineData(DbKind.SqlServer, "select * from openrowset('x', 'y', 'z')")]
    [InlineData(DbKind.SqlServer, "select * from t with (tablockx)")]
    [InlineData(DbKind.SqlServer, "select * into #x from t")]
    [InlineData(DbKind.SqlServer, "select next value for s")]
    [InlineData(DbKind.SqlServer, "select * from sys.fn_xe_file_target_read_file('c:\\x*.xel', null, null, null)")]
    [InlineData(DbKind.SqlServer, "select 1\ngo\ndrop table t")]
    [InlineData(DbKind.MySql, "select load_file('/etc/passwd')")]
    [InlineData(DbKind.MySql, "select * from t into outfile '/tmp/x'")]
    [InlineData(DbKind.MySql, "select 1 /*! ; delete from t */")]
    [InlineData(DbKind.MySql, "select '\\'; delete from t; -- '")]
    [InlineData(DbKind.MySql, "select benchmark(1000000000, md5('a'))")]
    [InlineData(DbKind.MySql, "select get_lock('a', 10)")]
    [InlineData(DbKind.MySql, "delimiter $$\nselect 1$$")]
    public void Sql_AnythingElse_IsRefused(DbKind kind, string sql) => Assert.NotNull(ClaudeSqlPolicy.Refuse(sql, kind));

    [Theory]
    [InlineData(DbKind.SqlServer, "select * from prod.dbo.t")]
    [InlineData(DbKind.SqlServer, "select * from [Prod]..t")]
    [InlineData(DbKind.SqlServer, "select * from \"prod\" . dbo . t")]
    [InlineData(DbKind.SqlServer, "select * from prod/**/.dbo.t")]
    [InlineData(DbKind.SqlServer, "select * from srv.other.dbo.t")]
    [InlineData(DbKind.SqlServer, "select * from srv...t")]
    [InlineData(DbKind.MySql, "select * from `prod`.t")]
    [InlineData(DbKind.MySql, "select a from t union select a from prod.t")]
    public void Sql_ReachingAClosedDatabase_IsRefused(DbKind kind, string sql) =>
        Assert.NotNull(ClaudeSqlPolicy.RefuseOtherDatabase(sql, kind, ["prod"]));

    [Theory]
    [InlineData(DbKind.SqlServer, "select t.a, dbo.t.b from dev.dbo.t")]
    [InlineData(DbKind.SqlServer, "select prod from t where a = 'prod.dbo.t' and b = 1.5")]
    [InlineData(DbKind.Postgres, "select prod.a from prod")]
    public void Sql_OtherNames_Pass(DbKind kind, string sql) =>
        Assert.Null(ClaudeSqlPolicy.RefuseOtherDatabase(sql, kind, ["prod"]));

    [Fact]
    public void Sql_TooLong_IsRefused() =>
        Assert.NotNull(ClaudeSqlPolicy.Refuse("select " + new string('1', ClaudeSqlPolicy.MaxLength), DbKind.Sqlite));

    [Fact]
    public void Launcher_FindsClaude_OnlyInFullPaths()
    {
        var found = ClaudeLauncher.FindClaude(@".;bin;C:\tools", @"C:\Users\u",
            p => p is @"C:\tools\claude.cmd" or @"bin\claude.exe" or @".\claude.exe");
        Assert.Equal(@"C:\tools\claude.cmd", found);
        Assert.Equal(@"C:\Users\u\.local\bin\claude.exe",
            ClaudeLauncher.FindClaude(null, @"C:\Users\u", p => p == @"C:\Users\u\.local\bin\claude.exe"));
        Assert.Null(ClaudeLauncher.FindClaude(@"C:\tools", @"C:\Users\u", _ => false));
    }

    [Fact]
    public void Launcher_ConfigStartsTheServer_AndTheTerminalQuotesPaths()
    {
        using var json = System.Text.Json.JsonDocument.Parse(ClaudeLauncher.McpConfig(@"C:\A B\DbExplorer.exe", @"C:\data"));
        var server = json.RootElement.GetProperty("mcpServers").GetProperty("dbexplorer");
        Assert.Equal(@"C:\A B\DbExplorer.exe", server.GetProperty("command").GetString());
        Assert.Equal(["--mcp", "--data-dir", @"C:\data"], server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));

        var start = ClaudeLauncher.Terminal(@"C:\o'brien\claude.exe", "C:\\x’; calc; ’\\mcp.json", @"C:\repo & co");
        var encoded = start.Arguments.Split(' ')[^1];
        var command = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        Assert.Equal("& 'C:\\o''brien\\claude.exe' --mcp-config 'C:\\x’’; calc; ’’\\mcp.json'", command);
        Assert.Equal(@"C:\repo & co", start.WorkingDirectory);
        Assert.True(start.UseShellExecute);
    }
}
