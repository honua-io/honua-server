// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Unit coverage for the shared subtype fidelity seam (honua-server#4824 REQ-002). A source
/// editing construct the canonical model cannot represent must reach the operator as a named
/// finding against the resource, carrying the construct code and the offending type, rather
/// than as an opaque failure of the whole layer read.
/// </summary>
public sealed class EsriSubtypeFidelityClassifierTests
{
    private static readonly EsriConstructCapabilityDescriptor Descriptor =
        new EsriConstructCapabilityRegistry(EsriConstructCapabilityRegistry.BuiltInDescriptors)
            .ResolveOrUnknown(EsriConstructCapabilityRegistry.Keys.ResourceSubtypes);

    [Fact]
    public void Classify_MultipleEditingTemplates_NamesTheConstructAndTheType()
    {
        var resource = ParseResource("""
            { "typeIdField": "hazardtype", "types": [
              { "id": "Flooding", "name": "Flooding",
                "templates": [{ "name": "Minor" }, { "name": "Major" }] }] }
            """);

        var finding = EsriSubtypeFidelityClassifier.Classify(resource, Descriptor);

        finding.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
        finding.Code.Should().Be(ImportCompatibilityCodes.ArcGisFeatureTypeTemplatesUnsupported);
        finding.Reason.Should().Contain("more than one editing template");
        finding.ManualSteps.Should().NotBeEmpty();
        finding.Metadata[EsriSubtypeFidelityClassifier.UnsupportedConstructMetadataKey]
            .Should().Be(ImportCompatibilityCodes.ArcGisFeatureTypeTemplatesUnsupported);
        finding.Metadata[EsriSubtypeFidelityClassifier.UnsupportedDetailMetadataKey]
            .Should().Contain("Flooding");
    }

    [Fact]
    public void Classify_ExplicitDomainClearing_NamesTheConstructAndTheField()
    {
        var resource = ParseResource("""
            { "typeIdField": "status", "types": [
              { "id": "Closed", "name": "Closed", "domains": { "priority": null } }] }
            """);

        var finding = EsriSubtypeFidelityClassifier.Classify(resource, Descriptor);

        finding.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
        finding.Code.Should().Be(ImportCompatibilityCodes.ArcGisFeatureTypeDomainClearingUnsupported);
        finding.Reason.Should().Contain("clears a field domain");
        finding.Metadata[EsriSubtypeFidelityClassifier.UnsupportedDetailMetadataKey]
            .Should().Contain("priority");
    }

    [Fact]
    public void Classify_UnusableTypeIdentity_NamesTheConstruct()
    {
        var resource = ParseResource("""
            { "typeIdField": "kind", "types": [{ "id": [1], "name": "One" }] }
            """);

        var finding = EsriSubtypeFidelityClassifier.Classify(resource, Descriptor);

        finding.Code.Should().Be(ImportCompatibilityCodes.ArcGisFeatureTypeIdentityUnsupported);
        finding.AutomationStatus.Should().Be(MigrationFidelityAutomationStatuses.Unsupported);
    }

    [Fact]
    public void Classify_CapturedFeatureTypes_KeepsTheRegistryTier()
    {
        var resource = ParseResource("""
            { "typeIdField": "hazardtype", "types": [
              { "id": "Flooding", "name": "Flooding",
                "templates": [{ "prototype": { "attributes": { "hazardtype": "Flooding" } } }] }] }
            """);

        var finding = EsriSubtypeFidelityClassifier.Classify(resource, Descriptor);

        finding.AutomationStatus.Should().Be(Descriptor.AutomationStatus);
        finding.Code.Should().Be(ImportCompatibilityCodes.ArcGisSubtypesManualReview);
        finding.Metadata.Should().BeEmpty("a captured construct carries no unsupported-construct metadata");
    }

    [Fact]
    public void Classify_CapturedSubtypeEncoding_KeepsTheRegistryTier()
    {
        var resource = ParseResource("""
            { "subtypeField": "buildingtype", "subtypes": [{ "code": 1, "name": "Residential" }] }
            """);

        var finding = EsriSubtypeFidelityClassifier.Classify(resource, Descriptor);

        finding.Code.Should().Be(ImportCompatibilityCodes.ArcGisSubtypesManualReview);
        finding.Metadata.Should().BeEmpty();
    }

    private static JsonElement ParseResource(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
