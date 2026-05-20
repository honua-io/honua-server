// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Simplify;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.simplify</c>
/// process. Slice 5 of #1031 — completes the per-feature vector "shape
/// transform" set alongside <see cref="GeometryDissolveJobExecutor"/> and
/// <see cref="GeometrySnapJobExecutor"/> by exposing the deterministic
/// Douglas-Peucker simplification primitive.
///
/// Reduces vertex count by collapsing points within the supplied tolerance
/// in the input CRS units. By default the executor uses NetTopologySuite's
/// <see cref="TopologyPreservingSimplifier"/> — the same algorithm
/// surfaced by PostGIS <c>ST_SimplifyPreserveTopology</c> — which avoids
/// introducing self-intersections on collapsed rings. Callers may opt in
/// to the faster, non-topology-preserving <see cref="DouglasPeuckerSimplifier"/>
/// by setting <c>preserveTopology=false</c>; that path matches plain
/// <c>ST_Simplify</c> and can produce invalid geometries on near-tolerance
/// vertices, which is why preservation is the default.
/// </summary>
internal sealed partial class GeometrySimplifyJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.simplify";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometrySimplifyJobExecutor> _logger;

    public GeometrySimplifyJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometrySimplifyJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.simplify executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing simplify inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid simplify inputs: {inputError}");
        }

        Geometry geometry;
        try
        {
            var reader = new WKBReader { HandleSRID = true };
            geometry = reader.Read(inputs.WkbBytes);
        }
        catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
        {
            Log.InvalidWkb(_logger, job.OperationId, ex.Message);
            return JobExecutionResult.Failed("Invalid simplify inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid simplify inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Simplifying geometry", cancellationToken).ConfigureAwait(false);

        Geometry simplified;
        try
        {
            // Topology-preserving simplification is the safer default because it
            // refuses to collapse rings that would self-intersect at the chosen
            // tolerance — matching ST_SimplifyPreserveTopology. The faster
            // Douglas-Peucker path is exposed via preserveTopology=false for
            // callers who already validate their inputs and want the cheaper
            // walk.
            simplified = inputs.PreserveTopology
                ? TopologyPreservingSimplifier.Simplify(geometry, inputs.Tolerance)
                : DouglasPeuckerSimplifier.Simplify(geometry, inputs.Tolerance);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SimplifyComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Simplify computation failed: {ex.GetType().Name}.");
        }

        if (simplified == null || simplified.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Simplify computation produced an empty geometry; reduce the tolerance or verify the input geometry.");
        }

        simplified.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding simplify artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            simplified,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
                ("tolerance", (object)inputs.Tolerance),
                ("preserveTopology", (object)inputs.PreserveTopology),
                ("inputGeometryType", (object)geometry.GeometryType),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Simplify artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Increase the simplification tolerance or pre-clip the input geometry.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Simplify completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out SimplifyInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkb", out var wkb) || string.IsNullOrWhiteSpace(wkb))
        {
            error = "missing required input 'wkb'";
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

        // preserveTopology defaults to true to match ST_SimplifyPreserveTopology
        // and the catalog default.
        var preserveTopology = true;
        if (parameters.TryGetValue(prefix + "preserveTopology", out var preserveRaw)
            && !string.IsNullOrWhiteSpace(preserveRaw)
            && !bool.TryParse(preserveRaw, out preserveTopology))
        {
            error = "invalid input 'preserveTopology'; expected boolean";
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

        inputs = new SimplifyInputs(wkbBytes, srid, tolerance, preserveTopology);
        error = "";
        return true;
    }

    private readonly record struct SimplifyInputs(byte[] WkbBytes, int Srid, double Tolerance, bool PreserveTopology);

    private static partial class Log
    {
        [LoggerMessage(9180, LogLevel.Warning,
            "Geometry simplify executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9181, LogLevel.Warning,
            "Geometry simplify executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9182, LogLevel.Warning,
            "Geometry simplify executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9183, LogLevel.Error,
            "Geometry simplify executor failed job {OperationId} during simplify computation")]
        public static partial void SimplifyComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9184, LogLevel.Warning,
            "Geometry simplify executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
