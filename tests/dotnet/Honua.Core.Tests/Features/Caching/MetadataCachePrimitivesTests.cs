// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Caching;

namespace Honua.Core.Tests.Features.Caching;

public sealed class MetadataCachePrimitivesTests
{
    [Trait("Tier", "Fast")]
    [Fact]
    public void BuildKey_NormalizesComponents_AndBuildsDeterministicRedisSafeKey()
    {
        var first = MetadataCacheKeyBuilder.Build(new MetadataCacheKeyRequest
        {
            TenantId = " Tenant A ",
            ProjectId = "Project 7",
            AuthScope = "role:Reader scope:Layer.Metadata",
            SourceUrl = "HTTPS://user:pa55@Example.COM/ArcGIS/rest/services/Foo/FeatureServer?token=abc123&f=json",
            Adapter = " GeoServices ",
            Protocol = "FeatureServer",
            ResourceKind = " Layer ",
            ResourceId = "Roads/Primary",
            Crs = " EPSG: 4326 ",
            Format = " application/geo+json ",
            AdapterVersion = " 11.2 ",
            ProjectionVersion = "PROJ 9.4"
        });

        var second = MetadataCacheKeyBuilder.Build(new MetadataCacheKeyRequest
        {
            TenantId = "tenant-a",
            ProjectId = "project-7",
            AuthScope = "ROLE:reader   SCOPE:layer.metadata",
            SourceUrl = "https://example.com/ArcGIS/rest/services/Foo/FeatureServer?f=json&token=rotated",
            Adapter = "geoservices",
            Protocol = "featureserver",
            ResourceKind = "layer",
            ResourceId = "roads primary",
            Crs = "epsg-4326",
            Format = "APPLICATION GEO JSON",
            AdapterVersion = "11.2",
            ProjectionVersion = "proj-9.4"
        });

        second.Value.Should().Be(first.Value);
        second.KeyFingerprint.Should().Be(first.KeyFingerprint);
        second.SourceFingerprint.Should().Be(first.SourceFingerprint);
        second.AuthScopeFingerprint.Should().Be(first.AuthScopeFingerprint);

        first.Value.Should().StartWith("honua:metadata:v1:");
        first.Value.Should().Contain(":tenant:id-tenant-a:");
        first.Value.Should().Contain(":project:id-project-7:");
        first.Value.Should().Contain(":adapter:geoservices:");
        first.Value.Should().Contain(":protocol:featureserver:");
        first.Value.Should().Contain(":kind:layer:");
        first.Value.Should().Contain(":resource:id-roads-primary:");
        first.Value.Should().Contain(":crs:epsg-4326:");
        first.Value.Should().Contain(":format:application-geo-json:");
        first.Value.Should().Contain(":adapter-version:11.2:");
        first.Value.Should().Contain(":projection-version:proj-9.4");
        Regex.IsMatch(first.Value, "^[a-z0-9:._-]+$", RegexOptions.CultureInvariant).Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuildKey_DistinguishesMissingScopesFromLiteralScopeIds()
    {
        var missingScopes = MetadataCacheKeyBuilder.Build(new MetadataCacheKeyRequest
        {
            SourceUrl = "https://example.com/geoserver/rest?f=json",
            Adapter = "geoserver",
            Protocol = "wms",
            ResourceKind = "service"
        });
        var literalScopes = MetadataCacheKeyBuilder.Build(new MetadataCacheKeyRequest
        {
            TenantId = "global",
            ProjectId = "global",
            SourceUrl = "https://example.com/geoserver/rest?f=json",
            Adapter = "geoserver",
            Protocol = "wms",
            ResourceKind = "service",
            ResourceId = "root"
        });

        literalScopes.Value.Should().NotBe(missingScopes.Value);
        missingScopes.Value.Should().Contain(":tenant:none:");
        missingScopes.Value.Should().Contain(":project:none:");
        missingScopes.Value.Should().Contain(":resource:none:");
        literalScopes.Value.Should().Contain(":tenant:id-global:");
        literalScopes.Value.Should().Contain(":project:id-global:");
        literalScopes.Value.Should().Contain(":resource:id-root:");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuildKey_FingerprintsCredentialBearingInputs_WithoutPuttingSecretsOrSourceAddressesInKey()
    {
        var built = MetadataCacheKeyBuilder.Build(new MetadataCacheKeyRequest
        {
            TenantId = "tenant-a",
            ProjectId = "project-7",
            AuthScope = "Bearer super-secret-jwt role:admin",
            SourceUrl = "https://user:pa55@example.com/geoserver/rest?access_token=secret-token&f=json",
            Adapter = "geoserver",
            Protocol = "wfs",
            ResourceKind = "layer",
            ResourceId = "roads"
        });

        built.Value.Should().NotContain("super-secret");
        built.Value.Should().NotContain("bearer");
        built.Value.Should().NotContain("example.com");
        built.Value.Should().NotContain("pa55");
        built.Value.Should().NotContain("secret-token");
        built.Value.Should().NotContain("access_token");
        built.SourceFingerprint.Should().HaveLength(32);
        built.AuthScopeFingerprint.Should().HaveLength(32);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void FingerprintSource_IgnoresCredentialRotation_ButKeepsBehaviorChangingQueryParameters()
    {
        var first = MetadataCacheKeyBuilder.FingerprintSource(
            "https://user:first@example.com/geoserver/rest?token=first&x-api-key=first&f=json");
        var second = MetadataCacheKeyBuilder.FingerprintSource(
            "https://user:second@example.com/geoserver/rest?f=json&token=second&x-api-key=second");
        var changedFormat = MetadataCacheKeyBuilder.FingerprintSource(
            "https://example.com/geoserver/rest?f=pjson&token=second");

        second.Should().Be(first);
        changedFormat.Should().NotBe(first);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void FingerprintSource_PreservesCaseSensitivePathAndQueryValues()
    {
        var canonical = MetadataCacheKeyBuilder.FingerprintSource(
            "https://example.com/Data/Layer?where=Name%3D%27Roads%27&f=json");
        var hostCaseChanged = MetadataCacheKeyBuilder.FingerprintSource(
            "HTTPS://EXAMPLE.COM/Data/Layer?f=json&where=Name%3D%27Roads%27");
        var pathCaseChanged = MetadataCacheKeyBuilder.FingerprintSource(
            "https://example.com/data/layer?where=Name%3D%27Roads%27&f=json");
        var queryValueCaseChanged = MetadataCacheKeyBuilder.FingerprintSource(
            "https://example.com/Data/Layer?where=Name%3D%27roads%27&f=json");

        hostCaseChanged.Should().Be(canonical);
        pathCaseChanged.Should().NotBe(canonical);
        queryValueCaseChanged.Should().NotBe(canonical);
    }
}
