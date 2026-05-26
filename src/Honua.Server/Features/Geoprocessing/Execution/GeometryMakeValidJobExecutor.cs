// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.make-valid</c>
/// process. Closes a catalog-honesty gap on trunk: the process was advertised
/// in <see cref="BuiltInProcessCatalog"/> (and flagged synchronous by
/// <c>GPServerExecutionPolicy</c> / first-slice automated by
/// <c>ProcessMigrationEvidenceClassifier</c>) but had no job executor, so a
/// workflow/codemod submitting it as a job could never reach it.
///
/// Repairs an invalid input geometry — fixing self-intersections, duplicate
/// vertices, and ring orientation — using the managed NetTopologySuite
/// <see cref="GeometryFixer"/>, the GEOS-free analogue of PostGIS
/// <c>ST_MakeValid</c>. WKB in, a single GeoJSON Feature out on the canonical
/// <c>data:application/geo+json;base64,</c> envelope, matching the rest of the
/// deterministic single-geometry <c>geometry.*</c> family.
/// </summary>
internal sealed partial class GeometryMakeValidJobExecutor : IJobExecutor
{
    /// <summary>
    /// The single process id this executor handles. Matches the catalog
    /// entry in <see cref="BuiltInProcessCatalog"/>.
    /// </summary>
    internal const string HandledProcessId = "geometry.make-valid";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryMakeValidJobExecutor> _logger;

    public GeometryMakeValidJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryMakeValidJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.make-valid executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing make-valid inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid make-valid inputs: {inputError}");
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
            return JobExecutionResult.Failed("Invalid make-valid inputs: WKB payload could not be decoded.");
        }

        if (geometry == null || geometry.IsEmpty)
        {
            return JobExecutionResult.Failed("Invalid make-valid inputs: geometry is empty.");
        }

        geometry.SRID = inputs.Srid;
        var wasValid = geometry.IsValid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(50, "Repairing geometry", cancellationToken).ConfigureAwait(false);

        Geometry repaired;
        try
        {
            // GeometryFixer is the managed NTS equivalent of ST_MakeValid: it
            // returns the input unchanged when already valid and otherwise
            // repairs self-intersections / ring orientation without a native
            // GEOS dependency. KeepCollapsed=false matches ST_MakeValid's
            // default of dropping degenerate (collapsed) components.
            repaired = GeometryFixer.Fix(geometry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.MakeValidComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Make-valid computation failed: {ex.GetType().Name}.");
        }

        if (repaired == null || repaired.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Make-valid computation produced an empty geometry; the input may have fully collapsed.");
        }

        repaired.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding make-valid artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            repaired,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
                ("inputWasValid", (object)wasValid),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Make-valid artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Make-valid completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out MakeValidInputs inputs,
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

        inputs = new MakeValidInputs(wkbBytes, srid);
        error = "";
        return true;
    }

    private readonly record struct MakeValidInputs(byte[] WkbBytes, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9210, LogLevel.Warning,
            "Geometry make-valid executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9211, LogLevel.Warning,
            "Geometry make-valid executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9212, LogLevel.Warning,
            "Geometry make-valid executor rejected job {OperationId}: WKB decode failed: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, string reason);

        [LoggerMessage(9213, LogLevel.Error,
            "Geometry make-valid executor failed job {OperationId} during repair computation")]
        public static partial void MakeValidComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9214, LogLevel.Warning,
            "Geometry make-valid executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
