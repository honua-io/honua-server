// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Benchmarks.RasterStorage;

internal static class RasterStorageCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            WriteUsage();
            return 0;
        }

        try
        {
            var arguments = ParsedArguments.Parse(args[1..]);
            return args[0] switch
            {
                "describe" => await DescribeAsync(arguments, cancellationToken).ConfigureAwait(false),
                "validate" => await ValidateAsync(arguments, cancellationToken).ConfigureAwait(false),
                "run-postgis" => await RunPostgisAsync(arguments, cancellationToken).ConfigureAwait(false),
                "run-cog" => await RunCogAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown raster-storage command '{args[0]}'."),
            };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine($"raster-storage: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> DescribeAsync(ParsedArguments arguments, CancellationToken cancellationToken)
    {
        var output = arguments.RequireSingle("output");
        await WriteJsonAsync(
                output,
                RasterStorageProtocol.Create(),
                RasterStorageJsonContext.Default.RasterStorageProtocolDefinition,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Wrote raster storage protocol {RasterStorageProtocol.Version} to {output}.");
        return 0;
    }

    private static async Task<int> ValidateAsync(ParsedArguments arguments, CancellationToken cancellationToken)
    {
        var input = arguments.RequireSingle("input");
        await using var stream = File.OpenRead(input);
        var run = await JsonSerializer.DeserializeAsync(
                stream,
                RasterStorageJsonContext.Default.RasterStorageBenchmarkRun,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Result document was empty.");
        RasterStorageProtocolValidator.ValidateRun(RasterStorageProtocol.Create(), run);
        Console.WriteLine($"Validated {run.Results.Count} result(s) for {run.ProtocolVersion}.");
        return run.Results.Any(result => result.Status == BenchmarkResultStatus.Failed) ? 1 : 0;
    }

    private static async Task<int> RunPostgisAsync(ParsedArguments arguments, CancellationToken cancellationToken)
    {
        var output = arguments.RequireSingle("output");
        var connectionString = arguments.SingleOrDefault("connection") ??
            Environment.GetEnvironmentVariable("HONUA_RASTER_BENCHMARK_CONNECTION") ??
            throw new ArgumentException(
                "Provide --connection or HONUA_RASTER_BENCHMARK_CONNECTION. Use an isolated benchmark database.");
        var adapter = new PostgisRasterStorageBenchmarkAdapter(new PostgisRasterStorageBenchmarkOptions(
            connectionString,
            arguments.Many("fixture"),
            arguments.PositiveIntOrDefault("warmup", 2, allowZero: true),
            arguments.PositiveIntOrDefault("samples", 10),
            arguments.PositiveIntOrDefault("block-size", 256),
            arguments.PositiveIntOrDefault("tenants", 4),
            arguments.HasFlag("keep-schema")));
        var run = await adapter.RunAsync(cancellationToken).ConfigureAwait(false);
        RasterStorageProtocolValidator.ValidateRun(RasterStorageProtocol.Create(), run);
        await WriteJsonAsync(
                output,
                run,
                RasterStorageJsonContext.Default.RasterStorageBenchmarkRun,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Wrote {run.Results.Count} PostGIS raster storage result(s) to {output}.");
        return run.Results.Any(result => result.Status == BenchmarkResultStatus.Failed) ? 1 : 0;
    }

    private static async Task<int> RunCogAsync(ParsedArguments arguments, CancellationToken cancellationToken)
    {
        var output = arguments.RequireSingle("output");
        var uriText = arguments.RequireSingle("url");
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("--url must be an absolute HTTP(S) object URL with byte-range support.");
        }

        var adapter = new CogRasterStorageBenchmarkAdapter(new CogRasterStorageBenchmarkOptions(
            uri,
            arguments.RequireSingle("fixture"),
            arguments.PositiveIntOrDefault("warmup", 2, allowZero: true),
            arguments.PositiveIntOrDefault("samples", 10)));
        var run = await adapter.RunAsync(cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
                output,
                run,
                RasterStorageJsonContext.Default.RasterStorageBenchmarkRun,
                cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"Wrote COG tile result to {output}.");
        return 0;
    }

    private static async Task WriteJsonAsync<T>(
        string output,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(output);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static void WriteUsage()
    {
        Console.WriteLine("""
            Raster storage benchmark protocol (RAST-015)

              raster-storage describe --output <protocol.json>
              raster-storage validate --input <results.json>
              raster-storage run-postgis --output <results.json> [--connection <npgsql>] [--fixture <id>] [--samples <n>] [--warmup <n>] [--block-size <n>] [--tenants <n>] [--keep-schema]
              raster-storage run-cog --url <signed-http-url> --fixture <small-raster|large-scene> --output <results.json> [--samples <n>] [--warmup <n>]

            If --connection is omitted, run-postgis reads HONUA_RASTER_BENCHMARK_CONNECTION.
            Repeat --fixture to select multiple PostGIS fixtures; no fixture flag runs all four.
            """);
    }

    private sealed class ParsedArguments(
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        IReadOnlySet<string> flags)
    {
        public static ParsedArguments Parse(string[] args)
        {
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var flags = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                {
                    throw new ArgumentException($"Unexpected argument '{token}'.");
                }

                var name = token[2..];
                if (name == "keep-schema")
                {
                    flags.Add(name);
                    continue;
                }

                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Argument '--{name}' requires a value.");
                }

                if (!values.TryGetValue(name, out var entries))
                {
                    entries = [];
                    values.Add(name, entries);
                }

                entries.Add(args[index]);
            }

            return new ParsedArguments(
                values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal),
                flags);
        }

        public string RequireSingle(string name)
            => SingleOrDefault(name) ?? throw new ArgumentException($"Missing required argument '--{name}'.");

        public string? SingleOrDefault(string name)
        {
            if (!values.TryGetValue(name, out var entries))
            {
                return null;
            }

            if (entries.Count != 1)
            {
                throw new ArgumentException($"Argument '--{name}' must be provided exactly once.");
            }

            return entries[0];
        }

        public IReadOnlyList<string> Many(string name)
            => values.TryGetValue(name, out var entries) ? entries : [];

        public bool HasFlag(string name) => flags.Contains(name);

        public int PositiveIntOrDefault(string name, int defaultValue, bool allowZero = false)
        {
            var text = SingleOrDefault(name);
            if (text is null)
            {
                return defaultValue;
            }

            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ||
                value < (allowZero ? 0 : 1))
            {
                throw new ArgumentException($"--{name} must be {(allowZero ? "a non-negative" : "a positive")} integer.");
            }

            return value;
        }
    }
}
