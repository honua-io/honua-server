// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting;
using Honua.Core.Features.Reporting.Services;
using Honua.Core.Features.Reporting.Templates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Deterministic <see cref="AnalysisResultPackage"/> fixtures used by the
/// golden-file renderer tests. All timestamps are pinned to 2026-04-24 so
/// rendered output is byte-stable for golden comparisons.
/// </summary>
internal static class ReportingFixtures
{
    public static readonly DateTimeOffset ExecutedAt =
        DateTimeOffset.Parse("2026-04-24T09:55:00Z", CultureInfo.InvariantCulture);

    public static readonly DateTimeOffset GeneratedAt =
        DateTimeOffset.Parse("2026-04-24T10:00:00Z", CultureInfo.InvariantCulture);

    private static readonly string[] _bufferAssumptions = ["Places layer is in EPSG:4326."];
    private static readonly string[] _bufferProcessDefinitions = ["analytics.buffer-aggregate"];
    private static readonly string[] _bufferGeneratedArtifactIds = ["buffered-layer"];
    private static readonly string[] _densityProcessDefinitions = ["analytics.density"];
    private static readonly string[] _densityGeneratedArtifactIds = ["density-layer"];
    private static readonly string[] _slopeProcessDefinitions = ["surface.slope"];
    private static readonly string[] _slopeGeneratedArtifactIds = ["slope-raster"];
    private static readonly string[] _dissolveProcessDefinitions = ["generalization.dissolve"];
    private static readonly string[] _dissolveGeneratedArtifactIds = ["dissolved-layer"];

    public static AnalysisResultPackage BufferAggregatePackage() => AnalysisResultPackage.CreateCompleted(
        resultPackageId: "pkg-buffer",
        summary: new ResultSummary
        {
            Title = "Buffered places",
            Description = "500m buffers applied to the seed places layer."
        },
        artifacts: new[]
        {
            new ArtifactRef
            {
                ArtifactId = "buffered-layer",
                Kind = ArtifactKind.FeatureLayer,
                Label = "Buffered places",
                Uri = "honua://artifacts/buffered-layer",
                ContentType = "application/geo+json",
                Metadata = new Dictionary<string, string>
                {
                    ["distance"] = "500",
                    ["unit"] = "meters",
                    ["bufferedFeatureCount"] = "42",
                    ["dissolvedFeatureCount"] = "7",
                    ["totalAreaSquareMeters"] = "123456.789"
                }
            }
        },
        workspaceRefs: Array.Empty<WorkspaceRef>(),
        provenance: new ProvenanceRecord
        {
            Sources = new[]
            {
                new ProvenanceSource { SourceId = "places", Version = "v1", Description = "Seed places layer" }
            },
            ProcessDefinitions = _bufferProcessDefinitions,
            ExecutedAt = ExecutedAt,
            GeneratedArtifactIds = _bufferGeneratedArtifactIds
        },
        assumptions: _bufferAssumptions);

    public static AnalysisResultPackage DensityPackage() => AnalysisResultPackage.CreateCompleted(
        resultPackageId: "pkg-density",
        summary: new ResultSummary
        {
            Title = "Density bins",
            Description = "Hex-bin density over the places layer."
        },
        artifacts: new[]
        {
            new ArtifactRef
            {
                ArtifactId = "density-layer",
                Kind = ArtifactKind.FeatureLayer,
                Label = "Density bins",
                Uri = "honua://artifacts/density-layer",
                Metadata = new Dictionary<string, string>
                {
                    ["mode"] = "hex",
                    ["cellSizeMeters"] = "500",
                    ["binCount"] = "128",
                    ["maxBinValue"] = "17",
                    ["totalBinValue"] = "420",
                    ["topBin0Label"] = "H-01",
                    ["topBin0Value"] = "17",
                    ["topBin1Label"] = "H-02",
                    ["topBin1Value"] = "15",
                    ["topBin2Label"] = "H-03",
                    ["topBin2Value"] = "12"
                }
            }
        },
        workspaceRefs: Array.Empty<WorkspaceRef>(),
        provenance: new ProvenanceRecord
        {
            Sources = new[]
            {
                new ProvenanceSource { SourceId = "places", Description = "Seed places layer" }
            },
            ProcessDefinitions = _densityProcessDefinitions,
            ExecutedAt = ExecutedAt,
            GeneratedArtifactIds = _densityGeneratedArtifactIds
        });

    public static AnalysisResultPackage SlopePackage() => AnalysisResultPackage.CreateCompleted(
        resultPackageId: "pkg-slope",
        summary: new ResultSummary
        {
            Title = "Slope raster",
            Description = "Slope derived from the DEM."
        },
        artifacts: new[]
        {
            new ArtifactRef
            {
                ArtifactId = "slope-raster",
                Kind = ArtifactKind.Raster,
                Label = "Slope",
                Uri = "honua://artifacts/slope-raster",
                Metadata = new Dictionary<string, string>
                {
                    ["units"] = "degrees",
                    ["zFactor"] = "1",
                    ["minSlope"] = "0.1",
                    ["meanSlope"] = "12.4",
                    ["maxSlope"] = "64.8",
                    ["spatialReference"] = "EPSG:3857"
                }
            }
        },
        workspaceRefs: Array.Empty<WorkspaceRef>(),
        provenance: new ProvenanceRecord
        {
            Sources = new[]
            {
                new ProvenanceSource { SourceId = "dem", Description = "Input elevation raster" }
            },
            ProcessDefinitions = _slopeProcessDefinitions,
            ExecutedAt = ExecutedAt,
            GeneratedArtifactIds = _slopeGeneratedArtifactIds
        });

    public static AnalysisResultPackage DissolvePackage() => AnalysisResultPackage.CreateCompleted(
        resultPackageId: "pkg-dissolve",
        summary: new ResultSummary
        {
            Title = "Dissolved districts",
            Description = "Districts dissolved by county code."
        },
        artifacts: new[]
        {
            new ArtifactRef
            {
                ArtifactId = "dissolved-layer",
                Kind = ArtifactKind.FeatureLayer,
                Label = "Dissolved districts",
                Uri = "honua://artifacts/dissolved-layer",
                Metadata = new Dictionary<string, string>
                {
                    ["groupByFields"] = "county_code",
                    ["dissolve"] = "true",
                    ["inputFeatureCount"] = "342",
                    ["outputFeatureCount"] = "12"
                }
            }
        },
        workspaceRefs: Array.Empty<WorkspaceRef>(),
        provenance: new ProvenanceRecord
        {
            Sources = new[]
            {
                new ProvenanceSource { SourceId = "districts", Description = "Municipal districts" }
            },
            ProcessDefinitions = _dissolveProcessDefinitions,
            ExecutedAt = ExecutedAt,
            GeneratedArtifactIds = _dissolveGeneratedArtifactIds
        });

    public static AnalysisReportBuilder CreateBuilder()
    {
        IEnumerable<Honua.Core.Features.Reporting.Abstractions.IAnalysisReportTemplate> templates = new Honua.Core.Features.Reporting.Abstractions.IAnalysisReportTemplate[]
        {
            new GenericAnalysisReportTemplate(),
            new AnalyticsBufferAggregateReportTemplate(),
            new AnalyticsDensityReportTemplate(),
            new SurfaceSlopeReportTemplate(),
            new GeneralizationDissolveReportTemplate()
        };
        var registry = new AnalysisReportTemplateRegistry(templates);
        var deterministic = new DeterministicNarrativeProvider();
        var options = Options.Create(new ReportingConfiguration());
        return new AnalysisReportBuilder(
            registry,
            deterministic,
            options,
            new FixedTimeProvider(GeneratedAt),
            NullLogger<AnalysisReportBuilder>.Instance);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
