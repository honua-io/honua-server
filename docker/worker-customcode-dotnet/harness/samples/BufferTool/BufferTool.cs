// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.CustomCode.Sdk;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.CustomCode.Samples;

/// <summary>
/// Sample custom-code GP tool: buffer an input WKT geometry and write GeoJSON. This is
/// the .NET mirror of the Python sample <c>buffer_tool.py</c>. A real tool would pull
/// features via <see cref="GpContext.Client"/> and/or read staged
/// <see cref="GpContext.Inputs"/>; here we accept a WKT geometry + a buffer distance in
/// <see cref="GpContext.Params"/> and emit a buffered polygon as a GeoJSON artifact.
/// </summary>
/// <remarks>
/// Entrypoint spec for this tool: <c>"BufferTool::Honua.CustomCode.Samples.BufferTool"</c>.
/// Expected <c>params_json</c>:
/// <c>{"wkt": "POINT (1 1)", "distance": 0.5, "out_name": "buffered.geojson"}</c>.
/// </remarks>
public sealed class BufferTool : IGeoprocessingTool
{
    /// <inheritdoc />
    public Task<GpResult> ExecuteAsync(GpContext context, CancellationToken cancellationToken)
    {
        if (!context.Params.TryGetProperty("wkt", out var wktElement) ||
            wktElement.ValueKind != JsonValueKind.String ||
            wktElement.GetString() is not { Length: > 0 } wkt)
        {
            return Task.FromResult(GpResult.Failed("params.wkt is required (a WKT geometry)."));
        }

        var distance = context.Params.TryGetProperty("distance", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : 0.0;
        var outName = context.Params.TryGetProperty("out_name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : "buffered.geojson";

        context.Log.Info($"buffering '{wkt}' by {distance}");
        context.Progress.Report(10.0, "parsing input");

        Geometry geometry;
        try
        {
            geometry = new WKTReader().Read(wkt);
        }
        catch (Exception ex)
        {
            return Task.FromResult(GpResult.Failed($"failed to parse WKT: {ex.Message}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        context.Progress.Report(50.0, "buffering");

        var buffered = geometry.Buffer(distance);

        var geometryJson = new GeoJsonWriter().Write(buffered);
        using var doc = JsonDocument.Parse(geometryJson);
        var feature = new
        {
            type = "Feature",
            geometry = doc.RootElement,
            properties = new { source_wkt = wkt, distance },
        };

        var outPath = Path.Combine(context.WorkDirectory, outName);
        File.WriteAllText(outPath, JsonSerializer.Serialize(feature));
        context.Progress.Report(90.0, "writing output");

        context.Output.AddArtifact(outName, outPath);
        context.Log.Info($"wrote {outName} ({new FileInfo(outPath).Length} bytes)");
        context.Progress.Report(100.0, "done");

        return Task.FromResult(GpResult.Succeeded($"buffered geometry written to {outName}"));
    }
}
