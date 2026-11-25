using System.Text.RegularExpressions;
using DbExplorer.Core.Upgrade;

namespace DbExplorer.Core.Scripting;

/// <summary>What to look for in scripts. A plain text is searched literally; <see cref="UseRegex"/> makes it a .NET regular expression.</summary>
public sealed record ScriptSearchOptions(string Text, bool CaseSensitive = false, bool WholeWord = false, bool UseRegex = false)
{
    /// <summary>A regex that runs longer than this on one line is abandoned for that line (catastrophic backtracking).</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Why the search cannot run (empty text, invalid regular expression), or null. The message is meant for the user.</summary>
    public string? Validate()
    {
        if (string.IsNullOrEmpty(Text)) return "Type something to search for.";
        if (!UseRegex) return null;
        try
        {
            _ = new Regex(Text, RegexOptions.None, RegexTimeout);
            return null;
        }
        catch (ArgumentException e)
        {
            return "Invalid regular expression: " + e.Message;
        }
    }

    internal Regex Build()
    {
        var pattern = UseRegex ? Text : Regex.Escape(Text);
        // Whole word: no letter, digit or underscore on either side (\b would fail for a text that starts or ends with a symbol).
        if (WholeWord) pattern = @"(?<![\p{L}\p{N}_])(?:" + pattern + @")(?![\p{L}\p{N}_])";
        var options = RegexOptions.CultureInvariant | (CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(pattern, options, RegexTimeout);
    }
}

/// <summary>
/// One hit. <see cref="Line"/> is 1-based; <see cref="LineText"/> is the line (cropped around the hit when very long) and
/// <see cref="Start"/> / <see cref="Length"/> the hit inside <see cref="LineText"/>. <see cref="Column"/> is the 0-based column in the
/// full line (what an editor needs); <see cref="Offset"/> the 0-based offset in the whole text.
/// </summary>
public sealed record ScriptMatch(
    string Source, string DisplayName, string? Path, bool Unsaved,
    int Line, int Column, int Offset, string LineText, int Start, int Length);

/// <summary>What a search saw besides its matches: files it could not or would not read, regex timeouts.</summary>
public sealed class ScriptSearchStats
{
    public int FilesSearched { get; internal set; }
    public int FilesSkippedTooLarge { get; internal set; }
    public int FilesUnreadable { get; internal set; }
    public int FilesBinary { get; internal set; }
    public int LinesTimedOut { get; internal set; }
}

/// <summary>
/// Full-text search in scripts: pure and lazy. Matches are produced as the text is read, so a caller can stream them to a list and
/// stop (cancel, cap) at any time. Lines are matched one by one, so a regular expression never spans lines.
/// </summary>
public sealed class ScriptSearch
{
    public const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxLineText = 240;

    private readonly Regex _regex;

    /// <exception cref="ArgumentException">The options are not valid (see <see cref="ScriptSearchOptions.Validate"/>).</exception>
    public ScriptSearch(ScriptSearchOptions options)
    {
        if (options.Validate() is { } problem) throw new ArgumentException(problem, nameof(options));
        Options = options;
        _regex = options.Build();
    }

    public ScriptSearchOptions Options { get; }
    public ScriptSearchStats Stats { get; } = new();

    /// <summary>Matches in one text (an open tab, or a file already read).</summary>
    public IEnumerable<ScriptMatch> SearchText(string text, string source, string displayName, string? path, bool unsaved, CancellationToken ct = default)
    {
        var offset = 0;
        var line = 1;
        while (offset <= text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var end = text.AsSpan(offset).IndexOfAny('\r', '\n');
            var lineEnd = end < 0 ? text.Length : offset + end;
            var lineText = text.Substring(offset, lineEnd - offset);

            foreach (var m in MatchesIn(lineText))
            {
                var (shown, start) = Crop(lineText, m.Index, m.Length);
                yield return new ScriptMatch(source, displayName, path, unsaved, line, m.Index, offset + m.Index, shown, start, Math.Min(m.Length, shown.Length - start));
            }

            if (end < 0) yield break;
            offset = lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1);
            line++;
        }
    }

    /// <summary>
    /// Matches in the .sql files under <paramref name="root"/> (hidden and dot-named entries skipped, like the library lists them), in
    /// path order. Files larger than <see cref="MaxFileBytes"/>, binary files and unreadable files are skipped and counted in
    /// <see cref="Stats"/>. Paths in <paramref name="skip"/> are not read (their text came from an open tab).
    /// </summary>
    public IEnumerable<ScriptMatch> SearchFolder(string root, IReadOnlySet<string>? skip = null, CancellationToken ct = default)
    {
        foreach (var file in EnumerateScripts(root, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (skip is not null && skip.Contains(file)) continue;
            var text = TryRead(file);
            if (text is null) continue;
            Stats.FilesSearched++;
            var name = System.IO.Path.GetRelativePath(root, file);
            foreach (var match in SearchText(text, file, name, file, false, ct)) yield return match;
        }
    }

    private IEnumerable<Match> MatchesIn(string line)
    {
        MatchCollection matches;
        try { matches = _regex.Matches(line); }
        catch (RegexMatchTimeoutException) { Stats.LinesTimedOut++; yield break; }

        var e = matches.GetEnumerator();
        while (true)
        {
            Match m;
            try
            {
                if (!e.MoveNext()) yield break;
                m = (Match)e.Current;
            }
            catch (RegexMatchTimeoutException) { Stats.LinesTimedOut++; yield break; }
            if (m.Length > 0) yield return m;   // `a*` also "matches" nothing everywhere: not a hit
        }
    }

    /// <summary>Keeps a very long line readable: a window around the hit, with an ellipsis where it was cut.</summary>
    private static (string Text, int Start) Crop(string line, int index, int length)
    {
        var trimmedStart = 0;
        while (trimmedStart < index && trimmedStart < line.Length && char.IsWhiteSpace(line[trimmedStart])) trimmedStart++;
        if (line.Length - trimmedStart <= MaxLineText) return (line[trimmedStart..], index - trimmedStart);

        var from = Math.Max(trimmedStart, index - 60);
        var take = Math.Min(MaxLineText, line.Length - from);
        var prefix = from > trimmedStart ? "… " : "";
        var suffix = from + take < line.Length ? " …" : "";
        return (prefix + line.Substring(from, take) + suffix, prefix.Length + index - from);
    }

    private static IEnumerable<string> EnumerateScripts(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            var folders = new List<string>();
            var files = new List<string>();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.Hidden) || entry.Name.StartsWith('.')) continue;
                    if (entry is DirectoryInfo) folders.Add(entry.FullName);
                    else if (entry.Extension.Equals(ScriptLibrary.Extension, StringComparison.OrdinalIgnoreCase)) files.Add(entry.FullName);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Sort((a, b) => NaturalComparer.Instance.Compare(a, b));
            folders.Sort((a, b) => NaturalComparer.Instance.Compare(b, a));   // stack: first folder is popped first
            foreach (var f in files) yield return f;
            foreach (var d in folders) pending.Push(d);
        }
    }

    private string? TryRead(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                Stats.FilesSkippedTooLarge++;
                return null;
            }
            var bytes = File.ReadAllBytes(file);
            if (LooksBinary(bytes))
            {
                Stats.FilesBinary++;
                return null;
            }
            return SqlTextDecoder.Decode(bytes).Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Stats.FilesUnreadable++;
            return null;
        }
    }

    /// <summary>A NUL byte in the first 8 KB of a file without a UTF-16 / UTF-32 BOM: not text.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return false;
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF) return false;
        return bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0);
    }
}
