// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

// image/tiff, image/geotiff, and image/tiff parameters are ingested; every other format is manual review before any bytes are downloaded.
public sealed partial class OgcWcsImportService : IOgcWcsImportService
{
    private const string DefaultOutputFormat = "image/tiff";
    private const string DefaultVersion = "2.0.1";

    private readonly IOgcCoverageImportService _coverageImportService;
    private readonly ILogger<OgcWcsImportService> _logger;

    public OgcWcsImportService(
        IOgcCoverageImportService coverageImportService,
        ILogger<OgcWcsImportService> logger)
    {
        _coverageImportService = coverageImportService
            ?? throw new ArgumentNullException(nameof(coverageImportService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OgcWcsImportResult> ImportAsync(
        OgcWcsImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inventory);

        if (string.IsNullOrWhiteSpace(request.ServiceUrl))
        {
            throw new ArgumentException("ServiceUrl is required.", nameof(request));
        }

        var resolvedVersion = NormalizeVersion(request.Version);
        var negotiatedFormat = NormalizeOutputFormat(request.OutputFormat);
        var isAutomatedFormat = IsGeoTiffFormat(negotiatedFormat);

        // When the caller requested a non-GeoTIFF output format we rewrite the
        // inventory so every otherwise-automatable coverage is downgraded to
        // manual-review. Slice-2 only knows how to ingest GeoTIFF/COG bytes.
        var inventory = isAutomatedFormat
            ? request.Inventory
            : DowngradeAutomatedCoveragesToManualReview(request.Inventory, negotiatedFormat);

        if (!isAutomatedFormat)
        {
            Log.NonGeoTiffFormatDowngraded(_logger, negotiatedFormat);
        }

        var coverageRequest = new OgcCoverageImportRequest
        {
            ServiceType = "WCS",
            ServiceUrl = request.ServiceUrl,
            Version = resolvedVersion,
            Inventory = inventory,
            CoverageSelection = request.CoverageSelection,
            Targets = request.Targets,
            ApplyMode = request.ApplyMode,
            DryRun = request.DryRun,
            TargetServiceName = request.TargetServiceName,
            Username = request.Username,
            Password = request.Password,
            TimeoutSeconds = request.TimeoutSeconds,
            AllowUnsafeLocalUrls = request.AllowUnsafeLocalUrls
        };

        var coverageResult = await _coverageImportService
            .ImportAsync(coverageRequest, cancellationToken)
            .ConfigureAwait(false);

        return new OgcWcsImportResult
        {
            Manifest = coverageResult.Manifest,
            Records = coverageResult.Records,
            RequestedOutputFormat = negotiatedFormat,
            ResolvedVersion = resolvedVersion,
            ApplyMode = coverageResult.ApplyMode,
            DryRun = coverageResult.DryRun,
            StyleDiagnostics = coverageResult.StyleDiagnostics
        };
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return DefaultVersion;
        }

        var trimmed = version.Trim();
        if (!trimmed.StartsWith("1.", StringComparison.Ordinal) &&
            !trimmed.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported WCS version: {version}. Expected 1.x or 2.x.",
                nameof(version));
        }

        return trimmed;
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
        {
            return DefaultOutputFormat;
        }

        return outputFormat.Trim();
    }

    private static bool IsGeoTiffFormat(string negotiatedFormat)
    {
        // Accept the common GeoTIFF MIME variants WCS servers advertise.
        if (negotiatedFormat.Equals("image/tiff", StringComparison.OrdinalIgnoreCase) ||
            negotiatedFormat.Equals("image/geotiff", StringComparison.OrdinalIgnoreCase) ||
            negotiatedFormat.Equals("image/tiff;application=geotiff", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return negotiatedFormat.StartsWith("image/tiff;", StringComparison.OrdinalIgnoreCase);
    }

    private static MigrationSourceInventoryArtifact DowngradeAutomatedCoveragesToManualReview(
        MigrationSourceInventoryArtifact inventory,
        string requestedFormat)
    {
        var manualReason = $"WCS import requested output format '{requestedFormat}', which is not supported by the slice-2 GeoTIFF/COG ingestion pipeline. Resolve manually before promoting.";
        var manualSteps = new[]
        {
            $"Output format '{requestedFormat}' is not currently ingestible. Convert the coverage to GeoTIFF before re-running the import, or request a GeoTIFF response from the WCS server.",
            "Confirm the source service can serve image/tiff for this coverage and re-run with outputFormat=image/tiff."
        };

        var rewritten = inventory.Resources
            .Select(resource =>
            {
                if (!string.Equals(resource.Kind, "coverage", StringComparison.Ordinal))
                {
                    return resource;
                }

                return resource with
                {
                    Compatibility = resource.Compatibility with
                    {
                        Level = "incompatible",
                        Code = OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported,
                        Reason = manualReason,
                        ManualSteps = manualSteps
                    }
                };
            })
            .ToArray();

        return inventory with { Resources = rewritten };
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 8820,
            Level = LogLevel.Information,
            Message = "WCS import: requested output format '{Format}' is not GeoTIFF; routing coverages to manual review.")]
        public static partial void NonGeoTiffFormatDowngraded(ILogger logger, string format);
    }
}
