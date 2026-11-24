namespace DbExplorer.Core.Upgrade;

/// <summary>
/// Natural string order: case-insensitive, runs of digits compare as numbers (so <c>adj - 2</c> sorts before
/// <c>adj - 10</c> and <c>adj - 003</c> before <c>adj - 037</c>), every other character compares ordinally.
/// Distinct strings never compare equal: ties fall back to the digit-run length (leading zeros), then to a
/// plain ordinal comparison, so sorting is deterministic.
/// </summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int i = 0, j = 0, zerosTie = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i; var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var ra = a.AsSpan(si, i - si);
                var rb = b.AsSpan(sj, j - sj);
                var ta = ra.TrimStart('0');
                var tb = rb.TrimStart('0');
                if (ta.Length != tb.Length) return ta.Length < tb.Length ? -1 : 1;
                var c = ta.CompareTo(tb, StringComparison.Ordinal);
                if (c != 0) return c < 0 ? -1 : 1;
                // Numerically equal (only leading zeros differ): remember, keep comparing the rest.
                if (zerosTie == 0 && ra.Length != rb.Length) zerosTie = ra.Length < rb.Length ? -1 : 1;
            }
            else
            {
                var ca = char.ToUpperInvariant(a[i]);
                var cb = char.ToUpperInvariant(b[j]);
                if (ca != cb) return ca < cb ? -1 : 1;
                i++; j++;
            }
        }

        var restA = a.Length - i;
        var restB = b.Length - j;
        if (restA != restB) return restA < restB ? -1 : 1;
        if (zerosTie != 0) return zerosTie;
        return string.CompareOrdinal(a, b);
    }
}
