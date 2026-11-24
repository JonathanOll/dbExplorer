using System.Globalization;
using System.Text;

namespace DbExplorer.Core.Upgrade;

/// <summary>The copyable text log of an upgrade run. Pure formatting, lines end with LF.</summary>
public static class UpgradeLog
{
    /// <summary>
    /// <c>label</c>, then per database <c>== name: State (n of m scripts, s s)</c> and one line per script
    /// <c>  [OK|FAILED|CANCELLED|SKIPPED] name</c> (failed ones followed by <c> : error</c>). A database that failed before any script
    /// failed (connection refused...) gets an extra <c>  ERROR: message</c> line so its reason is not lost, and a database with a
    /// note (e.g. listed twice) a <c>  NOTE: ...</c> line.
    /// </summary>
    public static string Format(string label, IReadOnlyList<UpgradeResult> results)
    {
        var sb = new StringBuilder();
        sb.Append(label);
        foreach (var r in results)
        {
            var done = r.Scripts.Count(s => s.State == ScriptOutcomeState.Done);
            var seconds = r.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            sb.Append('\n').Append("== ").Append(r.Connection.Name).Append(": ").Append(r.State)
              .Append(" (").Append(done).Append(" of ").Append(r.Scripts.Count).Append(" scripts, ").Append(seconds).Append(" s)");

            foreach (var s in r.Scripts)
            {
                sb.Append('\n').Append("  [").Append(Tag(s.State)).Append("] ").Append(s.Name);
                if (s.State == ScriptOutcomeState.Failed && s.Error is not null) sb.Append(" : ").Append(s.Error);
            }

            if (r.Error is not null && !r.Scripts.Any(s => s.State == ScriptOutcomeState.Failed))
                sb.Append('\n').Append("  ERROR: ").Append(r.Error);

            if (r.Note is not null)
                sb.Append('\n').Append("  NOTE: ").Append(r.Note);
        }
        return sb.ToString();
    }

    private static string Tag(ScriptOutcomeState state) => state switch
    {
        ScriptOutcomeState.Done => "OK",
        ScriptOutcomeState.Failed => "FAILED",
        ScriptOutcomeState.Cancelled => "CANCELLED",
        _ => "SKIPPED",
    };
}
