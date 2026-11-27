using Avalonia.Controls;
using DbExplorer.App.ViewModels;
using DbExplorer.Core.Comparing;

namespace UiShots;

internal static partial class Scenes
{
    // Two results of the same query a while apart: changed cells, rows of one side only, an extra column, a row limit.
    private static async Task<Window?> CompareScene(Demo d, bool onlyDifferences)
    {
        await Open(d);

        CompareSnapshot Side(string name, string connection, string? partial, string[] columns, IEnumerable<object?[]> rows) =>
            new(name, $"{name} ({connection})", d.Shop,
                new ComparedData(columns.Select(c => new ComparedColumn(c)).ToList(), rows.ToArray()), partial);

        string[] states = ["open", "paid", "shipped", "closed"];
        object?[] Row(long i) => [i, $"Customer {i % 60 + 1:00}", states[i % 4], Math.Round(5 + i * 37 % 1000 / 10.0, 2), i % 9 == 0 ? "gift wrap" : null];
        var before = Enumerable.Range(1, 40).Select(i => Row(i)).ToList();
        var after = Enumerable.Range(1, 42).Where(i => i is not 4 and not 11).Select(i =>
        {
            var row = Row(i);
            if (i % 6 == 2) row[2] = "closed";
            if (i is 3 or 8) row[3] = (double)row[3]! + 10;
            if (i == 6) row[4] = "call before delivery";
            return row.Append((object?)"EUR").ToArray();
        });
        string[] names = ["id", "customer", "status", "total", "notes"];

        var tab = d.Main.OpenComparison(
            Side("orders", "Shop (local)", null, names, before),
            Side("Script 1 · 1", "Staging billing", "the result stopped at the row limit (40 rows).", [.. names, "currency"], after));
        await tab.Pending;
        tab.OnlyDifferences = onlyDifferences;
        await Settle();
        tab.NextDifference();
        await Settle();
        return null;
    }
}
