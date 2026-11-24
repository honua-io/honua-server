// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

// The parsed input/path/stream preamble shared by the file-writing sinks.
internal sealed record FileSinkInputs
{
    public required StepInputReader Inputs { get; init; }

    public required string RequestedPath { get; init; }

    public required string OutputRoot { get; init; }

    public required string ResolvedPath { get; init; }

    public required IAsyncEnumerable<IFeature> Source { get; init; }
}

// Either the parsed sink inputs or the deterministic rejection that replaces them.
internal sealed record FileSinkRead(FileSinkInputs? Inputs, JobExecutionResult? Rejection);

internal sealed class FileSinkInputReader(string processId, GeoprocessingExecutorOptions options)
{
    public FileSinkRead Read(ExecutionJobRecord job, IJobExecutionContext context)
    {
        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGetRequired("input", out var inputUri, out var inputError))
        {
            return Reject(inputError);
        }

        if (!inputs.TryGetRequired("path", out var path, out var pathError))
        {
            return Reject(pathError);
        }

        var outputRoot = options.OutputRootDirectory;
        if (!SinkPathResolver.TryResolveOutputPath(outputRoot, path, job, out var resolvedPath, out var pathResolutionError))
        {
            return Reject(pathResolutionError ?? "path is invalid");
        }

        // Stream the input so a large collection writes incrementally; spilled NDJSON
        // streams are unbounded. The output root rejects path traversal in the reference.
        if (!FeatureStreamArtifact.TryOpenRead(
                inputUri, out var parseError, out var source, options.MaxArtifactBytes, outputRoot, context))
        {
            return Reject($"'input' {parseError}");
        }

        return new FileSinkRead(new FileSinkInputs
        {
            Inputs = inputs,
            RequestedPath = path,
            OutputRoot = outputRoot,
            ResolvedPath = resolvedPath,
            Source = source
        }, null);
    }

    public static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Best-effort cleanup; never mask the original failure/cancellation.
        }
    }

    private FileSinkRead Reject(string error)
        => new(null, JobExecutionResult.InvalidInput($"Invalid {processId} inputs: {error}"));
}
