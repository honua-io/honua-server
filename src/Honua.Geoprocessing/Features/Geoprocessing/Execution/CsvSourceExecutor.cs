// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// <c>source.csv</c> executor. Parses an inline CSV document into a FeatureCollection,
/// deriving geometry from either a WKT column or a longitude/latitude coordinate pair,
/// and publishes the standard FeatureCollection artifact. Pure managed
/// (System / NetTopologySuite WKT) — no native dependency. Reconciled from the GeoETL
/// baseline CsvSourceConnector onto the #1185 process/executor contract; the column
/// auto-detection mirrors CsvFormatReader's name conventions.
/// </summary>
/// <remarks>
/// The baseline connector streamed from a file <c>path</c> via the internal
/// CsvFormatReader (with delimiter sampling and RFC-4180 quoting). On the #1185 contract
/// inputs are durable spec strings, so this executor accepts an <c>inline</c> CSV body
/// with a single-character <c>delimiter</c> override (default comma). Heavyweight
/// delimiter auto-detection and file-path streaming belong to a later streaming-source
/// stream.
/// </remarks>
internal sealed class CsvSourceExecutor(IOptionsMonitor<GeoprocessingExecutorOptions> options) : IJobExecutor
{
    internal const string HandledProcessId = "source.csv";

    private static readonly FrozenSet<string> WktColumnNames =
        new[] { "wkt", "geomwkt", "geometrywkt", "thegeom", "geom", "geometry", "shape" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> LongitudeColumnNames =
        new[] { "lon", "lng", "long", "longitude", "x" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> LatitudeColumnNames =
        new[] { "lat", "latitude", "y" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMonitor<GeoprocessingExecutorOptions> _options = options;

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var resolved = GeoprocessingDispatchHelper.ResolveProcessId(job.Spec.Parameters);
        if (!string.Equals(resolved, HandledProcessId, StringComparison.Ordinal))
        {
            return JobExecutionResult.Failed(
                $"Process id '{resolved ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(10, "Parsing CSV source", cancellationToken).ConfigureAwait(false);

        var inputs = new StepInputReader(job.Spec.Parameters);
        if (!inputs.TryGet("inline", out var inline) || string.IsNullOrWhiteSpace(inline))
        {
            return JobExecutionResult.Failed(
                $"Invalid {HandledProcessId} inputs: requires an 'inline' CSV document.");
        }

        var delimiter = ',';
        if (inputs.TryGet("delimiter", out var rawDelimiter) && rawDelimiter!.Length == 1)
        {
            delimiter = rawDelimiter[0];
        }

        List<IFeature> features;
        try
        {
            features = ParseCsv(inline!, delimiter, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return JobExecutionResult.Failed($"{HandledProcessId} parse failed: {ex.GetType().Name}.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(70, "Encoding source artifact", cancellationToken).ConfigureAwait(false);

        var payload = FeatureCollectionArtifact.WriteFeatureCollection(features, HandledProcessId);

        var maxBytes = _options.CurrentValue.MaxArtifactBytes;
        if (payload.Length > maxBytes)
        {
            return JobExecutionResult.Failed(
                $"{HandledProcessId} artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={maxBytes}.");
        }

        var artifactUri = FeatureCollectionArtifact.BuildDataUri(payload);

        cancellationToken.ThrowIfCancellationRequested();
        await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
        await context.ReportProgressAsync(100, $"{HandledProcessId} completed", cancellationToken).ConfigureAwait(false);

        return JobExecutionResult.Succeeded();
    }

    private static List<IFeature> ParseCsv(string body, char delimiter, CancellationToken cancellationToken)
    {
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var wktReader = new WKTReader();

        using var reader = new StringReader(body);
        string[]? headers = null;
        int? wktIndex = null;
        int? lonIndex = null;
        int? latIndex = null;

        var features = new List<IFeature>();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseRecord(line, delimiter);
            if (fields.Count == 0)
            {
                continue;
            }

            if (headers is null)
            {
                headers = fields.Select(f => f.Trim()).ToArray();
                for (var i = 0; i < headers.Length; i++)
                {
                    if (wktIndex is null && WktColumnNames.Contains(headers[i]))
                    {
                        wktIndex = i;
                    }
                    else if (lonIndex is null && LongitudeColumnNames.Contains(headers[i]))
                    {
                        lonIndex = i;
                    }
                    else if (latIndex is null && LatitudeColumnNames.Contains(headers[i]))
                    {
                        latIndex = i;
                    }
                }

                continue;
            }

            var attributes = new AttributesTable();
            for (var i = 0; i < headers.Length; i++)
            {
                if (i == wktIndex || i == lonIndex || i == latIndex)
                {
                    continue;
                }

                var value = i < fields.Count ? fields[i] : null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    attributes.Add(headers[i], value);
                }
            }

            var geometry = ParseGeometry(fields, wktIndex, lonIndex, latIndex, factory, wktReader);
            if (geometry is null && attributes.Count == 0)
            {
                continue;
            }

            features.Add(new Feature(geometry, attributes));
        }

        return features;
    }

    private static NtsGeometry? ParseGeometry(
        List<string> fields,
        int? wktIndex,
        int? lonIndex,
        int? latIndex,
        GeometryFactory factory,
        WKTReader wktReader)
    {
        if (wktIndex.HasValue && wktIndex.Value < fields.Count)
        {
            var wkt = fields[wktIndex.Value];
            if (!string.IsNullOrWhiteSpace(wkt))
            {
                try
                {
                    return wktReader.Read(wkt.Trim());
                }
                catch (Exception ex) when (ex is ParseException or ArgumentException)
                {
                    // Ignore malformed WKT; fall through to lon/lat.
                }
            }
        }

        if (lonIndex.HasValue && latIndex.HasValue
            && lonIndex.Value < fields.Count && latIndex.Value < fields.Count
            && double.TryParse(fields[lonIndex.Value], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && double.TryParse(fields[latIndex.Value], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
        {
            return factory.CreatePoint(new Coordinate(lon, lat));
        }

        return null;
    }

    // Minimal RFC-4180-style record parse: handles quoted fields and embedded
    // delimiters/quotes. Single-line records only (the #1185 inline contract does not
    // carry multi-line quoted values; that is a streaming-source concern).
    private static List<string> ParseRecord(string record, char delimiter)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < record.Length && record[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
