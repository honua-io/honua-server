// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.CustomCode.Sdk;

/// <summary>A named output file the tool wants uploaded to the job's output prefix.</summary>
/// <param name="Name">A simple file name (no path separators).</param>
/// <param name="Path">The absolute local path of the file on disk.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
public sealed record Artifact(string Name, string Path, long SizeBytes);

/// <summary>
/// Collects artifacts a tool wants persisted to the job's S3 output prefix. The
/// harness performs the actual upload after the tool returns; this sink only records
/// intent and enforces the per-job output-size cap eagerly so a runaway tool fails
/// fast. Mirrors the Python harness's <c>OutputSink</c>.
/// </summary>
public interface IOutputSink
{
    /// <summary>
    /// Register a file on disk for upload under <paramref name="name"/>. Throws if the
    /// file is missing, the name is not a simple file name, or adding it would exceed
    /// the configured output-size cap.
    /// </summary>
    /// <param name="name">A simple file name (no <c>/</c>, <c>\\</c>, <c>.</c>, or <c>..</c>).</param>
    /// <param name="path">The path of the file on disk.</param>
    /// <returns>The registered artifact.</returns>
    Artifact AddArtifact(string name, string path);
}
