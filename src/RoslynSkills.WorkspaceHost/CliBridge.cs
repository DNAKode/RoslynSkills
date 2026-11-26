using System.Reflection;
using RoslynSkills.Contracts;
using RoslynSkills.Core;

namespace RoslynSkills.WorkspaceHost;

/// <summary>Outcome of a <c>cli/run</c> request.</summary>
internal sealed record CliBridgeResult(bool Eligible, int ExitCode, string Stdout, string Stderr, string? Reason);

/// <summary>
/// Runs a shell <c>roscli</c> invocation inside the warm host so a thin native client can skip the .NET process start
/// of the shell CLI (the dominant cost of a warm query) and still print exactly what <c>roscli</c> prints: argument
/// parsing, path resolution, routing and envelope formatting stay in <c>CliApplication</c>; the host only supplies the
/// process. <c>RoslynSkills.Cli.dll</c> is loaded from the host's own folder by reflection (the CLI references this
/// project, not the reverse); when it is absent every request answers <c>cli_fallback</c>.
/// </summary>
/// <remarks>
/// Only hot-workspace commands run here, never workspace/daemon lifecycle or stdin-fed forms. The working directory and
/// <c>ROSCLI_*</c>/<c>ROSLYNSKILLS_*</c> environment of the client are applied for the duration of one request under a
/// lock, so concurrent bridge calls are serialized (queries are milliseconds).
/// </remarks>
internal static class CliBridge
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object LoadLock = new();
    private static object? _application;
    private static MethodInfo? _runAsync;
    private static MethodInfo? _runAsyncWithFormat;
    private static string? _loadFailure;
    private static bool _loadAttempted;

    public static Task<CliBridgeResult> RunAsync(
        ICommandRegistry registry,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
        => RunAsync(registry, args, workingDirectory, environment, defaultCompactJson: true, cancellationToken);

    public static async Task<CliBridgeResult> RunAsync(
        ICommandRegistry registry,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        bool defaultCompactJson,
        CancellationToken cancellationToken)
    {
        string? ineligible = CheckEligibility(registry, args, workingDirectory);
        if (ineligible is not null)
        {
            return new CliBridgeResult(false, 0, string.Empty, string.Empty, ineligible);
        }

        if (!TryLoad(registry, out object? application, out MethodInfo? runAsync, out MethodInfo? runAsyncWithFormat, out string? loadFailure))
        {
            return new CliBridgeResult(false, 0, string.Empty, string.Empty, loadFailure);
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string previousDirectory = Directory.GetCurrentDirectory();
        Dictionary<string, string?> savedEnvironment = new(StringComparer.Ordinal);
        try
        {
            Directory.SetCurrentDirectory(workingDirectory);
            ApplyEnvironment(environment, savedEnvironment);

            using StringWriter stdout = new();
            using StringWriter stderr = new();
            Task<int> run = runAsyncWithFormat is not null
                ? (Task<int>)runAsyncWithFormat.Invoke(
                    application,
                    new object?[] { args.ToArray(), stdout, stderr, cancellationToken, TextReader.Null, (bool?)defaultCompactJson })!
                : (Task<int>)runAsync!.Invoke(
                    application,
                    new object?[] { args.ToArray(), stdout, stderr, cancellationToken, TextReader.Null })!;
            int exitCode = await run.ConfigureAwait(false);
            return new CliBridgeResult(true, exitCode, stdout.ToString(), stderr.ToString(), null);
        }
        finally
        {
            foreach ((string name, string? value) in savedEnvironment)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            try
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The previous directory vanished; the host never depends on its own working directory.
            }

            Gate.Release();
        }
    }

    internal static string? CheckEligibility(ICommandRegistry registry, IReadOnlyList<string> args, string workingDirectory)
    {
        if (args.Count == 0)
        {
            return "no arguments";
        }

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            return "working directory does not exist on the host";
        }

        int verbIndex = 0;
        while (verbIndex < args.Count &&
               (string.Equals(args[verbIndex], "--compact-json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[verbIndex], "--pretty-json", StringComparison.OrdinalIgnoreCase)))
        {
            verbIndex++;
        }

        if (verbIndex >= args.Count)
        {
            return "no command id";
        }

        string verb = args[verbIndex];
        int commandIndex = string.Equals(verb, "run", StringComparison.Ordinal) ? verbIndex + 1 : verbIndex;
        while (commandIndex < args.Count &&
               (string.Equals(args[commandIndex], "--compact-json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[commandIndex], "--pretty-json", StringComparison.OrdinalIgnoreCase)))
        {
            commandIndex++;
        }

        if (commandIndex >= args.Count)
        {
            return "no command id";
        }

        if (!registry.TryGet(args[commandIndex], out IAgentCommand? command) || command is null)
        {
            return $"'{args[commandIndex]}' is a CLI verb, not a registry command";
        }

        if (!command.Descriptor.RunsOnHotWorkspace)
        {
            return $"'{args[commandIndex]}' does not run on the hot workspace";
        }

        for (int i = commandIndex + 1; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--input-stdin", StringComparison.Ordinal) ||
                (string.Equals(args[i], "--input", StringComparison.Ordinal) &&
                 i + 1 < args.Count &&
                 (string.Equals(args[i + 1], "-", StringComparison.Ordinal) ||
                  args[i + 1].StartsWith('@'))))
            {
                return "input comes from stdin or a file relative to the client";
            }
        }

        return null;
    }

    private static void ApplyEnvironment(IReadOnlyDictionary<string, string> requested, Dictionary<string, string?> saved)
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && IsForwardedName(name))
            {
                saved[name] = entry.Value as string;
                if (!requested.ContainsKey(name))
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }
        }

        foreach ((string name, string value) in requested)
        {
            if (!IsForwardedName(name))
            {
                continue;
            }

            saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    internal static bool IsForwardedName(string name)
        => name.StartsWith("ROSCLI_", StringComparison.Ordinal) ||
           name.StartsWith("ROSLYNSKILLS_", StringComparison.Ordinal);

    private static bool TryLoad(
        ICommandRegistry registry,
        out object? application,
        out MethodInfo? runAsync,
        out MethodInfo? runAsyncWithFormat,
        out string? failure)
    {
        lock (LoadLock)
        {
            if (!_loadAttempted)
            {
                _loadAttempted = true;
                try
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "RoslynSkills.Cli.dll");
                    if (!File.Exists(path))
                    {
                        _loadFailure = "RoslynSkills.Cli.dll is not next to the host";
                    }
                    else
                    {
                        Assembly cli = Assembly.LoadFrom(path);
                        Type? type = cli.GetType("RoslynSkills.Cli.CliApplication");
                        MethodInfo? method = type?.GetMethod(
                            "RunAsync",
                            new[] { typeof(string[]), typeof(TextWriter), typeof(TextWriter), typeof(CancellationToken), typeof(TextReader) });
                        MethodInfo? methodWithFormat = type?.GetMethod(
                            "RunAsync",
                            new[] { typeof(string[]), typeof(TextWriter), typeof(TextWriter), typeof(CancellationToken), typeof(TextReader), typeof(bool?) });
                        if (type is null || (method is null && methodWithFormat is null))
                        {
                            _loadFailure = "CliApplication.RunAsync was not found";
                        }
                        else
                        {
                            _application = Activator.CreateInstance(type, registry);
                            _runAsync = method;
                            _runAsyncWithFormat = methodWithFormat;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException or MissingMethodException or TargetInvocationException or FileLoadException)
                {
                    _loadFailure = $"CLI bridge could not load: {ex.Message}";
                }
            }

            application = _application;
            runAsync = _runAsync;
            runAsyncWithFormat = _runAsyncWithFormat;
            failure = _application is null ? _loadFailure ?? "CLI bridge unavailable" : null;
            return _application is not null;
        }
    }
}
