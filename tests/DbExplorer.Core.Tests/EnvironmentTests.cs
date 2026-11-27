using DbExplorer.Core.Config;
using DbExplorer.Core.Model;
using DbExplorer.Core.Plans;
using DbExplorer.Core.Scripting;
using DbExplorer.Core.Sessions;

namespace DbExplorer.Core.Tests;

public class EnvironmentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-env-" + Guid.NewGuid().ToString("N"));

    public EnvironmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private static ConnectionConfig Conn() =>
        new(Guid.NewGuid(), "c", DbKind.Postgres, null, "h", null, "d", null, null, false, false, false);

    [Fact]
    public void OldConfig_LoadsWithoutEnvironment()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """
            { "Folders": [], "VirtualKeys": {}, "Connections": [
              { "Id": "11111111-1111-1111-1111-111111111111", "Name": "old", "Kind": "Sqlite", "Database": "x.db",
                "Encrypt": false, "TrustServerCertificate": false, "ReadOnly": false } ] }
            """);

        var connection = Assert.Single(new ConfigStore(path, new PlainProtector()).Load().Connections);

        Assert.Equal(DbEnvironment.None, connection.Environment);
        Assert.False(connection.IsProduction);
    }

    [Fact]
    public void Environment_IsSavedByName_AndLoaded()
    {
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), new PlainProtector());
        var config = AppConfig.Empty();
        config.Connections.Add(Conn() with { Environment = DbEnvironment.Production });

        store.Save(config);

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"Production\"", json);
        Assert.DoesNotContain("IsProduction", json);
        Assert.True(Assert.Single(store.Load().Connections).IsProduction);
    }

    [Fact]
    public void Environment_DoesNotChangeTheTarget()
    {
        var a = Conn();

        Assert.True(SessionManager.SameTarget(a, a with { Environment = DbEnvironment.Production }));
    }

    [Theory]
    [InlineData(DbKind.Postgres, "SELECT 1", false)]
    [InlineData(DbKind.Postgres, "WITH a AS (SELECT 1) SELECT * FROM a", false)]
    [InlineData(DbKind.Postgres, "SELECT nextval FROM t", false)]
    [InlineData(DbKind.Postgres, "SELECT nextval('s')", true)]
    [InlineData(DbKind.Postgres, "SELECT \"setval\"('s', 1)", true)]
    [InlineData(DbKind.Postgres, "UPDATE t SET a = 1", true)]
    [InlineData(DbKind.Postgres, "EXPLAIN ANALYZE DELETE FROM t", true)]
    [InlineData(DbKind.SqlServer, "SELECT 1 DISABLE TRIGGER x ON t", true)]
    [InlineData(DbKind.SqlServer, "DECLARE @a int", true)]
    [InlineData(DbKind.SqlServer, "something_unknown", true)]
    [InlineData(DbKind.Sqlite, "PRAGMA table_info(t)", false)]
    public void MayWrite_IsConservative(DbKind kind, string sql, bool expected) =>
        Assert.Equal(expected, ProductionWrites.MayWrite(sql, kind));

    [Fact]
    public void EstimatedPlan_NeedsNoConfirmation_ActualPlanDoes()
    {
        string[] statements = ["SELECT 1", "DELETE FROM t"];

        Assert.Empty(ProductionWrites.In(statements, DbKind.SqlServer, PlanMode.Estimated));
        Assert.Equal(["DELETE FROM t"], ProductionWrites.In(statements, DbKind.SqlServer, PlanMode.Actual));
        Assert.Equal(["DELETE FROM t"], ProductionWrites.In(statements, DbKind.SqlServer));
    }

    [Fact]
    public void EstimatedPlan_ThatTouchesShowplan_IsJudgedAsARun() =>
        Assert.NotEmpty(ProductionWrites.In(["SET SHOWPLAN_XML OFF", "DELETE FROM t"], DbKind.SqlServer, PlanMode.Estimated));

    [Theory]
    [InlineData("dev", "{}", DbEnvironment.Development)]
    [InlineData("test", "{}", DbEnvironment.Test)]
    [InlineData("prod", "{}", DbEnvironment.Production)]
    [InlineData("c1", """{ "c1": { "name": "Production EU" } }""", DbEnvironment.Production)]
    [InlineData("c1", """{ "c1": { "name": "Pre-prod" } }""", DbEnvironment.PreProduction)]
    [InlineData("c1", """{ "c1": { "name": "Blue" } }""", DbEnvironment.None)]
    public void DBeaverImport_MapsTheConnectionType(string type, string types, DbEnvironment expected)
    {
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"), $$"""
            { "connections": { "a": { "provider": "postgresql", "name": "A",
              "configuration": { "host": "h", "database": "d", "type": "{{type}}" } } }, "connection-types": {{types}} }
            """);
        var target = AppConfig.Empty();

        DBeaverImporter.Import(_dir, target, new PlainProtector());

        Assert.Equal(expected, Assert.Single(target.Connections).Environment);
    }
}
