// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Geoprocessing;

/// <summary>
/// <c>source.ogc-features</c> DAG connector. Streams features from an OGC API
/// Features collection using the same link-based pagination the one-shot OGC API
/// Features import uses: an initial <c>items</c> request with <c>limit</c> (plus
/// optional <c>bbox</c>/<c>filter</c>/<c>datetime</c>) followed by the chain of
/// <c>rel="next"</c> links until exhausted. Page fetch + GeoJSON projection are reused
/// from <see cref="GeoJsonPageReader"/> (bounded-buffer body read).
/// </summary>
internal sealed partial class OgcFeaturesDagSource : IDagFeatureSource
{
    private const int DefaultMaxPages = 1000;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OgcFeaturesDagSource> _logger;

    public OgcFeaturesDagSource(HttpClient httpClient, ILogger<OgcFeaturesDagSource> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string SourceId => "source.ogc-features";

    public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new InvalidOperationException("source.ogc-features requires a serviceUrl.");
        }

        if (string.IsNullOrWhiteSpace(request.CollectionId))
        {
            throw new InvalidOperationException("source.ogc-features requires a collectionId.");
        }

        var maxPages = request.MaxPages is > 0 ? request.MaxPages.Value : DefaultMaxPages;
        Uri? nextUri = BuildItemsUri(request);
        string? previousFirstFeatureRaw = null;
        var page = 0;

        while (nextUri is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (page >= maxPages)
            {
                Log.PageCapReached(_logger, SourceId, maxPages);
                yield break;
            }

            page++;
            var fetched = await GeoJsonPageReader
                .FetchPageAsync(_httpClient, nextUri, request.Username, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.Features.Count == 0)
            {
                yield break;
            }

            // Guard against a non-advancing server returning the same first page.
            if (page > 1 && fetched.FirstFeatureRaw is not null
                && string.Equals(fetched.FirstFeatureRaw, previousFirstFeatureRaw, StringComparison.Ordinal))
            {
                yield break;
            }

            previousFirstFeatureRaw = fetched.FirstFeatureRaw;

            foreach (var feature in fetched.Features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return feature;
            }

            nextUri = string.IsNullOrWhiteSpace(fetched.NextLink)
                ? null
                : new Uri(nextUri, fetched.NextLink);
        }
    }

    private static Uri BuildItemsUri(DagSourceRequest request)
    {
        var trimmed = request.ServiceUrl!.TrimEnd('/');
        var itemsUrl = $"{trimmed}/collections/{Uri.EscapeDataString(request.CollectionId!)}/items";
        var builder = new UriBuilder(itemsUrl);

        var query = new List<string>
        {
            $"limit={(request.PageSize <= 0 ? 1000 : request.PageSize).ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(request.Bbox))
        {
            query.Add($"bbox={Uri.EscapeDataString(request.Bbox)}");
        }

        // Where is interpreted as a CQL2-text filter on the OGC API Features path.
        if (!string.IsNullOrWhiteSpace(request.Where))
        {
            query.Add($"filter={Uri.EscapeDataString(request.Where)}");
            query.Add("filter-lang=cql2-text");
        }

        // Watermark (incremental extract) maps to the standard datetime open-interval.
        if (!string.IsNullOrWhiteSpace(request.Since))
        {
            query.Add($"datetime={Uri.EscapeDataString(request.Since)}/..");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static partial class Log
    {
        [LoggerMessage(9271, LogLevel.Warning,
            "DAG source {SourceId} stopped after reaching the page cap of {MaxPages}")]
        public static partial void PageCapReached(ILogger logger, string sourceId, int maxPages);
    }
}
