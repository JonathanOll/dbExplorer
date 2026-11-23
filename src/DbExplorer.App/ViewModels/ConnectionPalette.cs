namespace DbExplorer.App.ViewModels;

/// <summary>Stable colour per connection, used for the chips in the tree and on tabs.</summary>
public static class ConnectionPalette
{
    private static readonly string[] Colors =
    [
        "#E5534B", "#E08A3C", "#C9A227", "#57AB5A", "#2DA29C", "#4C8DFF", "#8E6FE0", "#D1559A",
    ];

    public static string ColorFor(Guid connectionId)
    {
        var bytes = connectionId.ToByteArray();
        var hash = 17;
        foreach (var b in bytes) hash = unchecked(hash * 31 + b);
        return Colors[(hash & int.MaxValue) % Colors.Length];
    }
}
