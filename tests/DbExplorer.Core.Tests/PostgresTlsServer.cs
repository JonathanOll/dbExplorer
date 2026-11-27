using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DbExplorer.Core.Tests;

/// <summary>A throw-away certificate authority, a server certificate it signed, and a client certificate.</summary>
internal sealed class TestCertificates : IDisposable
{
    public const string ServerName = "db.internal.example";
    public const string Passphrase = "open sesame";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbx-tls-" + Guid.NewGuid().ToString("N"));

    public TestCertificates()
    {
        Directory.CreateDirectory(_dir);
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = from.AddDays(30);

        using var caKey = RSA.Create(2048);
        using var ca = Authority("CN=Test CA", caKey).CreateSelfSigned(from, to);
        CaFile = Write("ca.crt", ca.ExportCertificatePem());
        using var otherKey = RSA.Create(2048);
        using var other = Authority("CN=Other CA", otherKey).CreateSelfSigned(from, to);
        OtherCaFile = Write("other-ca.crt", other.ExportCertificatePem());

        using var serverKey = RSA.Create(2048);
        var request = Request("CN=" + ServerName, serverKey);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(ServerName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = request.Create(ca, from, to, [1, 2, 3, 4]);
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // SChannel cannot use an in-memory key: through PKCS#12 it gets a usable one.
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);

        using var clientKey = RSA.Create(2048);
        using var client = Request("CN=dbx client", clientKey).CreateSelfSigned(from, to);
        ClientCertificateFile = Write("client.crt", client.ExportCertificatePem());
        ClientKeyFile = Write("client.key", clientKey.ExportEncryptedPkcs8PrivateKeyPem(Passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000)));
    }

    public X509Certificate2 Server { get; }
    public string CaFile { get; }
    public string OtherCaFile { get; }
    public string ClientCertificateFile { get; }
    public string ClientKeyFile { get; }

    private static CertificateRequest Request(string name, RSA key) =>
        new(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static CertificateRequest Authority(string name, RSA key)
    {
        var request = Request(name, key);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request;
    }

    private string Write(string name, string pem)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, pem);
        return path;
    }

    public void Dispose()
    {
        Server.Dispose();
        Directory.Delete(_dir, recursive: true);
    }
}

/// <summary>
/// Enough of a PostgreSQL server for the TLS stage: it answers the SSL request, does the handshake, reads the startup message
/// and refuses the sign-in. A client that gets that refusal accepted the connection's security.
/// </summary>
internal sealed class PostgresTlsServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly bool _askClientCertificate;

    /// <param name="certificate">Null: the server does not offer TLS.</param>
    public PostgresTlsServer(X509Certificate2? certificate, bool askClientCertificate = false)
    {
        _certificate = certificate;
        _askClientCertificate = askClientCertificate;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Sign-ins received over TLS, and in clear.</summary>
    public int TlsStartups;
    public int ClearStartups;

    public string? ClientSubject { get; private set; }

    private async Task AcceptAsync()
    {
        try
        {
            while (true) _ = ServeAsync(await _listener.AcceptTcpClientAsync());
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { /* stopped */ }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var owned = client;
        try
        {
            Stream stream = client.GetStream();
            while (true)
            {
                var head = new byte[8];
                await stream.ReadExactlyAsync(head);
                var length = BinaryPrimitives.ReadInt32BigEndian(head);
                var code = BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(4));
                if (length == 8 && code == 80877103 && _certificate is not null)   // SSLRequest
                {
                    await stream.WriteAsync("S"u8.ToArray());
                    var tls = new SslStream(stream);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ClientCertificateRequired = _askClientCertificate,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        {
                            ClientSubject = certificate?.Subject;
                            return true;
                        },
                        ApplicationProtocols = [new SslApplicationProtocol("postgresql")],
                    });
                    stream = tls;
                    continue;
                }
                if (length == 8)   // an encryption request this server does not take up
                {
                    await stream.WriteAsync("N"u8.ToArray());
                    continue;
                }

                await stream.ReadExactlyAsync(new byte[length - 8]);
                if (stream is SslStream) Interlocked.Increment(ref TlsStartups);
                else Interlocked.Increment(ref ClearStartups);
                await stream.WriteAsync(Refusal());
                await stream.FlushAsync();
                // Wait for the client to hang up: closing first could lose the message.
                await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                return;
            }
        }
        catch (Exception) { /* the client gave up: the test looks at the client's side */ }
    }

    private static byte[] Refusal()
    {
        var fields = Encoding.ASCII.GetBytes("SFATAL\0VFATAL\0C28P01\0Mpassword authentication failed\0\0");
        var message = new byte[5 + fields.Length];
        message[0] = (byte)'E';
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), 4 + fields.Length);
        fields.CopyTo(message, 5);
        return message;
    }

    public void Dispose() => _listener.Stop();
}
