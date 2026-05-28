// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Overlay.Snap;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.snap</c>
/// process. Slice 5 of #1031 — completes the vertex-conditioning trio
/// alongside <see cref="GeometrySimplifyJobExecutor"/> and
/// <see cref="GeometryDissolveJobExecutor"/>.
///
/// Snaps the vertices of the input geometry to those of a reference
/// geometry whenever the distance between two candidates falls within the
/// supplied tolerance, using NetTopologySuite's
/// <see cref="GeometrySnapper.SnapTo(Geometry, double)"/>. The classic use
/// case is reconciling adjacent dataset boundaries (e.g., aligning a
/// freshly-digitized parcel with the authoritative cadaster) before
/// running overlay operations that would otherwise produce sliver
/// polygons. The reference geometry is consumed for vertex
/// reference only; only the input geometry's coordinates are mutated.
/// </summary>
internal sealed partial class GeometrySnapJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.snap";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometrySnapJobExecutor> _logger;

    public GeometrySnapJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometrySnapJobExecutor> logger)
    {
        _options = options;
        _logger = logger;
    }

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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.snap executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing snap inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid snap inputs: {inputError}");
        }

        var reader = new WKBReader { HandleSRID = true };

        Geometry input;
        try
        {
            input = reader.Read(inputs.WkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, "wkb", ex.Message);
            return JobExecutionResult.Failed("Invalid snap inputs: WKB payload could not be decoded.");
        }

        if (input == null || input.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid snap inputs: input geometry is empty.");
        }

        Geometry reference;
        try
        {
            reference = reader.Read(inputs.ReferenceWkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, "referenceWkb", ex.Message);
            return JobExecutionResult.Failed("Invalid snap inputs: reference WKB payload could not be decoded.");
        }

        if (reference == null || reference.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid snap inputs: reference geometry is empty.");
        }

        input.SRID = inputs.Srid;
        reference.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Snapping geometry to reference", cancellationToken).ConfigureAwait(false);

        Geometry snapped;
        try
        {
            // GeometrySnapper.SnapTo walks every vertex of the input and
            // pulls coordinates within tolerance onto the matching vertex
            // of the reference geometry. It returns a new Geometry; the
            // input is not mutated in place.
            var snapper = new GeometrySnapper(input);
            snapped = snapper.SnapTo(reference, inputs.Tolerance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SnapComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Snap computation failed: {ex.GetType().Name}.");
        }

        if (snapped == null || snapped.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Snap computation produced an empty geometry; reduce the tolerance or verify the inputs.");
        }

        snapped.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding snap artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            snapped,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
                ("tolerance", (object)inputs.Tolerance),
                ("inputGeometryType", (object)input.GeometryType),
                ("referenceGeometryType", (object)reference.GeometryType),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Snap artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Snap completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out SnapInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "referenceWkb", out var referenceWkb)
            || string.IsNullOrWhiteSpace(referenceWkb))
        {
            error = "missing required input 'referenceWkb'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "tolerance", out var toleranceRaw)
            || !double.TryParse(toleranceRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var tolerance)
            || double.IsNaN(tolerance) || double.IsInfinity(tolerance) || tolerance < 0.0)
        {
            error = "missing or invalid input 'tolerance'; expected a finite non-negative number";
            return false;
        }

        byte[] wkbBytes;
        try
        {
            wkbBytes = Convert.FromBase64String(wkb);
        }
        catch (FormatException)
        {
            error = "input 'wkb' is not valid base64";
            return false;
        }

        if (wkbBytes.Length == 0)
        {
            error = "input 'wkb' decoded to zero bytes";
            return false;
        }

        byte[] referenceWkbBytes;
        try
        {
            referenceWkbBytes = Convert.FromBase64String(referenceWkb);
        }
        catch (FormatException)
        {
            error = "input 'referenceWkb' is not valid base64";
            return false;
        }

        if (referenceWkbBytes.Length == 0)
        {
            error = "input 'referenceWkb' decoded to zero bytes";
            return false;
        }

        inputs = new SnapInputs(wkbBytes, referenceWkbBytes, srid, tolerance);
        error = "";
        return true;
    }

    private readonly record struct SnapInputs(
        byte[] WkbBytes,
        byte[] ReferenceWkbBytes,
        int Srid,
        double Tolerance);

    private static partial class Log
    {
        [LoggerMessage(9190, LogLevel.Warning,
            "Geometry snap executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9191, LogLevel.Warning,
            "Geometry snap executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9192, LogLevel.Warning,
            "Geometry snap executor rejected job {OperationId}: WKB decode failed for {Field}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string field, string reason);

        [LoggerMessage(9193, LogLevel.Error,
            "Geometry snap executor failed job {OperationId} during snap computation")]
        public static partial void SnapComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9194, LogLevel.Warning,
            "Geometry snap executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
