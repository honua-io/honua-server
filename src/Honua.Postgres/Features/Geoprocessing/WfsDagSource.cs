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
/// <c>source.wfs</c> DAG connector. Streams features from a WFS <c>GetFeature</c>
/// endpoint using the same <c>startIndex</c>/<c>count</c> paging the one-shot WFS
/// import uses (GeoJSON output, terminate on an empty page, <c>numberMatched</c>
/// advance check, and a repeated-first-feature guard for servers that ignore
/// <c>startIndex</c>). Page fetch + GeoJSON projection are reused from
/// <see cref="GeoJsonPageReader"/>.
/// </summary>
internal sealed partial class WfsDagSource : IDagFeatureSource
{
    private const int DefaultMaxPages = 1000;
    private const string DefaultVersion = "2.0.0";

    private readonly HttpClient _httpClient;
    private readonly ILogger<WfsDagSource> _logger;

    public WfsDagSource(HttpClient httpClient, ILogger<WfsDagSource> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string SourceId => "source.wfs";

    public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
        DagSourceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new InvalidOperationException("source.wfs requires a serviceUrl.");
        }

        if (string.IsNullOrWhiteSpace(request.CollectionId))
        {
            throw new InvalidOperationException("source.wfs requires a typeName (collectionId).");
        }

        var pageSize = request.PageSize <= 0 ? 1000 : request.PageSize;
        var maxPages = request.MaxPages is > 0 ? request.MaxPages.Value : DefaultMaxPages;

        var startIndex = 0;
        var page = 0;
        string? previousFirstFeatureRaw = null;
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
            var url = BuildGetFeatureUri(request, startIndex, pageSize);
            var fetched = await GeoJsonPageReader
                .FetchPageAsync(_httpClient, url, request.Username, request.Password, cancellationToken)
                .ConfigureAwait(false);

            if (fetched.Features.Count == 0)
            {
                yield break;
            }

            if (page > 1 && fetched.FirstFeatureRaw is not null
                && string.Equals(fetched.FirstFeatureRaw, previousFirstFeatureRaw, StringComparison.Ordinal))
            {
                // The server ignores startIndex (returns the same page); stop to avoid duplicates.
                yield break;
            }

            previousFirstFeatureRaw = fetched.FirstFeatureRaw;

            foreach (var feature in fetched.Features)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return feature;
            }

            startIndex += fetched.Features.Count;

            // Do not infer "done" from a short page (many WFS servers cap below the
            // requested count). Advance until an empty page, or past numberMatched.
            hasMore = fetched.NumberMatched is null || startIndex < fetched.NumberMatched.Value;
        }
    }

    private static Uri BuildGetFeatureUri(DagSourceRequest request, int startIndex, int count)
    {
        var version = DefaultVersion;
        var builder = new UriBuilder(request.ServiceUrl!);

        var query = new List<string>
        {
            "service=WFS",
            $"version={version}",
            "request=GetFeature",
            $"typeNames={Uri.EscapeDataString(request.CollectionId!)}",
            "outputFormat=application/json",
            $"count={count.ToString(CultureInfo.InvariantCulture)}",
            $"startIndex={startIndex.ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(request.Bbox))
        {
            query.Add($"bbox={Uri.EscapeDataString(request.Bbox)}");
        }

        builder.Query = string.Join("&", query);
        return builder.Uri;
    }

    private static partial class Log
    {
        [LoggerMessage(9272, LogLevel.Warning,
            "DAG source {SourceId} stopped after reaching the page cap of {MaxPages}")]
        public static partial void PageCapReached(ILogger logger, string sourceId, int maxPages);
    }
}
