// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Builds deterministic migration inventory artifacts for OGC coverage service sources.
/// </summary>
public sealed partial class OgcCoverageMigrationInventoryScanner
{
    private readonly ICrsRegistry _crsRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="OgcCoverageMigrationInventoryScanner"/> class.
    /// </summary>
    /// <param name="crsRegistry">CRS registry used to normalize advertised coverage references.</param>
    public OgcCoverageMigrationInventoryScanner(ICrsRegistry crsRegistry)
    {
        _crsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
    }

    /// <summary>
    /// Builds a migration source inventory artifact from structured coverage service metadata.
    /// </summary>
    /// <param name="request">Coverage service scan request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A deterministic source inventory artifact.</returns>
    public async Task<MigrationSourceInventoryArtifact> ScanAsync(
        OgcCoverageServiceScanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var serviceKind = NormalizeServiceType(request.ServiceType);
        var sourceKind = serviceKind == "WCS" ? "ogc-wcs" : "ogc-api-coverages";
        var product = FirstNonBlank(
            request.ServiceMetadata.Product,
            serviceKind == "WCS" ? "OGC Web Coverage Service" : "OGC API Coverages")!;
        var serviceUri = ValidateServiceUri(request.ServiceUrl);
        var version = FirstNonBlank(request.Version) ?? "unknown";
        var displayName = FirstNonBlank(request.ServiceMetadata.Title, serviceUri.Host) ?? product;
        var containerId = $"service:{sourceKind}";
        var capabilities = BuildCoverageCapabilities(serviceKind);
        var warnings = new List<string>();
        var missingArtifacts = new List<string>();
        var dependencies = new List<MigrationExternalDependency>
        {
            BuildServiceEndpointDependency(containerId, serviceKind, serviceUri, version),
            BuildServiceMetadataDependency(containerId, request, sourceKind, product, version)
        };

        var resources = new List<MigrationInventoryResource>(request.Coverages.Length);
        foreach (var coverage in request.Coverages.OrderBy(static item => item.CoverageId, StringComparer.Ordinal))
        {
            var resource = await BuildCoverageResourceAsync(
                    containerId,
                    coverage,
                    serviceKind,
                    capabilities,
                    dependencies,
                    warnings,
                    missingArtifacts,
                    cancellationToken)
                .ConfigureAwait(false);
            resources.Add(resource);
        }

        if (resources.Count == 0)
        {
            warnings.Add("No coverage resources were advertised by the source service.");
        }

        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = containerId,
                Kind = "ogc-coverage-service",
                Name = serviceKind,
                Title = displayName,
                Description = request.ServiceMetadata.Description,
                IsDefault = true,
                Compatibility = MigrationInventoryHelpers.Aggregate(
                    resources.Select(static resource => resource.Compatibility)
                        .Concat(dependencies.Select(static dependency => dependency.Compatibility)),
                    "No OGC coverage inventory items were discovered.")
            }
        };

        var orderedDependencies = dependencies
            .OrderBy(static dependency => dependency.Id, StringComparer.Ordinal)
            .ToArray();
        var orderedResources = resources
            .OrderBy(static resource => resource.Id, StringComparer.Ordinal)
            .ToArray();
        var summary = MigrationInventoryHelpers.BuildSummary(containers, orderedResources, [], orderedDependencies);
        var overallCompatibility = MigrationInventoryHelpers.Aggregate(
            containers.Select(static container => container.Compatibility)
                .Concat(orderedResources.Select(static resource => resource.Compatibility))
                .Concat(orderedDependencies.Select(static dependency => dependency.Compatibility)),
            "No OGC coverage inventory items were discovered.");
        var fidelityClassifications = BuildFidelityClassifications(orderedResources, orderedDependencies);

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = sourceKind,
            Source = new MigrationSourceIdentity
            {
                DisplayName = displayName,
                BaseUrl = serviceUri.ToString(),
                Product = product,
                Version = version,
                ServiceType = serviceKind
            },
            AuthPosture = BuildAuthPosture(request.ServiceMetadata, request.Coverages),
            ScanCompleteness = MigrationInventoryHelpers.BuildCompleteness(
                warnings.Count == 0 ? "complete" : "partial",
                warnings,
                missingArtifacts),
            Summary = summary,
            OverallCompatibility = overallCompatibility,
            Containers = containers,
            Resources = orderedResources,
            ExternalDependencies = orderedDependencies,
            FidelityClassifications = fidelityClassifications
        };
    }

    private static MigrationFidelityClassificationRecord[] BuildFidelityClassifications(
        MigrationInventoryResource[] resources,
        MigrationExternalDependency[] dependencies)
    {
        var records = new List<MigrationFidelityClassificationRecord>();
        foreach (var resource in resources)
        {
            var relatedDependencies = dependencies
                .Where(dependency => string.Equals(dependency.ResourceId, resource.Id, StringComparison.Ordinal))
                .Select(static dependency => dependency.Id)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            records.Add(new MigrationFidelityClassificationRecord
            {
                Id = $"fidelity:{resource.Id}:metadata",
                SourceId = resource.Id,
                Kind = resource.Kind,
                Category = "coverage-metadata",
                Name = resource.Name,
                AutomationStatus = IsCoverageAutomated(resource.Compatibility)
                    ? MigrationFidelityAutomationStatuses.Assisted
                    : ToFidelityStatus(resource.Compatibility),
                Code = resource.Compatibility.Code ?? OgcCoverageMigrationCompatibilityCodes.ManualReview,
                Reason = "Coverage service metadata, CRS, output format, subset axis, range, band, no-data, and temporal facts were captured for parity review.",
                TargetKind = "raster-coverage",
                ManualSteps = IsCoverageAutomated(resource.Compatibility)
                    ? ["Run pilot coverage import and compare metadata, bbox/subsets, CRS axis order, sample window pixels, no-data values, band/range metadata, output format, and target WCS/OGC API Coverages/ImageServer exposure."]
                    : resource.Compatibility.ManualSteps,
                RelatedIds = relatedDependencies
            });
        }

        foreach (var dependency in dependencies.Where(static item => item.Kind == "coverage-output-format"))
        {
            records.Add(new MigrationFidelityClassificationRecord
            {
                Id = $"fidelity:{dependency.Id}:format",
                SourceId = dependency.Id,
                Kind = dependency.Kind,
                Category = "coverage-output-format",
                Name = dependency.Name,
                AutomationStatus = IsCoverageAutomated(dependency.Compatibility)
                    ? MigrationFidelityAutomationStatuses.Assisted
                    : ToFidelityStatus(dependency.Compatibility),
                Code = dependency.Compatibility.Code ?? OgcCoverageMigrationCompatibilityCodes.ManualReview,
                Reason = dependency.Compatibility.Reason,
                ManualSteps = dependency.Compatibility.ManualSteps,
                RelatedIds = dependency.ResourceId == null ? [] : [dependency.ResourceId],
                Metadata = new Dictionary<string, string>(dependency.Metadata, StringComparer.Ordinal)
            });
        }

        return records
            .OrderBy(static record => record.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsCoverageAutomated(MigrationCompatibilityAssessment compatibility)
        => compatibility.Code is OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported or OgcCoverageMigrationCompatibilityCodes.CogSupported;

    private static string ToFidelityStatus(MigrationCompatibilityAssessment compatibility)
        => compatibility.Level switch
        {
            "compatible" => MigrationFidelityAutomationStatuses.Automated,
            "partial" => MigrationFidelityAutomationStatuses.ManualReview,
            "incompatible" => MigrationFidelityAutomationStatuses.Unsupported,
            _ => MigrationFidelityAutomationStatuses.ManualReview
        };

    private async Task<MigrationInventoryResource> BuildCoverageResourceAsync(
        string containerId,
        OgcCoverageDescription coverage,
        string serviceKind,
        string[] capabilities,
        List<MigrationExternalDependency> dependencies,
        List<string> warnings,
        List<string> missingArtifacts,
        CancellationToken cancellationToken)
    {
        var resourceId = $"coverage:{ToStableId(coverage.CoverageId)}";
        var spatialReferences = await BuildSpatialReferencesAsync(coverage.Crs, cancellationToken).ConfigureAwait(false);
        var formatDependencies = BuildFormatDependencies(containerId, resourceId, coverage).ToArray();
        var axisDependencies = BuildAxisDependencies(containerId, resourceId, coverage).ToArray();
        var rangeDependencies = BuildRangeDependencies(containerId, resourceId, coverage).ToArray();
        var temporalDependencies = BuildTemporalDependencies(containerId, resourceId, coverage).ToArray();
        var compatibility = BuildCoverageCompatibility(coverage, formatDependencies, temporalDependencies);

        dependencies.AddRange(formatDependencies);
        dependencies.AddRange(axisDependencies);
        dependencies.AddRange(rangeDependencies);
        dependencies.AddRange(temporalDependencies);

        if (coverage.OutputFormats.Length == 0)
        {
            missingArtifacts.Add($"output-format:{coverage.CoverageId}");
            warnings.Add($"Coverage {coverage.CoverageId} did not advertise output formats.");
        }

        return new MigrationInventoryResource
        {
            Id = resourceId,
            ContainerId = containerId,
            Kind = "coverage",
            Name = coverage.CoverageId,
            Title = coverage.Title,
            Description = coverage.Description,
            Capabilities = capabilities,
            SpatialReferences = spatialReferences,
            Fields = coverage.Ranges
                .Where(static range => !string.IsNullOrWhiteSpace(range.Name))
                .OrderBy(static range => range.Name, StringComparer.Ordinal)
                .Select(static range => new MigrationInventoryField
                {
                    Name = range.Name,
                    Alias = range.Label,
                    FieldType = FirstNonBlank(range.DataType, "coverage-range")!,
                    Nullable = true,
                    DomainType = range.Interpretation,
                    DomainName = range.Unit
                })
                .ToArray(),
            ExternalDependencyIds = formatDependencies
                .Concat(axisDependencies)
                .Concat(rangeDependencies)
                .Concat(temporalDependencies)
                .Select(static dependency => dependency.Id)
                .OrderBy(static value => value, StringComparer.Ordinal)
                .ToArray(),
            Compatibility = compatibility
        };
    }

    private static MigrationExternalDependency BuildServiceEndpointDependency(
        string containerId,
        string serviceKind,
        Uri serviceUri,
        string version)
    {
        var operation = serviceKind == "WCS" ? "GetCapabilities" : "Landing Page";
        return new MigrationExternalDependency
        {
            Id = $"endpoint:{serviceKind.ToLowerInvariant().Replace(' ', '-')}",
            ContainerId = containerId,
            Kind = "ogc-coverage-endpoint",
            Name = $"{serviceKind} {operation}",
            DependencyType = "capabilities",
            Address = MigrationInventoryHelpers.NormalizeExternalAddress(serviceUri.ToString()),
            Metadata = new Dictionary<string, string>
            {
                ["service"] = serviceKind,
                ["version"] = version
            },
            Compatibility = MigrationInventoryHelpers.Compatible(
                $"{serviceKind} service endpoint was captured for coverage migration planning.",
                code: OgcCoverageMigrationCompatibilityCodes.ManualReview)
        };
    }

    private static MigrationExternalDependency BuildServiceMetadataDependency(
        string containerId,
        OgcCoverageServiceScanRequest request,
        string sourceKind,
        string product,
        string version)
    {
        var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["coverageCount"] = request.Coverages.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["product"] = product,
            ["serviceType"] = NormalizeServiceType(request.ServiceType),
            ["sourceKind"] = sourceKind,
            ["version"] = version
        };
        AddMetadata(metadata, "title", request.ServiceMetadata.Title);
        AddMetadata(metadata, "description", request.ServiceMetadata.Description);
        AddMetadata(metadata, "providerName", request.ServiceMetadata.ProviderName);
        AddMetadata(metadata, "fees", string.Join("; ", NormalizeStrings(request.ServiceMetadata.Fees)));
        var accessConstraints = NormalizeAccessConstraints(request.ServiceMetadata.AccessConstraints
            .Concat(request.Coverages.SelectMany(static coverage => coverage.AccessConstraints)));
        AddMetadata(metadata, "accessConstraints", string.Join("; ", accessConstraints));

        var hasAccessConstraints = accessConstraints.Length > 0;
        return new MigrationExternalDependency
        {
            Id = "metadata:coverage-service",
            ContainerId = containerId,
            Kind = "coverage-service-metadata",
            Name = "Coverage service metadata",
            DependencyType = "service-metadata",
            Metadata = metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal),
            Compatibility = hasAccessConstraints
                ? MigrationInventoryHelpers.Partial(
                    "Coverage service metadata advertises access constraints that must be reviewed before cutover.",
                    accessConstraints,
                    ["Confirm licensing, authentication, and redistribution constraints before automating coverage migration."],
                    OgcCoverageMigrationCompatibilityCodes.ManualReview)
                : MigrationInventoryHelpers.Compatible(
                    "Coverage service metadata was captured for migration planning.",
                    code: OgcCoverageMigrationCompatibilityCodes.ManualReview)
        };
    }

    private static IEnumerable<MigrationExternalDependency> BuildFormatDependencies(
        string containerId,
        string resourceId,
        OgcCoverageDescription coverage)
    {
        foreach (var format in coverage.OutputFormats
                     .Where(static item => !string.IsNullOrWhiteSpace(item.Format))
                     .OrderBy(static item => item.Format, StringComparer.Ordinal)
                     .ThenBy(static item => item.MediaType, StringComparer.Ordinal)
                     .ThenBy(static item => item.Profile, StringComparer.Ordinal))
        {
            var classification = ClassifyFormat(format);
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["classification"] = classification.Label,
                ["format"] = format.Format
            };
            AddMetadata(metadata, "mediaType", format.MediaType);
            AddMetadata(metadata, "profile", format.Profile);
            if (format.IsNative.HasValue)
            {
                metadata["isNative"] = format.IsNative.Value ? "true" : "false";
            }

            yield return new MigrationExternalDependency
            {
                Id = $"format:{ToStableId(coverage.CoverageId)}:{ToStableId(format.Format)}:{ShortHash(format.MediaType, format.Profile)}",
                ContainerId = containerId,
                ResourceId = resourceId,
                Kind = "coverage-output-format",
                Name = format.Format,
                DependencyType = classification.Label,
                Metadata = metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal),
                Compatibility = classification.Compatibility
            };
        }
    }

    private static IEnumerable<MigrationExternalDependency> BuildAxisDependencies(
        string containerId,
        string resourceId,
        OgcCoverageDescription coverage)
    {
        foreach (var axis in coverage.Axes
                     .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                     .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["axisName"] = axis.Name
            };
            AddMetadata(metadata, "axisType", axis.AxisType);
            AddMetadata(metadata, "crsAxisLabel", axis.CrsAxisLabel);
            AddMetadata(metadata, "unit", axis.Unit);
            AddMetadata(metadata, "lowerBound", axis.LowerBound);
            AddMetadata(metadata, "upperBound", axis.UpperBound);
            AddMetadata(metadata, "resolution", axis.Resolution);
            AddMetadata(metadata, "defaultValue", axis.DefaultValue);
            AddMetadata(metadata, "allowedValues", string.Join(",", NormalizeStrings(axis.AllowedValues)));
            if (axis.Subsettable.HasValue)
            {
                metadata["subsettable"] = axis.Subsettable.Value ? "true" : "false";
            }

            yield return new MigrationExternalDependency
            {
                Id = $"axis:{ToStableId(coverage.CoverageId)}:{ToStableId(axis.Name)}",
                ContainerId = containerId,
                ResourceId = resourceId,
                Kind = "coverage-axis",
                Name = axis.Name,
                DependencyType = axis.AxisType,
                Metadata = metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal),
                Compatibility = MigrationInventoryHelpers.Compatible(
                    "Coverage axis and subset metadata was captured for migration planning.",
                    code: OgcCoverageMigrationCompatibilityCodes.ManualReview)
            };
        }
    }

    private static IEnumerable<MigrationExternalDependency> BuildRangeDependencies(
        string containerId,
        string resourceId,
        OgcCoverageDescription coverage)
    {
        foreach (var range in coverage.Ranges
                     .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                     .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["rangeName"] = range.Name
            };
            AddMetadata(metadata, "label", range.Label);
            AddMetadata(metadata, "dataType", range.DataType);
            AddMetadata(metadata, "unit", range.Unit);
            AddMetadata(metadata, "noDataValue", range.NoDataValue);
            AddMetadata(metadata, "interpretation", range.Interpretation);
            AddMetadata(metadata, "minimumValue", range.MinimumValue);
            AddMetadata(metadata, "maximumValue", range.MaximumValue);

            yield return new MigrationExternalDependency
            {
                Id = $"range:{ToStableId(coverage.CoverageId)}:{ToStableId(range.Name)}",
                ContainerId = containerId,
                ResourceId = resourceId,
                Kind = "coverage-range",
                Name = range.Name,
                DependencyType = range.Interpretation,
                Metadata = metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal),
                Compatibility = MigrationInventoryHelpers.Compatible(
                    "Coverage range or band metadata was captured for migration planning.",
                    code: OgcCoverageMigrationCompatibilityCodes.ManualReview)
            };
        }
    }

    private static IEnumerable<MigrationExternalDependency> BuildTemporalDependencies(
        string containerId,
        string resourceId,
        OgcCoverageDescription coverage)
    {
        foreach (var temporal in coverage.TemporalDimensions
                     .Where(static item => !string.IsNullOrWhiteSpace(item.Name))
                     .OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["dimensionName"] = temporal.Name
            };
            AddMetadata(metadata, "start", temporal.Start);
            AddMetadata(metadata, "end", temporal.End);
            AddMetadata(metadata, "defaultValue", temporal.DefaultValue);
            AddMetadata(metadata, "interval", temporal.Interval);
            AddMetadata(metadata, "values", string.Join(",", NormalizeStrings(temporal.Values)));
            if (temporal.Subsettable.HasValue)
            {
                metadata["subsettable"] = temporal.Subsettable.Value ? "true" : "false";
            }

            yield return new MigrationExternalDependency
            {
                Id = $"temporal:{ToStableId(coverage.CoverageId)}:{ToStableId(temporal.Name)}",
                ContainerId = containerId,
                ResourceId = resourceId,
                Kind = "coverage-temporal-dimension",
                Name = temporal.Name,
                DependencyType = "time",
                Metadata = metadata.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.Ordinal),
                Compatibility = MigrationInventoryHelpers.Partial(
                    "Temporal coverage dimension metadata was captured and needs parity review before cutover.",
                    [],
                    ["Add temporal subset and sample-window parity probes before promoting this coverage migration."],
                    OgcCoverageMigrationCompatibilityCodes.ManualReview)
            };
        }
    }

    private static MigrationCompatibilityAssessment BuildCoverageCompatibility(
        OgcCoverageDescription coverage,
        MigrationExternalDependency[] formatDependencies,
        MigrationExternalDependency[] temporalDependencies)
    {
        if (formatDependencies.Length == 0)
        {
            return MigrationInventoryHelpers.Partial(
                "Coverage output formats were not advertised.",
                ["No output formats were available in the coverage metadata."],
                ["Run DescribeCoverage or the OGC API Coverages coverage metadata request and verify available output encodings."],
                OgcCoverageMigrationCompatibilityCodes.OutputFormatMissing);
        }

        var formatAssessments = formatDependencies.Select(static dependency => dependency.Compatibility).ToArray();
        var hasCog = formatAssessments.Any(static item => item.Code == OgcCoverageMigrationCompatibilityCodes.CogSupported);
        var hasGeoTiff = formatAssessments.Any(static item => item.Code == OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported);
        var hasUnsupported = formatAssessments.Any(static item => string.Equals(item.Level, "incompatible", StringComparison.Ordinal));
        var hasTemporalReview = temporalDependencies.Length > 0;

        if ((hasCog || hasGeoTiff) && (hasUnsupported || hasTemporalReview))
        {
            return MigrationInventoryHelpers.Partial(
                "Coverage advertises a GeoTIFF/COG migration path, but some dimensions or alternate formats need manual review.",
                formatAssessments.SelectMany(static item => item.Warnings),
                ["Use GeoTIFF or COG as the first automated import path and review unsupported scientific formats separately."],
                hasCog ? OgcCoverageMigrationCompatibilityCodes.CogSupported : OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported);
        }

        if (hasCog)
        {
            return MigrationInventoryHelpers.Compatible(
                "Coverage advertises Cloud Optimized GeoTIFF output that can seed the automated raster migration path.",
                code: OgcCoverageMigrationCompatibilityCodes.CogSupported);
        }

        if (hasGeoTiff)
        {
            return MigrationInventoryHelpers.Compatible(
                "Coverage advertises GeoTIFF output that can seed the automated raster migration path.",
                code: OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported);
        }

        if (formatAssessments.Any(static item =>
                item.Code is OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported or
                    OgcCoverageMigrationCompatibilityCodes.HdfUnsupported or
                    OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported))
        {
            return MigrationInventoryHelpers.Incompatible(
                "Coverage advertises scientific multidimensional formats without a current automated import path.",
                formatAssessments.SelectMany(static item => item.Warnings),
                ["Track NetCDF, HDF, or Zarr format support before claiming automated migration for this coverage."],
                OgcCoverageMigrationCompatibilityCodes.ScientificFormatUnsupported);
        }

        return MigrationInventoryHelpers.Incompatible(
            "Coverage does not advertise a recognized automated output format.",
            formatAssessments.SelectMany(static item => item.Warnings),
            ["Add a source-specific importer or identify a GeoTIFF/COG output option before migration."],
            OgcCoverageMigrationCompatibilityCodes.UnsupportedFormat);
    }

    private static (string Label, MigrationCompatibilityAssessment Compatibility) ClassifyFormat(
        OgcCoverageFormatMetadata format)
    {
        var haystack = string.Join(
            " ",
            format.Format,
            format.MediaType,
            format.Profile).ToUpperInvariant();

        if (haystack.Contains("COG", StringComparison.Ordinal) ||
            haystack.Contains("CLOUD OPTIMIZED GEOTIFF", StringComparison.Ordinal) ||
            haystack.Contains("CLOUD-OPTIMIZED-GEOTIFF", StringComparison.Ordinal))
        {
            return ("cog", MigrationInventoryHelpers.Compatible(
                "Cloud Optimized GeoTIFF output is supported by the first coverage migration path.",
                code: OgcCoverageMigrationCompatibilityCodes.CogSupported));
        }

        if (haystack.Contains("GEOTIFF", StringComparison.Ordinal) ||
            haystack.Contains("GEO TIFF", StringComparison.Ordinal) ||
            haystack.Contains("IMAGE/TIFF", StringComparison.Ordinal) ||
            haystack.Contains("TIFF", StringComparison.Ordinal))
        {
            return ("geotiff", MigrationInventoryHelpers.Compatible(
                "GeoTIFF output is supported by the first coverage migration path.",
                code: OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported));
        }

        if (haystack.Contains("NETCDF", StringComparison.Ordinal) ||
            haystack.Contains("X-NETCDF", StringComparison.Ordinal))
        {
            return ("netcdf", MigrationInventoryHelpers.Incompatible(
                "NetCDF coverage output needs a dedicated format importer before automated migration.",
                ["NetCDF is classified separately from GeoTIFF/COG coverage outputs."],
                ["Track NetCDF format support before automating this coverage migration."],
                OgcCoverageMigrationCompatibilityCodes.NetCdfUnsupported));
        }

        if (haystack.Contains("HDF", StringComparison.Ordinal) ||
            haystack.Contains("HDF5", StringComparison.Ordinal))
        {
            return ("hdf", MigrationInventoryHelpers.Incompatible(
                "HDF coverage output needs a dedicated format importer before automated migration.",
                ["HDF is classified separately from GeoTIFF/COG coverage outputs."],
                ["Track HDF format support before automating this coverage migration."],
                OgcCoverageMigrationCompatibilityCodes.HdfUnsupported));
        }

        if (haystack.Contains("ZARR", StringComparison.Ordinal))
        {
            return ("zarr", MigrationInventoryHelpers.Incompatible(
                "Zarr coverage output needs a dedicated format importer before automated migration.",
                ["Zarr is classified separately from GeoTIFF/COG coverage outputs."],
                ["Track Zarr format support before automating this coverage migration."],
                OgcCoverageMigrationCompatibilityCodes.ZarrUnsupported));
        }

        return ("unsupported", MigrationInventoryHelpers.Incompatible(
            "Coverage output format is not recognized by the first automated coverage migration path.",
            [$"Unsupported coverage output format: {format.Format}"],
            ["Review the source coverage output list and decide whether a format importer or manual export is required."],
            OgcCoverageMigrationCompatibilityCodes.UnsupportedFormat));
    }

    private async Task<MigrationSpatialReferenceInfo[]> BuildSpatialReferencesAsync(
        IEnumerable<OgcCoverageCrsMetadata> crsValues,
        CancellationToken cancellationToken)
    {
        var references = new List<MigrationSpatialReferenceInfo>();
        foreach (var crs in crsValues
                     .Where(static value => !string.IsNullOrWhiteSpace(value.Role) && !string.IsNullOrWhiteSpace(value.Crs))
                     .OrderBy(static value => value.Role, StringComparer.Ordinal)
                     .ThenBy(static value => value.Crs, StringComparer.Ordinal))
        {
            var reference = await MigrationInventoryHelpers.BuildSpatialReferenceAsync(
                    _crsRegistry,
                    crs.Role,
                    crs.Crs,
                    cancellationToken,
                    explicitSrid: TryParseCoverageSrid(crs.Crs))
                .ConfigureAwait(false);
            if (reference == null)
            {
                continue;
            }

            reference = reference with
            {
                AxisOrder = FirstNonBlank(crs.AxisOrder, reference.AxisOrder)
            };

            if (!references.Any(existing =>
                    string.Equals(existing.Role, reference.Role, StringComparison.Ordinal) &&
                    string.Equals(existing.SourceValue, reference.SourceValue, StringComparison.Ordinal)))
            {
                references.Add(reference);
            }
        }

        return references.ToArray();
    }

    private static MigrationInventoryAuthPosture BuildAuthPosture(
        OgcCoverageServiceMetadata serviceMetadata,
        IEnumerable<OgcCoverageDescription> coverages)
    {
        var constraints = NormalizeAccessConstraints(serviceMetadata.AccessConstraints
            .Concat(coverages.SelectMany(static coverage => coverage.AccessConstraints)));
        return new MigrationInventoryAuthPosture
        {
            Mode = constraints.Length == 0 ? "anonymous" : "access-constrained",
            CredentialsSupplied = false,
            AccessConfirmed = true,
            Notes = constraints.Length == 0 ? [] : constraints
        };
    }

    private static string[] BuildCoverageCapabilities(string serviceKind)
        => serviceKind == "WCS"
            ? ["wcs:GetCapabilities", "wcs:DescribeCoverage", "wcs:GetCoverage"]
            : ["ogcapi-coverages:LandingPage", "ogcapi-coverages:Collection", "ogcapi-coverages:Coverage"];

    private static string NormalizeServiceType(string serviceType)
    {
        var normalized = serviceType.Trim().ToUpperInvariant().Replace("_", " ", StringComparison.Ordinal);
        normalized = SpaceRegex().Replace(normalized, " ");
        return normalized switch
        {
            "WCS" or "WEB COVERAGE SERVICE" => "WCS",
            "OGC API COVERAGES" or "OGC API - COVERAGES" or "OGCAPI COVERAGES" or "COVERAGES" => "OGC API Coverages",
            _ => throw new InvalidOperationException("Unsupported OGC coverage service type.")
        };
    }

    private static Uri ValidateServiceUri(string serviceUrl)
    {
        if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("OGC coverage service URL must be a valid HTTP or HTTPS URL.", nameof(serviceUrl));
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ArgumentException("OGC coverage service URL must not include embedded credentials.", nameof(serviceUrl));
        }

        return uri;
    }

    private static string ToStableId(string value)
    {
        var normalized = StableIdRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unnamed" : normalized;
    }

    private static string ShortHash(params string?[] values)
    {
        var payload = string.Join("|", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "default";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static string[] NormalizeAccessConstraints(IEnumerable<string> values)
        => NormalizeStrings(values)
            .Where(static value => !IsUnconstrainedAccessValue(value))
            .ToArray();

    private static string[] NormalizeStrings(IEnumerable<string>? values)
        => values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray() ?? [];

    private static bool IsUnconstrainedAccessValue(string value)
        => string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "no constraints", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "unrestricted", StringComparison.OrdinalIgnoreCase);

    private static int? TryParseCoverageSrid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var epsgMatch = EpsgSridRegex().Match(value);
        if (epsgMatch.Success &&
            int.TryParse(epsgMatch.Groups["srid"].Value, out var epsgSrid))
        {
            return epsgSrid;
        }

        return int.TryParse(value, out var srid) ? srid : null;
    }

    private static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static void AddMetadata(SortedDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceRegex();

    [GeneratedRegex("EPSG(?::|/)(?::|0/)?(?<srid>\\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpsgSridRegex();
}
