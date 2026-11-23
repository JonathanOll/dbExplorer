using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

/// <summary>An imported production connection must never silently become writable.</summary>
public class DBeaverReadOnlyImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbExplorer-dbeaver-ro-" + Guid.NewGuid().ToString("N"));

    public DBeaverReadOnlyImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private static string Connection(string name, string type, string extra = "") => $$"""
        "{{name}}-id": {
          "provider": "postgresql", "driver": "postgres-jdbc", "name": "{{name}}", {{extra}}
          "configuration": { "host": "h", "port": "5432", "database": "d", "type": "{{type}}" }
        }
        """;

    private ImportResult Import(AppConfig target, string connections, string connectionTypes = "{}")
    {
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"),
            "{ \"folders\": {}, \"connections\": {" + connections + "}, \"connection-types\": " + connectionTypes + " }");
        return DBeaverImporter.Import(_dir, target, new PlainProtector());
    }

    [Fact]
    public void ReadOnlyFlagOnTheConnection_ImportsReadOnly()
    {
        var target = AppConfig.Empty();

        var result = Import(target, Connection("Reporting", "dev", "\"read-only\": true,"));

        Assert.True(Assert.Single(target.Connections).ReadOnly);
        Assert.Equal("Reporting (read-only in DBeaver)", Assert.Single(result.ReadOnlyImported!));
    }

    [Fact]
    public void ReadOnlyFlagInTheConfiguration_AlsoCounts()
    {
        var target = AppConfig.Empty();
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"), """
            { "connections": { "a": { "provider": "postgresql", "name": "Cfg",
              "configuration": { "database": "d", "type": "dev", "read-only": true } } } }
            """);

        DBeaverImporter.Import(_dir, target, new PlainProtector());

        Assert.True(Assert.Single(target.Connections).ReadOnly);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData("Production")]
    public void ProdConnectionType_ImportsReadOnly(string type)
    {
        var target = AppConfig.Empty();

        var result = Import(target, Connection("Live", type));

        Assert.True(Assert.Single(target.Connections).ReadOnly);
        Assert.Contains($"DBeaver connection type '{type}'", Assert.Single(result.ReadOnlyImported!));
    }

    [Fact]
    public void CustomTypeNamedProduction_ViaTheConnectionTypesSection_ImportsReadOnly()
    {
        var target = AppConfig.Empty();

        var result = Import(target, Connection("Live", "my-id"),
            "{ \"my-id\": { \"name\": \"Production EU\" }, \"dev\": { \"name\": \"Development\" } }");

        Assert.True(Assert.Single(target.Connections).ReadOnly);
        Assert.Contains("Production EU", Assert.Single(result.ReadOnlyImported!));
    }

    [Fact]
    public void CustomTypeThatForbidsDataEdits_ImportsReadOnly()
    {
        var target = AppConfig.Empty();

        Import(target, Connection("Locked", "locked"),
            "{ \"locked\": { \"name\": \"Locked\", \"security\": { \"permission-restrictions\": [ \"EDIT_DATA\" ] } } }");

        Assert.True(Assert.Single(target.Connections).ReadOnly);
    }

    [Fact]
    public void DevAndTestConnections_StayWritable_AndReportNothing()
    {
        var target = AppConfig.Empty();

        var result = Import(target, Connection("Dev", "dev") + "," + Connection("Test", "test") + "," + Connection("Odd", "unknown"),
            "{ \"dev\": { \"name\": \"Development\", \"security\": { \"permission-restrictions\": [] } } }");

        Assert.All(target.Connections, c => Assert.False(c.ReadOnly));
        Assert.Empty(result.ReadOnlyImported!);
    }

    [Fact]
    public void OnlyTheRestrictedConnectionsAreListed()
    {
        var target = AppConfig.Empty();

        var result = Import(target, Connection("Dev", "dev") + "," + Connection("Live", "prod"));

        Assert.Equal(["Live"], target.Connections.Where(c => c.ReadOnly).Select(c => c.Name));
        Assert.Single(result.ReadOnlyImported!);
    }
}
