// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Geoprocessing.Execution;

internal sealed partial class GeometryLengthJobExecutor : IProcessExecutor
{
    internal const string HandledProcessId = "geometry.length";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryLengthJobExecutor> _logger;

    public GeometryLengthJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryLengthJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.length executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing length inputs", cancellationToken).ConfigureAwait(false);

        var inputRead = GeometryExecutorSupport.ReadLengthGeometryInputs(parameters);
        if (!inputRead.IsValid)
        {
            Log.InvalidInputs(_logger, job.OperationId, inputRead.Error!);
            return JobExecutionResult.InvalidInput($"Invalid length inputs: {inputRead.Error}");
        }

        var inputs = inputRead.Inputs;
        var decodeResult = GeometryExecutorSupport.DecodeWkbAndValidateSrid(inputs.WkbBytes, inputs.Srid);
        if (!decodeResult.IsValid)
        {
            return RejectDecodeFailure(job.OperationId, decodeResult);
        }

        var geometry = decodeResult.Geometry!;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Computing length", cancellationToken).ConfigureAwait(false);

        double length;
        try
        {
            if (inputs.IsGeodesic && GeodesicGeometryOperations.IsGeographicSrid(inputs.Srid))
            {
                length = GeodesicGeometryOperations.ComputeGeodesicLengthMeters(geometry) / inputs.MetersPerUnit;
            }
            else if (inputs.IsGeodesic)
            {
                length = geometry.Length / inputs.MetersPerUnit;
            }
            else
            {
                length = geometry.Length;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LengthComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Length computation failed: {ex.GetType().Name}.");
        }

        if (double.IsNaN(length) || double.IsInfinity(length))
        {
            return JobExecutionResult.Failed(
                "Length computation produced a non-finite value; verify the input geometry coordinates.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding length artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryExecutorSupport.BuildMeasurePayload(new MeasurePayloadRequest
        {
            ProcessId = HandledProcessId,
            Measure = "length",
            Value = length,
            Unit = inputs.IsGeodesic ? inputs.Unit : "input-crs-units",
            InputSrid = inputs.Srid,
            InputGeometryType = geometry.GeometryType,
        });
        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Length artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryExecutorSupport.BuildJsonDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Length completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private JobExecutionResult RejectDecodeFailure(string operationId, ValidatedGeometryDecodeResult decodeResult)
    {
        if (decodeResult.FailureKind == WkbDecodeFailureKind.InvalidWkb)
        {
            Log.InvalidWkb(_logger, operationId, decodeResult.Error!);
            return JobExecutionResult.InvalidInput("Invalid length inputs: WKB payload could not be decoded.");
        }

        if (decodeResult.FailureKind == WkbDecodeFailureKind.EmptyGeometry)
        {
            return JobExecutionResult.InvalidInput("Invalid length inputs: geometry is empty.");
        }

        Log.InvalidInputs(_logger, operationId, decodeResult.Error!);
        return JobExecutionResult.InvalidInput($"Invalid length inputs: {decodeResult.Error}.");
    }

    private static partial class Log
    {
        [LoggerMessage(9160, LogLevel.Warning,
            "Geometry length executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9161, LogLevel.Warning,
            "Geometry length executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9162, LogLevel.Warning,
            "Geometry length executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9163, LogLevel.Error,
            "Geometry length executor failed job {OperationId} during length computation")]
        public static partial void LengthComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9164, LogLevel.Warning,
            "Geometry length executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
