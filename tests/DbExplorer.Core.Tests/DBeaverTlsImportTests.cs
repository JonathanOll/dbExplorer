using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class DBeaverTlsImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-dbeaver-tls-" + Guid.NewGuid().ToString("N"));

    public DBeaverTlsImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    [Fact]
    public void Import_MapsSslSettings()
    {
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"), """
            {"connections":{
              "a":{"provider":"postgresql","name":"Full","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"type":"CONFIG","enabled":true,"properties":{"sslMode":"verify-full",
                     "ssl.method":"CERTIFICATES","ssl.ca.cert":"C:\\ca.crt","ssl.client.cert":"C:\\c.crt","ssl.client.key":"C:\\c.key"}}}}},
              "b":{"provider":"postgresql","name":"Old names","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"enabled":true,"properties":{"sslMode":"verify-ca","rootCert":"C:\\old.crt",
                     "clientKey":"C:\\orphan.key"}}}}},
              "c":{"provider":"postgresql","name":"No mode","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"enabled":true,"properties":{}}}}},
              "d":{"provider":"postgresql","name":"Off","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"enabled":false,"properties":{"sslMode":"verify-full"}}}}},
              "e":{"provider":"postgresql","name":"Property","configuration":{"host":"db","database":"d",
                   "properties":{"sslmode":"require"}}},
              "f":{"provider":"postgresql","name":"Prefer","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"enabled":true,"properties":{"sslMode":"prefer"}}}}},
              "g":{"provider":"postgresql","name":"Odd","configuration":{"host":"db","database":"d",
                   "handlers":{"postgre_ssl":{"enabled":true,"properties":{"sslMode":"strictest",
                     "ssl.client.cert":"C:\\c.crt","ssl.client.key":"C:\\c.pk8"}}}}},
              "h":{"provider":"sqlserver","name":"Other engine","configuration":{"host":"db","database":"d",
                   "properties":{"sslmode":"require"}}}
            }}
            """);
        using (var aes = Aes.Create())
        {
            aes.Key = Convert.FromHexString("babb4a9f774ab853c96c2d653dfe544a");
            aes.GenerateIV();
            var json = """{"a":{"network/postgre_ssl":{"password":"phrase"}}}""";
            var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), aes.IV, PaddingMode.PKCS7);
            File.WriteAllBytes(Path.Combine(_dir, "credentials-config.json"), [.. aes.IV, .. cipher]);
        }
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, new PlainProtector());

        TlsConfig? Tls(string name) => Assert.Single(target.Connections, c => c.Name == name).Tls;
        Assert.Equal(new TlsConfig(TlsMode.VerifyFull, @"C:\ca.crt", @"C:\c.crt", @"C:\c.key", "p:phrase"), Tls("Full"));
        Assert.Equal(new TlsConfig(TlsMode.VerifyCA, @"C:\old.crt"), Tls("Old names"));
        Assert.Equal(new TlsConfig(TlsMode.VerifyFull), Tls("No mode"));
        Assert.Null(Tls("Off"));
        Assert.Equal(new TlsConfig(TlsMode.Require), Tls("Property"));
        Assert.Null(Tls("Prefer"));
        Assert.Equal(TlsMode.VerifyFull, Tls("Odd")!.Mode);
        Assert.Null(Tls("Other engine"));
        Assert.Contains(result.Warnings, w => w.Contains("Odd") && w.Contains("strictest"));
        Assert.Contains(result.Warnings, w => w.Contains("Odd") && w.Contains("PEM"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Full'"));
    }
}
