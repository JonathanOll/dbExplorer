using System.Text.Json;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Tunnels;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Tests;

/// <summary>Opt-in (env var DBX_MSSQL): the MCP tools against a real SQL Server.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ClaudeSqlServerTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class NoApp : IClaudeApp
    {
        public Task<ClaudeAppReply> SendAsync(ClaudeAppRequest request, CancellationToken ct) =>
            Task.FromResult(new ClaudeAppReply(false));
    }

    [SkippableFact]
    public async Task Tools_ReadPlanAndNeverWrite()
    {
        var text = Environment.GetEnvironmentVariable("DBX_MSSQL");
        Skip.If(string.IsNullOrWhiteSpace(text), "DBX_MSSQL is not set");
        var cs = new SqlConnectionStringBuilder(text);
        var connection = new ConnectionConfig(Guid.NewGuid(), "ms", DbKind.SqlServer, null, cs.DataSource, null,
                cs.InitialCatalog.Length == 0 ? "tempdb" : cs.InitialCatalog, cs.IntegratedSecurity ? null : cs.UserID,
                cs.IntegratedSecurity ? null : cs.Password, false, true, false)
            { Auth = cs.IntegratedSecurity ? AuthMode.Integrated : AuthMode.Password };

        var schema = "dbx_claude_" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = new SqlConnection(new SqlConnectionStringBuilder(text) { InitialCatalog = connection.Database }.ConnectionString);
        await admin.OpenAsync();
        async Task<object?> Run(string sql)
        {
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync();
        }

        var dir = Path.Combine(Path.GetTempPath(), schema);
        await Run($"CREATE SCHEMA {schema}");
        try
        {
            await Run($"CREATE TABLE {schema}.t (a int); INSERT INTO {schema}.t VALUES (1), (2)");
            await Run($"CREATE VIEW {schema}.v AS SELECT a FROM {schema}.t");
            var config = AppConfig.Empty();
            config.Connections.Add(connection);
            config.Claude = new ClaudeSettings { Enabled = true, Connections = { [connection.Id] = ClaudeAccess.SchemaAndData } };
            using var database = new ClaudeDatabase(new PlainProtector(), new KnownHostsStore(Path.Combine(dir, "known_hosts")));
            var tools = new ClaudeTools(() => config, database, new PlainProtector(),
                new ClaudeActivityLog(Path.Combine(dir, "log.jsonl")), new NoApp());
            Task<ClaudeReply> Call(string tool, Dictionary<string, object> args) => tools.CallAsync(tool,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!, CancellationToken.None);

            async Task<string> Ok(string tool, Dictionary<string, object> args)
            {
                var reply = await Call(tool, args);
                Assert.False(reply.IsError, reply.Text);
                return reply.Text;
            }

            var sql = $"SELECT a FROM {schema}.t ORDER BY a";
            Assert.Contains("[[1],[2]]", await Ok("query", new() { ["connection"] = "ms", ["sql"] = sql }));
            Assert.Contains("Nothing was executed", await Ok("explain", new() { ["connection"] = "ms", ["sql"] = sql }));
            Assert.Contains("\"truncated\":true", await Ok("query", new() { ["connection"] = "ms", ["sql"] = sql, ["max_rows"] = 1 }));
            Assert.Contains("CREATE VIEW", await Ok("get_definition", new() { ["connection"] = "ms", ["schema"] = schema, ["name"] = "v" }));
            // The connection still runs queries after a plan (SHOWPLAN is switched off again).
            Assert.Contains("[[1],[2]]", await Ok("query", new() { ["connection"] = "ms", ["sql"] = sql }));
            Assert.Contains("too long", (await Call("query", new()
            {
                ["connection"] = "ms", ["timeout_seconds"] = 1,
                ["sql"] = "SELECT COUNT_BIG(*) FROM sys.all_columns a CROSS JOIN sys.all_columns b CROSS JOIN sys.all_columns c",
            })).Text);

            foreach (var hostile in new[]
            {
                $"SELECT 1 DELETE FROM {schema}.t", $"SELECT 1 COMMIT TRUNCATE TABLE {schema}.t", $"SELECT * INTO {schema}.copy FROM {schema}.t",
            })
                Assert.True((await Call("query", new() { ["connection"] = "ms", ["sql"] = hostile })).IsError);
            Assert.Equal(2, await Run($"SELECT COUNT(*) FROM {schema}.t"));
        }
        finally
        {
            await Run($"DROP VIEW IF EXISTS {schema}.v; DROP TABLE IF EXISTS {schema}.t; DROP TABLE IF EXISTS {schema}.copy; DROP SCHEMA {schema}");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
