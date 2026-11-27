namespace DbExplorer.Core.Model;

/// <summary>What a connection is used for. Only <see cref="Production"/> changes behaviour: writes there are confirmed.</summary>
public enum DbEnvironment { None, Development, Test, PreProduction, Production }

public static class DbEnvironments
{
    /// <summary>Every value, in the order the pickers show them.</summary>
    public static IReadOnlyList<DbEnvironment> All { get; } = Enum.GetValues<DbEnvironment>();

    /// <summary>"Pre-production": what the user reads. "None" for <see cref="DbEnvironment.None"/>.</summary>
    public static string Label(this DbEnvironment e) => e == DbEnvironment.PreProduction ? "Pre-production" : e.ToString();

    /// <summary>The label of a small badge ("PROD"); empty for <see cref="DbEnvironment.None"/>.</summary>
    public static string ShortLabel(this DbEnvironment e) => e switch
    {
        DbEnvironment.Development => "DEV",
        DbEnvironment.Test => "TEST",
        DbEnvironment.PreProduction => "PRE-PROD",
        DbEnvironment.Production => "PROD",
        _ => "",
    };
}
