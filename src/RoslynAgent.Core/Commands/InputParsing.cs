using System.Text.Json;
using RoslynAgent.Contracts;

namespace RoslynAgent.Core.Commands;

internal static class InputParsing
{
    public static bool TryGetRequiredString(
        JsonElement input,
        string propertyName,
        List<CommandError> errors,
        out string value)
    {
        value = string.Empty;
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' is required and must be a string."));
            return false;
        }

        string? raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' must not be empty."));
            return false;
        }

        value = raw;
        return true;
    }

    public static int GetOptionalInt(JsonElement input, string propertyName, int defaultValue, int minValue, int maxValue)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number)
        {
            return defaultValue;
        }

        if (!property.TryGetInt32(out int value))
        {
            return defaultValue;
        }

        if (value < minValue)
        {
            return minValue;
        }

        if (value > maxValue)
        {
            return maxValue;
        }

        return value;
    }
}
