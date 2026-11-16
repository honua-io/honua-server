// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicPublicationEligibilityTests
{
    [Theory]
    [InlineData("resource")]
    [InlineData("service")]
    [InlineData("publication")]
    [InlineData("filter")]
    [InlineData("restricted-resource")]
    [InlineData("restricted-service")]
    [Trait("Tier", "Fast")]
    public async Task IsEligibleAsync_RestrictedSource_RefusesPublicCopy(string restriction)
    {
        var source = Source();
        source = restriction switch
        {
            "resource" => source with { Resource = source.Resource with { Metadata = source.Resource.Metadata with { Tenant = "tenant-a" } } },
            "service" => source with { Service = source.Service with { Metadata = source.Service.Metadata with { Tenant = "tenant-a" } } },
            "publication" => source with { Publication = source.Publication with { Metadata = source.Publication.Metadata with { Tenant = "tenant-a" } } },
            "filter" => source with { Resource = source.Resource with { PermanentFilter = new() { Expression = "private = false" } } },
            "restricted-resource" => source with { Resource = source.Resource with { AccessPolicy = new() { AllowAnonymous = false } } },
            _ => source with { Service = source.Service with { AccessPolicy = new() { AllowAnonymous = false } } }
        };
        (await Eligibility().IsEligibleAsync(Snapshot(source), source, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData("rows", "alias", "ROADS")]
    [InlineData("masks", "ALIAS", "*")]
    [InlineData("rows", "*", "*")]
    [InlineData("masks", "unrelated", "roads")]
    [InlineData("rows", "alias", "other-layer")]
    [Trait("Tier", "Fast")]
    public async Task IsEligibleAsync_PolicyThroughAnyResourceService_IsAppliedRegardlessOfRole(
        string kind, string service, string layer)
    {
        var source = Source();
        RlsPolicy[] rows = kind == "rows" ? [new() { Role = "signed-in-only", Service = service, Layer = layer, Attribute = "owner", ClaimType = "sub" }] : [];
        FieldMaskPolicy[] masks = kind == "masks" ? [new() { Role = "signed-in-only", Service = service, Layer = layer, Attribute = "secret" }] : [];
        (await Eligibility(rows, masks).IsEligibleAsync(Snapshot(source), source, default)).Should().Be(service == "unrelated" || layer == "other-layer");
    }

    [UnitTest]
    public async Task IsEligibleAsync_UnrestrictedAnonymousSource_AllowsPublicCopy()
    {
        var source = Source();
        (await Eligibility().IsEligibleAsync(Snapshot(source), source, default)).Should().BeTrue();
    }

    private static PublicPublicationEligibility Eligibility(RlsPolicy[]? rows = null, FieldMaskPolicy[]? masks = null)
    {
        var rowStore = new Mock<IRlsPolicyStore>(MockBehavior.Strict);
        rowStore.Setup(s => s.ListPoliciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rows ?? []);
        var maskStore = new Mock<IFieldMaskPolicyStore>(MockBehavior.Strict);
        maskStore.Setup(s => s.ListPoliciesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(masks ?? []);
        return new(new AccessPolicyEvaluator(), rowStore.Object, maskStore.Object);
    }

    private static PMTilesPublishedSource Source() => new(
        new() { Metadata = new() { Id = "resource-a", Name = "roads" }, AccessPolicy = new AccessPolicy { AllowAnonymous = true } },
        new() { Metadata = new() { Id = "service-a", Name = "primary" }, AccessPolicy = new AccessPolicy { AllowAnonymous = true } },
        new() { Metadata = new() { Id = "publication-a" }, ResourceId = "resource-a", ServiceId = "service-a" });

    private static MetadataV2GraphSnapshot Snapshot(PMTilesPublishedSource source) => new(new()
    {
        Resources = [source.Resource],
        Services = [source.Service, source.Service with { Metadata = new() { Id = "service-b", Name = "alias" } }],
        Publications = [source.Publication, source.Publication with { Metadata = new() { Id = "publication-b" }, ServiceId = "service-b" }]
    }, "etag", DateTimeOffset.UtcNow);
}
