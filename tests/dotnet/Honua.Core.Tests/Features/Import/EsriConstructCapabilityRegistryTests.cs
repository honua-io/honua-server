// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

public sealed class EsriConstructCapabilityRegistryTests
{
    [Theory]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ServiceIdentity, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ServiceCapabilities, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceIdentity, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceCapabilities, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceFields, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceDomains, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceSubtypes, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ArcGisSubtypesManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceRelationships, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ArcGisRelationshipsManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceAttachments, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ArcGisAttachments, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceRenderers, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceTimeMetadata, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ArcGisTimeMetadataManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.FacadeFeatureService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.FacadeMapService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.FacadeImageService, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpVectorDeterministic, MigrationFidelityAutomationStatuses.Automated, ImportCompatibilityCodes.Compatible, true, true, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpDestructiveDataManagement, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview, false, false, true)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpRasterSurface, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpRasterConversion, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpDataManagement, MigrationFidelityAutomationStatuses.Assisted, ImportCompatibilityCodes.ManualReview, false, false, false)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.GpUnsupported, MigrationFidelityAutomationStatuses.Unsupported, ImportCompatibilityCodes.ManualReview, false, false, false)]
    public void ResolveOrUnknown_KnownConstructKey_ReturnsSeededDescriptor(
        string constructKey,
        string expectedAutomationStatus,
        string expectedCode,
        bool expectedCanTransform,
        bool expectedCanServe,
        bool expectedRequiresCheck)
    {
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.ConstructKey.Should().Be(constructKey);
        descriptor.AutomationStatus.Should().Be(expectedAutomationStatus);
        descriptor.Code.Should().Be(expectedCode);
        descriptor.CanTransform.Should().Be(expectedCanTransform);
        descriptor.CanServe.Should().Be(expectedCanServe);
        descriptor.RequiresCheck.Should().Be(expectedRequiresCheck);
        descriptor.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ServiceCapabilities, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceCapabilities, MigrationFidelityAutomationStatuses.Unsupported, ImportCompatibilityCodes.ArcGisQueryCapabilityMissing)]
    [InlineData(EsriConstructCapabilityRegistry.Keys.ResourceFields, MigrationFidelityAutomationStatuses.ManualReview, ImportCompatibilityCodes.ManualReview)]
    public void ResolveOrUnknown_ConditionalConstruct_ExposesUnsupportedFallback(
        string constructKey,
        string expectedFallbackStatus,
        string expectedFallbackCode)
    {
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.UnsupportedAutomationStatus.Should().Be(expectedFallbackStatus);
        descriptor.UnsupportedCode.Should().Be(expectedFallbackCode);
        descriptor.UnsupportedReason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("not-a-real-construct")]
    [InlineData("")]
    [InlineData("RESOURCE.IDENTITY")]
    public void ResolveOrUnknown_UnknownKey_ReturnsManualReviewFallback(string constructKey)
    {
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

        var descriptor = registry.ResolveOrUnknown(constructKey);

        descriptor.Should().BeSameAs(EsriConstructCapabilityRegistry.UnknownDescriptor);
        descriptor.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.ManualReview);
        descriptor.Code.Should().Be(ImportCompatibilityCodes.ManualReview);
        descriptor.CanTransform.Should().BeFalse();
        descriptor.CanServe.Should().BeFalse();
        descriptor.RequiresCheck.Should().BeTrue();
    }

    [Fact]
    public void RegisteredConstructKeys_ContainsAllBuiltInKeys()
    {
        var registry = new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors);

        registry.RegisteredConstructKeys.Should().BeEquivalentTo(
        [
            EsriConstructCapabilityRegistry.Keys.ServiceIdentity,
            EsriConstructCapabilityRegistry.Keys.ServiceCapabilities,
            EsriConstructCapabilityRegistry.Keys.ResourceIdentity,
            EsriConstructCapabilityRegistry.Keys.ResourceCapabilities,
            EsriConstructCapabilityRegistry.Keys.ResourceFields,
            EsriConstructCapabilityRegistry.Keys.ResourceDomains,
            EsriConstructCapabilityRegistry.Keys.ResourceSubtypes,
            EsriConstructCapabilityRegistry.Keys.ResourceRelationships,
            EsriConstructCapabilityRegistry.Keys.ResourceAttachments,
            EsriConstructCapabilityRegistry.Keys.ResourceRenderers,
            EsriConstructCapabilityRegistry.Keys.ResourceTimeMetadata,
            EsriConstructCapabilityRegistry.Keys.FacadeFeatureService,
            EsriConstructCapabilityRegistry.Keys.FacadeMapService,
            EsriConstructCapabilityRegistry.Keys.FacadeImageService,
            EsriConstructCapabilityRegistry.Keys.GpVectorDeterministic,
            EsriConstructCapabilityRegistry.Keys.GpDestructiveDataManagement,
            EsriConstructCapabilityRegistry.Keys.GpRasterSurface,
            EsriConstructCapabilityRegistry.Keys.GpRasterConversion,
            EsriConstructCapabilityRegistry.Keys.GpDataManagement,
            EsriConstructCapabilityRegistry.Keys.GpUnsupported
        ]);
    }

    [Fact]
    public void Constructor_DuplicateConstructKey_ThrowsInvalidOperationException()
    {
        var duplicate = new EsriConstructCapabilityDescriptor
        {
            ConstructKey = EsriConstructCapabilityRegistry.Keys.ResourceIdentity,
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "duplicate"
        };

        var act = () => _ = new EsriConstructCapabilityRegistry(
        [
            ..EsriConstructCapabilityRegistry.BuiltInDescriptors,
            duplicate
        ]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*Duplicate*'{EsriConstructCapabilityRegistry.Keys.ResourceIdentity}'*");
    }

    [Fact]
    public void Constructor_DescriptorWithEmptyConstructKey_ThrowsInvalidOperationException()
    {
        var invalid = new EsriConstructCapabilityDescriptor
        {
            ConstructKey = "  ",
            AutomationStatus = MigrationFidelityAutomationStatuses.Automated,
            Code = ImportCompatibilityCodes.Compatible,
            Reason = "blank key"
        };

        var act = () => _ = new EsriConstructCapabilityRegistry([invalid]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-empty ConstructKey*");
    }
}
