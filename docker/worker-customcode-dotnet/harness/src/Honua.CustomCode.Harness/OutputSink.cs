// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.CustomCode.Sdk;

namespace Honua.CustomCode.Harness;

/// <summary>Raised when staged artifacts exceed the job's output-size cap.</summary>
public sealed class OutputSizeExceededException(string message) : Exception(message);

/// <summary>
/// Collects artifacts a tool wants persisted to the job's output prefix and enforces
/// the per-job output-size cap eagerly. The harness uploads after the tool returns.
/// Mirrors the Python harness's <c>OutputSink</c>.
/// </summary>
public sealed class OutputSink : IOutputSink
{
    private readonly long _maxTotalBytes;
    private readonly List<Artifact> _artifacts = [];
    private long _totalBytes;

    /// <summary>Creates a sink with a positive total-size cap.</summary>
    /// <param name="maxTotalBytes">The maximum total bytes across all artifacts.</param>
    public OutputSink(long maxTotalBytes)
    {
        if (maxTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "maxTotalBytes must be positive.");
        }

        _maxTotalBytes = maxTotalBytes;
    }

    /// <summary>The registered artifacts, in registration order.</summary>
    public IReadOnlyList<Artifact> Artifacts => _artifacts;

    /// <summary>The total staged bytes so far.</summary>
    public long TotalBytes => _totalBytes;

    /// <inheritdoc />
    public Artifact AddArtifact(string name, string path)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('/') || name.Contains('\\') || name is "." or "..")
        {
            throw new ArgumentException($"Artifact name '{name}' must be a simple file name.", nameof(name));
        }

        var resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Artifact '{name}' path does not exist: {resolved}");
        }

        var size = new FileInfo(resolved).Length;
        var projected = _totalBytes + size;
        if (projected > _maxTotalBytes)
        {
            throw new OutputSizeExceededException(
                $"Adding artifact '{name}' ({size} bytes) would exceed the output cap of " +
                $"{_maxTotalBytes} bytes (already staged {_totalBytes} bytes).");
        }

        var artifact = new Artifact(name, resolved, size);
        _artifacts.Add(artifact);
        _totalBytes = projected;
        return artifact;
    }
}
