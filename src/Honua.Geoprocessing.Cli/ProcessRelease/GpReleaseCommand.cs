// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Nodes;
using Honua.Geoprocessing.Release;

namespace Honua.Geoprocessing.Cli.Release;

/// <summary>
/// Implements the <c>honua-gp-devkit release</c> verb (honua-server#6221):
/// <list type="bullet">
/// <item><c>release --descriptor honua-gp.yaml --output out/release [--source-repository o/n] [--source-sha sha] [--runtime-image ref@sha256:..] [--generated-at ts] [--json]</c>
/// builds the process artifact once and writes the immutable lock the release workflow
/// publishes by digest.</item>
/// <item><c>release verify &lt;lock.json&gt; [--artifacts &lt;dir&gt;] [--json]</c> validates a lock,
/// prints its digest, and checks that the artifacts on disk are the bytes it names.</item>
/// </list>
/// The command is offline: it talks to Git and the file system only. Publishing a lock to the
/// server's release store is the binding/admission half of #6221 and is not part of this verb.
/// </summary>
internal static class GpReleaseCommand
{
    /// <summary>Runs the verb.</summary>
    /// <param name="args">Everything after <c>release</c>.</param>
    /// <param name="output">Standard output sink.</param>
    /// <param name="error">Error output sink.</param>
    /// <returns>0 on success, 1 when the release cannot be built or verified, 2 on usage error.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length > 0 && string.Equals(args[0], "verify", StringComparison.Ordinal))
        {
            return Verify(args[1..], output, error);
        }

        string? descriptor = null;
        string? outputDirectory = null;
        string? repository = null;
        string? sha = null;
        string? runtimeImage = null;
        string? generatedAt = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--descriptor" or "-d":
                    descriptor = Next(args, ref i, "--descriptor");
                    break;
                case "--output" or "-o":
                    outputDirectory = Next(args, ref i, "--output");
                    break;
                case "--source-repository":
                    repository = Next(args, ref i, "--source-repository");
                    break;
                case "--source-sha":
                    sha = Next(args, ref i, "--source-sha");
                    break;
                case "--runtime-image":
                    runtimeImage = Next(args, ref i, "--runtime-image");
                    break;
                case "--generated-at":
                    generatedAt = Next(args, ref i, "--generated-at");
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    throw new GpCliUsageException($"Unknown option '{args[i]}' for release.");
            }
        }

        descriptor ??= "honua-gp.yaml";
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new GpCliUsageException("release requires --output <dir> (receives process-release-lock.json and artifacts/).");
        }

        GpReleaseResult result;
        try
        {
            result = GpReleaseBuilder.Build(new GpReleaseRequest
            {
                DescriptorPath = descriptor,
                OutputDirectory = outputDirectory,
                SourceRepository = repository,
                SourceSha = sha,
                RuntimeImage = runtimeImage,
                GeneratedAt = generatedAt,
            });
        }
        catch (GpReleaseException ex)
        {
            error.WriteLine("error   : the release cannot be built:");
            foreach (var finding in ex.Errors)
            {
                error.WriteLine($"          - {finding}");
            }

            return 1;
        }

        if (json)
        {
            var document = new JsonObject
            {
                ["digest"] = result.Lock.Digest,
                ["shortDigest"] = result.Lock.ShortDigest,
                ["process"] = result.Lock.Process.Name,
                ["version"] = result.Lock.Process.Version,
                ["sourceSha"] = result.Lock.Source.Sha,
                ["lockPath"] = result.LockPath,
                ["artifacts"] = new JsonArray(result.ArtifactPaths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            };
            output.WriteLine(document.ToJsonString());
            return 0;
        }

        output.WriteLine($"release : {result.Lock.Process.Name} {result.Lock.Process.Version} ({result.Lock.Process.Runtime}) at {result.Lock.Source.Sha}");
        output.WriteLine($"digest  : {result.Lock.Digest}");
        output.WriteLine($"lock    : {result.LockPath}");
        foreach (var artifact in result.ArtifactPaths)
        {
            output.WriteLine($"artifact: {artifact}");
        }

        return 0;
    }

    private static int Verify(string[] args, TextWriter output, TextWriter error)
    {
        string? lockPath = null;
        string? artifactsDirectory = null;
        var json = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--artifacts":
                    artifactsDirectory = Next(args, ref i, "--artifacts");
                    break;
                case "--json":
                    json = true;
                    break;
                default:
                    if (args[i].StartsWith('-') || lockPath is not null)
                    {
                        throw new GpCliUsageException($"Unknown option '{args[i]}' for release verify.");
                    }

                    lockPath = args[i];
                    break;
            }
        }

        if (lockPath is null)
        {
            throw new GpCliUsageException("release verify requires the lock file path.");
        }

        if (!File.Exists(lockPath))
        {
            error.WriteLine($"error   : lock not found: {lockPath}");
            return 1;
        }

        GpProcessReleaseLock lockDocument;
        try
        {
            lockDocument = GpProcessReleaseLock.Parse(File.ReadAllText(lockPath));
        }
        catch (GpProcessReleaseLockException ex)
        {
            error.WriteLine("error   : the lock is not a valid honua.gp-process-release-lock/v1 document:");
            foreach (var finding in ex.Errors)
            {
                error.WriteLine($"          - {finding}");
            }

            return 1;
        }

        var findings = artifactsDirectory is null
            ? Array.Empty<string>()
            : GpReleaseBuilder.VerifyArtifacts(lockDocument, artifactsDirectory);
        if (json)
        {
            var document = new JsonObject
            {
                ["digest"] = lockDocument.Digest,
                ["shortDigest"] = lockDocument.ShortDigest,
                ["process"] = lockDocument.Process.Name,
                ["version"] = lockDocument.Process.Version,
                ["sourceSha"] = lockDocument.Source.Sha,
                ["artifactsVerified"] = artifactsDirectory is not null,
                ["findings"] = new JsonArray(findings.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            };
            output.WriteLine(document.ToJsonString());
        }
        else
        {
            output.WriteLine($"lock    : {lockDocument.Process.Name} {lockDocument.Process.Version} at {lockDocument.Source.Sha}");
            output.WriteLine($"digest  : {lockDocument.Digest}");
            foreach (var finding in findings)
            {
                error.WriteLine($"error   : {finding}");
            }

            if (artifactsDirectory is not null && findings.Count == 0)
            {
                output.WriteLine($"artifacts: {lockDocument.Artifacts.Count} match the lock");
            }
        }

        return findings.Count == 0 ? 0 : 1;
    }

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new GpCliUsageException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }
}
