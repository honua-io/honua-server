// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata;

[Protocol(Protocols.TestQuality)]
public sealed class MetadataV2SecurityCacheMigrationScaffoldTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void ConnectionSecrets_RedactsReferences_AndValidatesShape()
    {
        var secrets = new MetadataV2ConnectionSecrets
        {
            EndpointRef = MetadataV2SecretReference.Create("azure-key-vault", "kv/prod/honua-endpoint", "7"),
            CredentialRef = MetadataV2SecretReference.Create("env", "HONUA_PROD_PASSWORD")
        };

        var redacted = secrets.Redacted();

        secrets.Validate().Should().BeEmpty();
        redacted.EndpointRef.Should().NotBeNull();
        redacted.EndpointRef!.Provider.Should().Be("azure-key-vault");
        redacted.EndpointRef.Reference.Should().Be("ho***nt");
        redacted.EndpointRef.Version.Should().Be("***");
        redacted.EndpointRef.ToString().Should().NotContain("honua-endpoint");

        var invalid = new MetadataV2ConnectionSecrets
        {
            ConnectionRef = MetadataV2SecretReference.Create("env:bad", "HONUA_CONNECTION"),
            CredentialRef = MetadataV2SecretReference.Create("env", "HONUA_PASSWORD"),
            InlineDevOnly = new MetadataV2InlineDevelopmentSecretMarker { Enabled = true }
        };

        invalid.Validate().Should().BeEquivalentTo(
        [
            new MetadataV2SecretReferenceValidationIssue(
                "connectionRef.provider",
                "provider must be a provider name, not a full secret URI."),
            new MetadataV2SecretReferenceValidationIssue(
                "connectionRef",
                "connectionRef is mutually exclusive with endpointRef and credentialRef."),
            new MetadataV2SecretReferenceValidationIssue(
                "inlineDevOnly",
                "inlineDevOnly cannot be combined with external secret references."),
            new MetadataV2SecretReferenceValidationIssue(
                "inlineDevOnly.reason",
                "inlineDevOnly requires a reason.")
        ], options => options.WithStrictOrdering());
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void PolicyPresets_ExposeExpectedActions()
    {
        MetadataV2PolicyActions.All.Select(action => action.Value).Should().BeEquivalentTo(
        [
            "metadata.read",
            "metadata.write",
            "features.query",
            "features.edit",
            "tiles.read",
            "raster.render",
            "catalog.publish",
            "connections.manage",
            "admin.rbac.write"
        ], options => options.WithStrictOrdering());

        MetadataV2PolicyPresets.Reader.Actions.Should().Equal(
            MetadataV2PolicyActions.MetadataRead,
            MetadataV2PolicyActions.FeaturesQuery,
            MetadataV2PolicyActions.TilesRead,
            MetadataV2PolicyActions.RasterRender);

        MetadataV2PolicyPresets.Publisher.Allows(MetadataV2PolicyActions.CatalogPublish).Should().BeTrue();
        MetadataV2PolicyPresets.Publisher.Allows(MetadataV2PolicyActions.ConnectionsManage).Should().BeFalse();
        MetadataV2PolicyPresets.ConnectionManager.Actions.Should().Contain(MetadataV2PolicyActions.ConnectionsManage);
        MetadataV2PolicyPresets.RbacAdministrator.Actions.Should().Contain(MetadataV2PolicyActions.AdminRbacWrite);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void CacheKeyBuilder_BuildsDeterministicSnapshotAndProjectionKeys()
    {
        var first = new MetadataV2CacheKeyRequest
        {
            Environment = "Prod",
            CatalogId = "Main/Catalog",
            SchemaVersion = "honua.io/v2",
            Revision = " Rev 42 ",
            ProjectionTarget = "OGC API Features",
            ProjectionProfileVersion = "Profile 1"
        };

        var second = first with
        {
            Environment = "prod",
            CatalogId = "main catalog",
            Revision = "rev-42",
            ProjectionTarget = "ogc-api-features",
            ProjectionProfileVersion = "profile-1"
        };

        var snapshot = MetadataV2CacheKeyBuilder.BuildSnapshot(first);
        var projection = MetadataV2CacheKeyBuilder.BuildProjection(first);

        MetadataV2CacheKeyBuilder.BuildSnapshot(second).Value.Should().Be(snapshot.Value);
        MetadataV2CacheKeyBuilder.BuildProjection(second).Value.Should().Be(projection.Value);
        snapshot.Value.Should().Be(
            "honua:metadata:v2:snapshot:environment:id-prod:catalog:id-main-catalog:schema:id-honua.io-v2:revision:id-rev-42");
        projection.Value.Should().Be(
            "honua:metadata:v2:projection:environment:id-prod:catalog:id-main-catalog:schema:id-honua.io-v2:revision:id-rev-42:target:id-ogc-api-features:profile:id-profile-1");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void CacheKeyBuilder_PreservesUniquenessWhenComponentsAreTruncated()
    {
        var sharedPrefix = new string('a', 96);
        var first = new MetadataV2CacheKeyRequest
        {
            Environment = "prod",
            CatalogId = sharedPrefix + "-first",
            SchemaVersion = "v2",
            Revision = "current"
        };

        var second = first with
        {
            CatalogId = sharedPrefix + "-second"
        };

        var firstKey = MetadataV2CacheKeyBuilder.BuildSnapshot(first).Value;
        var secondKey = MetadataV2CacheKeyBuilder.BuildSnapshot(second).Value;

        firstKey.Should().NotBe(secondKey);
        firstKey.Should().MatchRegex("catalog:id-a{79}-[0-9a-f]{16}:schema");
        secondKey.Should().MatchRegex("catalog:id-a{79}-[0-9a-f]{16}:schema");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void MigrationDiagnosticReport_AggregatesSeverityAndDiagnosticGroups()
    {
        var report = MetadataV2MigrationDiagnosticReport.Create(
        [
            MetadataV2MigrationDiagnostic.InferredDefault(
                "metadata.v2.default.environment",
                "Environment defaulted to prod.",
                "service:roads",
                "metadata.environment"),
            MetadataV2MigrationDiagnostic.Warning(
                "metadata.v2.legacy.style",
                "Legacy style requires review.",
                "layer:roads",
                "spec.style"),
            MetadataV2MigrationDiagnostic.ManualFollowUp(
                "metadata.v2.connection.owner",
                "Assign an owner to the migrated connection.",
                resourceRef: "connection:roads-db"),
            MetadataV2MigrationDiagnostic.Blocker(
                "metadata.v2.inline.secret",
                "Inline production secret cannot be migrated automatically.",
                "connection:roads-db",
                "spec.connection")
        ]);

        report.MaxSeverity.Should().Be(MetadataV2MigrationDiagnosticSeverity.Blocker);
        report.HasBlockers.Should().BeTrue();
        report.InferredDefaults.Should().ContainSingle().Which.Code.Should().Be("metadata.v2.default.environment");
        report.Warnings.Should().HaveCount(2);
        report.Blockers.Should().ContainSingle().Which.Code.Should().Be("metadata.v2.inline.secret");
        report.ManualFollowUps.Should().ContainSingle().Which.ResourceRef.Should().Be("connection:roads-db");
    }
}
