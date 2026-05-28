// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;

namespace Honua.Core.Tests.Features.Import;

/// <summary>
/// Tests for <see cref="OgcWfsImportRequest"/> and <see cref="OgcWfsImportResult"/> defaults.
/// </summary>
public sealed class OgcWfsImportRequestTests
{
    [Fact]
    public void OgcWfsImportRequest_Defaults_AreSafeForOperatorReview()
    {
        var request = new OgcWfsImportRequest { ServiceUrl = "https://example.com/wfs" };

        request.PageSize.Should().Be(1000);
        request.RequestTimeoutSeconds.Should().Be(120);
        request.DryRun.Should().BeFalse();
        request.ApplyMode.Should().BeFalse("operators must explicitly opt in to data copy");
        request.OverwriteExisting.Should().BeFalse();
        request.AllowUnsafeLocalUrls.Should().BeFalse();
        request.FeatureTypeNames.Should().BeNull();
        request.TargetSrid.Should().BeNull();
        request.TargetSchema.Should().BeNull();
        request.Version.Should().BeNull();
    }

    [Fact]
    public void OgcWfsImportResult_Defaults_HaveEmptyCollections()
    {
        var manifest = BuildManifestStub();
        var parity = BuildParityStub();

        var result = new OgcWfsImportResult
        {
            Success = true,
            SourceServiceUrl = "https://example.com/wfs",
            Manifest = manifest,
            ParityEvidence = parity
        };

        result.FeatureTypes.Should().BeEmpty();
        result.Warnings.Should().BeEmpty();
        result.FeatureTypesPlanned.Should().Be(0);
        result.FeatureTypesImported.Should().Be(0);
        result.FeaturesCopied.Should().Be(0);
        result.WasDryRun.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void OgcWfsImportedFeatureType_DefaultsCarryEmptyWarnings()
    {
        var record = new OgcWfsImportedFeatureType
        {
            SourceName = "demo:cities",
            Classification = MigrationFidelityAutomationStatuses.Automated
        };

        record.Warnings.Should().BeEmpty();
        record.FeaturesCopied.Should().Be(0);
        record.FeaturesFailed.Should().Be(0);
        record.TargetSchema.Should().BeNull();
        record.TargetTable.Should().BeNull();
    }

    private static MigrationManifestArtifact BuildManifestStub()
    {
        return new MigrationManifestArtifact
        {
            SourceKind = "ogc-wfs",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Demo WFS",
                BaseUrl = "https://example.com/wfs",
                ServiceType = "WFS"
            },
            Summary = new MigrationManifestSummary()
        };
    }

    private static MigrationParityEvidenceArtifact BuildParityStub()
    {
        return new MigrationParityEvidenceArtifact
        {
            SourceKind = "ogc-wfs",
            Source = new MigrationSourceIdentity
            {
                DisplayName = "Demo WFS",
                BaseUrl = "https://example.com/wfs",
                ServiceType = "WFS"
            },
            OverallState = "unknown",
            Summary = "Stub parity evidence for default-shape tests.",
            CutoverReadiness = new MigrationCutoverReadinessSummary
            {
                State = "unknown"
            }
        };
    }
}
