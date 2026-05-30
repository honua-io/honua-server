// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Production <see cref="IJobExecutor"/> for the <c>geometry.union</c>
/// process. Slice 3 of #1031 — extends the buffer / clip / intersect /
/// project / area set with a collection-aggregation operation.
///
/// The catalog declares <c>wkbs</c> as <c>ProcessParameterValueType.WkbArray</c>:
/// a JSON array of base64-encoded WKB geometries. The OGC API Processes
/// canonical-input encoder persists that array as the raw JSON-array string
/// onto <see cref="ExecutionJobSpec.Parameters"/>; this executor parses the
/// array, decodes each element, and produces a single merged
/// <see cref="Geometry"/> using <see cref="UnaryUnionOp"/> — a robust
/// fan-in union that handles polygon, line, and point inputs uniformly.
/// All inputs must share the supplied SRID; reprojection is handled by
/// <see cref="GeometryProjectJobExecutor"/>.
/// </summary>
internal sealed partial class GeometryUnionJobExecutor : IJobExecutor
{
    internal const string HandledProcessId = "geometry.union";

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options;
    private readonly ILogger<GeometryUnionJobExecutor> _logger;

    public GeometryUnionJobExecutor(
        IOptionsMonitor<GeoprocessingExecutorOptions> options,
        ILogger<GeometryUnionJobExecutor> logger)
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
                $"Process id '{processId ?? "<none>"}' is not handled by the geometry.union executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing union inputs", cancellationToken).ConfigureAwait(false);

        if (!TryReadInputs(parameters, out var inputs, out var inputError))
        {
            Log.InvalidInputs(_logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid union inputs: {inputError}");
        }

        var geometries = new List<Geometry>(inputs.WkbItems.Count);
        var reader = new WKBReader { HandleSRID = true };
        for (var i = 0; i < inputs.WkbItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(inputs.WkbItems[i]);
            }
            catch (FormatException)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "is not valid base64");
                return JobExecutionResult.Failed(
                    $"Invalid union inputs: wkbs[{i}] is not valid base64.");
            }

            if (bytes.Length == 0)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "decoded to zero bytes");
                return JobExecutionResult.Failed(
                    $"Invalid union inputs: wkbs[{i}] decoded to zero bytes.");
            }

            Geometry geometry;
            try
            {
                geometry = reader.Read(bytes);
            }
            catch (Exception ex) when (ex is ParseException or ArgumentException or IndexOutOfRangeException)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, $"could not be decoded ({ex.GetType().Name})");
                return JobExecutionResult.Failed(
                    $"Invalid union inputs: wkbs[{i}] could not be decoded.");
            }

            if (geometry == null || geometry.IsEmpty)
            {
                Log.InvalidWkb(_logger, job.OperationId, i, "decoded to an empty geometry");
                return JobExecutionResult.Failed(
                    $"Invalid union inputs: wkbs[{i}] decoded to an empty geometry.");
            }

            geometry.SRID = inputs.Srid;
            geometries.Add(geometry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(40, "Computing union geometry", cancellationToken).ConfigureAwait(false);

        Geometry unioned;
        try
        {
            // UnaryUnionOp is the canonical NTS aggregation primitive: it handles
            // mixed geometry types, deduplicates overlapping rings, and avoids
            // the O(N) pairwise Union calls that can amplify floating-point
            // error across larger collections.
            unioned = UnaryUnionOp.Union(geometries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.UnionComputationFailed(_logger, job.OperationId, ex);
            return JobExecutionResult.Failed($"Union computation failed: {ex.GetType().Name}.");
        }

        if (unioned == null || unioned.IsEmpty)
        {
            return JobExecutionResult.Failed(
                "Union computation produced an empty geometry; verify the input collection.");
        }

        unioned.SRID = inputs.Srid;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(75, "Encoding union artifact", cancellationToken).ConfigureAwait(false);

        var payload = GeometryFeatureWriter.WriteFeature(
            unioned,
            HandledProcessId,
            new[]
            {
                ("inputSrid", (object)inputs.Srid),
                ("inputCount", (object)inputs.WkbItems.Count),
            });

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            Log.ArtifactTooLarge(_logger, job.OperationId, payload.Length, maxBytes);
            return JobExecutionResult.Failed(
                $"Union artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}. " +
                "Reduce the input collection size or simplify the inputs before unioning.");
        }

        var artifactUri = GeometryFeatureWriter.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Union completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static bool TryReadInputs(
        IReadOnlyDictionary<string, string> parameters,
        out UnionInputs inputs,
        out string error)
    {
        inputs = default;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";

        if (!parameters.TryGetValue(prefix + "wkbs", out var wkbsRaw) || string.IsNullOrWhiteSpace(wkbsRaw))
        {
            error = "missing required input 'wkbs'";
            return false;
        }

        if (!parameters.TryGetValue(prefix + "srid", out var sridRaw)
            || !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid)
            || srid <= 0)
        {
            error = "missing or invalid input 'srid'; expected a positive integer";
            return false;
        }

        // wkbs is a JSON array of base64-encoded WKB strings; see
        // ProcessPlanValidator.TryDecodeWkbArray for the canonical encoding
        // contract.
        List<string> items;
        try
        {
            using var doc = JsonDocument.Parse(wkbsRaw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "input 'wkbs' must be a JSON array of base64-encoded WKB strings";
                return false;
            }

            items = new List<string>(doc.RootElement.GetArrayLength());
            var index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    error = $"input 'wkbs' element at index {index} is not a string";
                    return false;
                }

                var value = element.GetString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = $"input 'wkbs' element at index {index} is empty";
                    return false;
                }

                items.Add(value);
                index++;
            }
        }
        catch (JsonException)
        {
            error = "input 'wkbs' is not valid JSON";
            return false;
        }

        if (items.Count == 0)
        {
            error = "input 'wkbs' must contain at least one geometry";
            return false;
        }

        inputs = new UnionInputs(items, srid);
        error = "";
        return true;
    }

    private readonly record struct UnionInputs(IReadOnlyList<string> WkbItems, int Srid);

    private static partial class Log
    {
        [LoggerMessage(9140, LogLevel.Warning,
            "Geometry union executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9141, LogLevel.Warning,
            "Geometry union executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9142, LogLevel.Warning,
            "Geometry union executor rejected job {OperationId}: WKB decode failed at index {Index}: {Reason}")]
        public static partial void InvalidWkb(ILogger logger, string operationId, int index, string reason);

        [LoggerMessage(9143, LogLevel.Error,
            "Geometry union executor failed job {OperationId} during union computation")]
        public static partial void UnionComputationFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(9144, LogLevel.Warning,
            "Geometry union executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);
    }
}
