// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryAreaJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.area";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryAreaJobExecutor> _logger;

    public GeometryAreaJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryAreaJobExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GeoprocessingDispatchHelper.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(_logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.area executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing area inputs", cancellationToken).ConfigureAwait(false);

        var inputRead = GeometryExecutorSupport.ReadAreaGeometryInputs(parameters);
        if (!inputRead.IsValid)
        {
            Log.InvalidInputs(_logger, job.OperationId, inputRead.Error!);
            return JobExecutionResult.InvalidInput($"Invalid area inputs: {inputRead.Error}");
        }

        var inputs = inputRead.Inputs;
        var decodeResult = GeometryExecutorSupport.DecodeWkbAndValidateSrid(inputs.WkbBytes, inputs.Srid);
        if (!decodeResult.IsValid)
        {
            return RejectDecodeFailure(job.OperationId, decodeResult);
        }

        var geometry = decodeResult.Geometry!;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing area", cancellationToken).ConfigureAwait(false);

        double area;
        try
        {
            if (inputs.IsGeodesic && GeodesicGeometryOperations.IsGeographicSrid(inputs.Srid))
            {
                area = GeodesicGeometryOperations.ComputeGeodesicAreaSquareMeters(geometry) / inputs.MetersPerUnit;
            }
            else if (inputs.IsGeodesic)
            {
                area = geometry.Area / inputs.MetersPerUnit;
            }
            else
            {
                area = geometry.Area;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AreaComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Area computation failed: {ex.GetType().Name}.");
        }

        if (double.IsNaN(area) || double.IsInfinity(area))
        {
            return JobExecutionResult.Failed(
                "Area computation produced a non-finite value; verify the input geometry coordinates.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding area artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryExecutorSupport.BuildMeasurePayload(new MeasurePayloadRequest
        {
            ProcessId = HandledProcessId,
            Measure = "area",
            Value = area,
            Unit = inputs.IsGeodesic ? inputs.Unit : "input-crs-units-squared",
            InputSrid = inputs.Srid,
            InputGeometryType = geometry.GeometryType,
        });
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Area artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryExecutorSupport.BuildJsonDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Area completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private JobExecutionResult RejectDecodeFailure(string operationId, ValidatedGeometryDecodeResult decodeResult)
    {
        if (decodeResult.FailureKind == WkbDecodeFailureKind.InvalidWkb)
        {
            Log.InvalidWkb(_logger, operationId, decodeResult.Error!);
            return JobExecutionResult.InvalidInput("Invalid area inputs: WKB payload could not be decoded.");
        }

        if (decodeResult.FailureKind == WkbDecodeFailureKind.EmptyGeometry)
        {
            return JobExecutionResult.InvalidInput("Invalid area inputs: geometry is empty.");
        }

        Log.InvalidInputs(_logger, operationId, decodeResult.Error!);
        return JobExecutionResult.InvalidInput($"Invalid area inputs: {decodeResult.Error}.");
    }

    private static partial class Log
    {
        [LoggerMessage(9130, LogLevel.Warning,
            "Geometry area executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9131, LogLevel.Warning,
            "Geometry area executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9132, LogLevel.Warning,
            "Geometry area executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9133, LogLevel.Error,
            "Geometry area executor failed job {OperationId} during area computation")]
        public static partial void AreaComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9134, LogLevel.Warning,
            "Geometry area executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
