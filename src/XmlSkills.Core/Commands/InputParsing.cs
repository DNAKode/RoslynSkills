using System.Text.Json;
using XmlSkills.Contracts;

namespace XmlSkills.Core.Commands;

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

    public static bool GetOptionalBool(JsonElement input, string propertyName, bool defaultValue)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
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

    public static void ValidateOptionalBool(
        JsonElement input,
        string propertyName,
        List<CommandError> errors)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        errors.Add(new CommandError(
            "invalid_input",
            $"Property '{propertyName}' must be a boolean when provided."));
    }

    public static void ValidateOptionalInt(
        JsonElement input,
        string propertyName,
        List<CommandError> errors,
        int minValue,
        int maxValue)
    {
        if (!input.TryGetProperty(propertyName, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int value))
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' must be a 32-bit integer when provided."));
            return;
        }

        if (value < minValue || value > maxValue)
        {
            errors.Add(new CommandError(
                "invalid_input",
                $"Property '{propertyName}' must be between {minValue} and {maxValue} when provided."));
        }
    }
}
