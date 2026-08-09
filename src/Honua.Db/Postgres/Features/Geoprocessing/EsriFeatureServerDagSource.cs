// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Postgres.Features.Migration;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// <c>source.esri-featureserver</c> DAG connector. Streams features from an ArcGIS
/// GeoServices FeatureServer/MapServer layer by REUSING the migration
/// <see cref="ArcGisRestClient"/> — the same paginated reader the one-shot ArcGIS
/// import drives (<c>resultOffset</c>/<c>resultRecordCount</c> paging terminated by
/// <c>exceededTransferLimit</c>) — and the same Esri-JSON → GeoJSON geometry
/// conversion the importer uses (<see cref="GeoservicesImportService.ConvertEsriGeometryToGeoJson"/>).
/// No transport, pagination, SSRF guard, or geometry-conversion logic is
/// re-implemented here.
/// </summary>
internal sealed partial class EsriFeatureServerDagSource : IDagFeatureSource
{
    private const int DefaultMaxPages = 1000;

    private readonly ArcGisRestClient _restClient;
    private readonly ILogger<EsriFeatureServerDagSource> _logger;

    public EsriFeatureServerDagSource(
        ArcGisRestClient restClient,
        ILogger<EsriFeatureServerDagSource> logger)
    {
        _restClient = restClient;
        _logger = logger;
    }

    public string SourceId => "source.esri-featureserver";

    public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new InvalidOperationException("source.esri-featureserver requires a serviceUrl.");
        }

        var layerId = request.EsriLayerId ?? 0;
        var batchSize = request.PageSize <= 0 ? 1000 : request.PageSize;
        var maxPages = request.MaxPages is > 0 ? request.MaxPages.Value : DefaultMaxPages;
        var where = BuildWhereClause(request);
        var outFields = string.IsNullOrWhiteSpace(request.OutFields)
            ? null
            : request.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var offset = 0;
        var page = 0;
        var hasMore = true;

        while (hasMore)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page >= maxPages)
            {
                Log.PageCapReached(_logger, SourceId, maxPages);
                yield break;
            }

            page++;

            // REUSE: the migration ArcGIS REST client owns the query URL build, retry,
            // SSRF guard, credential/token handling, and exceededTransferLimit paging.
            var result = await _restClient.QueryFeaturesAsync(
                request.ServiceUrl,
                layerId,
                offset,
                batchSize,
                where,
                outFields,
                request.OutputSrid,
                request.TimeoutSeconds,
                maxRetries: 3,
                cancellationToken,
                request.EsriCredentials).ConfigureAwait(false);

            if (result.Features.Length == 0)
            {
                yield break;
            }

            foreach (var feature in result.Features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ProjectFeature(feature);
            }

            offset += result.Features.Length;
            hasMore = result.ExceededTransferLimit || result.Features.Length == batchSize;
        }
    }

    private static string BuildWhereClause(DagSourceRequest request)
    {
        var clauses = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            clauses.Add(request.Where);
        }

        // Watermark (incremental extract): narrow to features changed at/after the
        // cursor. The persistence of the next watermark is owned by the
        // incremental-extract orchestration; the reader only applies it.
        if (!string.IsNullOrWhiteSpace(request.Since) && !string.IsNullOrWhiteSpace(request.WatermarkField))
        {
            clauses.Add($"{request.WatermarkField} >= TIMESTAMP '{EscapeSqlLiteral(request.Since)}'");
        }

        // Bbox is expressed as a geometry envelope filter via the where clause for the
        // GeoServices query path; the FeatureServer query honours a standard envelope.
        if (!string.IsNullOrWhiteSpace(request.Bbox))
        {
            // The ArcGIS REST client does not currently surface a geometry= envelope
            // parameter; callers pass an attribute where clause. A spatial bbox push-down
            // is deferred to the streaming-source reconciliation.
        }

        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static string EscapeSqlLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static DagSourceFeature ProjectFeature(ArcGisFeature feature)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (feature.Attributes is { } source)
        {
            foreach (var (key, value) in source)
            {
                attributes[key] = ConvertScalar(value);
            }
        }

        string? geometryGeoJson = null;
        if (feature.Geometry is { ValueKind: JsonValueKind.Object } geometry)
        {
            geometryGeoJson = GeoservicesImportService.ConvertEsriGeometryToGeoJson(geometry);
        }

        return new DagSourceFeature
        {
            GeometryGeoJson = geometryGeoJson,
            Attributes = attributes
        };
    }

    private static object? ConvertScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l)
            ? l
            : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Warning,
            "DAG source {SourceId} stopped after reaching the page cap of {MaxPages}")]
        public static partial void PageCapReached(ILogger logger, string sourceId, int maxPages);
    }
}
