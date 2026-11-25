using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;
using DbExplorer.Core.Scripting;

namespace DbExplorer.App.ViewModels;

/// <summary>The sample statement of the SQL formatting page, formatted with the options chosen above it.</summary>
public sealed partial class FormatPreviewViewModel : ObservableObject
{
    private const string Sample =
        "select o.id, c.name, sum(l.qty * l.price) as total from orders o join customers c on c.id = o.customer_id " +
        "left join lines l on l.order_id = o.id where o.status in ('new', 'paid') and o.created >= '2024-01-01' " +
        "group by o.id, c.name order by total desc";

    [ObservableProperty]
    private string _text = "";

    /// <summary>Tabs are shown as spaces: a tab character would not measure the same in the preview box.</summary>
    public void Update(SqlFormatOptions options) =>
        Text = SqlFormatter.Format(Sample, DbKind.Postgres, options).Replace("\t", "    ");
}

/// <summary>The SQL formatter style (Format SQL in a script): keyword case, indentation, commas, width, with a live preview.</summary>
public sealed class FormatSettingsPage : SettingsPageViewModel
{
    private const int TabsValue = 0;
    private readonly SettingsContext _ctx;

    public FormatSettingsPage(SettingsContext ctx)
        : base("format", "SQL formatting", "IconFormat", "How Format SQL lays out a statement. A change applies to the next format.")
    {
        _ctx = ctx;
        const string words = " format sql beautify style";

        var keywordCase = new ChoiceSettingItem("Keyword case", "SELECT, FROM, WHERE and the other keywords.",
            [new("UPPER CASE", KeywordCase.Upper), new("lower case", KeywordCase.Lower), new("As typed", KeywordCase.Keep)],
            () => Current.KeywordCase, v => Apply(Current with { KeywordCase = (KeywordCase)v }), KeywordCase.Upper)
        { Keywords = "uppercase lowercase keywords case" + words };

        var indent = new ChoiceSettingItem("Indentation", "What one indent level is made of.",
            [new("2 spaces", 2), new("4 spaces", 4), new("8 spaces", 8), new("Tab", TabsValue)],
            () => Current.UseTabs ? TabsValue : NearestSize(Current.IndentSize),
            v => Apply((int)v == TabsValue ? Current with { UseTabs = true } : Current with { UseTabs = false, IndentSize = (int)v }), 4)
        { Keywords = "indent indentation tab tabs spaces" + words };

        var comma = new ChoiceSettingItem("Comma position", "Where the comma goes when a list is split over lines.",
            [new("End of the line", CommaPosition.Trailing), new("Start of the line", CommaPosition.Leading)],
            () => Current.CommaPosition, v => Apply(Current with { CommaPosition = (CommaPosition)v }), CommaPosition.Trailing)
        { Keywords = "comma leading trailing list" + words };

        var width = new NumberSettingItem("Maximum line width", "Lists (IN, argument lists, AND / OR groups) wrap past this width.",
            () => Current.MaxLineWidth, v => Apply(Current with { MaxLineWidth = (int)v }), 40, 400, 10, 100, "characters")
        { Keywords = "width wrap line length columns" + words };

        AddGroup("Style", keywordCase, indent, comma, width);
        Preview.Update(Current);
    }

    /// <summary>The live sample, shown under the choices.</summary>
    public FormatPreviewViewModel Preview { get; } = new();

    public override object? Footer => Preview;

    private SqlFormatOptions Current => _ctx.GetFormat!();

    private static int NearestSize(int size) => size switch { <= 2 => 2, <= 6 => 4, _ => 8 };

    private void Apply(SqlFormatOptions options)
    {
        _ctx.SetFormat!(options);
        Preview.Update(Current);
    }

    public override void Refresh()
    {
        base.Refresh();
        Preview.Update(Current);
    }
}
