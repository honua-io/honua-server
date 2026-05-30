// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CA1859 // Tile-cache export helpers return IReadOnlyList<T>/IReadOnlySet<T> so callers can pass any compatible collection uniformly; the perf difference is not on a hot path.

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
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
/// Deterministic XYZ/TMS tile cache exporter for WMTS sources whose slice-3
/// plan entry classified the tile-set as <c>automated</c>. Fetches tiles
/// within a bounded zoom and tile-count window and hands them to an
/// <see cref="IOgcTileCacheSink"/>. Re-runs are idempotent because the sink
/// reports already-present tiles instead of overwriting them.
/// </summary>
public sealed partial class OgcTileCacheExportService : IOgcTileCacheExportService
{
    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> identifier for the WMTS tile
    /// fetch client.
    /// </summary>
    public const string HttpClientName = "ogc-tile-cache-export";

    private const string SourceKindOgcWmts = "ogc-wmts";
    private const string DefaultTileFormat = "image/png";
    private const string DefaultStyle = "default";
    private const int MaxTileBytes = 4 * 1024 * 1024;

    private readonly IOgcServiceMigrationScanner _scanner;
    private readonly IOgcTileCacheSink _sink;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OgcTileCacheExportService> _logger;

    /// <summary>
    /// Initializes a new <see cref="OgcTileCacheExportService"/>.
    /// </summary>
    /// <param name="scanner">OGC service migration scanner used to resolve the manifest.</param>
    /// <param name="sink">Tile cache sink used to persist tile bytes.</param>
    /// <param name="httpClient">HTTP client used to fetch WMTS tiles.</param>
    /// <param name="logger">Logger.</param>
    public OgcTileCacheExportService(
        IOgcServiceMigrationScanner scanner,
        IOgcTileCacheSink sink,
        HttpClient httpClient,
        ILogger<OgcTileCacheExportService> logger)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<OgcTileCacheExportResult> ExportAsync(
        OgcTileCacheExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new ArgumentException("ServiceUrl is required.", nameof(request));
        }

        if (request.MinZoom < 0 || request.MaxZoom < 0 || request.MaxZoom < request.MinZoom)
        {
            throw new ArgumentException("MinZoom/MaxZoom must form a non-negative inclusive range.", nameof(request));
        }

        var stopwatch = Stopwatch.StartNew();
        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? Guid.NewGuid().ToString("N")[..8]
            : request.JobId;
        var safeSourceUrl = ToSafeSourceUrl(request.ServiceUrl);
        var tileFormat = string.IsNullOrWhiteSpace(request.TileFormat) ? DefaultTileFormat : request.TileFormat;
        var styleIdentifier = string.IsNullOrWhiteSpace(request.StyleIdentifier) ? DefaultStyle : request.StyleIdentifier;

        Log.ExportStarting(_logger, safeSourceUrl, jobId, request.DryRun);

        MigrationSourceInventoryArtifact inventory;
        try
        {
            inventory = await _scanner.ScanSourceAsync(
                new OgcServiceScanRequest
                {
                    ServiceType = "WMTS",
                    ServiceUrl = request.ServiceUrl,
                    Version = request.Version,
                    TimeoutSeconds = request.RequestTimeoutSeconds,
                    AllowUnsafeLocalUrls = request.AllowUnsafeLocalUrls
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.InventoryScanFailed(_logger, safeSourceUrl, ex);
            return CreateFailureResult(request, safeSourceUrl, "Failed to scan WMTS source for tile-cache export.", stopwatch.Elapsed);
        }

        if (!string.Equals(inventory.SourceKind, SourceKindOgcWmts, StringComparison.OrdinalIgnoreCase))
        {
            return CreateFailureResult(request, safeSourceUrl, "Source URL did not resolve to a WMTS endpoint.", stopwatch.Elapsed, inventory);
        }

        if (string.Equals(inventory.ScanCompleteness.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var reason = inventory.ScanCompleteness.Warnings.FirstOrDefault() ?? "WMTS source scan failed.";
            return CreateFailureResult(request, safeSourceUrl, reason, stopwatch.Elapsed, inventory);
        }

        var manifest = MigrationManifestTranslator.Translate(inventory);
        var plans = ResolveWmtsPlans(manifest);

        var perTileSet = new List<OgcTileCacheExportedTileSet>();
        var topLevelWarnings = new List<string>();
        var tileSetsPlanned = 0;
        var tileSetsExported = 0;
        var tileSetsSkipped = 0;
        var tilesPersisted = 0;
        var tilesAlreadyPresent = 0;
        var tilesFailed = 0;

        var requestedLayers = ToOrdinalSet(request.LayerIdentifiers);
        var requestedTileMatrixSets = ToOrdinalSet(request.TileMatrixSetIdentifiers);

        foreach (var plan in plans)
        {
            var tileSetEntries = plan.PlanEntries
                .Where(entry => string.Equals(entry.Category, "tile-set", StringComparison.Ordinal))
                .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
                .ToArray();

            var layerEntries = plan.PlanEntries
                .Where(entry => string.Equals(entry.Category, "layer-metadata", StringComparison.Ordinal))
                .OrderBy(static entry => entry.Id, StringComparer.Ordinal)
                .ToArray();

            foreach (var layerEntry in layerEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (requestedLayers != null && !requestedLayers.Contains(layerEntry.SourceId))
                {
                    continue;
                }

                var layerResource = inventory.Resources.FirstOrDefault(resource =>
                    string.Equals(resource.Id, layerEntry.SourceId, StringComparison.Ordinal));
                if (layerResource == null)
                {
                    continue;
                }

                var layerTileMatrixSetIds = new HashSet<string>(layerResource.ExternalDependencyIds, StringComparer.Ordinal);

                foreach (var tileSetEntry in tileSetEntries)
                {
                    if (!layerTileMatrixSetIds.Contains(tileSetEntry.SourceId))
                    {
                        continue;
                    }

                    if (requestedTileMatrixSets != null && !requestedTileMatrixSets.Contains(tileSetEntry.SourceId))
                    {
                        continue;
                    }

                    tileSetsPlanned++;

                    if (!string.Equals(tileSetEntry.AutomationStatus, MigrationFidelityAutomationStatuses.Automated, StringComparison.Ordinal))
                    {
                        perTileSet.Add(new OgcTileCacheExportedTileSet
                        {
                            LayerIdentifier = layerResource.Name,
                            TileMatrixSetIdentifier = tileSetEntry.Name ?? tileSetEntry.SourceId,
                            MinZoom = request.MinZoom,
                            MaxZoom = request.MaxZoom,
                            Classification = MigrationFidelityAutomationStatuses.ManualReview,
                            Code = ImportCompatibilityCodes.OgcWmtsTileCacheSkippedManualReview,
                            Warnings = ["Slice-3 plan entry was not classified as automated; tile-set export skipped."]
                        });
                        tileSetsSkipped++;
                        continue;
                    }

                    if (request.MaxZoom > OgcTileCacheExportSafetyLimits.MaxAutomatedZoomLevel)
                    {
                        perTileSet.Add(new OgcTileCacheExportedTileSet
                        {
                            LayerIdentifier = layerResource.Name,
                            TileMatrixSetIdentifier = tileSetEntry.Name ?? tileSetEntry.SourceId,
                            MinZoom = request.MinZoom,
                            MaxZoom = request.MaxZoom,
                            Classification = MigrationFidelityAutomationStatuses.ManualReview,
                            Code = ImportCompatibilityCodes.OgcWmtsTileCacheZoomThresholdExceeded,
                            Warnings =
                            [
                                string.Create(CultureInfo.InvariantCulture,
                                    $"Requested max zoom {request.MaxZoom} exceeds the slice-4 safety threshold of {OgcTileCacheExportSafetyLimits.MaxAutomatedZoomLevel}; reclassify as manual review.")
                            ]
                        });
                        tileSetsSkipped++;
                        continue;
                    }

                    var projectedTileCount = ProjectTileCount(request);
                    if (projectedTileCount > OgcTileCacheExportSafetyLimits.MaxAutomatedTileCount)
                    {
                        perTileSet.Add(new OgcTileCacheExportedTileSet
                        {
                            LayerIdentifier = layerResource.Name,
                            TileMatrixSetIdentifier = tileSetEntry.Name ?? tileSetEntry.SourceId,
                            MinZoom = request.MinZoom,
                            MaxZoom = request.MaxZoom,
                            Classification = MigrationFidelityAutomationStatuses.ManualReview,
                            Code = ImportCompatibilityCodes.OgcWmtsTileCacheZoomThresholdExceeded,
                            Warnings =
                            [
                                string.Create(CultureInfo.InvariantCulture,
                                    $"Projected tile count {projectedTileCount} exceeds the slice-4 safety cap of {OgcTileCacheExportSafetyLimits.MaxAutomatedTileCount}; reclassify as manual review.")
                            ]
                        });
                        tileSetsSkipped++;
                        continue;
                    }

                    OgcTileCacheExportedTileSet outcome;
                    try
                    {
                        outcome = await ExportTileSetAsync(
                            request,
                            inventory,
                            layerResource,
                            tileSetEntry,
                            tileFormat,
                            styleIdentifier,
                            safeSourceUrl,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.TileSetExportFailed(_logger, layerResource.Name, tileSetEntry.SourceId, ex);
                        outcome = new OgcTileCacheExportedTileSet
                        {
                            LayerIdentifier = layerResource.Name,
                            TileMatrixSetIdentifier = tileSetEntry.Name ?? tileSetEntry.SourceId,
                            MinZoom = request.MinZoom,
                            MaxZoom = request.MaxZoom,
                            Classification = MigrationFidelityAutomationStatuses.ManualReview,
                            Code = ImportCompatibilityCodes.OgcWmtsTileCacheFetchFailed,
                            Warnings = [ToSafeFailureReason(ex)]
                        };
                    }

                    perTileSet.Add(outcome);
                    tilesPersisted += outcome.TilesPersisted;
                    tilesAlreadyPresent += outcome.TilesAlreadyPresent;
                    tilesFailed += outcome.TilesFailed;

                    if (string.Equals(outcome.Classification, MigrationFidelityAutomationStatuses.Automated, StringComparison.Ordinal))
                    {
                        tileSetsExported++;
                    }
                    else
                    {
                        tileSetsSkipped++;
                    }
                }
            }
        }

        stopwatch.Stop();
        Log.ExportCompleted(_logger, safeSourceUrl, tileSetsExported, tileSetsSkipped, tilesPersisted, tilesAlreadyPresent, tilesFailed, stopwatch.Elapsed.TotalSeconds);

        return new OgcTileCacheExportResult
        {
            Success = true,
            WasDryRun = request.DryRun,
            SourceServiceUrl = safeSourceUrl,
            TileSetsPlanned = tileSetsPlanned,
            TileSetsExported = tileSetsExported,
            TileSetsSkipped = tileSetsSkipped,
            TilesPersisted = tilesPersisted,
            TilesAlreadyPresent = tilesAlreadyPresent,
            TilesFailed = tilesFailed,
            TileSets = perTileSet,
            Warnings = topLevelWarnings,
            Manifest = manifest,
            Duration = stopwatch.Elapsed
        };
    }

    private async Task<OgcTileCacheExportedTileSet> ExportTileSetAsync(
        OgcTileCacheExportRequest request,
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource layerResource,
        MigrationManifestPlanEntry tileSetEntry,
        string tileFormat,
        string styleIdentifier,
        string safeSourceUrl,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var template = ResolveTileTemplate(inventory, layerResource, tileFormat);
        var descriptor = new OgcTileCacheDescriptor
        {
            LayerIdentifier = layerResource.Name,
            TileMatrixSetIdentifier = tileSetEntry.Name ?? tileSetEntry.SourceId,
            SourceServiceUrl = safeSourceUrl,
            TileFormat = tileFormat,
            StyleIdentifier = styleIdentifier,
            MinZoom = request.MinZoom,
            MaxZoom = request.MaxZoom
        };

        string? targetTileCacheId = null;
        if (!request.DryRun)
        {
            targetTileCacheId = await _sink.EnsureTileCacheAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }

        var serviceUri = new Uri(request.ServiceUrl, UriKind.Absolute);
        var inserted = 0;
        var alreadyPresent = 0;
        var failed = 0;

        for (var z = request.MinZoom; z <= request.MaxZoom; z++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (minCol, maxCol, minRow, maxRow) = ResolveWindow(request.Window, z);
            for (var x = minCol; x <= maxCol; x++)
            {
                for (var y = minRow; y <= maxRow; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var tileUri = BuildTileUri(template, serviceUri, layerResource.Name, descriptor.TileMatrixSetIdentifier, styleIdentifier, tileFormat, z, x, y);

                    if (request.DryRun)
                    {
                        continue;
                    }

                    try
                    {
                        var bytes = await FetchTileAsync(tileUri, request.RequestTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                        var status = await _sink.WriteTileAsync(new OgcTileCacheRecord
                        {
                            TileCacheId = targetTileCacheId!,
                            Z = z,
                            X = x,
                            Y = y,
                            ContentType = tileFormat,
                            Content = bytes,
                            SourceUrl = ToSafeSourceUrl(tileUri.AbsoluteUri)
                        }, cancellationToken).ConfigureAwait(false);

                        if (status == OgcTileCacheWriteStatus.Inserted)
                        {
                            inserted++;
                        }
                        else
                        {
                            alreadyPresent++;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Log.TileFetchFailed(_logger, layerResource.Name, z, x, y, ex);
                        if (warnings.Count == 0)
                        {
                            warnings.Add($"Tile fetch failed (z={z},x={x},y={y}): {ToSafeFailureReason(ex)}");
                        }

                        failed++;
                    }
                }
            }
        }

        var classification = failed == 0
            ? MigrationFidelityAutomationStatuses.Automated
            : MigrationFidelityAutomationStatuses.ManualReview;
        var code = failed == 0
            ? ImportCompatibilityCodes.OgcWmtsTileCacheExported
            : ImportCompatibilityCodes.OgcWmtsTileCacheFetchFailed;

        return new OgcTileCacheExportedTileSet
        {
            LayerIdentifier = layerResource.Name,
            TileMatrixSetIdentifier = descriptor.TileMatrixSetIdentifier,
            TargetTileCacheId = targetTileCacheId,
            MinZoom = request.MinZoom,
            MaxZoom = request.MaxZoom,
            TilesPersisted = inserted,
            TilesAlreadyPresent = alreadyPresent,
            TilesFailed = failed,
            Classification = classification,
            Code = code,
            Warnings = [.. warnings]
        };
    }

    private async Task<byte[]> FetchTileAsync(Uri tileUri, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, tileUri);
        requestMessage.Headers.Accept.Clear();
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 30 : timeoutSeconds));

        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var totalRead = 0;
        var pool = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(pool.AsMemory(0, pool.Length), timeout.Token).ConfigureAwait(false)) > 0)
        {
            totalRead += read;
            if (totalRead > MaxTileBytes)
            {
                throw new InvalidOperationException(
                    $"Tile response exceeded the slice-4 maximum tile size of {MaxTileBytes} bytes.");
            }

            await buffer.WriteAsync(pool.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static (int MinCol, int MaxCol, int MinRow, int MaxRow) ResolveWindow(OgcTileCacheTileWindow? window, int z)
    {
        var grid = checked(1 << z);
        if (window == null)
        {
            return (0, grid - 1, 0, grid - 1);
        }

        var minCol = Math.Clamp(window.MinColumn, 0, grid - 1);
        var maxCol = Math.Clamp(window.MaxColumn, 0, grid - 1);
        var minRow = Math.Clamp(window.MinRow, 0, grid - 1);
        var maxRow = Math.Clamp(window.MaxRow, 0, grid - 1);
        return (Math.Min(minCol, maxCol), Math.Max(minCol, maxCol), Math.Min(minRow, maxRow), Math.Max(minRow, maxRow));
    }

    private static int ProjectTileCount(OgcTileCacheExportRequest request)
    {
        long total = 0;
        for (var z = request.MinZoom; z <= request.MaxZoom; z++)
        {
            var (minCol, maxCol, minRow, maxRow) = ResolveWindow(request.Window, z);
            total += checked((long)(maxCol - minCol + 1) * (maxRow - minRow + 1));
            if (total > int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return (int)total;
    }

    private static string? ResolveTileTemplate(
        MigrationSourceInventoryArtifact inventory,
        MigrationInventoryResource layerResource,
        string tileFormat)
    {
        foreach (var dependency in inventory.ExternalDependencies)
        {
            if (!string.Equals(dependency.ResourceId, layerResource.Id, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(dependency.Kind, "ogc-endpoint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!dependency.Metadata.TryGetValue("operation", out var operation) ||
                !string.Equals(operation, "ResourceURL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (dependency.Metadata.TryGetValue("format", out var format) &&
                !string.IsNullOrWhiteSpace(format) &&
                !string.Equals(format, tileFormat, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dependency.Address))
            {
                return dependency.Address;
            }
        }

        return null;
    }

    private static Uri BuildTileUri(
        string? template,
        Uri serviceUri,
        string layerIdentifier,
        string tileMatrixSetIdentifier,
        string styleIdentifier,
        string tileFormat,
        int z,
        int x,
        int y)
    {
        if (!string.IsNullOrWhiteSpace(template))
        {
            var resolved = template
                .Replace("{TileMatrixSet}", tileMatrixSetIdentifier, StringComparison.Ordinal)
                .Replace("{TileMatrix}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{TileCol}", x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{TileRow}", y.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{Style}", styleIdentifier, StringComparison.Ordinal)
                .Replace("{Layer}", layerIdentifier, StringComparison.Ordinal);
            if (Uri.TryCreate(resolved, UriKind.Absolute, out var templateUri))
            {
                return templateUri;
            }
        }

        var query = string.Create(CultureInfo.InvariantCulture,
            $"service=WMTS&version=1.0.0&request=GetTile&layer={Uri.EscapeDataString(layerIdentifier)}&style={Uri.EscapeDataString(styleIdentifier)}&tilematrixset={Uri.EscapeDataString(tileMatrixSetIdentifier)}&tilematrix={z}&tilerow={y}&tilecol={x}&format={Uri.EscapeDataString(tileFormat)}");
        var builder = new UriBuilder(serviceUri) { Query = query };
        return builder.Uri;
    }

    private static IReadOnlySet<string>? ToOrdinalSet(string[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return null;
        }

        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static IReadOnlyList<MigrationManifestServicePlan> ResolveWmtsPlans(MigrationManifestArtifact manifest)
        => manifest.ServicePlans
            .Where(plan => string.Equals(plan.ServiceType, "WMTS", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static plan => plan.SourceContainerId, StringComparer.Ordinal)
            .ToArray();

    private static OgcTileCacheExportResult CreateFailureResult(
        OgcTileCacheExportRequest request,
        string safeSourceUrl,
        string errorMessage,
        TimeSpan duration,
        MigrationSourceInventoryArtifact? inventory = null)
    {
        inventory ??= new MigrationSourceInventoryArtifact
        {
            SourceKind = SourceKindOgcWmts,
            Source = new MigrationSourceIdentity
            {
                DisplayName = "WMTS",
                BaseUrl = safeSourceUrl,
                Product = "OGC Web Map Tile Service",
                ServiceType = "WMTS"
            },
            AuthPosture = new MigrationInventoryAuthPosture { Mode = "anonymous" },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "failed",
                Warnings = [errorMessage]
            },
            Summary = new MigrationInventorySummary(),
            OverallCompatibility = new MigrationCompatibilityAssessment
            {
                Level = "incompatible",
                Reason = errorMessage
            }
        };

        var manifest = MigrationManifestTranslator.Translate(inventory);
        return new OgcTileCacheExportResult
        {
            Success = false,
            WasDryRun = request.DryRun,
            SourceServiceUrl = safeSourceUrl,
            ErrorMessage = errorMessage,
            TileSets = [],
            Warnings = [errorMessage],
            Manifest = manifest,
            Duration = duration
        };
    }

    private static string ToSafeSourceUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string ToSafeFailureReason(Exception ex)
        => ex switch
        {
            HttpRequestException => "WMTS tile endpoint could not be reached or returned an error response.",
            TaskCanceledException => "WMTS tile request timed out.",
            InvalidOperationException io => io.Message,
            _ => "WMTS tile fetch failed."
        };

    private static partial class Log
    {
        [LoggerMessage(7950, LogLevel.Information,
            "Starting WMTS tile-cache export from {SafeUrl} (job {JobId}, dryRun {DryRun})")]
        public static partial void ExportStarting(ILogger logger, string safeUrl, string jobId, bool dryRun);

        [LoggerMessage(7951, LogLevel.Information,
            "WMTS tile-cache export complete for {SafeUrl}: exported {Exported}, skipped {Skipped}, tiles inserted {Inserted}, already-present {AlreadyPresent}, failed {Failed} in {ElapsedSeconds:F1}s")]
        public static partial void ExportCompleted(ILogger logger, string safeUrl, int exported, int skipped, int inserted, int alreadyPresent, int failed, double elapsedSeconds);

        [LoggerMessage(7952, LogLevel.Warning, "WMTS tile-cache inventory scan failed for {SafeUrl}")]
        public static partial void InventoryScanFailed(ILogger logger, string safeUrl, Exception exception);

        [LoggerMessage(7953, LogLevel.Warning,
            "WMTS tile-set export failed for layer {Layer} tile-matrix-set {TileMatrixSet}")]
        public static partial void TileSetExportFailed(ILogger logger, string layer, string tileMatrixSet, Exception exception);

        [LoggerMessage(7954, LogLevel.Warning,
            "WMTS tile fetch failed for layer {Layer} z={Zoom} x={Col} y={Row}")]
        public static partial void TileFetchFailed(ILogger logger, string layer, int zoom, int col, int row, Exception exception);
    }
}
