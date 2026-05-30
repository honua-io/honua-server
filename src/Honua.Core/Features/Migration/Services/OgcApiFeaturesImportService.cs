// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// HTTP-backed OGC API Features collection importer. Paginates the source items endpoint,
/// projects features deterministically, and writes them to the configured sink.
/// </summary>
public sealed partial class OgcApiFeaturesImportService : IOgcApiFeaturesImportService
{
    private const string DisallowedNetworkAddressMessage = "OGC API Features URL resolves to a disallowed network address.";
    private const int MaxJsonDocumentRedirects = 5;
    private const int DefaultPageSize = 500;
    private const int DefaultTimeoutSeconds = 300;
    private const int DefaultMaxPages = 1_000;
    private const int MaxIdentifierLength = 63;

    private static readonly AsyncLocal<bool> UnsafeLocalUrlsAllowed = new();

    private readonly HttpClient _httpClient;
    private readonly IOgcApiFeaturesCollectionSink _sink;
    private readonly ILogger<OgcApiFeaturesImportService> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="OgcApiFeaturesImportService"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client used to fetch OGC API Features documents.</param>
    /// <param name="sink">Target sink that persists features.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="hostAddressResolver">Optional DNS resolver, used by tests to pin addresses.</param>
    public OgcApiFeaturesImportService(
        HttpClient httpClient,
        IOgcApiFeaturesCollectionSink sink,
        ILogger<OgcApiFeaturesImportService> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostAddressResolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    /// <summary>
    /// Creates an HTTP handler that pins DNS resolution before connecting to an OGC API Features host.
    /// </summary>
    /// <param name="hostAddressResolver">Optional host resolver used by tests.</param>
    /// <returns>HTTP message handler with bounded connections and pinned DNS validation.</returns>
    public static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 16,
            ConnectCallback = (context, cancellationToken) =>
                ConnectWithPinnedDnsAsync(context, resolver, UnsafeLocalUrlsAllowed.Value, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<OgcApiFeaturesImportResult> ImportCollectionAsync(
        OgcApiFeaturesImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.CollectionId))
        {
            return Failure(request, "CollectionId is required.", OgcApiFeaturesImportErrorCodes.InvalidServiceUrl, stopwatch.Elapsed);
        }

        if (string.IsNullOrWhiteSpace(request.TargetSchema))
        {
            return Failure(request, "TargetSchema is required.", OgcApiFeaturesImportErrorCodes.InvalidServiceUrl, stopwatch.Elapsed);
        }

        var schema = SanitizeIdentifier(request.TargetSchema);
        var table = SanitizeIdentifier(string.IsNullOrWhiteSpace(request.TargetTable)
            ? request.CollectionId
            : request.TargetTable!);
        if (schema == null || table == null)
        {
            return Failure(
                request,
                "TargetSchema and TargetTable must be valid SQL identifiers (letters, digits, underscores).",
                OgcApiFeaturesImportErrorCodes.InvalidServiceUrl,
                stopwatch.Elapsed,
                table: $"{request.TargetSchema}.{request.TargetTable ?? request.CollectionId}");
        }

        string? normalizedFilter;
        try
        {
            normalizedFilter = NormalizeFilter(request.Filter);
        }
        catch (ArgumentException ex)
        {
            return Failure(
                request,
                ex.Message,
                OgcApiFeaturesImportErrorCodes.InvalidFilter,
                stopwatch.Elapsed,
                target: $"{schema}.{table}");
        }

        string? normalizedBbox;
        try
        {
            normalizedBbox = NormalizeBbox(request.Bbox);
        }
        catch (ArgumentException ex)
        {
            return Failure(
                request,
                ex.Message,
                OgcApiFeaturesImportErrorCodes.InvalidBbox,
                stopwatch.Elapsed,
                target: $"{schema}.{table}");
        }

        string? normalizedDatetime;
        try
        {
            normalizedDatetime = NormalizeDatetime(request.Datetime);
        }
        catch (ArgumentException ex)
        {
            return Failure(
                request,
                ex.Message,
                OgcApiFeaturesImportErrorCodes.InvalidDatetime,
                stopwatch.Elapsed,
                target: $"{schema}.{table}");
        }

        Uri serviceUri;
        try
        {
            serviceUri = ValidateServiceUri(request.ServiceUrl, request.AllowUnsafeLocalUrls);
        }
        catch (HttpRequestException ex)
        {
            return Failure(
                request,
                ex.Message,
                OgcApiFeaturesImportErrorCodes.InvalidServiceUrl,
                stopwatch.Elapsed,
                table: $"{schema}.{table}");
        }

        try
        {
            _ = await ResolveAllowedAddressesAsync(
                    serviceUri.DnsSafeHost,
                    _hostAddressResolver,
                    request.AllowUnsafeLocalUrls,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Failure(
                request,
                ex.Message,
                OgcApiFeaturesImportErrorCodes.InvalidServiceUrl,
                stopwatch.Elapsed,
                table: $"{schema}.{table}");
        }

        var previousUnsafeLocalUrlsAllowed = UnsafeLocalUrlsAllowed.Value;
        UnsafeLocalUrlsAllowed.Value = request.AllowUnsafeLocalUrls;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds <= 0 ? DefaultTimeoutSeconds : request.TimeoutSeconds));

        var target = new OgcApiFeaturesSinkTarget
        {
            Schema = schema,
            Table = table,
            CollectionId = request.CollectionId
        };
        var warnings = new List<string>();

        try
        {
            var itemsUri = await ResolveItemsUriAsync(serviceUri, request.CollectionId, timeout.Token).ConfigureAwait(false);
            itemsUri = AddOrReplaceQueryParameter(itemsUri, "limit", ResolvePageSize(request).ToString(CultureInfo.InvariantCulture));

            if (normalizedFilter != null)
            {
                itemsUri = AddOrReplaceQueryParameter(itemsUri, "filter", normalizedFilter);
                itemsUri = AddOrReplaceQueryParameter(itemsUri, "filter-lang", "cql2-text");
            }

            if (normalizedBbox != null)
            {
                itemsUri = AddOrReplaceQueryParameter(itemsUri, "bbox", normalizedBbox);
            }

            if (normalizedDatetime != null)
            {
                itemsUri = AddOrReplaceQueryParameter(itemsUri, "datetime", normalizedDatetime);
            }

            await _sink.EnsureTargetAsync(target, timeout.Token).ConfigureAwait(false);

            using var schemaDocumentHandle = await TryFetchSchemaDocumentAsync(serviceUri, request.CollectionId, timeout.Token).ConfigureAwait(false);
            var schemaDocument = schemaDocumentHandle?.RootElement;
            IReadOnlyList<OgcApiFeaturesSchemaMappingDiagnostic> mappingDiagnostics = [];

            var scopeSignature = ComputeScopeSignature(normalizedFilter, normalizedBbox, normalizedDatetime);
            var scopeDriftDetected = false;
            string? manualReviewReason = null;
            try
            {
                var priorScope = await _sink.GetLastScopeSignatureAsync(target, timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(priorScope) &&
                    !string.Equals(priorScope, scopeSignature, StringComparison.Ordinal))
                {
                    scopeDriftDetected = true;
                    manualReviewReason =
                        "OGC API Features import scope changed since the previous run; previously-imported rows that fall outside the new filter/bbox/datetime were NOT deleted and require manual reconciliation.";
                    warnings.Add(manualReviewReason);
                    Log.ScopeDriftDetected(_logger, request.CollectionId, $"{schema}.{table}", priorScope, scopeSignature);
                }

                await _sink.RecordScopeSignatureAsync(target, scopeSignature, timeout.Token).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                // Sink implementation explicitly opted out of scope tracking; treat as a first run.
            }

            var maxFeatures = request.MaxFeatures < 0 ? 0 : request.MaxFeatures;
            var maxPages = request.MaxPages <= 0 ? DefaultMaxPages : request.MaxPages;

            var pagesFetched = 0;
            var featuresImported = 0;
            var featuresSkipped = 0;
            var truncated = false;
            long? sourceFeatureCountReported = null;
            Uri? nextUri = itemsUri;
            var visited = new HashSet<string>(StringComparer.Ordinal);

            while (nextUri != null)
            {
                if (pagesFetched >= maxPages)
                {
                    truncated = true;
                    warnings.Add($"Stopped after reaching the configured page limit of {maxPages}.");
                    break;
                }

                var canonical = nextUri.AbsoluteUri;
                if (!visited.Add(canonical))
                {
                    warnings.Add("Source advertised a paging cycle; stopping to avoid an infinite loop.");
                    break;
                }

                using var document = await GetJsonDocumentAsync(nextUri, timeout.Token).ConfigureAwait(false);
                pagesFetched++;

                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("features", out var featuresElement)
                    || featuresElement.ValueKind != JsonValueKind.Array)
                {
                    return Failure(
                        request,
                        "Items endpoint did not return a GeoJSON FeatureCollection.",
                        OgcApiFeaturesImportErrorCodes.UnsupportedItemsEncoding,
                        stopwatch.Elapsed,
                        target: $"{schema}.{table}",
                        partialImported: featuresImported,
                        pagesFetched: pagesFetched,
                        warnings: warnings);
                }

                if (pagesFetched == 1)
                {
                    mappingDiagnostics = await ComputeMappingDiagnosticsAsync(
                            target,
                            schemaDocument,
                            featuresElement,
                            warnings,
                            timeout.Token)
                        .ConfigureAwait(false);

                    sourceFeatureCountReported = TryReadNonNegativeLong(root, "numberMatched");
                }

                var batch = new List<OgcApiFeaturesSinkFeature>(featuresElement.GetArrayLength());
                var pageIndex = 0;
                foreach (var feature in featuresElement.EnumerateArray())
                {
                    if (maxFeatures > 0 && featuresImported + batch.Count >= maxFeatures)
                    {
                        truncated = true;
                        break;
                    }

                    var projected = TryProjectFeature(feature, request.CollectionId, pageIndex, warnings);
                    if (projected != null)
                    {
                        batch.Add(projected);
                    }
                    else
                    {
                        featuresSkipped++;
                    }

                    pageIndex++;
                }

                if (batch.Count > 0)
                {
                    var written = await _sink.WriteFeaturesAsync(target, batch, timeout.Token).ConfigureAwait(false);
                    featuresImported += written;
                }

                if (truncated)
                {
                    warnings.Add($"Stopped after reaching the configured feature limit of {maxFeatures}.");
                    break;
                }

                var nextLink = ReadLinks(root)
                    .FirstOrDefault(static link => IsRel(link, "next"));

                nextUri = nextLink != null ? ResolveUri(nextLink.Href, nextUri) : null;
            }

            var featureCountParity = BuildFeatureCountParity(
                sourceFeatureCountReported,
                featuresImported,
                featuresSkipped,
                truncated,
                normalizedFilter,
                normalizedBbox,
                normalizedDatetime);

            return new OgcApiFeaturesImportResult
            {
                Success = true,
                CollectionId = request.CollectionId,
                Target = $"{schema}.{table}",
                FeaturesImported = featuresImported,
                FeaturesSkipped = featuresSkipped,
                PagesFetched = pagesFetched,
                Truncated = truncated,
                Warnings = warnings,
                Duration = stopwatch.Elapsed,
                ScopeDriftDetected = scopeDriftDetected,
                ManualReviewReason = manualReviewReason,
                MappingDiagnostics = mappingDiagnostics,
                SourceFeatureCountReported = sourceFeatureCountReported,
                FeatureCountParity = featureCountParity
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.ImportTimedOut(_logger, request.CollectionId, ToSafeSourceUrl(serviceUri));
            return Failure(
                request,
                "OGC API Features collection import timed out.",
                OgcApiFeaturesImportErrorCodes.Timeout,
                stopwatch.Elapsed,
                target: $"{schema}.{table}",
                warnings: warnings);
        }
        catch (HttpRequestException ex)
        {
            Log.ImportFailed(_logger, request.CollectionId, ToSafeSourceUrl(serviceUri), ex);
            return Failure(
                request,
                "OGC API Features collection items endpoint could not be reached.",
                OgcApiFeaturesImportErrorCodes.SourceUnreachable,
                stopwatch.Elapsed,
                target: $"{schema}.{table}",
                warnings: warnings);
        }
        catch (JsonException ex)
        {
            Log.ImportFailed(_logger, request.CollectionId, ToSafeSourceUrl(serviceUri), ex);
            return Failure(
                request,
                "OGC API Features items document could not be parsed as JSON.",
                OgcApiFeaturesImportErrorCodes.InvalidItemsDocument,
                stopwatch.Elapsed,
                target: $"{schema}.{table}",
                warnings: warnings);
        }
        catch (Exception ex) when (IsSinkFailure(ex))
        {
            Log.SinkFailed(_logger, request.CollectionId, $"{schema}.{table}", ex);
            return Failure(
                request,
                "OGC API Features collection sink rejected the import.",
                OgcApiFeaturesImportErrorCodes.SinkFailure,
                stopwatch.Elapsed,
                target: $"{schema}.{table}",
                warnings: warnings);
        }
        finally
        {
            UnsafeLocalUrlsAllowed.Value = previousUnsafeLocalUrlsAllowed;
        }
    }

    private static bool IsSinkFailure(Exception ex)
        => ex is InvalidOperationException
            || ex.GetType().FullName == "Npgsql.NpgsqlException"
            || (ex.GetType().FullName?.StartsWith("Npgsql.", StringComparison.Ordinal) ?? false);

    private static string? NormalizeFilter(string? filter)
    {
        if (filter == null)
        {
            return null;
        }

        var trimmed = filter.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Filter must be a non-empty CQL2-text expression when supplied.");
        }

        return trimmed;
    }

    private static string? NormalizeBbox(double[]? bbox)
    {
        if (bbox == null)
        {
            return null;
        }

        if (bbox.Length is not (4 or 6))
        {
            throw new ArgumentException("Bbox must contain exactly 4 (2D) or 6 (3D) ordinates.");
        }

        foreach (var value in bbox)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException("Bbox ordinates must be finite numbers.");
            }
        }

        if (bbox.Length == 4)
        {
            if (bbox[2] < bbox[0] || bbox[3] < bbox[1])
            {
                throw new ArgumentException("Bbox maximum ordinates must be greater than or equal to the corresponding minima.");
            }
        }
        else
        {
            if (bbox[3] < bbox[0] || bbox[4] < bbox[1] || bbox[5] < bbox[2])
            {
                throw new ArgumentException("Bbox maximum ordinates must be greater than or equal to the corresponding minima.");
            }
        }

        return string.Join(",", bbox.Select(static value => value.ToString("R", CultureInfo.InvariantCulture)));
    }

    private static string? NormalizeDatetime(string? datetime)
    {
        if (datetime == null)
        {
            return null;
        }

        var trimmed = datetime.Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Datetime must be a non-empty RFC3339 instant or interval when supplied.");
        }

        var parts = trimmed.Split('/');
        if (parts.Length == 1)
        {
            if (!TryParseRfc3339Instant(parts[0], out _))
            {
                throw new ArgumentException("Datetime instant must be a valid RFC3339 timestamp.");
            }

            return trimmed;
        }

        if (parts.Length != 2)
        {
            throw new ArgumentException("Datetime interval must contain exactly one '/' separator.");
        }

        var startToken = parts[0];
        var endToken = parts[1];

        if ((startToken == ".." || startToken.Length == 0) &&
            (endToken == ".." || endToken.Length == 0))
        {
            throw new ArgumentException("Datetime interval must bound at least one endpoint.");
        }

        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (startToken != ".." && startToken.Length > 0)
        {
            if (!TryParseRfc3339Instant(startToken, out var parsed))
            {
                throw new ArgumentException("Datetime interval start must be a valid RFC3339 timestamp or '..'.");
            }

            start = parsed;
        }

        if (endToken != ".." && endToken.Length > 0)
        {
            if (!TryParseRfc3339Instant(endToken, out var parsed))
            {
                throw new ArgumentException("Datetime interval end must be a valid RFC3339 timestamp or '..'.");
            }

            end = parsed;
        }

        if (start.HasValue && end.HasValue && end < start)
        {
            throw new ArgumentException("Datetime interval end must not be earlier than the start.");
        }

        return trimmed;
    }

    private static bool TryParseRfc3339Instant(string value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }

    private static string ComputeScopeSignature(string? filter, string? bbox, string? datetime)
    {
        return string.Concat(
            "f=", filter ?? string.Empty,
            "|b=", bbox ?? string.Empty,
            "|d=", datetime ?? string.Empty);
    }

    private static int ResolvePageSize(OgcApiFeaturesImportRequest request)
    {
        if (request.PageSize <= 0)
        {
            return DefaultPageSize;
        }

        return Math.Min(request.PageSize, 10_000);
    }

    private static long? TryReadNonNegativeLong(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!element.TryGetInt64(out var value) || value < 0)
        {
            return null;
        }

        return value;
    }

    private static OgcApiFeaturesFeatureCountParity BuildFeatureCountParity(
        long? sourceFeatureCountReported,
        int featuresImported,
        int featuresSkipped,
        bool truncated,
        string? normalizedFilter,
        string? normalizedBbox,
        string? normalizedDatetime)
    {
        // When the operator pushed a filter/bbox/datetime to the source we cannot compare the
        // source-advertised numberMatched (which already reflects the filtered subset on the page
        // we read) against the importer's accumulated count if subsequent pages would have
        // produced different counts on the source. The source numberMatched is page-stable for
        // OGC API Features, but truncation by the operator (MaxFeatures/MaxPages) makes the probe
        // not comparable in either case. Treat both situations as not-applicable so operators
        // don't see spurious failures.
        if (truncated)
        {
            return new OgcApiFeaturesFeatureCountParity
            {
                State = OgcApiFeaturesFeatureCountParityStates.NotApplicable,
                Expected = sourceFeatureCountReported,
                Observed = featuresImported,
                Summary = "Feature-count parity probe skipped: import was truncated by an operator-imposed MaxFeatures or MaxPages cap.",
            };
        }

        if (sourceFeatureCountReported is null)
        {
            return new OgcApiFeaturesFeatureCountParity
            {
                State = OgcApiFeaturesFeatureCountParityStates.NotApplicable,
                Expected = null,
                Observed = featuresImported,
                Summary = "Feature-count parity probe skipped: source did not advertise a numberMatched total on the first items page.",
            };
        }

        // featuresSkipped accumulates features the importer could not project (missing identifier,
        // invalid geometry). Those still count against the source numberMatched, so they belong on
        // the observed side of the probe even though the sink never saw them.
        var observedFromSource = (long)featuresImported + featuresSkipped;
        if (observedFromSource == sourceFeatureCountReported.Value)
        {
            var skippedSuffix = featuresSkipped > 0
                ? $" ({featuresSkipped} feature(s) skipped due to projection errors and excluded from the sink count)."
                : ".";
            return new OgcApiFeaturesFeatureCountParity
            {
                State = OgcApiFeaturesFeatureCountParityStates.Pass,
                Expected = sourceFeatureCountReported,
                Observed = featuresImported,
                Summary =
                    $"Source advertised {sourceFeatureCountReported.Value} feature(s); importer accounted for the same number{skippedSuffix}",
            };
        }

        var scopeNote = (normalizedFilter, normalizedBbox, normalizedDatetime) switch
        {
            (null, null, null) => string.Empty,
            _ => " Note: a filter/bbox/datetime was pushed to the source — review the scope before treating the mismatch as a defect."
        };

        return new OgcApiFeaturesFeatureCountParity
        {
            State = OgcApiFeaturesFeatureCountParityStates.Fail,
            Expected = sourceFeatureCountReported,
            Observed = featuresImported,
            Summary =
                $"Source advertised {sourceFeatureCountReported.Value} feature(s) but importer accounted for {observedFromSource} ({featuresImported} written, {featuresSkipped} skipped).{scopeNote}",
        };
    }

    private static OgcApiFeaturesSinkFeature? TryProjectFeature(
        JsonElement feature,
        string collectionId,
        int pageIndex,
        List<string> warnings)
    {
        if (feature.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? sourceId = null;
        if (feature.TryGetProperty("id", out var idElement))
        {
            sourceId = idElement.ValueKind switch
            {
                JsonValueKind.String => idElement.GetString(),
                JsonValueKind.Number => idElement.GetRawText(),
                _ => null
            };
        }

        if (string.IsNullOrWhiteSpace(sourceId))
        {
            sourceId = $"{collectionId}.synthetic.{pageIndex}";
            if (warnings.Count < 50)
            {
                warnings.Add($"Synthesized identifier for unidentified feature at page-index {pageIndex}.");
            }
        }

        string? geometryJson = null;
        if (feature.TryGetProperty("geometry", out var geometryElement) &&
            geometryElement.ValueKind == JsonValueKind.Object)
        {
            geometryJson = geometryElement.GetRawText();
        }

        var propertiesJson = "{}";
        if (feature.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            propertiesJson = propertiesElement.GetRawText();
        }

        return new OgcApiFeaturesSinkFeature
        {
            SourceFeatureId = sourceId!,
            GeoJsonGeometry = geometryJson,
            PropertiesJson = propertiesJson
        };
    }

    private async Task<Uri> ResolveItemsUriAsync(Uri serviceUri, string collectionId, CancellationToken cancellationToken)
    {
        var collectionUri = AppendPath(serviceUri, $"collections/{Uri.EscapeDataString(collectionId)}");

        try
        {
            using var document = await GetJsonDocumentAsync(collectionUri, cancellationToken).ConfigureAwait(false);
            var links = ReadLinks(document.RootElement);
            var itemsUri = links
                .Where(static link => IsRel(link, "items") || LinkLooksLikeItems(link))
                .Select(link => ResolveUri(link.Href, collectionUri))
                .FirstOrDefault(static uri => uri != null);

            if (itemsUri != null)
            {
                return itemsUri;
            }
        }
        catch (HttpRequestException ex) when (LogCollectionMetadataUnavailable(collectionId, collectionUri, ex))
        {
            // Swallowed after logging.
        }
        catch (JsonException ex) when (LogCollectionMetadataUnavailable(collectionId, collectionUri, ex))
        {
            // Swallowed after logging.
        }

        return AppendPath(serviceUri, $"collections/{Uri.EscapeDataString(collectionId)}/items");
    }

    private bool LogCollectionMetadataUnavailable(string collectionId, Uri collectionUri, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var url = ToSafeSourceUrl(collectionUri);
            Log.CollectionMetadataUnavailable(_logger, collectionId, url, exception);
        }

        return true;
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(Uri uri, CancellationToken cancellationToken)
    {
        var requestUri = uri;

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, requestUri);
            message.Headers.Accept.ParseAdd("application/geo+json");
            message.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirectCount >= MaxJsonDocumentRedirects)
                {
                    throw new HttpRequestException("OGC API Features endpoint returned too many redirects.");
                }

                var redirectUri = ResolveRedirectUri(requestUri, response.Headers.Location);
                if (redirectUri == null || !IsSafeRedirectUri(requestUri, redirectUri))
                {
                    throw new HttpRequestException("OGC API Features endpoint returned an unsafe redirect.");
                }

                requestUri = redirectUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static OgcApiFeaturesImportResult Failure(
        OgcApiFeaturesImportRequest request,
        string message,
        string errorCode,
        TimeSpan duration,
        string? target = null,
        string? table = null,
        int partialImported = 0,
        int pagesFetched = 0,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            Success = false,
            CollectionId = request.CollectionId ?? string.Empty,
            Target = target ?? table ?? $"{request.TargetSchema}.{request.TargetTable ?? request.CollectionId}",
            FeaturesImported = partialImported,
            PagesFetched = pagesFetched,
            Truncated = false,
            ErrorMessage = message,
            ErrorCode = errorCode,
            Warnings = warnings ?? [],
            Duration = duration
        };

    private static string? SanitizeIdentifier(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var builder = new StringBuilder(candidate.Length);
        foreach (var ch in candidate.Trim())
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            {
                builder.Append(ch);
            }
            else if (ch is '-' or '.' or ' ')
            {
                builder.Append('_');
            }
            else
            {
                return null;
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        if (builder[0] is >= '0' and <= '9')
        {
            builder.Insert(0, '_');
        }

        if (builder.Length > MaxIdentifierLength)
        {
            builder.Length = MaxIdentifierLength;
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static List<OgcApiFeaturesItemLink> ReadLinks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("links", out var links) ||
            links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<OgcApiFeaturesItemLink>();
        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var rel = ReadString(link, "rel") ?? "related";
            var href = ReadString(link, "href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            list.Add(new OgcApiFeaturesItemLink(rel, href, ReadString(link, "type")));
        }

        return list;
    }

    private static bool IsRel(OgcApiFeaturesItemLink link, string rel)
        => string.Equals(link.Rel, rel, StringComparison.OrdinalIgnoreCase);

    private static bool LinkLooksLikeItems(OgcApiFeaturesItemLink link)
        => link.Href.Contains("/items", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? ResolveUri(string href, Uri baseUri)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var absolute)
            ? absolute
            : Uri.TryCreate(EnsureDirectoryBase(baseUri), href, out var relative)
                ? relative
                : null;
    }

    private static Uri AppendPath(Uri baseUri, string relativePath)
    {
        var root = EnsureDirectoryBase(baseUri);
        return new Uri(root, relativePath.TrimStart('/'));
    }

    private static Uri EnsureDirectoryBase(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (builder.Path.Length == 0 || builder.Path[^1] != '/')
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri AddOrReplaceQueryParameter(Uri uri, string name, string value)
    {
        var parameters = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(parameter => !parameter.Split('=', 2)[0].Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        parameters.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        var builder = new UriBuilder(uri)
        {
            Query = string.Join("&", parameters)
        };
        return builder.Uri;
    }

    private static Uri? ResolveRedirectUri(Uri requestUri, Uri? location)
    {
        if (location == null)
        {
            return null;
        }

        return location.IsAbsoluteUri
            ? location
            : Uri.TryCreate(requestUri, location, out var redirectUri)
                ? redirectUri
                : null;
    }

    private static bool IsSafeRedirectUri(Uri requestUri, Uri redirectUri)
    {
        if (redirectUri.Scheme != Uri.UriSchemeHttp &&
            redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(redirectUri.UserInfo) ||
            !string.Equals(requestUri.DnsSafeHost, redirectUri.DnsSafeHost, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requestUri.Scheme == Uri.UriSchemeHttps && redirectUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (requestUri.Scheme == redirectUri.Scheme)
        {
            return requestUri.Port == redirectUri.Port;
        }

        return requestUri.Scheme == Uri.UriSchemeHttp &&
               redirectUri.Scheme == Uri.UriSchemeHttps &&
               IsDefaultPort(requestUri) &&
               IsDefaultPort(redirectUri);
    }

    private static bool IsDefaultPort(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return uri.Port == 80;
        }

        return uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443;
    }

    private static Uri ValidateServiceUri(string serviceUrl, bool allowUnsafeLocalUrls)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (allowUnsafeLocalUrls
                ? uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps
                : uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new HttpRequestException(allowUnsafeLocalUrls
                ? "OGC API Features URL must be a valid HTTP or HTTPS URL."
                : "OGC API Features URL must be a valid HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new HttpRequestException("OGC API Features URL must not include embedded credentials.");
        }

        if (!allowUnsafeLocalUrls &&
            (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        return uri;
    }

    private static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (!allowUnsafeLocalUrls && IsPrivateOrReservedAddress(literalAddress))
            {
                throw new HttpRequestException(DisallowedNetworkAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }
        catch (ArgumentException)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (!allowUnsafeLocalUrls)
        {
            foreach (var address in addresses)
            {
                if (IsPrivateOrReservedAddress(address))
                {
                    throw new HttpRequestException(DisallowedNetworkAddressMessage);
                }
            }
        }

        return addresses;
    }

    private static async ValueTask<Stream> ConnectWithPinnedDnsAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        bool allowUnsafeLocalUrls,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                allowUnsafeLocalUrls,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;

            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("Unable to establish a connection to the OGC API Features host.", lastException);
    }

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 0 ||
                   bytes[0] == 10 ||
                   (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                   (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) ||
                   (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                   bytes[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal ||
                   address.IsIPv6Multicast ||
                   address.IsIPv6SiteLocal ||
                   bytes.All(static value => value == 0) ||
                   bytes[0] == 0xfc ||
                   bytes[0] == 0xfd ||
                   bytes[0] == 0xfe;
        }

        return true;
    }

    private static string ToSafeSourceUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private async Task<JsonDocument?> TryFetchSchemaDocumentAsync(
        Uri serviceUri,
        string collectionId,
        CancellationToken cancellationToken)
    {
        var schemaUri = AppendPath(serviceUri, $"collections/{Uri.EscapeDataString(collectionId)}/schemas/feature");

        try
        {
            return await GetJsonDocumentAsync(schemaUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
#pragma warning disable CA1873 // safe-url helper only invoked inside the IsEnabled gate above
                Log.SchemaDocumentUnavailable(_logger, collectionId, ToSafeSourceUrl(schemaUri), ex);
#pragma warning restore CA1873
            }

            return null;
        }
        catch (JsonException ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
#pragma warning disable CA1873 // safe-url helper only invoked inside the IsEnabled gate above
                Log.SchemaDocumentUnavailable(_logger, collectionId, ToSafeSourceUrl(schemaUri), ex);
#pragma warning restore CA1873
            }

            return null;
        }
    }

    private async Task<IReadOnlyList<OgcApiFeaturesSchemaMappingDiagnostic>> ComputeMappingDiagnosticsAsync(
        OgcApiFeaturesSinkTarget target,
        JsonElement? schemaDocument,
        JsonElement firstPageFeatures,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OgcApiFeaturesSinkColumn> columns;
        try
        {
            columns = await _sink.GetTargetColumnsAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return [];
        }

        if (columns.Count == 0)
        {
            return [];
        }

        var sourceProperties = OgcApiFeaturesSchemaMapper.DeriveSourceProperties(schemaDocument, firstPageFeatures);
        var diagnostics = OgcApiFeaturesSchemaMapper.Diagnose(sourceProperties, columns);

        foreach (var diagnostic in diagnostics)
        {
            if (warnings.Count >= 100)
            {
                break;
            }

            warnings.Add($"Schema mapping {diagnostic.Classification.ToString().ToLowerInvariant()}: {diagnostic.Reason}");
        }

        return diagnostics;
    }

    private sealed record OgcApiFeaturesItemLink(string Rel, string Href, string? Type);

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 21101,
            Level = LogLevel.Warning,
            Message = "OGC API Features collection import failed for {CollectionId} at {SourceUrl}.")]
        public static partial void ImportFailed(ILogger logger, string collectionId, string sourceUrl, Exception exception);

        [LoggerMessage(
            EventId = 21102,
            Level = LogLevel.Warning,
            Message = "OGC API Features collection import timed out for {CollectionId} at {SourceUrl}.")]
        public static partial void ImportTimedOut(ILogger logger, string collectionId, string sourceUrl);

        [LoggerMessage(
            EventId = 21103,
            Level = LogLevel.Warning,
            Message = "OGC API Features collection sink failed for {CollectionId} writing to {Target}.")]
        public static partial void SinkFailed(ILogger logger, string collectionId, string target, Exception exception);

        [LoggerMessage(
            EventId = 21104,
            Level = LogLevel.Debug,
            Message = "OGC API Features collection metadata unavailable for {CollectionId} at {Url}.")]
        public static partial void CollectionMetadataUnavailable(ILogger logger, string collectionId, string url, Exception exception);

        [LoggerMessage(
            EventId = 21105,
            Level = LogLevel.Warning,
            Message = "OGC API Features import scope drift detected for {CollectionId} writing to {Target}; previous scope {PreviousScope} differs from current scope {CurrentScope}. Rows that fell out of scope were NOT deleted.")]
        public static partial void ScopeDriftDetected(ILogger logger, string collectionId, string target, string previousScope, string currentScope);

        [LoggerMessage(
            EventId = 21106,
            Level = LogLevel.Debug,
            Message = "OGC API Features schema document unavailable for {CollectionId} at {Url}; mapping diagnostics will fall back to inference from first-page properties.")]
        public static partial void SchemaDocumentUnavailable(ILogger logger, string collectionId, string url, Exception exception);
    }
}
