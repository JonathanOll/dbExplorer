using System.Security.Cryptography;
using System.Text;
using DbExplorer.Core.Config;
using DbExplorer.Core.Model;

namespace DbExplorer.Core.Tests;

public class DBeaverAuthImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-dbeaver-auth-" + Guid.NewGuid().ToString("N"));

    public DBeaverAuthImportTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plain) => "p:" + plain;
        public string Unprotect(string protectedValue) => protectedValue[2..];
    }

    [Fact]
    public void Import_MapsSshTunnelsAndAuthentication()
    {
        File.WriteAllText(Path.Combine(_dir, "data-sources.json"), """
            {"connections":{
              "a":{"provider":"postgresql","name":"Tunneled","configuration":{"host":"db","database":"d",
                   "handlers":{"ssh_tunnel":{"type":"TUNNEL","enabled":true,
                     "properties":{"host":"jump","port":2222,"authType":"PUBLIC_KEY","keyPath":"C:\\keys\\id"}}}}},
              "b":{"provider":"sqlserver","name":"Entra","configuration":{"host":"x","database":"d",
                   "provider-properties":{"authentication":"ActiveDirectoryInteractive"}}},
              "c":{"provider":"sqlserver","name":"Windows","configuration":{"host":"x","database":"d",
                   "provider-properties":{"integratedSecurity":"true"},
                   "handlers":{"ssh_tunnel":{"enabled":false,"properties":{"host":"unused"}}}}},
              "d":{"provider":"postgresql","name":"Agent","configuration":{"host":"db","database":"d",
                   "handlers":{"ssh_tunnel":{"enabled":true,"properties":{"host":"jump","port":"22","authType":"AGENT"}}}}}
            }}
            """);
        using (var aes = Aes.Create())
        {
            aes.Key = Convert.FromHexString("babb4a9f774ab853c96c2d653dfe544a");
            aes.GenerateIV();
            var json = """
                {"a":{"#connection":{"user":"dbu","password":"dbp"},"network/ssh_tunnel":{"user":"sshu","password":"phrase"}},
                 "c":{"#connection":{"user":"sa","password":"old"}}}
                """;
            var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(json), aes.IV, PaddingMode.PKCS7);
            File.WriteAllBytes(Path.Combine(_dir, "credentials-config.json"), [.. aes.IV, .. cipher]);
        }
        var target = AppConfig.Empty();

        var result = DBeaverImporter.Import(_dir, target, new PlainProtector());

        ConnectionConfig Named(string name) => Assert.Single(target.Connections, c => c.Name == name);
        var tunneled = Named("Tunneled");
        Assert.Equal(new SshTunnelConfig("jump", 2222, "sshu", SshAuthKind.PrivateKey, KeyFile: @"C:\keys\id", ProtectedPassphrase: "p:phrase"),
            tunneled.Ssh);
        Assert.Equal(("dbu", "p:dbp"), (tunneled.User, tunneled.ProtectedPassword));
        var entra = Named("Entra");
        Assert.Equal(AuthMode.EntraInteractive, entra.Auth);
        Assert.True(entra.Encrypt);
        var windows = Named("Windows");
        Assert.Equal(AuthMode.Integrated, windows.Auth);
        Assert.Null(windows.User);
        Assert.Null(windows.ProtectedPassword);
        Assert.Null(windows.Ssh);
        // An unsupported SSH sign-in keeps the tunnel (never a silent direct connection) and says so.
        Assert.Equal(SshAuthKind.PrivateKey, Named("Agent").Ssh!.Auth);
        Assert.Contains(result.Warnings, w => w.Contains("Agent") && w.Contains("AGENT"));
    }
}
