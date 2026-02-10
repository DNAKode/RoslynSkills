using System.Text.Json;
using RoslynSkills.Contracts;

namespace RoslynSkills.Core.Commands;

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

    public static bool TryGetRequiredInt(
        JsonElement input,
        string propertyName,
        List<CommandError> errors,
        out int value,
        int minValue = int.MinValue,
        int maxValue = int.MaxValue)
    {
        value = 0;
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Number)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' is required and must be a number."));
            return false;
        }

        if (!property.TryGetInt32(out int parsed))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' must be a valid 32-bit integer."));
            return false;
        }

        if (parsed < minValue || parsed > maxValue)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' must be between {minValue} and {maxValue}."));
            return false;
        }

        value = parsed;
        return true;
    }

    public static bool GetOptionalBool(JsonElement input, string propertyName, bool defaultValue)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) ||
            (property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False))
        {
            return defaultValue;
        }

        return property.GetBoolean();
    }

    public static string[] GetOptionalStringArray(JsonElement input, string propertyName)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        List<string> values = new();
        foreach (JsonElement item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? value = item.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            values.Add(value);
        }

        return values.ToArray();
    }
}

