using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Core.Model;

namespace DbExplorer.App.ViewModels;

/// <summary>One chip of the type filter.</summary>
public sealed partial class QuickOpenTypeToggle(QuickOpenTypes type, string label, string icon, int number, bool isOn) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _isOn = isOn;

    public QuickOpenTypes Type { get; } = type;
    public string Label { get; } = label;

    /// <summary>Resource key of the vector icon (Styles/Icons.axaml).</summary>
    public string Icon { get; } = icon;

    public string Tooltip => IsOn
        ? $"{Label} are searched: switch them off (Alt+{number})"
        : $"{Label} are left out: search them again (Alt+{number})";
}

// The type filter: which kinds of object are searched. The choice is the user's, kept from one opening to the next.
public sealed partial class QuickOpenViewModel
{
    private QuickOpenTypes _hidden;
    private Action<QuickOpenTypes>? _hiddenChanged;

    /// <summary>The chips, in the order of Alt+1..5; empty where the list has no types to filter (columns jump, connections).</summary>
    public IReadOnlyList<QuickOpenTypeToggle> TypeToggles { get; private set; } = [];

    public bool HasTypeToggles => TypeToggles.Count > 0;

    private bool EveryTypeHidden => TypeToggles.Count > 0 && TypeToggles.All(t => !t.IsOn);

    private bool Shows(QuickOpenTypes? type) => type is not { } t || (_hidden & t) == 0;

    /// <summary>Shows the chips, starting with <paramref name="hidden"/> off; <paramref name="changed"/> stores every change.</summary>
    public QuickOpenViewModel UseTypeFilter(QuickOpenTypes hidden, Action<QuickOpenTypes>? changed = null)
    {
        _hidden = hidden;
        _hiddenChanged = changed;
        TypeToggles = new (QuickOpenTypes Type, string Label, string Icon)[]
        {
            (QuickOpenTypes.Tables, "Tables", "IconTable"),
            (QuickOpenTypes.Views, "Views", "IconView"),
            (QuickOpenTypes.Procedures, "Procedures", "IconProcedure"),
            (QuickOpenTypes.Functions, "Functions", "IconFunction"),
            (QuickOpenTypes.Columns, "Columns", "IconColumn"),
        }.Select((x, i) => new QuickOpenTypeToggle(x.Type, x.Label, x.Icon, i + 1, (hidden & x.Type) == 0)).ToList();
        foreach (var toggle in TypeToggles) toggle.PropertyChanged += OnTypeToggled;
        OnPropertyChanged(nameof(TypeToggles));
        OnPropertyChanged(nameof(HasTypeToggles));
        Rank();
        return this;
    }

    /// <summary>Alt+1..5: switches the chip at <paramref name="index"/> (0-based); nothing when there is none.</summary>
    public void ToggleType(int index)
    {
        if (index >= 0 && index < TypeToggles.Count) TypeToggles[index].IsOn = !TypeToggles[index].IsOn;
    }

    private void OnTypeToggled(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickOpenTypeToggle.IsOn) || sender is not QuickOpenTypeToggle toggle) return;
        _hidden = toggle.IsOn ? _hidden & ~toggle.Type : _hidden | toggle.Type;
        _hiddenChanged?.Invoke(_hidden);
        Rank();
    }
}
