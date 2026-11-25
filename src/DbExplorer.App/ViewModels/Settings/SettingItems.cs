using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.App.ViewModels;

/// <summary>
/// One row of a settings page: a label, an optional explanation and a control. Items hold no value of their own: they read and
/// write through delegates (the settings service or the config), so the page always shows what is really stored.
/// </summary>
public abstract class SettingItemViewModel(string label, string? description = null) : ObservableObject
{
    public string Label { get; } = label;
    public string? Description { get; } = description;
    public bool HasDescription => !string.IsNullOrEmpty(Description);

    /// <summary>Extra words the search matches besides the label (synonyms: "dark" finds Theme).</summary>
    public string Keywords { get; init; } = "";

    /// <summary>Put the setting back to its default (through the same path as an edit, so it applies and persists).</summary>
    public abstract void Reset();

    /// <summary>Tells the control to re-read the stored value (after a reset or a change made elsewhere).</summary>
    public abstract void Refresh();

    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query)
        || Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
        || Keywords.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>An on/off check box.</summary>
public sealed class ToggleSettingItem(string label, string? description, Func<bool> read, Action<bool> write, bool defaultValue)
    : SettingItemViewModel(label, description)
{
    public bool Value
    {
        get => read();
        set
        {
            if (value == read()) return;
            write(value);
            OnPropertyChanged();
        }
    }

    public override void Reset() => Value = defaultValue;
    public override void Refresh() => OnPropertyChanged(nameof(Value));
}

/// <summary>One option of a <see cref="ChoiceSettingItem"/>; <see cref="Swatch"/> is an optional colour (hex) shown as a dot.</summary>
public sealed record ChoiceOption(string Label, object Value, string? Swatch = null)
{
    public bool HasSwatch => Swatch is not null;
}

/// <summary>A pick among a few named options: a segmented control (few options) or a drop-down list.</summary>
public sealed class ChoiceSettingItem : SettingItemViewModel
{
    private readonly Func<object> _get;
    private readonly Action<object> _set;
    private readonly object _default;

    public ChoiceSettingItem(string label, string? description, IReadOnlyList<ChoiceOption> options, Func<object> get, Action<object> set, object defaultValue)
        : base(label, description)
    {
        Options = options;
        _get = get;
        _set = set;
        _default = defaultValue;
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    /// <summary>Few options sit side by side as chips; more than that go in a combo box.</summary>
    public bool UseChips { get; init; } = true;
    public bool UseCombo => !UseChips;

    /// <summary>Chips sit beside the label unless there are too many for the row: then they wrap under it.</summary>
    public bool StackChips => UseChips && Options.Count > 4;
    public bool SideChips => UseChips && !StackChips;

    public ChoiceOption Selected
    {
        get => Options.FirstOrDefault(o => Equals(o.Value, _get())) ?? Options.First(o => Equals(o.Value, _default));
        set
        {
            if (value is null || Equals(value.Value, _get())) return;
            _set(value.Value);
            OnPropertyChanged();
        }
    }

    /// <summary>Per option: is it the chosen one (the chips bind to this).</summary>
    public IReadOnlyList<ChoiceChip> Chips => _chips ??= Options.Select(o => new ChoiceChip(this, o)).ToList();
    private IReadOnlyList<ChoiceChip>? _chips;

    public override void Reset() => Selected = Options.First(o => Equals(o.Value, _default));

    public override void Refresh()
    {
        OnPropertyChanged(nameof(Selected));
        if (_chips is not null) foreach (var chip in _chips) chip.Refresh();
    }
}

/// <summary>A chip of a choice: checked while its option is the chosen one; checking it chooses it.</summary>
public sealed class ChoiceChip(ChoiceSettingItem owner, ChoiceOption option) : ObservableObject
{
    public ChoiceOption Option { get; } = option;
    public string Label => Option.Label;
    public string? Swatch => Option.Swatch;
    public bool HasSwatch => Option.HasSwatch;

    public bool IsChecked
    {
        get => ReferenceEquals(owner.Selected, Option);
        set
        {
            if (value) owner.Selected = Option;
            OnPropertyChanged();   // a click on the chosen chip must not uncheck it
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>A number with a range: out-of-range input is clamped to it, empty or invalid input is ignored.</summary>
public sealed class NumberSettingItem(string label, string? description, Func<double> read, Action<double> write,
    double min, double max, double step, double defaultValue, string? suffix = null) : SettingItemViewModel(label, description)
{
    public double Minimum { get; } = min;
    public double Maximum { get; } = max;
    public double Step { get; } = step;
    public string? Suffix { get; } = suffix;

    /// <summary>The value as the spin box edits it (decimal: that is its type).</summary>
    public decimal? Value
    {
        get => (decimal)read();
        set
        {
            if (value is null) { OnPropertyChanged(); return; }   // blanked box: keep the stored value and re-show it
            var clamped = Math.Clamp((double)value.Value, Minimum, Maximum);
            if (clamped != read()) write(clamped);
            OnPropertyChanged();
        }
    }

    public decimal DecimalMinimum => (decimal)Minimum;
    public decimal DecimalMaximum => (decimal)Maximum;
    public decimal DecimalStep => (decimal)Step;

    public override void Reset() => Value = (decimal)defaultValue;
    public override void Refresh() => OnPropertyChanged(nameof(Value));
}
