using System.Diagnostics;
using DbExplorer.Core.Model;
using Microsoft.Data.SqlClient;

namespace DbExplorer.Core.Drivers;

/// <summary>
/// Microsoft Entra ID sign-in of SQL Server. The token providers live in the Microsoft.Data.SqlClient.Extensions.Azure
/// package, which is only loaded once a SQL Server connection is made: nothing here runs at start-up.
/// </summary>
internal static class EntraAuthentication
{
    private static readonly Lazy<bool> Interactive = new(RegisterInteractive);

#pragma warning disable CS0618 // Entra password sign-in is deprecated by Microsoft but still offered.
    public static SqlAuthenticationMethod Method(AuthMode mode) => mode switch
    {
        AuthMode.EntraInteractive => SqlAuthenticationMethod.ActiveDirectoryInteractive,
        AuthMode.EntraDefault => SqlAuthenticationMethod.ActiveDirectoryDefault,
        AuthMode.EntraIntegrated => SqlAuthenticationMethod.ActiveDirectoryIntegrated,
        AuthMode.EntraPassword => SqlAuthenticationMethod.ActiveDirectoryPassword,
        AuthMode.EntraServicePrincipal => SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a Microsoft Entra mode."),
    };
#pragma warning restore CS0618

    /// <summary>The Windows account broker needs an owner window; the default is the console window, which a GUI app lacks.</summary>
    public static void PrepareInteractive() => _ = Interactive.Value;

    private static bool RegisterInteractive()
    {
        var provider = new ActiveDirectoryAuthenticationProvider();
        provider.SetParentActivityOrWindowFunc(() => Process.GetCurrentProcess().MainWindowHandle);
        return SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive, provider);
    }
}
