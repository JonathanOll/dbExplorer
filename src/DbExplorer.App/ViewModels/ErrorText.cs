namespace DbExplorer.App.ViewModels;

internal static class ErrorText
{
    /// <summary>Exception message plus the innermost cause when it adds information (drivers often wrap timeouts).</summary>
    public static string Describe(Exception e)
    {
        if (e is AggregateException { InnerExceptions.Count: 1 } agg) e = agg.InnerExceptions[0];
        var root = e.GetBaseException();
        var text = e.Message.Trim();
        if (!ReferenceEquals(root, e) && !text.Contains(root.Message.Trim(), StringComparison.OrdinalIgnoreCase))
            text += ": " + root.Message.Trim();
        return text;
    }
}
