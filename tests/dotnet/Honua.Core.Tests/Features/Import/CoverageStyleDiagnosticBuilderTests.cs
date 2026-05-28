// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Coverage-style migration diagnostic tests for the slice-4 builder
/// (<see cref="CoverageStyleDiagnosticBuilder"/>) of issue #1030.
/// Each fact targets one of the four documented classification rules plus
/// JSON round-trip stability.
/// </summary>
public sealed class CoverageStyleDiagnosticBuilderTests
{
    [Fact]
    public void Build_SingleBandGrayscaleWithStats_EmitsAutomatedLinearStretch()
    {
        var resource = BuildResource(
            "dem-tiff",
            fields: [
                new MigrationInventoryField { Name = "elevation", FieldType = "Float32" }
            ]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var bandStats = diagnostics.Should()
            .ContainSingle(d => d.Kind == "bandStatistics").Subject;
        bandStats.Classification.Should().Be("automated");
        bandStats.SourceCoverageId.Should().Be("dem-tiff");
        bandStats.SourceStyleId.Should().Be("elevation");
        bandStats.SuggestedTargetStyleId.Should().Be("grayscale-linear-stretch");
        bandStats.ManualSteps.Should().BeEmpty();
    }

    [Fact]
    public void Build_IndexedColorTable_EmitsAssistedColorMapWithPreservedPalette()
    {
        var resource = BuildResource(
            "landcover",
            styleIds: ["landcover-palette"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var colorMap = diagnostics.Should()
            .ContainSingle(d => d.Kind == "colorMap").Subject;
        colorMap.Classification.Should().Be("assisted");
        colorMap.SourceStyleId.Should().Be("landcover-palette");
        colorMap.SuggestedTargetStyleId.Should().Be("indexed-color-table");
        colorMap.Reason.Should().Contain("transferred verbatim");
    }

    [Fact]
    public void Build_ContinuousColorRamp_EmitsManualReviewWithGuidance()
    {
        var resource = BuildResource(
            "ndvi-monthly",
            styleIds: ["ndvi-continuous-ramp"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var colorMap = diagnostics.Should()
            .ContainSingle(d => d.Kind == "colorMap").Subject;
        colorMap.Classification.Should().Be("manual-review");
        colorMap.SuggestedTargetStyleId.Should().BeNull();
        colorMap.ManualSteps.Should().NotBeEmpty();
        colorMap.ManualSteps[0].Should().Contain("Export the source style document");
    }

    [Fact]
    public void Build_VendorMarker_EsriExtension_EmitsManualReviewWithVendorName()
    {
        var resource = BuildResource(
            "imagery",
            styleIds: ["esri:rendering:stretchedRaster"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var hint = diagnostics.Should()
            .ContainSingle(d => d.Kind == "renderingHint").Subject;
        hint.Classification.Should().Be("manual-review");
        hint.VendorName.Should().Be("Esri");
        hint.SourceStyleId.Should().Be("esri:rendering:stretchedRaster");
        hint.ManualSteps.Should().HaveCount(2);
    }

    [Fact]
    public void Build_VendorMarker_GeoServerSld_EmitsManualReviewWithGeoServerVendor()
    {
        var resource = BuildResource(
            "ortho",
            styleIds: ["gs:rgb-stretch"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        diagnostics.Should().ContainSingle()
            .Which.VendorName.Should().Be("GeoServer");
    }

    [Fact]
    public void Build_NoDataMarker_EmitsAutomatedDiagnostic()
    {
        var resource = BuildResource(
            "dem-tiff",
            fields: [
                new MigrationInventoryField
                {
                    Name = "elevation",
                    FieldType = "Float32",
                    DomainName = "NoData=-9999"
                }
            ]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var nodata = diagnostics.Should()
            .ContainSingle(d => d.Kind == "noDataValue").Subject;
        nodata.Classification.Should().Be("automated");
        nodata.SourceStyleId.Should().Be("elevation");
    }

    [Fact]
    public void Build_TransparencyCapability_EmitsAssistedDiagnostic()
    {
        var resource = BuildResource(
            "satellite",
            capabilities: ["transparency"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        diagnostics.Should().ContainSingle(d => d.Kind == "transparency")
            .Which.Classification.Should().Be("assisted");
    }

    [Fact]
    public void Build_MultiBandCoverage_EmitsAssistedBandStatisticsDiagnostic()
    {
        var resource = BuildResource(
            "rgb-ortho",
            fields: [
                new MigrationInventoryField { Name = "red", FieldType = "UInt8" },
                new MigrationInventoryField { Name = "green", FieldType = "UInt8" },
                new MigrationInventoryField { Name = "blue", FieldType = "UInt8" }
            ]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        var bandStats = diagnostics.Should()
            .ContainSingle(d => d.Kind == "bandStatistics").Subject;
        bandStats.Classification.Should().Be("assisted");
        bandStats.SuggestedTargetStyleId.Should().BeNull();
        bandStats.ManualSteps.Should().HaveCount(2);
    }

    [Fact]
    public void Build_NonCoverageResources_Ignored()
    {
        var resource = BuildResource(
            "feature-set",
            kind: "feature",
            styleIds: ["any-style"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);

        diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Build_Ordering_IsDeterministic()
    {
        var first = BuildResource(
            "z-coverage",
            styleIds: ["z-palette"]);
        var second = BuildResource(
            "a-coverage",
            styleIds: ["a-palette"]);
        var third = BuildResource(
            "m-coverage",
            styleIds: ["m-ramp"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([first, second, third]);

        diagnostics.Select(d => d.SourceCoverageId)
            .Should().ContainInOrder("a-coverage", "m-coverage", "z-coverage");
    }

    [Fact]
    public void Diagnostic_JsonRoundTrip_PreservesAllFields()
    {
        var resource = BuildResource(
            "ndvi",
            fields: [
                new MigrationInventoryField
                {
                    Name = "ndvi",
                    FieldType = "Float32",
                    DomainName = "NoData=-9999"
                }
            ],
            styleIds: ["ndvi-continuous-ramp", "esri:colorizer"],
            capabilities: ["transparency"]);

        var diagnostics = CoverageStyleDiagnosticBuilder.Build([resource]);
        diagnostics.Should().NotBeEmpty();

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        var json = JsonSerializer.Serialize(diagnostics, options);
        var round = JsonSerializer.Deserialize<MigrationCoverageStyleDiagnostic[]>(json, options);

        round.Should().BeEquivalentTo(diagnostics);
        json.Should().Contain("\"classification\":");
        json.Should().Contain("\"kind\":");
        json.Should().Contain("\"reason\":");
    }

    private static MigrationInventoryResource BuildResource(
        string name,
        string kind = "coverage",
        MigrationInventoryField[]? fields = null,
        string[]? styleIds = null,
        string[]? capabilities = null)
        => new()
        {
            Id = $"{kind}:{name}",
            ContainerId = "service:wcs",
            Kind = kind,
            Name = name,
            Title = name,
            Fields = fields ?? [],
            StyleIds = styleIds ?? [],
            Capabilities = capabilities ?? [],
            Compatibility = new MigrationCompatibilityAssessment
            {
                Level = "compatible",
                Code = OgcCoverageMigrationCompatibilityCodes.GeoTiffSupported,
                Reason = "test fixture"
            }
        };
}
