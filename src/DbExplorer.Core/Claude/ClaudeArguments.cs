using System.Text.Json;

namespace DbExplorer.Core.Claude;

/// <summary>The arguments of one call, taken as hostile: unknown names, wrong types and out-of-range values are refused.</summary>
internal sealed class ClaudeArguments
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public ClaudeArguments(IReadOnlyDictionary<string, JsonElement> values, ClaudeToolDefinition tool)
    {
        _values = values;
        using var schema = JsonDocument.Parse(tool.InputSchema);
        var known = schema.RootElement.GetProperty("properties");
        if (values.Keys.FirstOrDefault(k => !known.TryGetProperty(k, out _)) is { } unknown)
            throw new ClaudeRefusedException($"Unknown argument '{unknown}'.");
    }

    public string Required(string name, int maxLength = 500) =>
        Optional(name, maxLength) is { Length: > 0 } value ? value : throw new ClaudeRefusedException($"The argument '{name}' is required.");

    public string? Optional(string name, int maxLength = 500)
    {
        if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ClaudeRefusedException($"The argument '{name}' must be a text.");
        var text = value.GetString()!;
        return text.Length <= maxLength ? text : throw new ClaudeRefusedException($"The argument '{name}' is too long.");
    }

    public int Number(string name, int fallback, int min, int max)
    {
        if (!_values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new ClaudeRefusedException($"The argument '{name}' must be a whole number.");
        return Math.Clamp(number, min, max);
    }
}
