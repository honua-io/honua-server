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

namespace Honua.Geoprocessing.Execution;

/// <summary>Converts WKB through the shared managed process runtime into a typed scalar document.</summary>
internal sealed class GeometryFormatJobExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IProcessExecutor
{
    internal const string HandledProcessId = "conversion.geometry-format";
    public IReadOnlySet<string> ProcessIds { get; } = new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };
    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(ExecutionJobRecord job, IJobExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = job.Spec.Parameters;
        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (GeoprocessingDispatchHelper.ResolveProcessId(parameters) != HandledProcessId
            || !parameters.TryGetValue(prefix + "geometry", out var encoded)
            || !parameters.TryGetValue(prefix + "target", out var target))
        {
            return JobExecutionResult.Failed("Geometry format conversion requires geometry and target inputs.");
        }
        target = target.Trim().ToLowerInvariant();
        if (target is not ("wkt" or "geojson" or "wkb" or "ewkt"))
        {
            return JobExecutionResult.Failed("Invalid target: expected wkt, geojson, wkb or ewkt.");
        }
        var limit = options.CurrentValue.MaxArtifactBytes;
        if (encoded.Length > ((limit + 2) / 3) * 4)
        {
            return JobExecutionResult.Failed("Geometry input exceeds MaxArtifactBytes.");
        }

        byte[] input;
        Geometry geometry;
        try
        {
            input = Convert.FromBase64String(encoded);
            geometry = new WKBReader { HandleSRID = true }.Read(input);
        }
        catch (Exception exception) when (exception is FormatException or ParseException or ArgumentException or IndexOutOfRangeException)
        {
            return JobExecutionResult.Failed("Geometry input must be valid base64-encoded WKB.");
        }
        if (target == "geojson" && geometry.Coordinates.Any(coordinate => double.IsFinite(coordinate.M)))
        {
            return JobExecutionResult.Failed("GeoJSON cannot represent measured (M) ordinates; use WKB, WKT or EWKT.");
        }

        var contentType = target switch
        {
            "geojson" => "application/geo+json",
            "wkb" => "application/wkb",
            _ => "text/plain"
        };
        using var payload = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("processId", HandledProcessId);
            writer.WriteString("format", target);
            writer.WriteString("contentType", contentType);
            writer.WriteNumber("srid", geometry.SRID);
            if (target == "geojson")
            {
                writer.WritePropertyName("value");
                writer.WriteRawValue(GeoJsonArtifactCodec.CreateWriter().Write(geometry));
            }
            else if (target == "wkb")
            {
                // Identity encoding preserves byte order, optional SRID and every
                // ordinate exactly; the input was independently decoded above.
                writer.WriteBase64String("value", input);
            }
            else
            {
                var wkt = new WKTWriter(4) { OutputOrdinates = Ordinates.XYZM }.Write(geometry);
                writer.WriteString("value", target == "ewkt"
                    ? "SRID=" + geometry.SRID.ToString(CultureInfo.InvariantCulture) + ";" + wkt : wkt);
            }
            writer.WriteEndObject();
        }
        if (payload.Length > limit)
        {
            return JobExecutionResult.Failed("Converted geometry exceeds MaxArtifactBytes.");
        }
        await context.PublishArtifactAsync("data:application/json;base64," + Convert.ToBase64String(payload.ToArray()), cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, "Geometry format conversion completed", cancellationToken).ConfigureAwait(false);
        return JobExecutionResult.Succeeded();
    }
}
