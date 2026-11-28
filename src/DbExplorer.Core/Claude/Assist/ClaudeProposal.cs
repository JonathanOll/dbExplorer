namespace DbExplorer.Core.Claude.Assist;

public enum DiffKind { Same, Removed, Added }

public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>The SQL an answer proposes to put in place of the user's, and how it differs.</summary>
public static class ClaudeProposal
{
    // Above this many cells the middle is shown as removed then added.
    private const int MaxCells = 4_000_000;

    /// <summary>The last block fenced as <see cref="ClaudePayload.Replacement"/>, closed; null when there is none.</summary>
    public static string? Find(string answer)
    {
        string? found = null;
        List<string>? block = null;
        var fence = "";
        foreach (var line in answer.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (block is null)
            {
                var ticks = trimmed.Length - trimmed.TrimStart('`').Length;
                if (ticks < 3 || !trimmed[ticks..].Trim().Equals(ClaudePayload.Replacement, StringComparison.OrdinalIgnoreCase)) continue;
                fence = trimmed[..ticks];
                block = [];
            }
            else if (trimmed == fence)
            {
                found = string.Join('\n', block).TrimEnd();
                block = null;
            }
            else block.Add(line);
        }
        return string.IsNullOrWhiteSpace(found) ? null : found;
    }

    /// <summary>The answer with its replacement blocks left out (they are shown as a difference).</summary>
    public static string Without(string answer)
    {
        var kept = new List<string>();
        string? fence = null;
        foreach (var line in answer.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (fence is null)
            {
                var ticks = trimmed.Length - trimmed.TrimStart('`').Length;
                if (ticks >= 3 && trimmed[ticks..].Trim().Equals(ClaudePayload.Replacement, StringComparison.OrdinalIgnoreCase))
                {
                    fence = trimmed[..ticks];
                    kept.Add("(See the proposed change above.)");
                }
                else kept.Add(line);
            }
            else if (trimmed == fence) fence = null;
        }
        return string.Join('\n', kept);
    }

    /// <summary>A line by line difference, common lines kept (longest common subsequence).</summary>
    public static IReadOnlyList<DiffLine> Diff(string before, string after)
    {
        var a = before.Length == 0 ? [] : before.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var b = after.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        var tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[^(tail + 1)] == b[^(tail + 1)]) tail++;
        var (n, m) = (a.Length - head - tail, b.Length - head - tail);

        var result = new List<DiffLine>();
        result.AddRange(a.Take(head).Select(l => new DiffLine(DiffKind.Same, l)));
        if ((long)n * m <= MaxCells)
        {
            var length = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    length[i, j] = a[head + i] == b[head + j] ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[head + x] == b[head + y]) { result.Add(new DiffLine(DiffKind.Same, a[head + x])); x++; y++; }
                else if (x < n && (y == m || length[x + 1, y] >= length[x, y + 1])) result.Add(new DiffLine(DiffKind.Removed, a[head + x++]));
                else result.Add(new DiffLine(DiffKind.Added, b[head + y++]));
            }
        }
        else
        {
            result.AddRange(a.Skip(head).Take(n).Select(l => new DiffLine(DiffKind.Removed, l)));
            result.AddRange(b.Skip(head).Take(m).Select(l => new DiffLine(DiffKind.Added, l)));
        }
        result.AddRange(a.Skip(a.Length - tail).Select(l => new DiffLine(DiffKind.Same, l)));
        return result;
    }
}
