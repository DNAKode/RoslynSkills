#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RoslynSkills.Contracts;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceHostRequest))]
[JsonSerializable(typeof(WorkspaceHostResponse))]
[JsonSerializable(typeof(WorkspaceHostHandshake))]
[JsonSerializable(typeof(WorkspaceHostWorkspaceMetadata))]
[JsonSerializable(typeof(CommandEnvelope))]
[JsonSerializable(typeof(CommandError))]
[JsonSerializable(typeof(CommandTransportMetadata))]
[JsonSerializable(typeof(CommandTransportTimings))]
[JsonSerializable(typeof(CommandDescriptor))]
public sealed partial class ContractsJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions CreateDefaultOptions() => new()
    {
        WriteIndented = false,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(new ContractsJsonContext(), new DefaultJsonTypeInfoResolver()),
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceHostRequest))]
[JsonSerializable(typeof(WorkspaceHostResponse))]
[JsonSerializable(typeof(WorkspaceHostHandshake))]
[JsonSerializable(typeof(WorkspaceHostWorkspaceMetadata))]
[JsonSerializable(typeof(CommandEnvelope))]
[JsonSerializable(typeof(CommandError))]
[JsonSerializable(typeof(CommandTransportMetadata))]
[JsonSerializable(typeof(CommandTransportTimings))]
[JsonSerializable(typeof(CommandDescriptor))]
public sealed partial class McpContractsJsonContext : JsonSerializerContext
{
    public static JsonSerializerOptions CreateMcpOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = JsonTypeInfoResolver.Combine(new McpContractsJsonContext(), new DefaultJsonTypeInfoResolver()),
    };
}
