// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationApplyPlanBuilderTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void Build_WithSameManifest_ProducesStableReplayableOutput()
    {
        var manifest = MigrationManifestTranslator.Translate(
            CreateInventory(
                resources:
                [
                    CreateResource("layer:demo:roads", "Roads", "compatible", "Layer can be published."),
                    CreateResource(
                        "layer:demo:closed",
                        "Closed",
                        "partial",
                        "Layer is disabled in GeoServer.",
                        code: ImportCompatibilityCodes.GeoServerDisabledLayer,
                        manualSteps: ["Confirm the target layer should be enabled."])
                ]),
            new MigrationManifestTranslationOptions { TargetServiceName = "Pilot Migration" });

        var firstPlan = MigrationApplyPlanBuilder.Build(manifest);
        var secondPlan = MigrationApplyPlanBuilder.Build(manifest);

        JsonSerializer.Serialize(firstPlan, SerializerOptions)
            .Should().Be(JsonSerializer.Serialize(secondPlan, SerializerOptions));
        firstPlan.ReplayToken.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        firstPlan.PlanFingerprint.Should().Be(firstPlan.ReplayToken);
        firstPlan.Steps.Select(step => step.Sequence).Should().Equal(1, 2);
        firstPlan.Steps.Should().ContainSingle(step =>
            step.SourceId == "layer:demo:roads" &&
            step.Action == "stage-catalog-resource" &&
            step.Disposition == "ready" &&
            step.TargetServiceName == "pilot-migration");
    }

    [Fact]
    public void Build_WithManualReviewAndUnsupportedItems_ClassifiesStepsAndReviewItems()
    {
        var manifest = MigrationManifestTranslator.Translate(
            CreateInventory(
                resources:
                [
                    CreateResource("layer:demo:roads", "Roads", "compatible", "Layer can be published."),
                    CreateResource(
                        "layer:demo:closed",
                        "Closed",
                        "partial",
                        "Layer is disabled in GeoServer.",
                        code: ImportCompatibilityCodes.GeoServerDisabledLayer,
                        manualSteps: ["Confirm the target layer should be enabled."]),
                    CreateResource(
                        "layer:demo:raster",
                        "Raster",
                        "incompatible",
                        "Coverage layers require the raster migration path.",
                        code: ImportCompatibilityCodes.GeoServerUnsupportedCoverageStore,
                        manualSteps: ["Route this item through raster import."])
                ],
                styles:
                [
                    CreateStyle(
                        "style:demo:line",
                        "incompatible",
                        "SLD conversion is not available in this slice.",
                        code: ImportCompatibilityCodes.GeoServerStyleConversionRequired,
                        manualSteps: ["Review the style in the admin SLD importer."])
                ]));

        var plan = MigrationApplyPlanBuilder.Build(manifest);

        plan.Summary.Should().BeEquivalentTo(new MigrationApplyPlanSummary
        {
            TotalStepCount = 3,
            ReadyStepCount = 1,
            ManualReviewStepCount = 1,
            UnsupportedStepCount = 1,
            UnsupportedItemCount = 2
        });
        plan.Steps.Should().ContainSingle(step =>
                step.SourceId == "layer:demo:closed" &&
                step.Disposition == "manual-review")
            .Which.ReviewCodes.Should().Equal(ImportCompatibilityCodes.GeoServerDisabledLayer);
        plan.Steps.Should().ContainSingle(step =>
                step.SourceId == "style:demo:line" &&
                step.Disposition == "unsupported")
            .Which.ReviewCodes.Should().Equal(ImportCompatibilityCodes.GeoServerStyleConversionRequired);
        plan.UnsupportedItems.Select(item => item.SourceId)
            .Should().Equal("layer:demo:raster", "style:demo:line");
        plan.ManualReviewItems.Should().ContainSingle(item => item.SourceId == "layer:demo:closed");
    }

    private static MigrationSourceInventoryArtifact CreateInventory(
        MigrationInventoryResource[] resources,
        MigrationInventoryStyle[]? styles = null)
    {
        var containers = new[]
        {
            new MigrationInventoryContainer
            {
                Id = "workspace:demo",
                Kind = "workspace",
                Name = "demo",
                Compatibility = Assessment("compatible", "Workspace can be represented.")
            }
        };
        var styleItems = styles ?? [];

        return new MigrationSourceInventoryArtifact
        {
            SourceKind = "geoserver-rest",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Demo GeoServer",
                BaseUrl = "https://example.com/geoserver/rest",
                Product = "GeoServer",
                Version = "2.28.0",
                ServiceType = "REST"
            },
            AuthPosture = new MigrationInventoryAuthPosture
            {
                Mode = "anonymous",
                AccessConfirmed = true
            },
            ScanCompleteness = new MigrationInventoryCompleteness
            {
                Status = "complete"
            },
            Summary = new MigrationInventorySummary
            {
                ContainerCount = containers.Length,
                ResourceCount = resources.Length,
                StyleCount = styleItems.Length,
                CompatibleCount = containers.Length +
                    resources.Count(resource => resource.Compatibility.Level == "compatible") +
                    styleItems.Count(style => style.Compatibility.Level == "compatible"),
                PartiallyCompatibleCount = resources.Count(resource => resource.Compatibility.Level == "partial") +
                    styleItems.Count(style => style.Compatibility.Level == "partial"),
                IncompatibleCount = resources.Count(resource => resource.Compatibility.Level == "incompatible") +
                    styleItems.Count(style => style.Compatibility.Level == "incompatible")
            },
            OverallCompatibility = Assessment("partial", "Fixture compatibility is computed per item."),
            Containers = containers,
            Resources = resources,
            Styles = styleItems
        };
    }

    private static MigrationInventoryResource CreateResource(
        string id,
        string name,
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace:demo",
            Kind = "layer",
            Name = name,
            GeometryType = "LineString",
            Capabilities = ["query"],
            Compatibility = Assessment(level, reason, code, manualSteps)
        };

    private static MigrationInventoryStyle CreateStyle(
        string id,
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null)
        => new()
        {
            Id = id,
            ContainerId = "workspace:demo",
            Kind = "style",
            Name = id,
            Format = "sld",
            ResourceIds = ["layer:demo:roads"],
            Compatibility = Assessment(level, reason, code, manualSteps)
        };

    private static MigrationCompatibilityAssessment Assessment(
        string level,
        string reason,
        string? code = null,
        string[]? manualSteps = null)
        => new()
        {
            Level = level,
            Code = code,
            Reason = reason,
            ManualSteps = manualSteps ?? []
        };
}
