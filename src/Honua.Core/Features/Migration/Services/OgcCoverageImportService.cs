// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.Logging;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Imports OGC WCS / OGC API Coverages GeoTIFF and Cloud Optimized GeoTIFF coverages
/// into Honua's raster store using a slice-1 migration source inventory as the
/// deterministic source-of-truth selection.
/// </summary>
public sealed partial class OgcCoverageImportService : IOgcCoverageImportService
{
    private const string GeoTiffFormat = "GeoTIFF";
    private const string CogFormat = "CloudOptimizedGeoTIFF";

    private readonly HttpClient _httpClient;
    private readonly IRasterImportService _rasterImportService;
    private readonly ILogger<OgcCoverageImportService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OgcCoverageImportService"/> class.
    /// </summary>
    public OgcCoverageImportService(
        HttpClient httpClient,
        IRasterImportService rasterImportService,
        ILogger<OgcCoverageImportService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _rasterImportService = rasterImportService ?? throw new ArgumentNullException(nameof(rasterImportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<OgcCoverageImportResult> ImportAsync(
        OgcCoverageImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inventory);

        var serviceType = NormalizeServiceType(request.ServiceType);
        var serviceUri = ValidateServiceUri(request.ServiceUrl, request.AllowUnsafeLocalUrls);
        var applyMode = request.ApplyMode && !request.DryRun;

        var inventoryResources = request.Inventory.Resources
            .Where(static resource => string.Equals(resource.Kind, "coverage", StringComparison.Ordinal))
            .ToArray();
        var selectedResources = SelectResources(inventoryResources, request.CoverageSelection);
        var styleDiagnostics = CoverageStyleDiagnosticBuilder.Build(selectedResources);

        var targets = new List<MigrationManifestTargetResource>();
        var manualReviewItems = new List<MigrationManifestReviewItem>();
        var unsupportedItems = new List<MigrationManifestReviewItem>();
        var identityRemaps = new List<MigrationManifestIdentityRemap>();
        var records = new List<OgcCoverageImportRecord>();

        var targetServiceName = NormalizeTargetServiceName(
            request.TargetServiceName,
            request.Inventory.Source.DisplayName);

        foreach (var resource in selectedResources.OrderBy(static r => r.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var classification = ClassifyResource(resource);
            var sourceCoverageId = resource.Name;
            var target = LookupTarget(request.Targets, resource);
            var targetName = NormalizeTargetName(
                target?.Name,
                resource.Title,
                resource.Name);
            var targetResourceId = $"target:{targetServiceName}:{ToStableId(targetName)}";
            var declaredCrs = resource.SpatialReferences
                .Select(static sr => sr.SourceValue ?? sr.CrsUri ?? (sr.Srid is { } srid ? $"EPSG:{srid}" : string.Empty))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();
            var declaredBands = resource.Fields
                .Select(static field => field.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray();

            identityRemaps.Add(new MigrationManifestIdentityRemap
            {
                SourceId = resource.Id,
                SourceKind = resource.Kind,
                TargetId = targetResourceId,
                TargetKind = "raster-coverage",
                TargetName = targetName,
                Action = classification.Action
            });

            if (!classification.IsAutomated)
            {
                var review = new MigrationManifestReviewItem
                {
                    SourceId = resource.Id,
                    Kind = resource.Kind,
                    Code = classification.Code,
                    Severity = classification.Severity,
                    Reason = classification.Reason,
                    ManualSteps = classification.ManualSteps,
                    Warnings = resource.Compatibility.Warnings
                };

                if (string.Equals(classification.Severity, "unsupported", StringComparison.Ordinal))
                {
                    unsupportedItems.Add(review);
                }
                else
                {
                    manualReviewItems.Add(review);
                }

                records.Add(new OgcCoverageImportRecord
                {
                    SourceCoverageId = sourceCoverageId,
                    TargetCoverageName = targetName,
                    OutputFormat = classification.OutputFormat,
                    Classification = classification.Severity,
                    Action = "manual-review",
                    DeclaredCrs = declaredCrs,
                    DeclaredBands = declaredBands,
                    Warnings = classification.ManualSteps
                });

                targets.Add(new MigrationManifestTargetResource
                {
                    SourceResourceId = resource.Id,
                    SourceKind = resource.Kind,
                    Action = "manual-review",
                    TargetResourceId = targetResourceId,
                    TargetServiceName = targetServiceName,
                    TargetResourceName = targetName,
                    MigrationMode = "raster-coverage-import",
                    SourceProtocol = serviceType,
                    Capabilities = resource.Capabilities,
                    SpatialReferences = resource.SpatialReferences,
                    Fields = resource.Fields,
                    ExternalDependencyIds = resource.ExternalDependencyIds,
                    Compatibility = resource.Compatibility
                });

                continue;
            }

            // Automated path — GeoTIFF/COG.
            var manifestTarget = new MigrationManifestTargetResource
            {
                SourceResourceId = resource.Id,
                SourceKind = resource.Kind,
                Action = applyMode ? "imported" : "planned",
                TargetResourceId = targetResourceId,
                TargetServiceName = targetServiceName,
                TargetResourceName = targetName,
                MigrationMode = "raster-coverage-import",
                SourceProtocol = serviceType,
                Capabilities = resource.Capabilities,
                SpatialReferences = resource.SpatialReferences,
                Fields = resource.Fields,
                ExternalDependencyIds = resource.ExternalDependencyIds,
                Compatibility = resource.Compatibility
            };

            if (!applyMode)
            {
                targets.Add(manifestTarget);
                records.Add(new OgcCoverageImportRecord
                {
                    SourceCoverageId = sourceCoverageId,
                    TargetCoverageName = targetName,
                    OutputFormat = classification.OutputFormat,
                    Classification = classification.Severity,
                    Action = "planned",
                    DeclaredCrs = declaredCrs,
                    DeclaredBands = declaredBands,
                    TargetLayerId = target?.LayerId
                });
                continue;
            }

            // Apply mode — actually download and import.
            if (target?.LayerId == null)
            {
                var warning = $"Coverage {sourceCoverageId} cannot be imported: no target layer was supplied.";
                manualReviewItems.Add(new MigrationManifestReviewItem
                {
                    SourceId = resource.Id,
                    Kind = resource.Kind,
                    Code = "OGC_COVERAGE_TARGET_LAYER_MISSING",
                    Severity = "manual-review",
                    Reason = warning,
                    ManualSteps = ["Provide targets[<coverage>].layerId before applying this coverage import."]
                });
                records.Add(new OgcCoverageImportRecord
                {
                    SourceCoverageId = sourceCoverageId,
                    TargetCoverageName = targetName,
                    OutputFormat = classification.OutputFormat,
                    Classification = classification.Severity,
                    Action = "skipped",
                    DeclaredCrs = declaredCrs,
                    DeclaredBands = declaredBands,
                    Warnings = [warning]
                });
                targets.Add(manifestTarget with { Action = "manual-review" });
                continue;
            }

            var record = await DownloadAndImportAsync(
                    request,
                    serviceUri,
                    serviceType,
                    resource,
                    classification,
                    target,
                    targetName,
                    declaredCrs,
                    declaredBands,
                    cancellationToken)
                .ConfigureAwait(false);
            records.Add(record);
            targets.Add(manifestTarget with
            {
                Action = record.Action switch
                {
                    "imported" => "imported",
                    "failed" => "failed",
                    _ => manifestTarget.Action
                }
            });
        }

        var summary = new MigrationManifestSummary
        {
            SourceResourceCount = inventoryResources.Length,
            TargetResourceCount = targets.Count,
            ManualReviewCount = manualReviewItems.Count,
            UnsupportedCount = unsupportedItems.Count
        };

        var manifest = new MigrationManifestArtifact
        {
            SourceKind = request.Inventory.SourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = request.Inventory.Source.DisplayName,
                BaseUrl = ToSafeUrl(serviceUri),
                Product = request.Inventory.Source.Product,
                Version = request.Inventory.Source.Version,
                Build = request.Inventory.Source.Build,
                ServiceType = request.Inventory.Source.ServiceType
            },
            Summary = summary,
            TargetResources = targets
                .OrderBy(static t => t.TargetResourceId, StringComparer.Ordinal)
                .ToArray(),
            IdentityRemaps = identityRemaps
                .OrderBy(static remap => remap.SourceId, StringComparer.Ordinal)
                .ToArray(),
            ManualReviewItems = manualReviewItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ToArray(),
            UnsupportedItems = unsupportedItems
                .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
                .ToArray()
        };

        return new OgcCoverageImportResult
        {
            Manifest = manifest,
            Records = records
                .OrderBy(static record => record.SourceCoverageId, StringComparer.Ordinal)
                .ToArray(),
            ApplyMode = applyMode,
            DryRun = !applyMode,
            StyleDiagnostics = styleDiagnostics
        };
    }

    private async Task<OgcCoverageImportRecord> DownloadAndImportAsync(
        OgcCoverageImportRequest request,
        Uri serviceUri,
        string serviceType,
        MigrationInventoryResource resource,
        CoverageClassification classification,
        OgcCoverageImportTarget target,
        string targetName,
        string[] declaredCrs,
        string[] declaredBands,
        CancellationToken cancellationToken)
    {
        var sourceCoverageId = resource.Name;
        var layerId = target.LayerId!.Value;
        var warnings = new List<string>();
        string? stagedFile = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)));

            var coverageUri = BuildGetCoverageUri(serviceUri, serviceType, sourceCoverageId, request.Version, classification.OutputFormat);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, coverageUri);
            if (!string.IsNullOrWhiteSpace(request.Username))
            {
                var raw = $"{request.Username}:{request.Password ?? string.Empty}";
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }

            using var response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            stagedFile = await StageGeoTiffAsync(response, sourceCoverageId, timeout.Token).ConfigureAwait(false);
            var byteCount = new FileInfo(stagedFile).Length;

            var importRequest = new RasterImportRequest
            {
                LayerId = layerId,
                Name = targetName,
                Description = target.Description ?? resource.Description,
                FilePath = stagedFile,
                FileName = $"{ToStableId(targetName)}.tif",
                Format = classification.OutputFormat == CogFormat
                    ? SupportedRasterFormat.CloudOptimizedGeoTiff
                    : SupportedRasterFormat.GeoTiff,
                TileZoomLevels = []
            };

            var importResult = await _rasterImportService
                .ImportAsync(importRequest, progress: null, timeout.Token)
                .ConfigureAwait(false);

            if (!importResult.Success)
            {
                return new OgcCoverageImportRecord
                {
                    SourceCoverageId = sourceCoverageId,
                    TargetCoverageName = targetName,
                    OutputFormat = classification.OutputFormat,
                    Classification = classification.Severity,
                    Action = "failed",
                    DeclaredCrs = declaredCrs,
                    DeclaredBands = declaredBands,
                    ByteCount = byteCount,
                    TargetLayerId = layerId,
                    Warnings = importResult.Warnings.Concat(warnings).ToArray(),
                    ErrorMessage = importResult.ErrorMessage
                };
            }

            warnings.AddRange(importResult.Warnings);
            return new OgcCoverageImportRecord
            {
                SourceCoverageId = sourceCoverageId,
                TargetCoverageName = targetName,
                OutputFormat = classification.OutputFormat,
                Classification = classification.Severity,
                Action = "imported",
                DeclaredCrs = declaredCrs,
                DeclaredBands = declaredBands,
                ByteCount = byteCount,
                RasterId = importResult.RasterId,
                TargetLayerId = layerId,
                Warnings = warnings.ToArray()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            Log.CoverageDownloadFailed(_logger, sourceCoverageId, ex);
            return new OgcCoverageImportRecord
            {
                SourceCoverageId = sourceCoverageId,
                TargetCoverageName = targetName,
                OutputFormat = classification.OutputFormat,
                Classification = classification.Severity,
                Action = "failed",
                DeclaredCrs = declaredCrs,
                DeclaredBands = declaredBands,
                TargetLayerId = layerId,
                ErrorMessage = "Failed to download or import coverage."
            };
        }
        finally
        {
            if (stagedFile != null)
            {
                TryDelete(stagedFile);
            }
        }
    }

    private static async Task<string> StageGeoTiffAsync(
        HttpResponseMessage response,
        string sourceCoverageId,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "honua-ogc-coverage-import");
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tif");
        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(path);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (destination.Length == 0)
            {
                throw new InvalidDataException($"GetCoverage response was empty for {sourceCoverageId}.");
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static MigrationInventoryResource[] SelectResources(
        MigrationInventoryResource[] resources,
        string[] selection)
    {
        if (selection.Length == 0)
        {
            return resources;
        }

        var normalized = new HashSet<string>(
            selection.Where(static s => !string.IsNullOrWhiteSpace(s)).Select(static s => s.Trim()),
            StringComparer.Ordinal);

        return resources
            .Where(resource => normalized.Contains(resource.Id) || normalized.Contains(resource.Name))
            .ToArray();
    }

    private static OgcCoverageImportTarget? LookupTarget(
        IReadOnlyDictionary<string, OgcCoverageImportTarget> targets,
        MigrationInventoryResource resource)
    {
        if (targets.TryGetValue(resource.Name, out var target))
        {
            return target;
        }

        return targets.TryGetValue(resource.Id, out target) ? target : null;
    }

    private static string NormalizeTargetServiceName(string? candidate, string? fallback)
        => ToStableId(candidate ?? fallback ?? "raster-coverages");

    private static string NormalizeTargetName(string? explicitName, string? title, string fallback)
        => ToStableId(explicitName ?? title ?? fallback);

    private static CoverageClassification ClassifyResource(MigrationInventoryResource resource)
    {
        var code = resource.Compatibility.Code ?? OgcCoverageMigrationCompatibilityCodes.ManualReview;
        return code switch
        {
            OgcCoverageMigrationCompatibilityCodes.CogSupported => new CoverageClassification(
                IsAutomated: true,
                Severity: "automated",
                Action: "import",
                OutputFormat: CogFormat,
                Code: code,
                Reason: resource.Compatibility.Reason,
                ManualSteps: []),
            OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported => new CoverageClassification(
                IsAutomated: true,
                Severity: "automated",
                Action: "import",
                OutputFormat: GeoTiffFormat,
                Code: code,
                Reason: resource.Compatibility.Reason,
                ManualSteps: []),
            OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported
                or OgcCoverageMigrationCompatibilityCodes.HdfUnsupported
                or OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported
                or OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported => new CoverageClassification(
                    IsAutomated: false,
                    Severity: "unsupported",
                    Action: "manual-review",
                    OutputFormat: GuessFormatFromCode(code),
                    Code: code,
                    Reason: resource.Compatibility.Reason,
                    ManualSteps: BuildManualSteps(code)),
            _ => new CoverageClassification(
                IsAutomated: false,
                Severity: "manual-review",
                Action: "manual-review",
                OutputFormat: GeoTiffFormat,
                Code: code,
                Reason: resource.Compatibility.Reason,
                ManualSteps: resource.Compatibility.ManualSteps.Length > 0
                    ? resource.Compatibility.ManualSteps
                    : ["Run pilot coverage import and verify metadata, CRS, and band parity before promotion."])
        };
    }

    private static string GuessFormatFromCode(string code) => code switch
    {
        OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported => "NetCDF",
        OgcCoverageMigrationCompatibilityCodes.HdfUnsupported => "HDF",
        OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported => "Zarr",
        _ => "Unknown"
    };

    private static string[] BuildManualSteps(string code) => code switch
    {
        OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported =>
        [
            "NetCDF coverage import depends on multidimensional format support (issue #1010). Track that issue before automating this coverage."
        ],
        OgcCoverageMigrationCompatibilityCodes.HdfUnsupported =>
        [
            "HDF coverage import depends on multidimensional format support (issue #1010). Track that issue before automating this coverage."
        ],
        OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported =>
        [
            "Zarr coverage import depends on the Zarr ingest pipeline (issue #1009). Track that issue before automating this coverage."
        ],
        _ =>
        [
            "Coverage output format is not supported by the slice-2 GeoTIFF/COG import path. Re-evaluate after additional format pipelines land."
        ]
    };

    private static Uri BuildGetCoverageUri(
        Uri serviceUri,
        string serviceType,
        string coverageId,
        string? version,
        string outputFormat)
    {
        if (string.Equals(serviceType, "OGC API Coverages", StringComparison.Ordinal))
        {
            var builder = new UriBuilder(serviceUri);
            var basePath = builder.Path.TrimEnd('/');
            if (basePath.EndsWith("/collections", StringComparison.OrdinalIgnoreCase))
            {
                basePath = basePath[..^"/collections".Length];
            }

            builder.Path = $"{basePath}/collections/{Uri.EscapeDataString(coverageId)}/coverage";
            builder.Query = BuildQuery(
                builder.Query,
                new Dictionary<string, string?>
                {
                    ["f"] = outputFormat == CogFormat ? "tif" : "geotiff"
                });
            return builder.Uri;
        }

        // WCS path (1.x or 2.x). Default to 2.0.1 semantics when version is unknown.
        var resolvedVersion = string.IsNullOrWhiteSpace(version) || version!.StartsWith("2.", StringComparison.Ordinal)
            ? version ?? "2.0.1"
            : version;
        var isWcs2 = resolvedVersion.StartsWith("2.", StringComparison.Ordinal);
        var wcsBuilder = new UriBuilder(serviceUri)
        {
            Query = BuildQuery(
                serviceUri.Query,
                new Dictionary<string, string?>
                {
                    ["service"] = "WCS",
                    ["version"] = resolvedVersion,
                    ["request"] = "GetCoverage",
                    [isWcs2 ? "coverageId" : "coverage"] = coverageId,
                    ["format"] = outputFormat == CogFormat ? "image/tiff;application=geotiff;profile=cloud-optimized" : "image/tiff"
                })
        };
        return wcsBuilder.Uri;
    }

    private static string BuildQuery(string existingQuery, IReadOnlyDictionary<string, string?> values)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var trimmed = existingQuery.TrimStart('?');
        if (trimmed.Length > 0)
        {
            foreach (var part in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? Uri.UnescapeDataString(part) : Uri.UnescapeDataString(part[..separator]);
                if (values.Keys.Any(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var value = separator < 0 ? string.Empty : Uri.UnescapeDataString(part[(separator + 1)..]);
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        foreach (var (key, value) in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                pairs.Add(new KeyValuePair<string, string>(key, value));
            }
        }

        return string.Join("&", pairs.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    [SuppressMessage("Performance", "CA1308:Normalize strings to uppercase", Justification = "Stable ASCII slug for manifest target identities.")]
    private static string ToStableId(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else
            {
                if (sb.Length > 0 && !lastWasSeparator)
                {
                    sb.Append('-');
                    lastWasSeparator = true;
                }
            }
        }

        while (sb.Length > 0 && sb[^1] == '-')
        {
            sb.Length--;
        }

        return sb.Length == 0 ? "coverage" : sb.ToString();
    }

    private static string NormalizeServiceType(string serviceType)
    {
        if (string.IsNullOrWhiteSpace(serviceType))
        {
            throw new ArgumentException("ServiceType is required.", nameof(serviceType));
        }

        var normalized = serviceType.Trim();
        if (normalized.Equals("WCS", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Web Coverage Service", StringComparison.OrdinalIgnoreCase))
        {
            return "WCS";
        }

        if (normalized.Equals("OGC API Coverages", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("OGC API - Coverages", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Coverages", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ogc-api-coverages", StringComparison.OrdinalIgnoreCase))
        {
            return "OGC API Coverages";
        }

        throw new ArgumentException($"Unsupported coverage service type: {serviceType}", nameof(serviceType));
    }

    private static Uri ValidateServiceUri(string serviceUrl, bool allowUnsafeLocalUrls)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Coverage service URL must be an absolute HTTP or HTTPS URL.", nameof(serviceUrl));
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Coverage service URL must not include embedded credentials.", nameof(serviceUrl));
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !allowUnsafeLocalUrls)
        {
            throw new ArgumentException("Coverage service URL must be HTTPS unless allowUnsafeLocalUrls is true.", nameof(serviceUrl));
        }

        return uri;
    }

    private static string ToSafeUrl(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.ToString();
    }

    private readonly record struct CoverageClassification(
        bool IsAutomated,
        string Severity,
        string Action,
        string OutputFormat,
        string Code,
        string Reason,
        string[] ManualSteps);

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8810,
            Level = LogLevel.Warning,
            Message = "OGC coverage import: failed to download or stage coverage {SourceCoverageId}.")]
        public static partial void CoverageDownloadFailed(ILogger logger, string sourceCoverageId, Exception exception);
    }
}
