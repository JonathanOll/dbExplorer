using DbExplorer.Core.Scripting;

namespace DbExplorer.Core.Upgrade;

/// <summary>
/// One script of an upgrade plan. <see cref="Text"/> is null when <see cref="LoadError"/> is set (unreadable file or
/// unsupported <c>GO n</c>); such an item blocks the run.
/// </summary>
public sealed record UpgradeScriptItem(
    DbUpdateScript Script, string? Text, string? LoadError, IReadOnlyList<ScriptWarning> Warnings)
{
    public string DisplayName => $"{Script.FolderName}/{Script.FileName}";
}

/// <summary>The scripts of the chosen sprint (or folder) with their text and warnings. <see cref="Ignored"/> = <c>folder/name</c> entries skipped by the scan.</summary>
public sealed record UpgradePlan(
    string Label, string ConfirmToken, IReadOnlyList<UpgradeScriptItem> Items, IReadOnlyList<string> Ignored);

public static class UpgradePlanner
{
    /// <summary>Reads and analyzes every script of <paramref name="folders"/> (in the given order). Never throws on a bad script.</summary>
    public static UpgradePlan Build(string label, string confirmToken, IReadOnlyList<DbUpdateFolder> folders)
    {
        var items = new List<UpgradeScriptItem>();
        var ignored = new List<string>();
        foreach (var folder in folders)
        {
            foreach (var script in folder.Scripts) items.Add(Load(script));
            ignored.AddRange(folder.Ignored.Select(i => $"{folder.Name}/{i}"));
        }
        return new UpgradePlan(label, confirmToken, items, ignored);
    }

    private static UpgradeScriptItem Load(DbUpdateScript script)
    {
        try
        {
            var (text, _) = SqlTextDecoder.Decode(File.ReadAllBytes(script.FullPath));
            if (ScriptSplitter.FindUnsupportedGo(text) is { } go)
                return new UpgradeScriptItem(script, null, $"{go.Reason} (line {go.Line}).", []);
            if (ScriptAnalyzer.FindUse(text) is { } use)
                return new UpgradeScriptItem(script, null, ScriptAnalyzer.UseMessage(use), []);
            return new UpgradeScriptItem(script, text, null, ScriptAnalyzer.Analyze(text));
        }
        catch (Exception ex)
        {
            return new UpgradeScriptItem(script, null, $"Cannot read the script: {ex.Message}", []);
        }
    }
}
