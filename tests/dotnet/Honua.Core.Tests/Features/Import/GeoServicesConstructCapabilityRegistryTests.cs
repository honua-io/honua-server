// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class GeoServicesConstructCapabilityRegistryTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ServiceIdentity, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ServiceCapabilities, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceIdentity, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceCapabilities, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceFields, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceDomains, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceSubtypes, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.GeoServicesSubtypesManualReview, false, false, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceRelationships, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceAttachments, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceRenderers, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview, false, false, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceTimeMetadata, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.GeoServicesTimeMetadataManualReview, false, false, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.FacadeFeatureService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.FacadeMapService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.FacadeImageService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpVectorDeterministic, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpDestructiveDataManagement, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview, false, false, true)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpRasterSurface, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, true, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpRasterConversion, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpDataManagement, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, false)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.GpUnsupported, MigrationFidelityAutomationStatuses.Unsupported, ImportCompatibilityCodes.ManualReview, false, false, false)]
    public void ResolveOrUnknown_KnownConstructKey_ReturnsSeededDescriptor(
        string constructKey,
        string expectedAutomationStatus,
        string expectedCode,
        bool expectedCanTransform,
        bool expectedCanServe,
        bool expectedRequiresCheck)
    {
        var registry = new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.ConstructKey.Should().Be(constructKey);
        descriptor.AutomationStatus.Should().Be(expectedAutomationStatus);
        descriptor.Code.Should().Be(expectedCode);
        descriptor.CanTransform.Should().Be(expectedCanTransform);
        descriptor.CanServe.Should().Be(expectedCanServe);
        descriptor.RequiresCheck.Should().Be(expectedRequiresCheck);
        descriptor.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ServiceCapabilities, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceCapabilities, MigrationFidelityAutomationStatuses.Unsupported, ImportCompatibilityCodes.GeoServicesQueryCapabilityMissing)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceFields, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceDomains, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.GeoServicesDomainTruncated)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceSubtypes, MigrationFidelityAutomationStatuses.Unsupported, ImportCompatibilityCodes.GeoServicesSubtypesManualReview)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceRelationships, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.GeoServicesRelationshipsManualReview)]
    [InlineData(GeoServicesConstructCapabilityRegistry.Keys.ResourceAttachments, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.GeoServicesAttachments)]
    public void ResolveOrUnknown_ConditionalConstruct_ExposesUnsupportedFallback(
        string constructKey,
        string expectedFallbackStatus,
        string expectedFallbackCode)
    {
        var registry = new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.UnsupportedAutomationStatus.Should().Be(expectedFallbackStatus);
        descriptor.UnsupportedCode.Should().Be(expectedFallbackCode);
        descriptor.UnsupportedReason.Should().NotBeNullOrWhiteSpace();
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("not-a-real-construct")]
    [InlineData("")]
    [InlineData("RESOURCE.IDENTITY")]
    public void ResolveOrUnknown_UnknownKey_ReturnsManualReviewFallback(string constructKey)
    {
        var registry = new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.Should().BeSameAs(GeoServicesConstructCapabilityRegistry.UnknownDescriptor);
        descriptor.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        descriptor.Code.Should().Be(ImportCompatibilityCodes.ManualReview);
        descriptor.CanTransform.Should().BeFalse();
        descriptor.CanServe.Should().BeFalse();
        descriptor.RequiresCheck.Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void RegisteredConstructKeys_ContainsAllBuiltInKeys()
    {
        var registry = new GeoServicesConstructCapabilityRegistry(GeoServicesConstructCapabilityRegistry.BuiltInDescriptors);

        registry.RegisteredConstructKeys.Should().BeEquivalentTo(
        [
            GeoServicesConstructCapabilityRegistry.Keys.ServiceIdentity,
            GeoServicesConstructCapabilityRegistry.Keys.ServiceCapabilities,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceIdentity,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceCapabilities,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceFields,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceDomains,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceSubtypes,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceRelationships,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceAttachments,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceRenderers,
            GeoServicesConstructCapabilityRegistry.Keys.ResourceTimeMetadata,
            GeoServicesConstructCapabilityRegistry.Keys.FacadeFeatureService,
            GeoServicesConstructCapabilityRegistry.Keys.FacadeMapService,
            GeoServicesConstructCapabilityRegistry.Keys.FacadeImageService,
            GeoServicesConstructCapabilityRegistry.Keys.GpVectorDeterministic,
            GeoServicesConstructCapabilityRegistry.Keys.GpDestructiveDataManagement,
            GeoServicesConstructCapabilityRegistry.Keys.GpRasterSurface,
            GeoServicesConstructCapabilityRegistry.Keys.GpRasterConversion,
            GeoServicesConstructCapabilityRegistry.Keys.GpDataManagement,
            GeoServicesConstructCapabilityRegistry.Keys.GpImageryInference,
            GeoServicesConstructCapabilityRegistry.Keys.GpUnsupported
        ]);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Constructor_DuplicateConstructKey_ThrowsInvalidOperationException()
    {
        var duplicate = new GeoServicesConstructCapabilityDescriptor
        {
            ConstructKey = GeoServicesConstructCapabilityRegistry.Keys.ResourceIdentity,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "duplicate"
        };

        var act = () => _ = new GeoServicesConstructCapabilityRegistry(
        [
            ..GeoServicesConstructCapabilityRegistry.BuiltInDescriptors,
            duplicate
        ]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Duplicate*'{GeoServicesConstructCapabilityRegistry.Keys.ResourceIdentity}'*");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void Constructor_DescriptorWithEmptyConstructKey_ThrowsInvalidOperationException()
    {
        var invalid = new GeoServicesConstructCapabilityDescriptor
        {
            ConstructKey = "  ",
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "blank key"
        };

        var act = () => _ = new GeoServicesConstructCapabilityRegistry([invalid]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-empty ConstructKey*");
    }
}
