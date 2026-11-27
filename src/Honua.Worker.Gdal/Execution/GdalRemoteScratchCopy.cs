// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;

namespace Honua.Worker.Gdal.Execution;

internal sealed class RemoteScratchCopyRequest
{
    public required IGdalCommandRunner Runner { get; init; }

    public required IJobExecutionContext Context { get; init; }

    public required string SourcePath { get; init; }

    public required string Workspace { get; init; }

    public long MaxBytes { get; init; }
}

internal static class GdalRemoteScratchCopy
{
    private const string StatScript = "gdal_vsi_stat.py";

    internal static async Task<(string? Path, GdalCommandResult? Result)> CopyIfWithinCapAsync(
        RemoteScratchCopyRequest request,
        CancellationToken cancellationToken)
    {
        var (objectSizeBytes, sizeFailure) = await ProbeSizeAsync(request, cancellationToken).ConfigureAwait(false);
        if (sizeFailure is not null || objectSizeBytes is null)
        {
            return FailedResult(sizeFailure ?? "remote object size could not be determined before scratch copy.");
        }

        if (objectSizeBytes.Value > request.MaxBytes)
        {
            var actual = objectSizeBytes.Value.ToString(CultureInfo.InvariantCulture);
            var limit = request.MaxBytes.ToString(CultureInfo.InvariantCulture);
            return FailedResult($"remote object size {actual} bytes exceeds configured MaxRemoteScratchCopyBytes={limit}.");
        }

        return await RunCopyAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(long? SizeBytes, string? Failure)> ProbeSizeAsync(
        RemoteScratchCopyRequest request,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Join(request.Workspace, StatScript);
        File.Copy(Path.Join(AppContext.BaseDirectory, "Scripts", StatScript), scriptPath, overwrite: true);
        var args = new List<string> { scriptPath, request.SourcePath };
        try
        {
            await GdalCommandLog.LogCommandAsync(request.Context, "python3", args, request.Workspace, cancellationToken).ConfigureAwait(false);
            var result = await request.Runner.RunAsync("python3", args, request.Workspace, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return (null, "remote object size could not be determined before scratch copy.");
            }

            var raw = result.StandardOutput.Trim();
            if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sizeBytes) || sizeBytes < 0)
            {
                return (null, "remote object size probe returned an invalid byte count before scratch copy.");
            }

            return (sizeBytes, null);
        }
        catch (OperationCanceledException)
        {
            return (null, "remote object size probe timed out before scratch copy.");
        }
    }

    private static async Task<(string? Path, GdalCommandResult? Result)> RunCopyAsync(
        RemoteScratchCopyRequest request,
        CancellationToken cancellationToken)
    {
        var destination = Path.Join(request.Workspace, "coverage-source.bin");
        var args = new List<string> { "copy", request.SourcePath, destination };
        try
        {
            await GdalCommandLog.LogCommandAsync(request.Context, "gdalmanage", args, request.Workspace, cancellationToken).ConfigureAwait(false);
            var result = await request.Runner.RunAsync("gdalmanage", args, request.Workspace, cancellationToken).ConfigureAwait(false);
            return result.Succeeded ? (destination, result) : (null, result);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
    }

    private static (string? Path, GdalCommandResult? Result) FailedResult(string message)
        => (null, new GdalCommandResult { ExitCode = 1, StandardError = message });
}
