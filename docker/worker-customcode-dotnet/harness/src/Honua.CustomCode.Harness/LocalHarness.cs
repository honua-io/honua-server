// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>Filesystem adapter for debugging trusted tools through the production contract.</summary>
public static class LocalHarness
{
    /// <summary>Run a built tool with local inputs and a fresh output directory, without cloud credentials.</summary>
    public static async Task<int> RunAsync(
        string assemblyDirectory, string entrypoint, JsonElement parameters,
        IReadOnlyDictionary<string, string> inputs, string outputDirectory,
        long maxOutputBytes = 100 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var runDirectory = UnderRoot(Path.GetFullPath(outputDirectory), runId);
        var workDirectory = UnderRoot(runDirectory, "work");
        Directory.CreateDirectory(workDirectory);
        using var journal = new LocalJournal(UnderRoot(runDirectory, "events.jsonl"));
        var receipt = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["runId"] = runId, ["entrypoint"] = entrypoint,
            ["executionMode"] = "local", ["contentClassification"] = "local-development-unsanitized",
            ["artifacts"] = new JsonArray(), ["events"] = "events.jsonl"
        };
        var phase = "setup";
        var exitCode = Harness.ExitHarnessError;
        journal.Event(phase);
        try
        {
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Parameters must be a JSON object.");
            }

            var localInputs = inputs.ToDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value), StringComparer.Ordinal);
            if (localInputs.Values.Any(path => !File.Exists(path)))
            {
                throw new FileNotFoundException("A local input file is missing.");
            }

            var sink = new OutputSink(maxOutputBytes);
            var tool = new EntrypointLoader().Load(Path.GetFullPath(assemblyDirectory), entrypoint);
            var context = new GpContext(parameters, localInputs, null, sink, journal, journal, workDirectory);
            phase = "execute";
            journal.Event(phase);
            cancellationToken.ThrowIfCancellationRequested();
            var result = await tool.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is { Ok: false })
            {
                throw new InvalidOperationException(result.Message ?? "Tool reported failure.");
            }

            phase = "artifacts";
            journal.Event(phase);
            long total = 0;
            foreach (var artifact in sink.Artifacts)
            {
                var relative = Path.GetRelativePath(workDirectory, artifact.Path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new IOException("Output artifacts must be files inside the run work directory.");
                }

                // Do not follow symlinks/junctions out of the run directory.
                var candidate = artifact.Path;
                var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                while (!string.Equals(candidate, workDirectory, pathComparison))
                {
                    if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("Output artifacts cannot traverse symbolic links.");
                    }

                    candidate = Path.GetDirectoryName(candidate)
                        ?? throw new IOException("Output artifact ancestry does not reach the run work directory.");
                }

                total = checked(total + new FileInfo(artifact.Path).Length);
                if (total > maxOutputBytes)
                {
                    throw new OutputSizeExceededException("Artifacts exceed the configured output cap.");
                }
            }

            var artifactDirectory = UnderRoot(runDirectory, "artifacts");
            Directory.CreateDirectory(artifactDirectory);
            var artifacts = new JsonArray();
            foreach (var artifact in sink.Artifacts)
            {
                var destination = UnderRoot(artifactDirectory, artifact.Name);
                File.Copy(artifact.Path, destination);
                using var stream = File.OpenRead(destination);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                artifacts.Add(new JsonObject
                {
                    ["name"] = artifact.Name, ["path"] = "artifacts/" + artifact.Name,
                    ["sizeBytes"] = stream.Length, ["sha256"] = hash
                });
            }

            receipt["artifacts"] = artifacts;
            receipt["status"] = "succeeded";
            exitCode = Harness.ExitOk;
        }
        catch (OperationCanceledException)
        {
            receipt["status"] = "cancelled";
            receipt["failedPhase"] = phase;
            exitCode = Harness.ExitCancelled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            receipt["status"] = phase == "setup" ? "setupFailed" : "failed";
            receipt["failedPhase"] = phase;
            receipt["error"] = new JsonObject { ["type"] = ex.GetType().Name, ["message"] = Limit(ex.Message, 2000) };
            exitCode = phase == "setup" ? Harness.ExitHarnessError : Harness.ExitToolFailed;
        }

        journal.Event(receipt["status"]!.GetValue<string>(), terminal: true);
        receipt["eventsTruncated"] = journal.Truncated;
        receipt["exitCode"] = exitCode;
        var receiptPath = UnderRoot(runDirectory, "receipt.json");
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine(new JsonObject
        {
            ["runId"] = runId, ["status"] = receipt["status"]!.GetValue<string>(), ["receipt"] = receiptPath,
            ["failedPhase"] = receipt["failedPhase"]?.DeepClone(), ["error"] = receipt["error"]?.DeepClone()
        }.ToJsonString());
        return exitCode;
    }

    /// <summary>Parse the explicit local mode; production job configuration is unchanged.</summary>
    public static async Task<int> RunCommandAsync(string[] args, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length)
                {
                    throw new ArgumentException($"Missing value for {args[i]}.");
                }

                if (args[i] == "--input")
                {
                    var pair = args[i + 1].Split('=', 2);
                    if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]) || string.IsNullOrWhiteSpace(pair[1]))
                    {
                        throw new ArgumentException("Input must be NAME=PATH.");
                    }

                    inputs.Add(pair[0], pair[1]);
                }
                else if (args[i] is "--assembly-dir" or "--entrypoint" or "--params" or "--output" or "--max-output-bytes")
                {
                    options.Add(args[i], args[i + 1]);
                }
                else
                {
                    throw new ArgumentException($"Unknown option {args[i]}.");
                }
            }

            if (!options.TryGetValue("--assembly-dir", out var assemblyDirectory) || !options.TryGetValue("--entrypoint", out var entrypoint))
            {
                throw new ArgumentException("local requires --assembly-dir and --entrypoint Assembly::Namespace.Type.");
            }

            using var parameters = JsonDocument.Parse(options.TryGetValue("--params", out var file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : "{}");
            var maxBytes = options.TryGetValue("--max-output-bytes", out var limit) ? long.Parse(limit, System.Globalization.CultureInfo.InvariantCulture) : 100 * 1024 * 1024;
            return await RunAsync(assemblyDirectory, entrypoint, parameters.RootElement, inputs,
                options.GetValueOrDefault("--output", ".honua/runs"), maxBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or FormatException or OverflowException)
        {
            Console.Error.WriteLine(ex.Message);
            return Harness.ExitHarnessError;
        }
    }

    private static string Limit(string value, int length) => value.Length > length ? value[..length] : value;

    // Path.Join keeps root. Path.Combine would drop it when segment is absolute.
    private static string UnderRoot(string root, string segment)
    {
        if (Path.IsPathRooted(segment))
        {
            throw new IOException("A path segment under the run directory must be relative.");
        }

        return Path.Join(root, segment);
    }

    private sealed class LocalJournal(string path) : IProgressReporter, IGpLogger, IDisposable
    {
        private readonly StreamWriter _stream = new(path) { AutoFlush = true };
        private readonly object _gate = new();
        private int _sequence;
        public bool Truncated { get; private set; }

        public void Event(string phase, double? percent = null, string? message = null, bool terminal = false)
        {
            lock (_gate)
            {
                if (_sequence >= 1000 && !terminal)
                {
                    Truncated = true;
                    return;
                }

                _stream.WriteLine(new JsonObject
                {
                    ["sequence"] = _sequence++, ["timestamp"] = DateTimeOffset.UtcNow,
                    ["phase"] = Limit(phase, 500), ["percentComplete"] = percent,
                    ["message"] = message is null ? null : Limit(message, 2000)
                }.ToJsonString());
            }
        }

        public void Report(double percent, string phase)
        {
            if (!double.IsFinite(percent))
            {
                throw new ArgumentOutOfRangeException(nameof(percent));
            }

            Event(phase, Math.Clamp(percent, 0, 100));
        }

        public void Info(string message) => Event("log", message: message);
        public void Warn(string message) => Event("warning", message: message);
        public void Dispose() => _stream.Dispose();
    }
}
