using System.Text.Json;
using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.App.Views;
using DbExplorer.Core.Claude;
using DbExplorer.Core.Claude.Assist;
using DbExplorer.Core.Model;

namespace UiShots;

/// <summary>The side panel of the questions to Claude, with a made-up answer: no process is ever started.</summary>
internal static partial class Scenes
{
    private sealed class CannedClaude(string answer) : IClaudeProcessRunner
    {
        public Task<ClaudeProcessExit> RunAsync(ClaudeProcessRequest request, Action<string> onLine, CancellationToken ct)
        {
            onLine(JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = answer }));
            return Task.FromResult(new ClaudeProcessExit(0, ""));
        }
    }

    private const string Query =
        "SELECT c.name, c.country, count(*) AS orders\nFROM orders o, customers c\nWHERE c.id = o.customer_id\n  AND o.status <> 'open'\nGROUP BY c.id\nORDER BY orders DESC;";

    private static void UseCannedClaude(Demo d, string answer)
    {
        d.Tree.Config.Claude = new ClaudeSettings { Enabled = true, Connections = { [d.Shop.Id] = ClaudeAccess.Schema } };
        d.Main.UseClaude(new ClaudeHooks(@"C:\Tools\dbExplorer\DbExplorer.exe", d.Dir, Path.Combine(d.Dir, "claude", "mcp.json"),
            Path.Combine(d.Dir, "library"), new ClaudeActivityLog(Path.Combine(d.Dir, "claude-activity.jsonl")), () => "claude",
            _ => { }, d.Dialogs, Runner: new CannedClaude(answer)));
    }

    private static async Task<ScriptTabViewModel> ClaudeScript(Demo d, string answer, string text)
    {
        UseCannedClaude(d, answer);
        await Open(d);
        var script = d.Main.NewScript()!;
        script.Text = text;
        d.Window.Show();
        Pump();
        return script;
    }

    private static async Task<Window?> ClaudePreview(Demo d)
    {
        var script = await ClaudeScript(d, "", Query.Replace("c.country", "c.countri"));
        await script.RunScriptAsync();
        await script.ExplainErrorAsync();
        await Settle();
        return null;
    }

    private static async Task<Window?> ClaudeDiff(Demo d)
    {
        var script = await ClaudeScript(d,
            "The comma join hides the join condition among the filters. An explicit JOIN reads better, and an index on the join column avoids scanning orders once per customer.\n\n"
            + "```sql replacement\nSELECT c.name, c.country, count(*) AS orders\nFROM orders o\nJOIN customers c ON c.id = o.customer_id\nWHERE o.status <> '__s1__'\nGROUP BY c.id\nORDER BY orders DESC;\n```\n\n"
            + "```sql\nCREATE INDEX ix_orders_customer ON orders (customer_id, status);\n```",
            Query);
        await script.OptimiseQueryAsync();
        await script.ClaudePanel!.SendCommand.ExecuteAsync(null);
        await Settle();
        return null;
    }

    private static async Task<Window?> ClaudeGenerate(Demo d)
    {
        var script = await ClaudeScript(d, "", "");
        await script.GenerateSqlAsync();
        script.ClaudePanel!.Request = "The ten customers with the most orders this year, with their country";
        await Settle();
        return null;
    }

    private static async Task<Window?> ClaudePlan(Demo d)
    {
        var script = await ClaudeScript(d,
            "Most of the work is the full scan of orders: every row is read to keep the ones that are not open.\n\n"
            + "An index that starts with the join column lets SQLite search instead:\n\n```sql\nCREATE INDEX ix_orders_customer ON orders (customer_id, status);\n```\n\nIt costs a little on every insert.",
            Query);
        await script.ShowEstimatedPlanAsync();
        await script.AnalysePlanAsync();
        await script.ClaudePanel!.SendCommand.ExecuteAsync(null);
        await Settle();
        return null;
    }

    private static async Task<Window?> ClaudeUpgrade(Demo d)
    {
        UseCannedClaude(d,
            "1. adj - 003: CREATE INDEX holds a shared lock on Suppliers while it builds; writers wait. Not re-runnable: it fails when the index exists.\n\n"
            + "2. fbo - 001: the UPDATE has no WHERE clause and rewrites every row in one transaction.\n\n"
            + "3. adj - 002: ADD column fails on a second run. Guard it with a check of sys.columns.");
        var dialog = (UpgradeDialog)(await UpgradeScripts(d, ""))!;
        var vm = (UpgradeViewModel)dialog.DataContext!;
        vm.UseClaude(d.Main.Assistant);
        await vm.ReviewWithClaudeAsync();
        await vm.ClaudePanel!.SendCommand.ExecuteAsync(null);
        await Settle();
        return dialog;
    }
}
