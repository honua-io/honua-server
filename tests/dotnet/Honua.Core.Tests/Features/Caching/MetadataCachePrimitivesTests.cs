// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Caching;

namespace Honua.Core.Tests.Features.Caching;

public sealed class MetadataCachePrimitivesTests
{
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
            SourceUrl = "https://example.com/arcgis/rest/services/foo/featureserver?f=json&token=rotated",
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
        first.Value.Should().Contain(":tenant:tenant-a:");
        first.Value.Should().Contain(":project:project-7:");
        first.Value.Should().Contain(":adapter:geoservices:");
        first.Value.Should().Contain(":protocol:featureserver:");
        first.Value.Should().Contain(":kind:layer:");
        first.Value.Should().Contain(":resource:roads-primary:");
        first.Value.Should().Contain(":crs:epsg-4326:");
        first.Value.Should().Contain(":format:application-geo-json:");
        first.Value.Should().Contain(":adapter-version:11.2:");
        first.Value.Should().Contain(":projection-version:proj-9.4");
        Regex.IsMatch(first.Value, "^[a-z0-9:._-]+$", RegexOptions.CultureInvariant).Should().BeTrue();
    }

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

    [Fact]
    public void For_KnownMetadataClasses_ReturnsTtlAndStaleIfErrorWindows()
    {
        MetadataCachePolicy.For(MetadataCacheContentClass.ServiceList).Should().BeEquivalentTo(
            new MetadataCachePolicy
            {
                ContentClass = MetadataCacheContentClass.ServiceList,
                IsCacheable = true,
                Ttl = TimeSpan.FromHours(1),
                StaleIfError = TimeSpan.FromHours(6)
            });

        MetadataCachePolicy.For(MetadataCacheContentClass.LayerDescriptor).Should().BeEquivalentTo(
            new MetadataCachePolicy
            {
                ContentClass = MetadataCacheContentClass.LayerDescriptor,
                IsCacheable = true,
                Ttl = TimeSpan.FromMinutes(30),
                StaleIfError = TimeSpan.FromHours(2)
            });

        MetadataCachePolicy.For(MetadataCacheContentClass.TileMatrixSets).Should().BeEquivalentTo(
            new MetadataCachePolicy
            {
                ContentClass = MetadataCacheContentClass.TileMatrixSets,
                IsCacheable = true,
                Ttl = TimeSpan.FromHours(24),
                StaleIfError = TimeSpan.FromDays(7)
            });

        var knownMetadataClasses = new[]
        {
            MetadataCacheContentClass.ServiceList,
            MetadataCacheContentClass.LayerDescriptor,
            MetadataCacheContentClass.Fields,
            MetadataCacheContentClass.Domains,
            MetadataCacheContentClass.Capabilities,
            MetadataCacheContentClass.Relationships,
            MetadataCacheContentClass.RenderersStyles,
            MetadataCacheContentClass.Legends,
            MetadataCacheContentClass.TileMatrixSets,
            MetadataCacheContentClass.StacCollectionMetadata,
            MetadataCacheContentClass.OgcProcessDescriptions
        };

        foreach (var contentClass in knownMetadataClasses)
        {
            var policy = MetadataCachePolicy.For(contentClass);
            policy.IsCacheable.Should().BeTrue(contentClass.ToString());
            policy.Ttl.Should().BePositive(contentClass.ToString());
            policy.StaleIfError.Should().BePositive(contentClass.ToString());
            policy.NoCacheReason.Should().BeNull(contentClass.ToString());
        }
    }

    [Fact]
    public void For_AdHocFeatureQueryAndResultClasses_ReturnsNoCachePolicy()
    {
        var noCacheClasses = new[]
        {
            MetadataCacheContentClass.FeatureResponse,
            MetadataCacheContentClass.QueryResponse,
            MetadataCacheContentClass.ResultResponse,
            MetadataCacheContentClass.RealtimeResponse,
            MetadataCacheContentClass.RouteResponse,
            MetadataCacheContentClass.ProcessResultResponse
        };

        foreach (var contentClass in noCacheClasses)
        {
            var policy = MetadataCachePolicy.For(contentClass);
            policy.IsCacheable.Should().BeFalse(contentClass.ToString());
            policy.Ttl.Should().Be(TimeSpan.Zero, contentClass.ToString());
            policy.StaleIfError.Should().Be(TimeSpan.Zero, contentClass.ToString());
            policy.NoCacheReason.Should().NotBeNullOrWhiteSpace(contentClass.ToString());
            MetadataCachePolicy.IsCacheableContent(contentClass).Should().BeFalse(contentClass.ToString());
        }
    }

    [Fact]
    public void MetadataCacheState_CapturesValidatorsRevalidationAndFailureShape()
    {
        var lastModified = new DateTimeOffset(2026, 5, 7, 10, 0, 0, TimeSpan.Zero);
        var revalidateAfter = lastModified.AddMinutes(30);

        var state = new MetadataCacheState
        {
            Status = MetadataCacheStatus.Stale,
            KeyFingerprint = "0123456789abcdef0123456789abcdef",
            Age = TimeSpan.FromMinutes(35),
            Ttl = TimeSpan.FromMinutes(30),
            StaleIfError = TimeSpan.FromHours(2),
            Validators = new MetadataCacheValidators
            {
                ETag = "\"abc123\"",
                LastModified = lastModified
            },
            RevalidateAfter = revalidateAfter,
            InvalidationReason = MetadataCacheInvalidationReason.SchemaChanged.ToString(),
            RefreshErrorId = "refresh-err-42"
        };

        state.HasUsableEntry.Should().BeTrue();
        state.Validators.HasValidators.Should().BeTrue();
        state.Validators.ETag.Should().Be("\"abc123\"");
        state.Validators.LastModified.Should().Be(lastModified);
        state.RevalidateAfter.Should().Be(revalidateAfter);
        state.InvalidationReason.Should().Be("SchemaChanged");
        state.RefreshErrorId.Should().Be("refresh-err-42");

        MetadataCacheValidators.None.HasValidators.Should().BeFalse();
        new MetadataCacheState
        {
            Status = MetadataCacheStatus.Bypass,
            KeyFingerprint = state.KeyFingerprint,
            InvalidationReason = "no-cache"
        }.HasUsableEntry.Should().BeFalse();
    }

    [Fact]
    public void InvalidationEvent_MatchesTargetedRequestUsingNormalizedComponentsAndSourceFingerprint()
    {
        var request = new MetadataCacheKeyRequest
        {
            TenantId = "tenant-a",
            ProjectId = "project-7",
            SourceUrl = "https://example.com/geoserver/rest?token=secret&f=json",
            Adapter = "geoserver",
            Protocol = "wfs",
            ResourceKind = "layer",
            ResourceId = "roads primary"
        };

        var sourceFingerprint = MetadataCacheKeyBuilder.FingerprintSource("https://example.com/geoserver/rest?f=json");
        var targeted = new MetadataCacheInvalidationEvent
        {
            TenantId = "TENANT A",
            ProjectId = "Project 7",
            SourceFingerprint = sourceFingerprint,
            Adapter = "GeoServer",
            ResourceId = "Roads/Primary",
            Reason = MetadataCacheInvalidationReason.SchemaChanged
        };

        targeted.Matches(request).Should().BeTrue();
        targeted.Reason.Should().Be(MetadataCacheInvalidationReason.SchemaChanged);

        var wrongResource = targeted with { ResourceId = "buildings", Reason = MetadataCacheInvalidationReason.StyleChanged };
        wrongResource.Matches(request).Should().BeFalse();

        var wrongSource = targeted with
        {
            SourceFingerprint = MetadataCacheKeyBuilder.FingerprintSource("https://other.example.com/geoserver/rest?f=json")
        };
        wrongSource.Matches(request).Should().BeFalse();

        var tenantWide = new MetadataCacheInvalidationEvent
        {
            TenantId = "tenant a",
            Reason = MetadataCacheInvalidationReason.PermissionChanged
        };
        tenantWide.Matches(request).Should().BeTrue();
    }
}
